namespace Convers.Core;

/// <summary>
/// The sans-IO output of <see cref="ConversHub.Advance"/>: a fan-out instruction the Host (W5)
/// turns into actual I/O. Two destinations, matching the leaf's two sides: <b>To a local session</b>
/// (deliver text to an RF/web user) and <b>To the uplink</b> (emit a host command). These are
/// <b>Core's own</b> action types — the uplink ones name the convers host command they correspond to
/// but carry plain values, so the Host maps them onto <c>Convers.Protocol</c>'s wire encoder without
/// Core depending on Protocol. The hub never performs I/O; it only returns these.
/// </summary>
public abstract record ConversAction
{
    private ConversAction()
    {
    }

    // ---------------------------------------------------------------- to local sessions

    /// <summary>Deliver a channel message to a local session's transport.</summary>
    public sealed record DeliverChannelMessage(
        string SessionId, int Channel, string FromUser, string Text) : ConversAction;

    /// <summary>Deliver a private message to a local session.</summary>
    public sealed record DeliverPrivateMessage(string SessionId, string FromUser, string Text) : ConversAction;

    /// <summary>Deliver an away-notice to a local session (e.g. "user is away: …" when PMing an away user).</summary>
    public sealed record DeliverAwayNotice(string SessionId, string User, string Away) : ConversAction;

    /// <summary>Deliver an invitation to a local session.</summary>
    public sealed record DeliverInvite(string SessionId, string FromUser, int Channel) : ConversAction;

    /// <summary>Tell a local session a user joined its channel (presence notice).</summary>
    public sealed record DeliverJoinNotice(string SessionId, int Channel, string User) : ConversAction;

    /// <summary>Tell a local session a user left its channel (presence notice).</summary>
    public sealed record DeliverLeaveNotice(
        string SessionId, int Channel, string User, string Reason) : ConversAction;

    /// <summary>Tell a local session the topic of a channel changed.</summary>
    public sealed record DeliverTopic(string SessionId, int Channel, string Topic, string SetBy) : ConversAction;

    // ---------------------------------------------------------------- to the uplink

    /// <summary>
    /// Emit a <c>/..USER</c> upstream: <paramref name="User"/>@<paramref name="Host"/> left
    /// <paramref name="FromChannel"/> and joined <paramref name="ToChannel"/> (-1 = sign-off).
    /// </summary>
    public sealed record SendUser(
        string User, string Host, DateTimeOffset Timestamp, int FromChannel, int ToChannel,
        string Personal) : ConversAction;

    /// <summary>Emit a <c>/..CMSG</c> channel message upstream.</summary>
    public sealed record SendChannelMessage(string User, int Channel, string Text) : ConversAction;

    /// <summary>Emit a <c>/..UMSG</c> private message upstream.</summary>
    public sealed record SendPrivateMessage(string From, string To, string Text) : ConversAction;

    /// <summary>Emit a <c>/..UDAT</c> personal-text update upstream.</summary>
    public sealed record SendPersonal(string User, string Host, string Text) : ConversAction;

    /// <summary>Emit a <c>/..AWAY</c> update upstream. Empty <paramref name="Text"/> = back.</summary>
    public sealed record SendAway(string User, string Host, DateTimeOffset Timestamp, string Text) : ConversAction;

    /// <summary>Emit a <c>/..TOPI</c> topic update upstream.</summary>
    public sealed record SendTopic(
        string User, string Host, DateTimeOffset Timestamp, int Channel, string Text) : ConversAction;

    /// <summary>Emit a <c>/..INVI</c> invitation upstream.</summary>
    public sealed record SendInvite(string From, string User, int Channel) : ConversAction;

    /// <summary>Emit a <c>/..PONG</c> upstream in answer to a ping, carrying our measured rtt (or a sentinel).</summary>
    public sealed record SendPong(long MillisecondsOrSentinel) : ConversAction;

    // ---------------------------------------------------------------- link control

    /// <summary>Drop the uplink (a <c>/..LOOP</c> was received — design decision 1's defensive loop guard).</summary>
    public sealed record DropUplink(string Reason) : ConversAction;
}
