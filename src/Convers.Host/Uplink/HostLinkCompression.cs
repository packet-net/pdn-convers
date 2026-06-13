using Convers.Protocol;

namespace Convers.Host.Uplink;

/// <summary>
/// The sans-IO, stateful host-link compression stage (W7-deferred plumbing): the byte-stream layer that
/// sits <em>below</em> the line framing on a host-link transport and applies the conversd-saupp Huffman
/// <see cref="Compression"/> codec once <c>//COMP</c> has been negotiated. It owns the on-wire framing and
/// the negotiation state machine; it performs no I/O, so the concrete transports (<c>TcpUpstreamLink</c>,
/// <c>RfUpstreamLink</c>, <c>InboundPeerLink</c>) drive it over their own sockets and it is unit-tested in
/// isolation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a byte stage, not a line decorator.</b> conversd compresses per <em>write chunk</em>, not per
/// line: <c>fast_write</c> splits the outbound string into ≤<see cref="Compression.MaxFrameLength"/>-byte
/// frames and Huffman-codes each; the reader decodes one frame per socket read and feeds the decoded text
/// to its line parser (the <c>\n</c> terminator lives <em>inside</em> the compressed payload). So this stage
/// transforms the already-terminator-framed outbound bytes and the raw inbound bytes, and the transport's
/// existing line splitter runs on the <em>decoded</em> bytes — compression stays invisible to the engine.
/// </para>
/// <para>
/// <b>Negotiation</b> mirrors conversd's <c>comp_command</c> / <c>//COMP</c> exchange. The toggle lines
/// (<c>//COMP 1</c> / <c>//COMP 0</c>) are always sent <em>uncompressed</em> so they are readable on a link
/// whose compression state is mid-flip (conversd <c>fast_write(..., -1)</c>). The key property that makes the
/// switch interop-clean is that each toggle is an <b>in-band marker on its own direction's byte stream</b>:
/// everything a side writes <em>after</em> the toggle is compressed, and the peer arms its receive side the
/// instant it reads that (uncompressed) toggle — so the two stay in lock-step per direction.
/// <list type="bullet">
/// <item><b>Offer</b> (<see cref="BuildOffer"/>): write <c>//COMP 1</c> and arm our <em>transmit</em> side —
/// from here our outbound is compressed, and the toggle we just wrote tells the peer where that begins.</item>
/// <item><b>Receive</b> <c>//COMP 1</c>: arm our <em>receive</em> side (the peer's stream is compressed from
/// here). If we had not already offered, reciprocate with our own <c>//COMP 1</c> — which arms our transmit
/// side too, opening compression on the return direction.</item>
/// <item><b>Receive</b> <c>//COMP 0</c>: disarm both sides.</item>
/// </list>
/// This is conversd's "always offer, use only when the peer accepts" posture: a peer that <em>reciprocates</em>
/// (another pdn-convers, or a conversd USER link that ran <c>/comp on</c>) ends fully compressed both ways;
/// a peer that <b>ignores</b> <c>//COMP</c> never arms its receive side and would mis-read our compressed
/// output — which is exactly conversd's own behaviour, and why the offer is gated by config
/// (<c>uplink.compression</c>) and should only be enabled toward a peer known to honour host-link
/// compression. Stock conversd honours <c>/comp</c> on USER links only, not on a HOST peer link.
/// </para>
/// <para>
/// <b>Resilience</b> matches conversd's read loop: an inbound frame that is too short, begins with the system
/// notice prefix <c>"*** "</c> (which conversd never compresses), or fails to Huffman-decode is passed
/// through verbatim rather than dropped.
/// </para>
/// </remarks>
public sealed class HostLinkCompression
{
    /// <summary>conversd never compresses a system notice; the reader skips decode when a frame starts here.</summary>
    private static readonly byte[] SystemNoticePrefix = "*** "u8.ToArray();

    private bool _txActive;
    private bool _rxActive;
    private bool _offered;

    /// <summary>True once our transmit side is compressing (we have offered/accepted <c>//COMP 1</c>).</summary>
    public bool TxActive => _txActive;

    /// <summary>True once our receive side is decompressing (the peer offered/accepted <c>//COMP 1</c>).</summary>
    public bool RxActive => _rxActive;

    /// <summary>True when compression is active in <em>both</em> directions — the steady compressed state.</summary>
    public bool FullyActive => _txActive && _rxActive;

    /// <summary>
    /// Build the on-wire bytes that <em>offer</em> compression to the peer: the uncompressed
    /// <c>\r//COMP 1\r</c> toggle (conversd brackets the token with carriage returns so it survives an AX.25
    /// CR line discipline) and arm our transmit side, so everything written <em>after</em> these bytes is
    /// compressed — the toggle marks that boundary on our outbound stream, and the peer arms its receive side
    /// when it reads the toggle. Idempotent: a re-offer re-sends the (harmless) toggle. Returns the raw bytes
    /// to write uncompressed before any further output.
    /// </summary>
    public byte[] BuildOffer()
    {
        _offered = true;
        byte[] toggle = ConversWire.Encode($"\r{CompressionNegotiation.Enable}\r");
        _txActive = true;
        return toggle;
    }

