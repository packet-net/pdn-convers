using Convers.Console;
using Convers.Host.Uplink;

namespace Convers.Host.Sessions;

/// <summary>
/// An <see cref="IConverseTerminal"/> over an <see cref="IUpstreamLink"/> that first replays a queue of
/// already-read lines, then reads further lines from the link. Used by the demux when downstream peering
/// is enabled (W7c): the demux must peek the first line to decide USER-vs-HOST, and if the connection is
/// <em>not</em> an admitted peer it falls back to an ordinary USER session — without losing the peeked
/// line(s). Writes go straight to the link (the convers wire is line-based; the engine's CR-terminated
/// text is sent as-is). The same byte transport an <see cref="InboundPeerLink"/> wraps, so the line
/// discipline matches.
/// </summary>
public sealed class ReplayTerminal : IConverseTerminal
{
    private readonly IUpstreamLink _link;
    private readonly Queue<string> _replay;

    /// <summary>Wraps <paramref name="link"/>, replaying <paramref name="bufferedLines"/> before reading more.</summary>
    public ReplayTerminal(IUpstreamLink link, string remoteCallsign, IEnumerable<string> bufferedLines)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(remoteCallsign);
        ArgumentNullException.ThrowIfNull(bufferedLines);
        _link = link;
        RemoteCallsign = remoteCallsign;
        _replay = new Queue<string>(bufferedLines);
    }

    /// <inheritdoc/>
    public string RemoteCallsign { get; }

    /// <inheritdoc/>
    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        if (_replay.Count > 0)
        {
            return _replay.Dequeue();
        }

        return await _link.ReceiveLineAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask WriteAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            await _link.SendLineAsync(text, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            throw new ConverseTerminalClosedException("The peer stream closed.", ex);
        }
    }
}
