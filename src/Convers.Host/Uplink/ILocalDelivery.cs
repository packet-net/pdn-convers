using Convers.Core;

namespace Convers.Host.Uplink;

/// <summary>
/// The sink for hub actions bound for <em>local</em> sessions (RF and web users) — the half of the bridge
/// the uplink does not own. As the <see cref="HostLink"/> applies inbound host commands to the hub, the
/// hub fans out <c>Deliver*</c> actions for local listeners; the link hands those here. W5's inbound demux
/// implements this against live RF/web sessions; W4 ships a recording implementation for tests and a
/// no-op default so the link runs standalone.
/// </summary>
public interface ILocalDelivery
{
    /// <summary>
    /// Deliver one local-bound action (a <c>DeliverChannelMessage</c>, <c>DeliverPrivateMessage</c>,
    /// <c>DeliverJoinNotice</c>, …). Implementations route it to the addressed session. Uplink-bound and
    /// link-control actions never reach here — the <see cref="HostLink"/> filters them out.
    /// </summary>
    void Deliver(ConversAction action);
}

/// <summary>A no-op <see cref="ILocalDelivery"/> — the default when no local sink is wired (W4 standalone).</summary>
public sealed class NullLocalDelivery : ILocalDelivery
{
    /// <summary>The shared instance.</summary>
    public static readonly NullLocalDelivery Instance = new();

    private NullLocalDelivery()
    {
    }

    /// <inheritdoc/>
    public void Deliver(ConversAction action)
    {
        // intentionally nothing
    }
}

/// <summary>
/// Observes every inbound (network-origin) <see cref="ConversEvent"/> the <see cref="HostLink"/> applies
/// to the hub — the half of the chat-log feed that arrives from the uplink (channel messages, PMs,
/// presence). The Host wires this to the chat-log writer (design decision 7); W5 sees each event exactly
/// once, before fan-out, so logging is neither missed nor double-counted. The default is a no-op.
/// </summary>
public interface IInboundObserver
{
    /// <summary>Called for each inbound network-origin event, in order, before it is fanned out.</summary>
    void OnInbound(ConversEvent inboundEvent);
}

/// <summary>A no-op <see cref="IInboundObserver"/> — the default when no inbound observer is wired.</summary>
public sealed class NullInboundObserver : IInboundObserver
{
    /// <summary>The shared instance.</summary>
    public static readonly NullInboundObserver Instance = new();

    private NullInboundObserver()
    {
    }

    /// <inheritdoc/>
    public void OnInbound(ConversEvent inboundEvent)
    {
        // intentionally nothing
    }
}

/// <summary>
/// Observes every <em>local-origin</em> <see cref="ConversEvent"/> the <see cref="HostLink"/> applies to
/// the hub — the other half of the chat-log feed (a local RF/web user's say / PM / join / leave / away).
/// The link resolves the speaker's callsign and channel from the hub on its owning loop (the events carry
/// a session id, not a callsign) and passes them in, so logging is centralised at the one fan-out point
/// and never duplicated or bypassed (design decision 7). The default is a no-op.
/// </summary>
public interface ILocalEventObserver
{
    /// <summary>
    /// True when <paramref name="localEvent"/> is one this observer cares about, so the link only does the
    /// (loop-bound) identity resolution when there is something to log.
    /// </summary>
    bool IsLoggable(ConversEvent localEvent);

    /// <summary>
    /// Called for each loggable local-origin event after the hub has applied it, with the speaker's
    /// resolved callsign and channel and the <paramref name="actions"/> the hub fanned out. The actions
    /// tell the observer whether the event was actually accepted (a say/join refused by a channel-mode rule
    /// produces only a notice and no Send/Deliver), so a refused action is not mis-logged.
    /// </summary>
    void OnLocal(ConversEvent localEvent, string fromCall, int channel, IReadOnlyList<ConversAction> actions);
}

/// <summary>A no-op <see cref="ILocalEventObserver"/> — the default when no local observer is wired.</summary>
public sealed class NullLocalEventObserver : ILocalEventObserver
{
    /// <summary>The shared instance.</summary>
    public static readonly NullLocalEventObserver Instance = new();

    private NullLocalEventObserver()
    {
    }

    /// <inheritdoc/>
    public bool IsLoggable(ConversEvent localEvent) => false;

    /// <inheritdoc/>
    public void OnLocal(ConversEvent localEvent, string fromCall, int channel, IReadOnlyList<ConversAction> actions)
    {
        // intentionally nothing
    }
}
