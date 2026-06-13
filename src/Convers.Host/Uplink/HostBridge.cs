using Convers.Core;
using Convers.Protocol;

namespace Convers.Host.Uplink;

/// <summary>
/// The sans-IO translation between the convers wire (<see cref="HostCommand"/>, from
/// <c>Convers.Protocol</c>) and the hub's domain language (<see cref="ConversEvent"/> /
/// <see cref="ConversAction"/>, from <c>Convers.Core</c>) — the seam design.md keeps deliberately
/// separate so Core and Protocol need not reference each other. The <see cref="HostLink"/> uses it to
/// feed inbound host commands to the hub and to render the hub's uplink-bound actions back onto the wire,
/// bridging presence both ways (design decision 5).
/// </summary>
/// <remarks>
/// Wire timestamps are Unix seconds (<see cref="long"/>); the hub speaks <see cref="DateTimeOffset"/>.
/// This is the only place that conversion lives. A strict leaf only ever <em>sends</em> the
/// uplink-bound action family (the <c>Send*</c> records and <c>SendPong</c>); local-delivery actions are
/// the Host's RF/web concern, not the uplink's, so <see cref="ToHostCommand"/> ignores them.
/// </remarks>
public static class HostBridge
{
    /// <summary>
    /// Translate one inbound wire <see cref="HostCommand"/> into the hub event it drives, or
    /// <see langword="null"/> when the command is handled entirely at the link layer (the
    /// <c>/..HOST</c> handshake) and never reaches the hub. PING/PONG do map to hub events
    /// (<see cref="ConversEvent.HostPing"/>/<see cref="ConversEvent.HostPong"/>) so the keepalive
    /// state and pong-answer policy stay in one place, but the <see cref="HostLink"/> also acts on them
    /// directly for link timing.
    /// </summary>
    public static ConversEvent? ToEvent(HostCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command switch
        {
            HostHandshake => null, // consumed by the link FSM, not the hub
            HostUser u => new ConversEvent.HostUser(
                u.User, u.Host, FromUnix(u.Timestamp), u.FromChannel, u.ToChannel, u.Text, u.IsObserver),
            HostChannelMessage m => new ConversEvent.HostChannelMessage(m.User, m.Channel, m.Text),
            HostUserMessage m => new ConversEvent.HostPrivateMessage(m.From, m.To, m.Text),
            HostUserData d => new ConversEvent.HostPersonal(d.User, d.Host, d.Text),
            HostAway a => new ConversEvent.HostAway(a.User, a.Host, FromUnix(a.Time), a.Text),
            HostTopic t => new ConversEvent.HostTopic(t.User, t.Host, FromUnix(t.Time), t.Channel, t.Text),
            HostInvite i => new ConversEvent.HostInvite(i.From, i.User, i.Channel),

            // /..MODE — the uplink is authoritative; the hub applies the toggle and honours it locally
            // (forwarding suppression, topic-lock, moderation, join-gating). W7b wires this over the wire.
            HostMode m => new ConversEvent.HostMode(m.Channel, m.Options),

            // /..OPER — user becomes (channel == -1: global) operator; the hub tracks it so it can enforce
            // +m/+t for the affected user and reflect op status in /who. The wire form carries the granter
            // (FromName) but not the affected user's host, so the host is left blank (the op grant is
            // recorded against the bare callsign; a same-named remote snapshot is reflected only if it
            // matches that blank host, which is fine — the grant itself is what gates enforcement).
            HostOper o => new ConversEvent.HostOper(o.User, string.Empty, o.Channel),

            HostPing => new ConversEvent.HostPing(),
            HostPong p => new ConversEvent.HostPong(p.Time),
            HostLoop l => new ConversEvent.HostLoop(l.Detail),
            UnknownHostCommand unknown => new ConversEvent.HostUnknown(HostCommandCodec.Format(unknown)),

            // Verbs a strict leaf answers at the link layer (ROUT/SYSI) or does not model into hub state
            // (UADD/DEST/ECMD/HELP): record them as unknown so the FSM stays total and nothing is silently
            // dropped. ROUT/SYSI are additionally handled by the HostLinkEngine before reaching here.
            _ => new ConversEvent.HostUnknown(HostCommandCodec.Format(command)),
        };
    }

    /// <summary>
    /// Translate one uplink-bound <see cref="ConversAction"/> into the wire <see cref="HostCommand"/> to
    /// send to the parent, or <see langword="null"/> for an action that targets a local session (those
    /// are the Host's RF/web concern, never the uplink). <see cref="ConversAction.DropUplink"/> also
    /// returns null — it is link control the <see cref="HostLink"/> acts on, not a wire command.
    /// </summary>
    public static HostCommand? ToHostCommand(ConversAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action switch
        {
            ConversAction.SendUser u => new HostUser(
                u.User, u.Host, ToUnix(u.Timestamp), u.FromChannel, u.ToChannel, u.Personal),
            ConversAction.SendChannelMessage m => new HostChannelMessage(m.User, m.Channel, m.Text),
            ConversAction.SendPrivateMessage m => new HostUserMessage(m.From, m.To, m.Text),
            ConversAction.SendPersonal p => new HostUserData(p.User, p.Host, p.Text),
            ConversAction.SendAway a => new HostAway(a.User, a.Host, ToUnix(a.Timestamp), a.Text),
            ConversAction.SendTopic t => new HostTopic(t.User, t.Host, ToUnix(t.Timestamp), t.Channel, t.Text),
            ConversAction.SendInvite i => new HostInvite(i.From, i.User, i.Channel),

            // /..MODE — a local channel-operator changed the modes; push the canonical full mode string up
            // so the parent and the rest of the network converge on the same set (design decision 5).
            ConversAction.SendMode m => new HostMode(m.Channel, m.Options),

            ConversAction.SendPong p => new HostPong(p.MillisecondsOrSentinel),
            _ => null, // Deliver* (local) and DropUplink (link control) are not wire commands
        };
    }

    /// <summary>Unix seconds → <see cref="DateTimeOffset"/> (UTC).</summary>
    public static DateTimeOffset FromUnix(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds);

    /// <summary>
    /// <see cref="DateTimeOffset"/> → Unix seconds, floored at 0 (the wire never carries negative
    /// timestamps; a pre-epoch value would be a bug, so clamp rather than emit garbage).
    /// </summary>
    public static long ToUnix(DateTimeOffset when)
    {
        long s = when.ToUnixTimeSeconds();
        return s < 0 ? 0 : s;
    }
}
