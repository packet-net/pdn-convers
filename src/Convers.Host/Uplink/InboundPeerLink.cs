using System.Text;
using Convers.Host.Rhp;
using Convers.Host.Sessions;

namespace Convers.Host.Uplink;

/// <summary>
/// The <see cref="IUpstreamLink"/> transport for an accepted <em>downstream</em> peer (W7c): an
/// <see cref="RhpChildConnection"/> the peer opened to our bound callsign, carrying the convers wire as
/// Latin-1, CR/CRLF/LF-tolerant lines (a peer may be AX.25 CR-discipline or telnet-ish CRLF — the
/// <see cref="LineAssembler"/> handles both). Outbound lines get a CR terminator (the AX.25 discipline the
/// RF peer expects). It is the inbound mirror of <see cref="RfUpstreamLink"/>; the demux hands the already-
/// read first <c>/..HOST</c> line back via the constructor so the engine sees the full handshake.
/// </summary>
public sealed class InboundPeerLink : IUpstreamLink
{
    private readonly RhpChildConnection _child;
    private readonly LineAssembler _assembler = new();
    private readonly Queue<string> _pending = new();

    /// <summary>
    /// Wraps <paramref name="child"/>. <paramref name="bufferedLines"/> (the already-peeked lines — the
    /// <c>/..HOST</c>, and an optional preceding <c>/..PASS</c>) are delivered first, in order, so no input
    /// is lost.
    /// </summary>
    public InboundPeerLink(RhpChildConnection child, IEnumerable<string> bufferedLines)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(bufferedLines);
        _child = child;
        foreach (string line in bufferedLines)
        {
            _pending.Enqueue(line);
        }
    }

    /// <summary>
    /// Re-injects already-consumed lines at the <em>front</em> of the read queue, before anything this link
    /// has since buffered. The demux uses this to hand the same link to the peer session after peeking the
    /// <c>/..HOST</c> (and optional <c>/..PASS</c>): the peeked lines are replayed first, then any lines the
    /// peek pipelined ahead, then fresh reads from the transport — so no input (or partial-line assembler
    /// state) is lost by re-wrapping the child.
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

    /// <inheritdoc/>
    public async Task SendLineAsync(string line, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(line);
        string framed = line.Length != 0 && (line[^1] == '\r' || line[^1] == '\n') ? line : line + "\r";
        await _child.SendAsync(Encoding.Latin1.GetBytes(framed), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<string?> ReceiveLineAsync(CancellationToken cancellationToken)
    {
        while (_pending.Count == 0)
        {
            byte[]? data = await _child.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (data is null)
            {
                return null; // stream closed
            }

            foreach (string line in _assembler.Feed(data))
            {
                _pending.Enqueue(line);
            }
        }

        return _pending.Dequeue();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() =>
        await _child.CloseAsync(CancellationToken.None).ConfigureAwait(false);
}
