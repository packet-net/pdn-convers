using Convers.Console;
using Convers.Core;
using Convers.Host.Rhp;
using Convers.Host.Uplink;
using Convers.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Convers.Host.Sessions;

/// <summary>
/// The inbound session demultiplexer (design decision 3): one RHP-bound callsign serves convers USERs by
/// default. Unlike the BBS — which sniffs a SID to split user-vs-forwarding-partner — nearly every inbound
/// RF connect to a convers leaf is a human USER, so the demux greets immediately and starts an
/// <see cref="RfUserSession"/> that auto-logs the user in from the accepted <c>RemoteCallsign</c> (the user
/// never types <c>/name</c> — decision 4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Downstream peering (W7c, off by default — decisions 1 and 4):</b> when a <see cref="PeeringPolicy"/>
/// is enabled, the demux peeks the connection's first line. If it is a <c>/..HOST</c> (optionally preceded
/// by a <c>/..PASS</c>) from an <em>allowlisted</em> callsign whose password (if required) checks out, the
/// connection is handed to a <see cref="DownstreamPeerSession"/> — the node becomes a small hub instead of
/// a strict leaf. Anything else (a non-<c>/..HOST</c> first line, a non-allowlisted caller, or peering
/// disabled) is an ordinary USER session, replaying the peeked line(s) so nothing is lost. With peering
/// disabled there is no peek at all — a leading <c>/..HOST</c> is just (invalid) user input, exactly as
/// before.
/// </para>
/// <para>
/// Each child runs concurrently to completion; the demux assigns a unique session id, runs the session
/// bridged to the shared hub via the <see cref="HostLink"/>, and closes the child when the session ends.
/// Never throws out of a child's lifetime (failures are logged and the child closed). Mirrors the structure
/// of pdn-bbs <c>InboundDemux</c>, minus the FBB handoff.
/// </para>
/// </remarks>
public sealed class InboundDemux
{
    /// <summary>
    /// How long to wait for a peer's first <c>/..HOST</c> line before falling back to a USER session. Kept
    /// short: a downstream peer is automated software that announces <c>/..HOST</c> immediately on connect
    /// (well under a second), whereas a human user types nothing first — so an ordinary user on a
    /// peering-enabled node waits at most this long for their welcome. Only relevant when peering is enabled
    /// (off by default, where there is no peek at all and the welcome is immediate).
    /// </summary>
    private static readonly TimeSpan PeekTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Peek at most this many lines for a leading <c>/..PASS</c> then a <c>/..HOST</c>.</summary>
    private const int MaxPeekLines = 2;

    private readonly RhpNodeLink _link;
    private readonly HostLink _hostLink;
    private readonly LocalSessionRegistry _registry;
    private readonly IConsolePreferences _preferences;
    private readonly RfSessionConfig _baseConfig;
    private readonly ILogger _logger;
    private readonly PeeringPolicy _peering;
    private readonly ConversHub? _hub;
    private readonly HostLinkOptions? _hostLinkOptions;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;
    private int _nextSession;
    private int _nextPeer;

