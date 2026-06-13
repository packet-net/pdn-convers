using Convers.Host.Sessions;
using Convers.Protocol;

namespace Convers.Host.Uplink;

/// <summary>
/// The shared host-link line transport with an in-line compression stage (W7-deferred plumbing). It turns a
/// raw byte duplex (a TCP <c>NetworkStream</c> or an RHP child stream) into the line-oriented
/// <see cref="IUpstreamLink"/> seam the engine speaks, inserting the conversd-saupp Huffman
/// <see cref="HostLinkCompression"/> codec between the line framing and the wire. The three concrete
/// transports (<c>TcpUpstreamLink</c>, <c>RfUpstreamLink</c>, <c>InboundPeerLink</c>) differ only in their
/// byte source/sink and their outbound line terminator, so they all delegate the line↔byte conversion,
/// compression, and <c>//COMP</c> negotiation here — keeping one implementation of the interop-critical
/// framing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Send.</b> A line is framed with the transport's terminator (LF for TCP, CR for AX.25), then the
/// compression stage encodes it — Huffman frames once armed, passthrough otherwise — and the bytes are
/// written. The <c>//COMP</c> offer is written uncompressed ahead of any armed output (conversd
/// <c>fast_write(..., -1)</c>).
/// </para>
/// <para>
/// <b>Receive.</b> Raw reads are decoded by the compression stage (frame-by-frame once armed, with the
/// conversd verbatim fallback), a partial trailing frame is carried to the next read, and the decoded bytes
/// are split into lines by the same CR/LF-tolerant assembler the wire has always used. Inbound
/// <c>//COMP</c> toggles are consumed here — they drive the negotiation state and are never surfaced to the
/// engine (which would not know what to do with them), exactly as conversd handles them out of band.
/// </para>
/// </remarks>
internal sealed class CompressingLineTransport
{
    /// <summary>Writes bytes to the wire (one logical write; the duplex flushes as needed).</summary>
    internal delegate Task WriteBytesAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);

    /// <summary>Reads the next raw chunk from the wire, or <see langword="null"/> at end of stream.</summary>
    internal delegate Task<byte[]?> ReadBytesAsync(CancellationToken cancellationToken);

    private readonly WriteBytesAsync _write;
    private readonly ReadBytesAsync _read;
    private readonly char _terminator;
    private readonly HostLinkCompression _compression = new();
    private readonly LineAssembler _assembler = new();
    private readonly Queue<string> _pending = new();
    private readonly Queue<byte[]> _pendingReplies = new();
    private readonly object _sendLock = new();
    private Task _sendTail = Task.CompletedTask;
    private byte[] _carry = [];

    /// <summary>
    /// Creates the transport over a byte duplex. <paramref name="terminator"/> is the outbound line
    /// terminator (<c>'\n'</c> for TCP, <c>'\r'</c> for AX.25). <paramref name="bufferedLines"/> are already-
    /// read inbound lines (the demux's peeked <c>/..HOST</c>/<c>/..PASS</c>) delivered before any fresh read.
    /// </summary>
    public CompressingLineTransport(
        WriteBytesAsync write, ReadBytesAsync read, char terminator, IEnumerable<string>? bufferedLines = null)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(read);
        _write = write;
        _read = read;
        _terminator = terminator;
        if (bufferedLines is not null)
        {
            foreach (string line in bufferedLines)
            {
                _pending.Enqueue(line);
            }
        }
    }

    /// <summary>Whether the transmit side is currently Huffman-coding outbound bytes.</summary>
    public bool CompressionEngaged => _compression.TxActive;

    /// <summary>Whether the receive side is currently Huffman-decoding inbound bytes (the peer accepted).</summary>
    public bool ReceiveCompressionEngaged => _compression.RxActive;

    /// <summary>
    /// Re-injects already-consumed lines at the <em>front</em> of the read queue (the demux replays the
    /// peeked <c>/..HOST</c>/<c>/..PASS</c> after handing the link to the peer session).
    /// </summary>
    public void PrependBufferedLines(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var head = new List<string>(lines);
        if (head.Count == 0)
        {
            return;
        }

        while (_pending.Count > 0)
        {
            head.Add(_pending.Dequeue());
        }

        foreach (string line in head)
        {
            _pending.Enqueue(line);
        }
    }

    /// <summary>Frame, compress (when armed), and write one logical line. Sends are serialised so a
    /// negotiation offer and the lines around it stay ordered on the wire.</summary>
    public Task SendLineAsync(string line, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(line);
        byte[] framed = FrameLine(line);
        // Capture-and-encode is deferred into the serialised section so the compression-armed state read by
        // EncodeOutbound reflects the on-wire order (an offer/confirm that lands first arms tx for this line).
        // Each frame is written separately so the peer's frame-per-read decode aligns (conversd writes one
        // frame per write()).
        return EnqueueSendAsync(async ct =>
        {
            foreach (byte[] frame in _compression.EncodeOutbound(framed))
            {
                await _write(frame, ct).ConfigureAwait(false);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Returns the next inbound line (terminator stripped), decompressing and consuming any <c>//COMP</c>
    /// negotiation lines transparently, or <see langword="null"/> at end of stream.
    /// </summary>
    public async Task<string?> ReceiveLineAsync(CancellationToken cancellationToken)
    {
        while (_pending.Count == 0)
        {
            byte[]? raw = await _read(cancellationToken).ConfigureAwait(false);
            if (raw is null)
            {
                return null; // stream closed
            }

            // Decode every complete frame the combined buffer holds (DecodeInbound takes one frame per call,
            // returning the rest as a remainder). A partial trailing frame stops the loop and is carried to
            // the next read; a passthrough (un-armed, or verbatim) consumes the whole buffer in one call.
            byte[] buffer = Combine(_carry, raw);
            _carry = [];
            while (buffer.Length != 0)
            {
                byte[] plain = _compression.DecodeInbound(buffer, out byte[] remainder);
                bool madeProgress = remainder.Length < buffer.Length;
                buffer = remainder;
                if (plain.Length == 0)
                {
                    // Nothing decoded this pass: either a held partial frame (remainder == whole buffer) or an
                    // empty stored payload. Carry the remainder and stop to avoid spinning.
                    _carry = buffer;
                    break;
                }

                foreach (string assembled in _assembler.Feed(plain))
                {
                    QueueAssembledLine(assembled);
                }

                if (!madeProgress)
                {
                    // Defensive: a decode that returned bytes but consumed nothing would loop forever; carry
                    // and stop. (Not reachable for well-formed frames, which always advance.)
                    _carry = buffer;
                    break;
                }
            }

            // Drain any reciprocal //COMP 1 replies queued while assembling. Each is written uncompressed and,
            // immediately after, our transmit side is armed in the SAME send-chain slot — so the toggle marks
            // the exact wire position where our outbound becomes compressed (no earlier-queued line can be
            // compressed ahead of it).
            while (_pendingReplies.Count > 0)
            {
                byte[] replyBytes = _pendingReplies.Dequeue();
                await EnqueueSendAsync(async ct =>
                {
                    await _write(replyBytes, ct).ConfigureAwait(false);
                    _compression.EngageTransmitAfterReciprocal();
                }, cancellationToken).ConfigureAwait(false);
            }
        }

        return _pending.Dequeue();
    }

    /// <summary>
    /// Routes one assembled line: drop an empty line (a bare terminator / the CRs bracketing a toggle never
    /// dispatch in conversd), consume a <c>//COMP</c> negotiation toggle out of band (queueing any reply),
    /// and enqueue everything else as a real line for the engine.
    /// </summary>
    private void QueueAssembledLine(string assembled)
    {
        if (assembled.Length == 0)
        {
            return;
        }

        if (_compression.TryApplyInboundToggle(assembled, out byte[]? reply))
        {
            if (reply is not null)
            {
                _pendingReplies.Enqueue(reply);
            }

            return;
        }

        _pending.Enqueue(assembled);
    }

    /// <summary>
    /// Offer compression: write our <c>//COMP 1</c> uncompressed (the transmit side arms only when the peer
    /// answers). Idempotent. Serialised against <see cref="SendLineAsync"/> so the offer lands cleanly
    /// between framed lines.
    /// </summary>
    public Task OfferCompressionAsync(CancellationToken cancellationToken) =>
        EnqueueSendAsync(ct => _write(_compression.BuildOffer(), ct), cancellationToken);

    /// <summary>
    /// Arm the transmit side for an offer the caller made <em>out of band</em> (it wrote the enable trigger
    /// itself — e.g. a USER-link <c>//comp on</c> — over <see cref="SendLineAsync"/>). Ordered through the
    /// send chain so the arm takes effect only after that already-queued trigger line is on the wire
    /// (uncompressed); the peer's <c>//COMP 1</c> answer then arms the receive side without a reciprocal.
    /// </summary>
    public Task NoteExternalCompressionOfferAsync(CancellationToken cancellationToken) =>
        EnqueueSendAsync(_ =>
        {
            _compression.NoteExternalOffer();
            return Task.CompletedTask;
        }, cancellationToken);

    /// <summary>
    /// Serialises a write behind the previous one (an async tail-task mutex — no disposable semaphore): each
    /// caller chains its send after the current tail, so the offer, the negotiation reply, and the framed
    /// lines never interleave their bytes on the wire and stay in submission order. A faulted predecessor
    /// does not block successors (the tail observes only completion, not the result).
    /// </summary>
    private Task EnqueueSendAsync(Func<CancellationToken, Task> send, CancellationToken cancellationToken)
    {
        Task mine;
        lock (_sendLock)
        {
            Task predecessor = _sendTail;
            mine = RunAfterAsync(predecessor, send, cancellationToken);
            // The tail tracks completion only, so one failed/cancelled send cannot wedge the chain.
            _sendTail = mine.ContinueWith(static _ => { }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        return mine;
    }

    private static async Task RunAfterAsync(Task predecessor, Func<CancellationToken, Task> send, CancellationToken cancellationToken)
    {
        try
        {
            await predecessor.ConfigureAwait(false);
        }
        catch
        {
            // A predecessor's failure is surfaced to its own awaiter, not to ours.
        }

        cancellationToken.ThrowIfCancellationRequested();
        await send(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Frames a line for the wire: append the transport's terminator if absent, then Latin-1 encode.</summary>
    private byte[] FrameLine(string line)
    {
        string framed = line.Length != 0 && (line[^1] == '\r' || line[^1] == '\n')
            ? line
            : line + _terminator;
        return ConversWire.Encode(framed);
    }

    private static byte[] Combine(byte[] head, ReadOnlySpan<byte> tail)
    {
        if (head.Length == 0)
        {
            return tail.ToArray();
        }

        var result = new byte[head.Length + tail.Length];
        head.CopyTo(result, 0);
        tail.CopyTo(result.AsSpan(head.Length));
        return result;
    }
}
