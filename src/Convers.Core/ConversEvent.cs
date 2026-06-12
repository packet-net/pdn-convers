namespace Convers.Core;

/// <summary>
/// The sans-IO input to <see cref="ConversHub.Advance"/>: a domain event, either a local user doing
/// something or a host-level (uplink) presence/message event arriving. These are <b>Core's own</b>
/// event types — deliberately independent of <c>Convers.Protocol</c>'s wire commands so Core and
/// Protocol can be built in parallel (the dependency rule in design.md). The Host (W5) translates
/// Protocol wire commands ↔ these events. The family split (Local* vs Host*) mirrors the two sides
/// the leaf bridges: local sessions ⇄ the one uplink.
/// </summary>
public abstract record ConversEvent
{
    private ConversEvent()
    {
    }

    // ---------------------------------------------------------------- local user events

    /// <summary>A local user connects and joins a channel (their first presence on the leaf).</summary>
    public sealed record LocalJoin(string SessionId, string Callsign, int Channel) : ConversEvent;

    /// <summary>A local user switches to a different channel.</summary>
    public sealed record LocalSwitchChannel(string SessionId, int Channel) : ConversEvent;

    /// <summary>A local user says something to their current channel.</summary>
    public sealed record LocalSay(string SessionId, string Text) : ConversEvent;

    /// <summary>A local user sends a private message to another user (local or remote).</summary>
    public sealed record LocalPrivateMessage(string SessionId, string ToUser, string Text) : ConversEvent;

    /// <summary>A local user sets (or clears, with empty text) their personal text.</summary>
    public sealed record LocalSetPersonal(string SessionId, string Personal) : ConversEvent;

    /// <summary>A local user sets (or clears) their away message.</summary>
    public sealed record LocalSetAway(string SessionId, string Away) : ConversEvent;

    /// <summary>A local user sets the topic of their current channel.</summary>
    public sealed record LocalSetTopic(string SessionId, string Topic) : ConversEvent;

    /// <summary>A local user invites another user to a channel.</summary>
    public sealed record LocalInvite(string SessionId, string ToUser, int Channel) : ConversEvent;

    /// <summary>A local user leaves / disconnects.</summary>
    public sealed record LocalLeave(string SessionId, string Reason = "") : ConversEvent;

    // ---------------------------------------------------------------- host (uplink) events

    /// <summary>
    /// A <c>/..USER</c> presence event from the uplink: <paramref name="User"/>@<paramref name="Host"/>
    /// left <paramref name="FromChannel"/> and joined <paramref name="ToChannel"/> at
    /// <paramref name="Timestamp"/>. <paramref name="ToChannel"/> == -1 is a sign-off (the
    /// <paramref name="Personal"/> field then carries the reason). <paramref name="FromChannel"/> == -1
    /// is a fresh join. <paramref name="Observer"/> marks an OBSERVER-only presence (<c>/..OBSV</c>).
    /// </summary>
    public sealed record HostUser(
        string User, string Host, DateTimeOffset Timestamp, int FromChannel, int ToChannel,
        string Personal, bool Observer = false) : ConversEvent;

    /// <summary>
    /// A <c>/..CMSG</c> channel message from the uplink: <paramref name="User"/> wrote
    /// <paramref name="Text"/> to <paramref name="Channel"/>. When <paramref name="User"/> is
    /// <c>conversd</c> it is a broadcast (no per-user formatting).
    /// </summary>
    public sealed record HostChannelMessage(string User, int Channel, string Text) : ConversEvent;

    /// <summary>A <c>/..UMSG</c> private message from the uplink: <paramref name="From"/> → <paramref name="To"/>.</summary>
    public sealed record HostPrivateMessage(string From, string To, string Text) : ConversEvent;

    /// <summary>A <c>/..UDAT</c> personal-text update from the uplink for <paramref name="User"/>@<paramref name="Host"/>.</summary>
    public sealed record HostPersonal(string User, string Host, string Text) : ConversEvent;

    /// <summary>A <c>/..AWAY</c> update from the uplink. Empty <paramref name="Text"/> means the user is back.</summary>
    public sealed record HostAway(string User, string Host, DateTimeOffset Timestamp, string Text) : ConversEvent;

    /// <summary>
    /// A <c>/..TOPI</c> topic update from the uplink: <paramref name="User"/>@<paramref name="Host"/>
    /// set the topic of <paramref name="Channel"/> at <paramref name="Timestamp"/>. Empty
    /// <paramref name="Text"/> removes the topic. A newer stored topic is not overwritten.
    /// </summary>
    public sealed record HostTopic(
        string User, string Host, DateTimeOffset Timestamp, int Channel, string Text) : ConversEvent;

    /// <summary>A <c>/..INVI</c> invitation from the uplink: <paramref name="From"/> invites <paramref name="User"/> to <paramref name="Channel"/>.</summary>
    public sealed record HostInvite(string From, string User, int Channel) : ConversEvent;

    /// <summary>A <c>/..PING</c> keepalive from the uplink (a pong is requested).</summary>
    public sealed record HostPing : ConversEvent;

    /// <summary>A <c>/..PONG</c> from the uplink carrying the peer's measured round-trip time.</summary>
    public sealed record HostPong(long MillisecondsOrSentinel) : ConversEvent;

    /// <summary>A <c>/..LOOP</c> from the uplink: a routing loop was detected; the link must be dropped.</summary>
    public sealed record HostLoop(string Host) : ConversEvent;

    /// <summary>
    /// An unrecognised <c>/..</c> host command. For a strict leaf the SPECS "relay to all other
    /// hosts" rule is a no-op (one uplink, no other host), so the hub records it and emits nothing —
    /// modelled explicitly so the FSM is total. <paramref name="Raw"/> is the verbatim line.
    /// </summary>
    public sealed record HostUnknown(string Raw) : ConversEvent;
}
