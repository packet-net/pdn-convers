using Convers.Console;
using Convers.Host.Rhp;
using Convers.Host.Uplink;
using Microsoft.Extensions.Logging;

namespace Convers.Host.Sessions;

/// <summary>
/// The inbound session demultiplexer (design decision 3): one RHP-bound callsign serves convers USERs.
/// Unlike the BBS — which sniffs a SID to split user-vs-forwarding-partner — nearly every inbound RF
/// connect to a convers leaf is a human USER, so the demux greets immediately and starts an
/// <see cref="RfUserSession"/> that auto-logs the user in from the accepted <c>RemoteCallsign</c> (the
/// user never types <c>/name</c> — decision 4). There is no first-line peek/gate: a leading
/// <c>/..HOST …</c> is treated as ordinary (invalid) user input, since downstream peering is deferred
/// (HANDOVER §2 — v1 is leaf-only). The session's surface (plain default / classic) comes from the
/// per-callsign preference store (decision 9).
/// </summary>
/// <remarks>
/// Each child runs concurrently to completion; the demux assigns a unique session id, picks the surface,
/// runs the session bridged to the shared hub via the <see cref="HostLink"/>, and closes the child when
/// the session ends. Never throws out of a child's lifetime (failures are logged and the child closed).
/// Mirrors the structure of pdn-bbs <c>InboundDemux</c>, minus the FBB handoff.
/// </remarks>
public sealed class InboundDemux
{
    private readonly RhpNodeLink _link;
    private readonly HostLink _hostLink;
    private readonly LocalSessionRegistry _registry;
    private readonly IConsolePreferences _preferences;
    private readonly RfSessionConfig _baseConfig;
    private readonly ILogger _logger;
    private int _nextSession;

    /// <summary>Creates the demux. Chat logging is centralised at the <see cref="HostLink"/> fan-out, not here.</summary>
    public InboundDemux(
        RhpNodeLink link,
        HostLink hostLink,
        LocalSessionRegistry registry,
        IConsolePreferences preferences,
        RfSessionConfig baseConfig,
        ILogger<InboundDemux> logger)
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

    /// <summary>One child's whole lifetime: pick the surface, run the USER session, close the child. Never throws.</summary>
    internal async Task HandleChildAsync(RhpChildConnection child, CancellationToken cancellationToken)
    {
        string sessionId = NextSessionId();
        try
        {
            ConsoleInterface surface = _preferences.GetInterface(child.RemoteCallsign);
            var terminal = new RhpTerminal(child);
            RfSessionConfig config = _baseConfig with { Interface = surface };

            LogSession(_logger, child.RemoteCallsign, surface.ToString(), sessionId, null);
            await RfUserSession.RunAsync(
                terminal, _hostLink, _registry, config, sessionId, cancellationToken).ConfigureAwait(false);

            await child.CloseAsync(CancellationToken.None).ConfigureAwait(false);
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

    private string NextSessionId() =>
        $"rf-{Interlocked.Increment(ref _nextSession).ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private static readonly Action<ILogger, string, string, string, Exception?> LogSession =
        LoggerMessage.Define<string, string, string>(LogLevel.Information, new EventId(1, "RfSession"),
            "RF user session for {Remote} ({Surface}) as {SessionId}");

    private static readonly Action<ILogger, string, Exception?> LogSessionFailed =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(2, "RfSessionFailed"),
            "RF session for {Remote} failed");
}
