using System.Net.Sockets;
using Convers.Protocol;

namespace Convers.Host.Uplink;

/// <summary>
/// The direct-TCP upstream provider (design decision 6): a socket to an internet convers hub (e.g.
/// HubNA <c>44.68.41.2:3600</c>), Latin-1 line transport. Outbound lines are framed with an LF terminator
/// (the canonical TCP terminator) and inbound bytes split on CR/LF into terminator-stripped lines, all via
/// the shared <see cref="CompressingLineTransport"/> — which also carries the conversd-saupp Huffman
/// compression once <c>//COMP</c> is negotiated. One instance models one dial; the <see cref="HostLink"/>
/// obtains a fresh one per (re)connect via <see cref="TcpUpstreamLinkFactory"/>.
/// </summary>
public sealed class TcpUpstreamLink : IUpstreamLink
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly CompressingLineTransport _transport;

    private TcpUpstreamLink(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
        _transport = new CompressingLineTransport(WriteAsync, ReadAsync, terminator: '\n');
    }

    /// <summary>Dials <paramref name="host"/>:<paramref name="port"/> and returns the connected link.</summary>
    public static async Task<TcpUpstreamLink> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        return new TcpUpstreamLink(client);
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

    /// <summary>
    /// Arm compression for an enable trigger written out of band via <see cref="SendLineAsync"/> (e.g. a
    /// USER-link <c>//comp on</c>, whose <c>//COMP 1</c> answer then arms the receive side). Used by the
    /// host-link compression interop test against the real conversd USER link, where the enable token is the
    /// <c>//comp</c> command rather than the raw host-link <c>//COMP 1</c> offer.
    /// </summary>
    internal Task NoteExternalCompressionOfferAsync(CancellationToken cancellationToken) =>
        _transport.NoteExternalCompressionOfferAsync(cancellationToken);

    /// <summary>Whether the receive side has engaged decompression (the peer's <c>//COMP 1</c> was consumed).</summary>
    internal bool ReceiveCompressionEngaged => _transport.ReceiveCompressionEngaged;

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _stream.Dispose();
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        int read;
        try
        {
            read = await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            return null;
        }

        return read == 0 ? null : buffer.AsSpan(0, read).ToArray();
    }
}

/// <summary>
/// An <see cref="IUpstreamLinkFactory"/> that dials a TCP convers hub for each (re)connect — the
/// direct-TCP uplink dialer (<c>config.Uplink.Tcp</c> host:port).
/// </summary>
public sealed class TcpUpstreamLinkFactory(string host, int port) : IUpstreamLinkFactory
{
    /// <inheritdoc/>
    public async Task<IUpstreamLink> ConnectAsync(CancellationToken cancellationToken) =>
        await TcpUpstreamLink.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
}
