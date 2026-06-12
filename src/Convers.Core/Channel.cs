namespace Convers.Core;

/// <summary>
/// A live channel snapshot exposed by <see cref="ConversHub"/>: its number, the current topic
/// (with who/when, per <c>/..TOPI</c>), modes, and the set of users present on it (local + remote).
/// This is a read-only view assembled on demand from the hub's internal tables; the hub owns the
/// mutable state. Mirrors the relevant fields of <c>conversd.h struct channel</c>.
/// </summary>
public sealed record Channel
{
    /// <summary>The channel number (0..32767, <see cref="ChannelNumber"/>).</summary>
    public required int Number { get; init; }

    /// <summary>The channel topic, or empty when none is set.</summary>
    public string Topic { get; init; } = string.Empty;

    /// <summary>Who set the current topic (a convers name), or empty when no topic.</summary>
    public string TopicSetBy { get; init; } = string.Empty;

    /// <summary>When the current topic was set; <see langword="null"/> when no topic.</summary>
    public DateTimeOffset? TopicSetAt { get; init; }

    /// <summary>The channel's modes (<c>+i/+l/+m/+p/+s/+t</c>).</summary>
    public ChannelMode Modes { get; init; } = ChannelMode.None;

    /// <summary>The users currently on this channel (local and remote), ordered by name.</summary>
    public IReadOnlyList<NetworkUser> Users { get; init; } = [];

    /// <summary>True when this channel suppresses text forwarding to links (<c>+l</c>).</summary>
    public bool IsLocal => (Modes & ChannelMode.Local) != 0;
}
