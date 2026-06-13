using Convers.Host.Rhp;

namespace Convers.Host.Uplink;

/// <summary>
/// The <see cref="IUpstreamLink"/> transport for an accepted <em>downstream</em> peer (W7c): an
/// <see cref="RhpChildConnection"/> the peer opened to our bound callsign, carrying the convers wire as
/// Latin-1, CR/CRLF/LF-tolerant lines via the shared <see cref="CompressingLineTransport"/> (a peer may be
/// AX.25 CR-discipline or telnet-ish CRLF). Outbound lines get a CR terminator (the AX.25 discipline the RF
/// peer expects); the transport also applies the conversd-saupp Huffman compression once <c>//COMP</c> is
/// negotiated. It is the inbound mirror of <see cref="RfUpstreamLink"/>; the demux hands the already-read
/// first <c>/..HOST</c> line back via the constructor so the engine sees the full handshake.
/// </summary>
public sealed class InboundPeerLink : IUpstreamLink
{
    private readonly RhpChildConnection _child;
    private readonly CompressingLineTransport _transport;

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
        _transport = new CompressingLineTransport(
            (bytes, ct) => _child.SendAsync(bytes, ct),
            ct => _child.ReceiveAsync(ct).AsTask(),
            terminator: '\r',
            bufferedLines);
    }

    /// <summary>
    /// Re-injects already-consumed lines at the <em>front</em> of the read queue, before anything this link
    /// has since buffered. The demux uses this to hand the same link to the peer session after peeking the
    /// <c>/..HOST</c> (and optional <c>/..PASS</c>): the peeked lines are replayed first, then any lines the
    /// peek pipelined ahead, then fresh reads from the transport — so no input (or partial-line assembler
    /// state) is lost by re-wrapping the child.
    /// </summary>
    public void PrependBufferedLines(IEnumerable<string> lines) => _transport.PrependBufferedLines(lines);

    /// <inheritdoc/>
    public Task SendLineAsync(string line, CancellationToken cancellationToken) =>
        _transport.SendLineAsync(line, cancellationToken);

    /// <inheritdoc/>
    public Task<string?> ReceiveLineAsync(CancellationToken cancellationToken) =>
        _transport.ReceiveLineAsync(cancellationToken);

    /// <inheritdoc/>
    public Task OfferCompressionAsync(CancellationToken cancellationToken) =>
        _transport.OfferCompressionAsync(cancellationToken);

    /// <inheritdoc/>
    public bool CompressionEngaged => _transport.CompressionEngaged;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() =>
        await _child.CloseAsync(CancellationToken.None).ConfigureAwait(false);
}
