using Convers.Core;
using Convers.Host.Uplink;
using Microsoft.Extensions.Logging;

namespace Convers.Host.Sessions;

/// <summary>
/// Persists every loggable convers event the node sees into the append-only, kept-forever
/// <c>chatlog</c> (design decision 7) via <see cref="ConversStore.AppendChatLog"/>. Chat logging is
/// <b>centralised at the hub-action fan-out</b> (the <see cref="HostLink"/>): the link calls
/// <see cref="LogNetwork"/> once for each inbound (network-origin) event it applies to the hub, and
/// <see cref="LogLocal"/> once for each local-origin event a session submits — both <em>at the single
/// owning loop</em>, before/around fan-out. Because the writer logs from the one place every event
/// passes through, a session can neither bypass it nor make it double-log.
/// <list type="bullet">
/// <item><b>Network-origin</b> — inbound <c>/..CMSG</c> / <c>/..UMSG</c> / <c>/..USER</c> /
/// <c>/..AWAY</c> from the uplink — logged with <see cref="ChatLogOrigin.Network"/> straight from the
/// event fields.</item>
/// <item><b>Local-origin</b> — a local RF/web user's say / PM / join / switch / leave / away — logged
/// with <see cref="ChatLogOrigin.Local"/>; the link resolves the speaker's callsign and channel from
/// the hub (the events carry a session id) and passes them in.</item>
/// </list>
/// Channel messages and PMs are logged once each (at ingestion, not per-recipient, so a message on a
/// channel with no local listeners is still kept). Presence (join / switch / leave / away) is logged as
/// a human-readable description. The store stamps the time from its <see cref="TimeProvider"/>.
/// </summary>
public sealed class ChatLogWriter : IInboundObserver, ILocalEventObserver
{
    private readonly ConversStore _store;
    private readonly ILogger _logger;

