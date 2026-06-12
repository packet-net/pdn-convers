using System.Text;
using Convers.Host.Rhp;
using Convers.Protocol;

namespace Convers.Host.Uplink;

/// <summary>
/// The RF-via-RHP upstream provider (design decision 6, the pdn-native path): dials a neighbouring
/// convers node over RHP <c>open</c>(Active) from our bound callsign (<c>config.Uplink.Rf.Call</c>), and
/// carries the convers wire as Latin-1 lines over the resulting AX.25 stream. Outbound lines get a CR
/// terminator (the AX.25 line discipline); inbound bytes are split on CR/LF into terminator-stripped
/// lines (<see cref="LineAssembler"/>-equivalent via <see cref="ConversWire.SplitLines"/>). One instance
/// models one dial; the <see cref="HostLink"/> redials via <see cref="RfUpstreamLinkFactory"/> on loss.
/// </summary>
public sealed class RfUpstreamLink : IUpstreamLink
{
    private readonly RhpChildConnection _child;
    private readonly Queue<string> _pending = new();
    private byte[] _remainder = [];

    private RfUpstreamLink(RhpChildConnection child)
    {
        _child = child;
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
    public async Task SendLineAsync(string line, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(line);
        // AX.25 line discipline is CR; the wire body carries no terminator, so append CR here.
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

            byte[] combined = Combine(_remainder, data);
            IReadOnlyList<string> lines = ConversWire.SplitLines(combined, out _remainder);
            foreach (string l in lines)
            {
                _pending.Enqueue(l);
            }
        }

        return _pending.Dequeue();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _child.CloseAsync(CancellationToken.None).ConfigureAwait(false);
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
