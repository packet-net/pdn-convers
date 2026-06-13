using Convers.Core;
using Convers.Protocol;

namespace Convers.Host.Uplink;

/// <summary>
/// The sans-IO core of the upstream HostLink: the <c>/..HOST</c> handshake FSM, inbound host-command
/// processing, and PING/PONG keepalive timing — all driven by an injected <see cref="TimeProvider"/> and
/// returning <see cref="EngineStep"/>s the (I/O-owning) <see cref="HostLink"/> carries out. It owns no
/// socket and no hub, so it is exercised directly over a scripted transport in unit tests (design
/// decision 2). One engine instance models one connection's lifetime; the driver makes a fresh one per
/// reconnect, so the handshake runs again on every reconnect.
/// </summary>
/// <remarks>
/// <para>
/// The leaf <em>dials</em> the parent (we are the connecting host). On <see cref="OnConnected"/> the
/// engine emits our <c>/..HOST &lt;hostname&gt; &lt;software&gt; &lt;facilities&gt;</c> and waits, in
/// <see cref="HostLinkState.Handshaking"/>, for the parent's <c>/..HOST</c> reply. The reply carries the
/// parent's facilities (the oracle answers <c>/..HOST ORACLE saupp1.62a Aadmpunfi</c>); we intersect
/// them with ours to get the negotiated set and move to <see cref="HostLinkState.Established"/>.
/// </para>
/// <para>
/// In the steady state every inbound <c>/..</c> command becomes a hub event (via <see cref="HostBridge"/>)
/// for the driver to apply; PING/PONG are additionally processed here for link timing (a PONG resets the
/// silence deadline and records the round-trip; an inbound PING is answered through the hub's
/// <c>SendPong</c>, keeping one source of truth — design decision 5). <see cref="OnTick"/> sends a
/// periodic keepalive PING and drops the link on prolonged silence.
/// </para>
/// </remarks>
public sealed class HostLinkEngine
{
    private readonly HostLinkOptions _options;
    private readonly TimeProvider _time;

    private DateTimeOffset _lastInbound;
    private DateTimeOffset _lastPingSent;
    private DateTimeOffset _handshakeSentAt;
    private DateTimeOffset _lastPingRoundTripAt;

    /// <summary>Creates the engine for one connection's lifetime with validated options.</summary>
    public HostLinkEngine(HostLinkOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _options = options.Validate();
        _time = timeProvider;
    }

    /// <summary>The current FSM state.</summary>
    public HostLinkState State { get; private set; } = HostLinkState.Disconnected;

    /// <summary>The facility set negotiated with the parent (empty until the handshake completes).</summary>
    public Facilities NegotiatedFacilities { get; private set; } = Facilities.None;

    /// <summary>The parent's reported host name from its handshake reply (empty until established).</summary>
    public string PeerHostName { get; private set; } = string.Empty;

    /// <summary>
    /// The last measured round-trip time to the parent (from our PING to its PONG), in milliseconds, or
    /// <see langword="null"/> if not yet measured. Held so the link's quality is observable.
    /// </summary>
    public long? LastRoundTripMs { get; private set; }

    /// <summary>
    /// The transport just connected: emit our <c>/..HOST</c> and enter <see cref="HostLinkState.Handshaking"/>.
    /// Resets all link timers to <paramref name="now"/>.
    /// </summary>
    public EngineStep OnConnected()
    {
        DateTimeOffset now = _time.GetUtcNow();
        State = HostLinkState.Handshaking;
        _lastInbound = now;
        _handshakeSentAt = now;
        _lastPingSent = now;
        NegotiatedFacilities = Facilities.None;
        PeerHostName = string.Empty;
        LastRoundTripMs = null;

        var handshake = new HostHandshake(_options.HostName, _options.Software, _options.Facilities);
        return new EngineStep { OutboundCommands = [handshake] };
    }

    /// <summary>
    /// Process one inbound line from the parent. Non-host-command lines (greeting/notice text the parent
    /// may send) only refresh the activity clock. A malformed host command never throws — it is treated as
    /// an unknown command and recorded.
    /// </summary>
    public EngineStep OnLineReceived(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        _lastInbound = _time.GetUtcNow();

        if (!HostCommandCodec.TryParse(line, out HostCommand? parsed) || parsed is null)
        {
            // Greeting / human-readable server notice (e.g. "* Welcome…"). Counts as activity; no event.
            return EngineStep.None;
        }

        return State switch
        {
            HostLinkState.Handshaking => OnLineWhileHandshaking(parsed),
            HostLinkState.Established => OnLineWhileEstablished(parsed),
            _ => EngineStep.None,
        };
    }

    /// <summary>
    /// A time-driven step: send a keepalive <c>/..PING</c> when due, and drop the link on prolonged
    /// silence or a stalled handshake. Idempotent and cheap; the driver calls it on a short cadence.
    /// </summary>
    public EngineStep OnTick()
    {
        DateTimeOffset now = _time.GetUtcNow();

        if (State == HostLinkState.Handshaking && now - _handshakeSentAt >= _options.HandshakeTimeout)
        {
            return new EngineStep { DropReason = "handshake timeout (no /..HOST reply)" };
        }

        if (State != HostLinkState.Established)
        {
            return EngineStep.None;
        }

        if (now - _lastInbound >= _options.SilenceTimeout)
        {
            return new EngineStep { DropReason = "silence timeout (no traffic from parent)" };
        }

        if (now - _lastPingSent >= _options.PingInterval)
        {
            _lastPingSent = now;
            _lastPingRoundTripAt = now;
            return new EngineStep { OutboundCommands = [new HostPing()] };
        }

        return EngineStep.None;
    }