    /// <summary>
    /// Record that we have offered compression <em>out of band</em> — i.e. the caller has itself written the
    /// offer line (e.g. a USER-link <c>//comp on</c>, which conversd answers with <c>//COMP 1</c>) — and arm
    /// our transmit side accordingly. Like <see cref="BuildOffer"/> but emits no bytes: the caller already
    /// sent the trigger uncompressed. Used when negotiating over a transport whose enable token is not the
    /// raw <c>//COMP 1</c> toggle; the peer's <c>//COMP 1</c> answer then arms our receive side without a
    /// (confusing) reciprocal, since we have already offered.
    /// </summary>
    public void NoteExternalOffer()
    {
        _offered = true;
        _txActive = true;
    }

    /// <summary>
    /// Encode one logical write (the already-terminator-framed line bytes) into the wire frames to send, in
    /// order. When the transmit side is armed, the bytes are split into ≤<see cref="Compression.MaxFrameLength"/>-byte
    /// chunks, each Huffman-coded into its own frame (conversd <c>fast_write</c>, which issues one
    /// <c>write()</c> per chunk); the caller writes each returned frame as its own flushed write so the peer's
    /// frame-per-read decode aligns, exactly as conversd's reader expects. When not armed (or for an empty
    /// write) a single passthrough buffer is returned. Never throws on content.
    /// </summary>
    public IReadOnlyList<byte[]> EncodeOutbound(ReadOnlySpan<byte> framedLine)
    {
        if (!_txActive || framedLine.Length == 0)
        {
            return [framedLine.ToArray()];
        }

        var frames = new List<byte[]>((framedLine.Length / Compression.MaxFrameLength) + 1);
        int offset = 0;
        while (offset < framedLine.Length)
        {
            int chunk = Math.Min(Compression.MaxFrameLength, framedLine.Length - offset);
            frames.Add(Compression.EncodeFrame(framedLine.Slice(offset, chunk)));
            offset += chunk;
        }

        return frames;
    }

    /// <summary>
    /// Decode a raw inbound socket read back to plaintext bytes the transport's line splitter consumes. When
    /// the receive side is armed, this decodes <em>one frame</em> from the front of the buffer (conversd
    /// reads and decodes exactly one frame per socket read) and returns any bytes <em>after</em> that frame
    /// via <paramref name="remainder"/> for the caller to prepend to the next read — so a frame split across
    /// two reads completes, and back-to-back self-delimiting compressed frames in one (coalesced) read are
    /// walked one per call. conversd's verbatim fallbacks are honoured: a frame beginning with the
    /// <c>"*** "</c> system notice (which conversd never compresses), or one too short or undecodable, is
    /// passed through as raw text. A stored frame (and any verbatim run) is sized by the read boundary, as in
    /// conversd's <c>decstathuf</c>: it consumes the whole remaining span. Because we write one frame per
    /// flush (conversd <c>fast_write</c> does too), the common case is one frame per read; if a stored frame
    /// is TCP-coalesced ahead of another frame in a single read, the trailing frame is swallowed — the exact
    /// limitation of conversd's own one-frame-per-read loop (<c>read(fd, cbuf, COMP_MTU+1)</c>), so this is
    /// interop-faithful rather than a regression. Compressed frames are self-delimiting and so coalesce
    /// safely. When not armed the buffer passes through unchanged (one line at a time so a coalesced
    /// <c>//COMP</c> toggle still arms us before the bytes after it are decoded).
    /// </summary>
    public byte[] DecodeInbound(ReadOnlySpan<byte> socketBytes, out byte[] remainder)
    {
        remainder = [];
        if (socketBytes.Length == 0)
        {
            return [];
        }

        if (!_rxActive)
        {
            // Un-armed: pass plaintext through, but only up to and INCLUDING the first line terminator, so a
            // mid-buffer "//COMP 1" toggle is isolated as its own line. The caller assembles that line, which
            // arms rx, and re-decodes the tail (which is compressed) on the next loop pass — otherwise a toggle
            // coalesced with the peer's first compressed frame in one read would be mis-read as raw text.
            int term = IndexOfTerminator(socketBytes);
            if (term < 0 || term == socketBytes.Length - 1)
            {
                return socketBytes.ToArray(); // no terminator, or it ends the buffer: nothing follows to re-decode
            }

            remainder = socketBytes.Slice(term + 1).ToArray();
            return socketBytes.Slice(0, term + 1).ToArray();
        }

        // conversd never compresses a "*** " system notice; a read beginning with it is verbatim text.
        if (socketBytes.Length >= SystemNoticePrefix.Length &&
            socketBytes.Slice(0, SystemNoticePrefix.Length).SequenceEqual(SystemNoticePrefix))
        {
            return socketBytes.ToArray();
        }

        // A compressed frame is self-delimiting: decode exactly one and carry the rest (a coalesced second
        // frame, or this frame's own trailing bytes) to the next read.
        if (Compression.TryDecodeFrame(socketBytes, out byte[] decoded, out int consumed) && consumed > 0)
        {
            if (consumed < socketBytes.Length)
            {
                remainder = socketBytes.Slice(consumed).ToArray();
            }

            return decoded;
        }

        // Undecodable from here. A truncated compressed frame (its declared decoded length cannot fit in the
        // bytes we hold) is the head of a frame split across reads → carry it whole. Otherwise conversd treats
        // the bytes verbatim (a stored frame sized by the read boundary, or junk), so we do too.
        if (IsLikelyTruncatedFrame(socketBytes))
        {
            remainder = socketBytes.ToArray();
            return [];
        }

        return socketBytes.ToArray();
    }

