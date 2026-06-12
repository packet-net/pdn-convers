using Convers.Core;
using Convers.Host.Uplink;
using Microsoft.Extensions.Logging;

namespace Convers.Host.Sessions;

/// <summary>
/// Persists every loggable convers event the node sees into the append-only, kept-forever
/// <c>chatlog</c> (design decision 7) via <see cref="ConversStore.AppendChatLog"/> — the write-wiring
/// the chat-logging storage PR left for this wave. It logs from the Host's vantage:
/// <list type="bullet">
/// <item><b>Local-origin</b> events — an RF/web user's say / PM / join / leave / away — recorded by the
/// demux as it submits them (<see cref="LogLocal"/>).</item>
/// <item><b>Network-origin</b> events — inbound <c>/..CMSG</c> / <c>/..UMSG</c> / <c>/..USER</c> /
/// <c>/..AWAY</c> from the uplink — recorded via <see cref="IInboundObserver.OnInbound"/> as the
/// <see cref="HostLink"/> applies them.</item>
/// </list>
/// Channel messages and PMs are logged once each (at ingestion, not per-recipient, so a message on a
/// channel with no local listeners is still kept). Presence (join / leave / away) is logged as a
/// human-readable description. The store stamps the time from its <see cref="TimeProvider"/>.
/// </summary>
public sealed class ChatLogWriter : IInboundObserver
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
    public void OnInbound(ConversEvent inboundEvent) => Log(inboundEvent, ChatLogOrigin.Network);

    /// <summary>
    /// Records a local-origin event (an RF/web user action). The demux calls this for the events it
    /// submits to the uplink, so the node's own users' chat is logged with <see cref="ChatLogOrigin.Local"/>.
    /// </summary>
    public void LogLocal(ConversEvent localEvent) => Log(localEvent, ChatLogOrigin.Local);

    private void Log(ConversEvent @event, ChatLogOrigin origin)
    {
        try
        {
            ChatLogEntry? entry = ToEntry(@event, origin);
            if (entry is not null)
            {
                _store.AppendChatLog(entry);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Logging is best-effort: never let a chat-log write fault the link or a session.
            LogFailed(_logger, ex.Message, null);
        }
    }

    /// <summary>
    /// Maps a hub event to a chat-log row, or <see langword="null"/> for an event that is not logged
    /// (keepalive, topics, invites, unknown). For a local say/PM the speaker is the session owner; the
    /// demux supplies the resolved callsign by rewriting the event before logging is not needed — Core's
    /// events carry the session id, not the callsign, for <c>LocalSay</c>/<c>LocalPrivateMessage</c>, so
    /// those local kinds are logged by the demux's callsign-aware overloads instead (see
    /// <see cref="LocalChannel"/> / <see cref="LocalPrivate"/> / <see cref="LocalPresence"/>).
    /// </summary>
    private static ChatLogEntry? ToEntry(ConversEvent @event, ChatLogOrigin origin) => @event switch
    {
        // Network-origin channel message (/..CMSG).
        ConversEvent.HostChannelMessage m when m.Text.Length != 0 => new ChatLogEntry
        {
            Kind = ChatLogKind.Channel,
            Channel = m.Channel,
            FromCall = m.User,
            Origin = origin,
            Text = m.Text,
        },

        // Network-origin private message (/..UMSG).
        ConversEvent.HostPrivateMessage m when m.Text.Length != 0 => new ChatLogEntry
        {
            Kind = ChatLogKind.PrivateMessage,
            FromCall = m.From,
            ToCall = m.To,
            Origin = origin,
            Text = m.Text,
        },

        // Network-origin presence (/..USER): a join, channel move, or sign-off (ToChannel == -1).
        ConversEvent.HostUser u => PresenceEntry(
            u.User,
            u.ToChannel == -1 ? u.FromChannel : u.ToChannel,
            u.ToChannel == -1
                ? (u.Personal.Length == 0 ? "left" : $"left: {u.Personal}")
                : (u.FromChannel == -1 ? "joined" : $"joined (from {u.FromChannel})"),
            origin),

        // Network-origin away change (/..AWAY).
        ConversEvent.HostAway a => PresenceEntry(
            a.User, null, a.Text.Length == 0 ? "back" : $"away: {a.Text}", origin),

        _ => null,
    };

    private static ChatLogEntry PresenceEntry(string fromCall, int? channel, string text, ChatLogOrigin origin) => new()
    {
        Kind = ChatLogKind.Presence,
        Channel = channel,
        FromCall = fromCall,
        Origin = origin,
        Text = text,
    };

    // ---------------------------------------------------------------- local (callsign-aware) overloads

    /// <summary>Logs a local user's channel message (the demux resolves the speaker's callsign).</summary>
    public void LocalChannel(string fromCall, int channel, string text) => Append(new ChatLogEntry
    {
        Kind = ChatLogKind.Channel,
        Channel = channel,
        FromCall = fromCall,
        Origin = ChatLogOrigin.Local,
        Text = text,
    });

    /// <summary>Logs a local user's private message.</summary>
    public void LocalPrivate(string fromCall, string toCall, string text) => Append(new ChatLogEntry
    {
        Kind = ChatLogKind.PrivateMessage,
        FromCall = fromCall,
        ToCall = toCall,
        Origin = ChatLogOrigin.Local,
        Text = text,
    });

    /// <summary>Logs a local user's presence change (join / leave / away).</summary>
    public void LocalPresence(string fromCall, int? channel, string text) => Append(new ChatLogEntry
    {
        Kind = ChatLogKind.Presence,
        Channel = channel,
        FromCall = fromCall,
        Origin = ChatLogOrigin.Local,
        Text = text,
    });

    private void Append(ChatLogEntry entry)
    {
        try
        {
            _store.AppendChatLog(entry);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(_logger, ex.Message, null);
        }
    }

    private static readonly Action<ILogger, string, Exception?> LogFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, "ChatLogFailed"),
            "Chat-log write failed: {Reason}");
}
