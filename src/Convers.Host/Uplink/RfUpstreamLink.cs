using Convers.Host.Rhp;

namespace Convers.Host.Uplink;

/// <summary>
/// The RF-via-RHP upstream provider (design decision 6, the pdn-native path): dials a neighbouring
/// convers node over RHP <c>open</c>(Active) from our bound callsign (<c>config.Uplink.Rf.Call</c>), and
/// carries the convers wire as Latin-1 lines over the resulting AX.25 stream via the shared
/// <see cref="CompressingLineTransport"/>. Outbound lines get a CR terminator (the AX.25 line discipline);
/// inbound bytes are CR/LF-split; the transport also applies the conversd-saupp Huffman compression once
/// <c>//COMP</c> is negotiated. One instance models one dial; the <see cref="HostLink"/> redials via
/// <see cref="RfUpstreamLinkFactory"/> on loss.
/// </summary>
public sealed class RfUpstreamLink : IUpstreamLink
{
    private readonly RhpChildConnection _child;
    private readonly CompressingLineTransport _transport;

    private RfUpstreamLink(RhpChildConnection child)
    {
        _child = child;
        _transport = new CompressingLineTransport(
            (bytes, ct) => _child.SendAsync(bytes, ct),
            ct => _child.ReceiveAsync(ct).AsTask(),
            terminator: '\r');
    }

    /// <summary>Opens an RHP Active connection to <paramref name="remote"/> and returns the link.</summary>
    public static async Task<RfUpstreamLink> OpenAsync(RhpNodeLink node, string remote, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(remote);
        RhpChildConnection child = await node.OpenAsync(remote, port: null, cancellationToken).ConfigureAwait(false);
        return new RfUpstreamLink(child);
    }

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
    public async ValueTask DisposeAsync()
    {
        await _child.CloseAsync(CancellationToken.None).ConfigureAwait(false);
    }
}

/// <summary>
/// An <see cref="IUpstreamLinkFactory"/> that dials a neighbour convers node over RHP for each
/// (re)connect — the RF uplink dialer (<c>config.Uplink.Rf.Call</c>). It dials through the shared
/// <see cref="RhpNodeLink"/>, so it requires the node link to be up; a dial while the link is down throws
/// and the <see cref="HostLink"/> backs off and retries.
/// </summary>
public sealed class RfUpstreamLinkFactory(RhpNodeLink node, string remoteCall) : IUpstreamLinkFactory
{
    /// <inheritdoc/>
    public async Task<IUpstreamLink> ConnectAsync(CancellationToken cancellationToken) =>
        await RfUpstreamLink.OpenAsync(node, remoteCall, cancellationToken).ConfigureAwait(false);
}