    /// <summary>Creates the demux. Chat logging is centralised at the <see cref="HostLink"/> fan-out, not here.</summary>
    /// <remarks>
    /// The peering dependencies are optional: when <paramref name="peering"/> is null or disabled the demux
    /// is a pure USER demux (the strict-leaf default) and the hub/options/time are never used.
    /// </remarks>
    public InboundDemux(
        RhpNodeLink link,
        HostLink hostLink,
        LocalSessionRegistry registry,
        IConsolePreferences preferences,
        RfSessionConfig baseConfig,
        ILogger<InboundDemux> logger,
        PeeringPolicy? peering = null,
        ConversHub? hub = null,
        HostLinkOptions? hostLinkOptions = null,
        TimeProvider? timeProvider = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(hostLink);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(baseConfig);
        ArgumentNullException.ThrowIfNull(logger);
        _link = link;
        _hostLink = hostLink;
        _registry = registry;
        _preferences = preferences;
        _baseConfig = baseConfig;
        _logger = logger;
        _peering = peering ?? PeeringPolicy.Disabled;
        _hub = hub;
        _hostLinkOptions = hostLinkOptions;
        _time = timeProvider ?? TimeProvider.System;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    /// <summary>Accepts children until cancelled; each runs concurrently to completion.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var sessions = new List<Task>();
        try
        {
            await foreach (RhpChildConnection child in _link.Accepted.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                sessions.RemoveAll(t => t.IsCompleted);
                sessions.Add(HandleChildAsync(child, cancellationToken));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown.
        }

        await Task.WhenAll(sessions).ConfigureAwait(false);
    }

    /// <summary>
    /// One child's whole lifetime. With peering enabled the demux peeks the first line and, on an admitted
    /// <c>/..HOST</c>, runs a downstream-peer session; otherwise (and always, with peering off) it runs a
    /// USER session. Never throws — failures are logged and the child closed.
    /// </summary>
    internal async Task HandleChildAsync(RhpChildConnection child, CancellationToken cancellationToken)
    {
        try
        {
            if (_peering.Enabled && CanBuildPeer)
            {
                bool handled = await TryHandleAsPeerAsync(child, cancellationToken).ConfigureAwait(false);
                if (handled)
                {
                    return;
                }
            }

            await RunUserSessionAsync(child, new RhpTerminal(child), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await child.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSessionFailed(_logger, child.RemoteCallsign, ex);
            await child.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Peeks the first line(s) and, when they form an admitted <c>/..HOST</c> from an allowlisted caller,
    /// runs a downstream-peer session (returning true). On anything else, falls back to a USER session over
    /// the peeked transport — replaying the lines already read — and returns true as well (the connection
    /// is fully handled). Returns false only when the peek yielded nothing (immediate close) so the caller
    /// can close the child.
    /// </summary>
    private async Task<bool> TryHandleAsPeerAsync(RhpChildConnection child, CancellationToken cancellationToken)
    {
        var peek = new InboundPeerLink(child, []);
        var buffered = new List<string>();
        string? password = null;
        string? hostLine = null;

        using var peekCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        peekCts.CancelAfter(PeekTimeout);
        try
        {
            for (int i = 0; i < MaxPeekLines; i++)
            {
                string? line = await peek.ReceiveLineAsync(peekCts.Token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                buffered.Add(line);
                if (!HostCommandCodec.TryParse(line, out HostCommand? cmd) || cmd is null)
                {
                    break; // first line is not a /.. command at all → ordinary user input
                }

                if (cmd is HostHandshake)
                {
                    hostLine = line;
                    break;
                }

                if (cmd is UnknownHostCommand { Verb: "PASS" } pass)
                {
                    password = pass.Body; // a /..PASS preceding the /..HOST carries the link password
                    continue;
                }

                break; // some other /.. verb before HOST → not a handshake; treat as user input
            }
        }
        catch (OperationCanceledException) when (peekCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The caller sent nothing (or no /..HOST) in time; treat as a USER session over what we have.
        }

        if (hostLine is not null && _peering.IsAllowed(child.RemoteCallsign) && _peering.PasswordOk(password))
        {
            // Reuse the SAME peek link for the peer session so any lines it pipelined after /..HOST (and the
            // assembler's partial-line state) are not lost; replay the peeked lines at the front.
            peek.PrependBufferedLines(buffered);
            await RunPeerSessionAsync(child, peek, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (hostLine is not null)
        {
            LogPeerRefused(_logger, child.RemoteCallsign, null);
        }

        // Not an admitted peer: a USER session over the peeked transport, replaying the lines we read.
        var terminal = new ReplayTerminal(peek, child.RemoteCallsign, buffered);
        await RunUserSessionAsync(child, terminal, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task RunPeerSessionAsync(
        RhpChildConnection child, InboundPeerLink transport, CancellationToken cancellationToken)
    {
        string peerId = NextPeerId();
        LogPeerAccepted(_logger, child.RemoteCallsign, peerId, null);
        var session = new DownstreamPeerSession(
            peerId, transport, _hostLink, _hub!, _hostLinkOptions!, _time,
            _loggerFactory.CreateLogger<DownstreamPeerSession>());
        await session.RunAsync(cancellationToken).ConfigureAwait(false);
        await child.CloseAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task RunUserSessionAsync(
        RhpChildConnection child, IConverseTerminal terminal, CancellationToken cancellationToken)
    {
        string sessionId = NextSessionId();
        ConsoleInterface surface = _preferences.GetInterface(child.RemoteCallsign);
        RfSessionConfig config = _baseConfig with { Interface = surface };

        LogSession(_logger, child.RemoteCallsign, surface.ToString(), sessionId, null);
        await RfUserSession.RunAsync(
            terminal, _hostLink, _registry, config, sessionId, cancellationToken).ConfigureAwait(false);
        await child.CloseAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>True when the peering collaborators were supplied (so a peer session can actually be built).</summary>
    private bool CanBuildPeer => _hub is not null && _hostLinkOptions is not null;

    private string NextSessionId() =>
        $"rf-{Interlocked.Increment(ref _nextSession).ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private string NextPeerId() =>
        $"peer-{Interlocked.Increment(ref _nextPeer).ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private static readonly Action<ILogger, string, string, string, Exception?> LogSession =
        LoggerMessage.Define<string, string, string>(LogLevel.Information, new EventId(1, "RfSession"),
            "RF user session for {Remote} ({Surface}) as {SessionId}");

    private static readonly Action<ILogger, string, Exception?> LogSessionFailed =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(2, "RfSessionFailed"),
            "RF session for {Remote} failed");

    private static readonly Action<ILogger, string, string, Exception?> LogPeerAccepted =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(3, "PeerAccepted"),
            "Accepted downstream peer {Remote} as {PeerId}");

    private static readonly Action<ILogger, string, Exception?> LogPeerRefused =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(4, "PeerRefused"),
            "Refused downstream /..HOST from {Remote} (not allowlisted, or wrong password) — handling as a user");
}