    /// <summary>Creates the writer over <paramref name="store"/>.</summary>
    public ChatLogWriter(ConversStore store, ILogger<ChatLogWriter> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc/>
    public void OnInbound(ConversEvent inboundEvent) => LogNetwork(inboundEvent);

    /// <inheritdoc/>
    public bool IsLoggable(ConversEvent localEvent) => IsLoggableLocal(localEvent);

    /// <inheritdoc/>
    public void OnLocal(ConversEvent localEvent, string fromCall, int channel, IReadOnlyList<ConversAction> actions) =>
        LogLocal(localEvent, fromCall, channel, actions);

    /// <summary>
    /// Records a network-origin (uplink) event. Channel/PM/presence shapes are logged straight from the
    /// event's own fields; anything not loggable (keepalive, modes, topics, invites, unknown) is ignored.
    /// </summary>
    public void LogNetwork(ConversEvent @event)
    {
        ChatLogEntry? entry = @event switch
        {
            ConversEvent.HostChannelMessage m when m.Text.Length != 0 => new ChatLogEntry
            {
                Kind = ChatLogKind.Channel,
                Channel = m.Channel,
                FromCall = m.User,
                Origin = ChatLogOrigin.Network,
                Text = m.Text,
            },

            ConversEvent.HostPrivateMessage m when m.Text.Length != 0 => new ChatLogEntry
            {
                Kind = ChatLogKind.PrivateMessage,
                FromCall = m.From,
                ToCall = m.To,
                Origin = ChatLogOrigin.Network,
                Text = m.Text,
            },

            // /..USER: a join, channel move, or sign-off (ToChannel == -1).
            ConversEvent.HostUser u => Presence(
                u.User,
                u.ToChannel == -1 ? u.FromChannel : u.ToChannel,
                u.ToChannel == -1
                    ? (u.Personal.Length == 0 ? "left" : $"left: {u.Personal}")
                    : (u.FromChannel == -1 ? "joined" : $"joined (from {u.FromChannel})"),
                ChatLogOrigin.Network),

            ConversEvent.HostAway a => Presence(
                a.User, null, a.Text.Length == 0 ? "back" : $"away: {a.Text}", ChatLogOrigin.Network),

            _ => null,
        };

        Append(entry);
    }

    /// <summary>
    /// Records a local-origin (RF/web) event with the speaker's already-resolved
    /// <paramref name="fromCall"/> and <paramref name="channel"/> (the link resolves these from the hub on
    /// its owning loop, since the events carry a session id, not a callsign). The <paramref name="actions"/>
    /// the hub fanned out gate logging: a say / join refused by a channel-mode rule produces only a notice
    /// (no Send/Deliver), so it is <em>not</em> logged as if it happened. Say / PM / presence are logged;
    /// anything else (mode, topic, personal, invite, operator) is ignored.
    /// </summary>
    public void LogLocal(ConversEvent @event, string fromCall, int channel, IReadOnlyList<ConversAction> actions)
    {
        // A request the hub refused on a channel-mode rule comes back as a DeliverModeNotice with no
        // Send/Deliver — that say/join did not happen, so it must not be logged. Everything else (a normal
        // say, a +l solo say with no listeners, a PM, presence) is a real event and is logged.
        if (Refused(actions))
        {
            return;
        }

        ChatLogEntry? entry = @event switch
        {
            ConversEvent.LocalSay s when s.Text.Length != 0 => new ChatLogEntry
            {
                Kind = ChatLogKind.Channel,
                Channel = channel,
                FromCall = fromCall,
                Origin = ChatLogOrigin.Local,
                Text = s.Text,
            },

            ConversEvent.LocalPrivateMessage m when m.Text.Length != 0 => new ChatLogEntry
            {
                Kind = ChatLogKind.PrivateMessage,
                FromCall = fromCall,
                ToCall = Callsigns.Normalize(m.ToUser),
                Origin = ChatLogOrigin.Local,
                Text = m.Text,
            },

            // A join/switch is a real presence change only if the hub actually announced it upstream
            // (SendUser). A no-op switch (already on that channel) or an out-of-range/invalid join returns
            // no actions, and an invite-only refusal returns only a notice — none of those should log a
            // phantom "joined".
            ConversEvent.LocalJoin j when AnnouncedPresence(actions) =>
                Presence(fromCall, j.Channel, "joined", ChatLogOrigin.Local),

            ConversEvent.LocalSwitchChannel sw when AnnouncedPresence(actions) =>
                Presence(fromCall, sw.Channel, "joined", ChatLogOrigin.Local),

            ConversEvent.LocalLeave l => Presence(
                fromCall, channel, l.Reason.Trim().Length == 0 ? "left" : $"left: {l.Reason.Trim()}",
                ChatLogOrigin.Local),

            ConversEvent.LocalSetAway a => Presence(
                fromCall, channel, a.Away.Trim().Length == 0 ? "back" : $"away: {a.Away.Trim()}",
                ChatLogOrigin.Local),

            _ => null,
        };

        Append(entry);
    }

    /// <summary>True when the hub refused the request on a channel-mode rule (only a notice was emitted).</summary>
    private static bool Refused(IReadOnlyList<ConversAction> actions)
    {
        foreach (ConversAction a in actions)
        {
            if (a is ConversAction.DeliverModeNotice)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the hub announced a presence change upstream (a join/switch actually took effect).</summary>
    private static bool AnnouncedPresence(IReadOnlyList<ConversAction> actions)
    {
        foreach (ConversAction a in actions)
        {
            if (a is ConversAction.SendUser)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when a local event is one this writer logs (so the link only resolves identity when needed).</summary>
    public static bool IsLoggableLocal(ConversEvent @event) => @event is
        ConversEvent.LocalSay or ConversEvent.LocalPrivateMessage or ConversEvent.LocalJoin or
        ConversEvent.LocalSwitchChannel or ConversEvent.LocalLeave or ConversEvent.LocalSetAway;

    private static ChatLogEntry Presence(string fromCall, int? channel, string text, ChatLogOrigin origin) => new()
    {
        Kind = ChatLogKind.Presence,
        Channel = channel,
        FromCall = fromCall,
        Origin = origin,
        Text = text,
    };

    private void Append(ChatLogEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        try
        {
            _store.AppendChatLog(entry);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Logging is best-effort: never let a chat-log write fault the link or a session.
            LogFailed(_logger, ex.Message, null);
        }
    }

    private static readonly Action<ILogger, string, Exception?> LogFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, "ChatLogFailed"),
            "Chat-log write failed: {Reason}");
}
