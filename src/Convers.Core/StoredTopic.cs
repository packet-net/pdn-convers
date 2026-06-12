namespace Convers.Core;

/// <summary>
/// A persisted channel topic — the one piece of channel state <c>conversd-saupp</c> keeps across
/// restarts (design decision 7). Carries who/when so the SPECS <c>/..TOPI</c> "newer topic wins"
/// rule can be applied: a stored topic with a later <see cref="SetAt"/> is not overwritten by an
/// older incoming one. Keyed by channel number.
/// </summary>
public sealed record StoredTopic
{
    /// <summary>The channel this topic belongs to.</summary>
    public required int Channel { get; init; }

    /// <summary>The topic text. Empty means "topic removed" (still recorded, with its time).</summary>
    public string Topic { get; init; } = string.Empty;

    /// <summary>Who set the topic (a convers name).</summary>
    public string SetBy { get; init; } = string.Empty;

    /// <summary>When the topic was set — the <c>/..TOPI</c> timestamp; drives the newer-wins rule.</summary>
    public required DateTimeOffset SetAt { get; init; }
}
