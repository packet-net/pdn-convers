namespace Convers.Core;

/// <summary>
/// The kind of a <see cref="ChatLogEntry"/> — the discriminator on the append-only <c>chatlog</c>
/// table (design decision 7). Three loggable shapes of convers activity: a channel message, a
/// point-to-point private message, and a presence event (join / leave / away).
/// </summary>
public enum ChatLogKind
{
    /// <summary>A channel message (a <c>/..CMSG</c> from the uplink, or a local user saying something).</summary>
    Channel = 0,

    /// <summary>A point-to-point private message (<c>/..UMSG</c>); <see cref="ChatLogEntry.Channel"/> is null.</summary>
    PrivateMessage = 1,

    /// <summary>A presence event — a join, leave, or away change. <see cref="ChatLogEntry.Text"/> describes it.</summary>
    Presence = 2,
}

/// <summary>
/// Where a logged event originated, relative to this leaf node: a <see cref="Local"/> user on this
/// node, or a <see cref="Network"/> user seen via the uplink (a <c>/..</c> host command). This is the
/// node-local view, not the convers-wide one.
/// </summary>
public enum ChatLogOrigin
{
    /// <summary>The event came from a local user/session on this node.</summary>
    Local = 0,

    /// <summary>The event arrived from the uplink — a network user seen via a host command.</summary>
    Network = 1,
}

/// <summary>
/// One row of the append-only, kept-forever convers <c>chatlog</c> (design decision 7): every channel
/// message the node sees (local <b>and</b> network origin), every private message, and every presence
/// event. The hub stays sans-IO — it surfaces each loggable event and the Host (W5) persists it via
/// <see cref="ConversStore.AppendChatLog"/>. The log is the node's durable record of all convers
/// activity and the source the web tile renders scrollback from.
///
/// <para>The <see cref="At"/> timestamp is supplied by the writer; when it is left at its default
/// (<see cref="DateTimeOffset"/> <c>default</c>) the store stamps it from its injected
/// <see cref="TimeProvider"/>. The store never reads the wall clock directly.</para>
/// </summary>
public sealed record ChatLogEntry
{
    /// <summary>What kind of event this row records (the table discriminator).</summary>
    public required ChatLogKind Kind { get; init; }

    /// <summary>
    /// When the event was logged (UTC). Left at <c>default</c>, the store stamps it from its
    /// <see cref="TimeProvider"/> on append.
    /// </summary>
    public DateTimeOffset At { get; init; }

    /// <summary>
    /// The channel the event belongs to, or <see langword="null"/> for a private message. Presence
    /// events carry the channel they relate to (e.g. the channel joined/left); an away change with no
    /// channel context may leave this null.
    /// </summary>
    public int? Channel { get; init; }

    /// <summary>The convers name the event is from (the speaker, the PM sender, the user whose presence changed).</summary>
    public required string FromCall { get; init; }

    /// <summary>
    /// The addressed recipient for a private message, or <see langword="null"/> for a channel message
    /// or presence event.
    /// </summary>
    public string? ToCall { get; init; }

    /// <summary>Where the event originated relative to this node (local user vs network/uplink).</summary>
    public required ChatLogOrigin Origin { get; init; }

    /// <summary>
    /// The payload: the message body for a channel/private message, or a human-readable description of
    /// the presence change (e.g. <c>joined</c>, <c>left: bye</c>, <c>away: lunch</c>). Empty is allowed
    /// but never null.
    /// </summary>
    public string Text { get; init; } = string.Empty;
}