    private EngineStep OnLineWhileHandshaking(HostCommand command)
    {
        if (command is HostHandshake reply)
        {
            // Negotiated set: the intersection of what we offered and what the parent advertised.
            NegotiatedFacilities = _options.Facilities & reply.Facilities;
            PeerHostName = reply.Hostname;
            State = HostLinkState.Established;
            return new EngineStep
            {
                HandshakeCompleted = true,
                NegotiatedFacilities = NegotiatedFacilities,
            };
        }

        // conversd announces its destinations/users immediately after sending its own /..HOST. Some peers
        // interleave; if presence arrives before we have seen the reply, buffer it as a hub event so
        // nothing is lost — but we are not yet "established" until the /..HOST reply lands.
        ConversEvent? early = HostBridge.ToEvent(command);
        return early is null ? EngineStep.None : new EngineStep { HubEvents = [early] };
    }

    private EngineStep OnLineWhileEstablished(HostCommand command)
    {
        switch (command)
        {
            case HostHandshake:
                // A second handshake on an established link is a protocol fault (or a reorg). Drop & redial.
                return new EngineStep { DropReason = "unexpected second /..HOST on an established link" };

            case HostPong pong:
                RecordPong(pong.Time);
                // Surface to the hub too (it currently ignores PONG) so the event stream is complete.
                return new EngineStep { HubEvents = [new ConversEvent.HostPong(pong.Time)] };

            case HostLoop loop:
                // SPECS: drop the link on /..LOOP. Feed the hub so it can react, and drop the transport.
                return new EngineStep
                {
                    HubEvents = [new ConversEvent.HostLoop(loop.Detail)],
                    DropReason = $"/..LOOP received: {loop.Detail}",
                };

            case HostRoute route:
                // /..ROUT — answer the route query at the link layer (SPECS line 126). As a strict leaf we
                // are not transit: we reply with the single hop we know (us → parent → dest) and NEVER
                // forward, regardless of the TTL (the loop/TTL guard — design decision 1).
                return new EngineStep { OutboundCommands = [RouteReply(route)] };

            case HostSysInfo sysi:
                // /..SYSI — answer with our system information when it is for us or "all" (SPECS line 136).
                // We never forward (strict leaf, no transit); a query for some other host is simply ignored.
                return new EngineStep { OutboundCommands = SysInfoReplies(sysi) };
        }

        ConversEvent? @event = HostBridge.ToEvent(command);
        return @event is null ? EngineStep.None : new EngineStep { HubEvents = [@event] };
    }

    /// <summary>The reserved <c>/..UMSG</c> sender for a system-generated reply (route / sysinfo).</summary>
    private const string SystemSender = "conversd";

    /// <summary>
    /// The strict-leaf <c>/..ROUT</c> reply: a single conversd-formatted route notice to the querying user
    /// showing the one route we can offer (us → our parent → the destination). We hold no transit table,
    /// so this is best-effort and never forwarded (the TTL is ignored — design decision 1).
    /// </summary>
    private HostUserMessage RouteReply(HostRoute route)
    {
        string parent = PeerHostName.Length == 0 ? "(uplink)" : PeerHostName;
        string text = $"*** route: {_options.HostName} -> {parent} -> {route.Dest}";
        return new HostUserMessage(SystemSender, route.User, text);
    }

    /// <summary>
    /// The strict-leaf <c>/..SYSI</c> reply: our identity / version line plus the configured sysinfo string
    /// when the request targets us or "all" (SPECS line 136). A request for some other host is not for us
    /// to answer (we are no transit), so it yields no reply. Returned as conversd-formatted UMSGs.
    /// </summary>
    private List<HostCommand> SysInfoReplies(HostSysInfo sysi)
    {
        bool forUs = string.Equals(sysi.Host, _options.HostName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(sysi.Host, "all", StringComparison.OrdinalIgnoreCase);
        if (!forUs)
        {
            return [];
        }

        var replies = new List<HostCommand>
        {
            new HostUserMessage(SystemSender, sysi.User,
                $"*** {_options.HostName}: pdn-convers {_options.Software} (strict leaf)"),
        };
        if (_options.SysInfo.Length != 0)
        {
            replies.Add(new HostUserMessage(SystemSender, sysi.User, $"*** {_options.HostName}: {_options.SysInfo}"));
        }

        return replies;
    }

    private void RecordPong(long reportedTime)
    {
        // The PONG carries the parent's own measured rtt (SPECS: -1 = no measurement, 0 = not yet). We
        // also have our own measurement: now − when we sent the PING that this answers.
        DateTimeOffset now = _time.GetUtcNow();
        long ourMs = (long)(now - _lastPingRoundTripAt).TotalMilliseconds;
        LastRoundTripMs = ourMs >= 0 ? ourMs : 0;
        _ = reportedTime; // the parent's figure is informational; our own measurement holds the link
    }
}
