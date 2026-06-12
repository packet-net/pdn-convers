using System.Net.Sockets;
using Convers.Protocol;

namespace Convers.Host.Uplink;

/// <summary>
/// The direct-TCP upstream provider (design decision 6): a socket to an internet convers hub (e.g.
/// HubNA <c>44.68.41.2:3600</c>), Latin-1 line transport. Outbound lines are framed with
/// <see cref="ConversWire.FrameLine"/> (LF terminator — the canonical TCP terminator); inbound bytes are
/// split on CR/LF into terminator-stripped lines. One instance models one dial; the
/// <see cref="HostLink"/> obtains a fresh one per (re)connect via <see cref="TcpUpstreamLinkFactory"/>.
/// </summary>
public sealed class TcpUpstreamLink : IUpstreamLink
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly Queue<string> _pending = new();
    private byte[] _remainder = [];

    private TcpUpstreamLink(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
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
    public async Task SendLineAsync(string line, CancellationToken cancellationToken)
    {
        byte[] framed = ConversWire.FrameLine(line);
        await _stream.WriteAsync(framed, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<string?> ReceiveLineAsync(CancellationToken cancellationToken)
    {
        while (_pending.Count == 0)
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

            if (read == 0)
            {
                return null; // peer closed
            }

            byte[] combined = Combine(_remainder, buffer.AsSpan(0, read));
            IReadOnlyList<string> lines = ConversWire.SplitLines(combined, out _remainder);
            foreach (string l in lines)
            {
                _pending.Enqueue(l);
            }
        }

        return _pending.Dequeue();
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _stream.Dispose();
        _client.Dispose();
        return ValueTask.CompletedTask;
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