    /// <summary>
    /// Apply an inbound <c>//COMP</c> toggle and report what to write back. A <c>//COMP 1</c> arms our
    /// <em>receive</em> side: the peer's outbound stream is compressed from the byte after this (uncompressed)
    /// toggle. When we had <em>not</em> already offered, it is the peer opening the negotiation, so we owe a
    /// reciprocal <c>//COMP 1</c> (returned in <paramref name="reply"/>, sent uncompressed); the caller writes
    /// it and then calls <see cref="EngageTransmitAfterReciprocal"/> so our transmit side arms <em>at that
    /// reply's wire position</em> (not before — otherwise a line already queued could be compressed and land
    /// ahead of the toggle, which the peer would mis-read). When we <em>had</em> offered, our transmit side is
    /// already armed and no reply is owed. A <c>//COMP 0</c> disarms both sides and clears the offered state,
    /// so a later re-enable renegotiates cleanly. <paramref name="line"/> must be a <c>//COMP</c> line (use
    /// <see cref="CompressionNegotiation.TryParse"/> first); returns false when it is not.
    /// </summary>
    public bool TryApplyInboundToggle(string line, out byte[]? reply)
    {
        reply = null;
        if (!CompressionNegotiation.TryParse(line, out bool enable))
        {
            return false;
        }

        if (enable)
        {
            // The peer's stream is compressed from here on — arm our receive side at the toggle boundary.
            _rxActive = true;
            if (!_offered)
            {
                // The peer opened the negotiation; we owe a reciprocal. We do NOT arm transmit yet — that
                // happens once the reciprocal is on the wire (EngageTransmitAfterReciprocal), so the toggle
                // correctly marks our outbound compression boundary.
                _offered = true;
                reply = ConversWire.Encode($"\r{CompressionNegotiation.Enable}\r");
            }
        }
        else
        {
            _rxActive = false;
            _txActive = false;
            _offered = false; // allow a clean re-negotiation after a disable/re-enable cycle
        }

        return true;
    }

    /// <summary>
    /// Arm the transmit side after the reciprocal <c>//COMP 1</c> (from <see cref="TryApplyInboundToggle"/>)
    /// has been written. Called by the transport in its send chain, immediately after the reply bytes, so the
    /// toggle marks the exact wire position where our outbound becomes compressed.
    /// </summary>
    public void EngageTransmitAfterReciprocal() => _txActive = true;

    /// <summary>Index of the first CR or LF in <paramref name="bytes"/>, or -1 if none.</summary>
    private static int IndexOfTerminator(ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] is (byte)'\r' or (byte)'\n')
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// True when <paramref name="frame"/> looks like a compressed (non-stored) frame whose declared decoded
    /// length is larger than the held payload could ever produce — i.e. it is almost certainly the head of a
    /// frame split across two socket reads, so the caller should buffer it rather than treat it as verbatim
    /// junk. A stored frame (header 255) is sized by the read boundary and so is never "truncated".
    /// </summary>
    private static bool IsLikelyTruncatedFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 2 || frame[0] == Compression.StoredMarker)
        {
            return false;
        }

        // A frame decoding to N symbols needs at least ceil(N * minCodeLen / 8) payload bytes. The shortest
        // Huffman code in the table is 3 bits, so a lower bound on the payload is (declaredLen*3 + 7)/8. If we
        // do not even hold that many payload bytes, the frame is truncated.
        int declaredLen = frame[0] + 1;
        int minPayload = ((declaredLen * 3) + 7) / 8;
        return (frame.Length - 1) < minPayload;
    }
}
