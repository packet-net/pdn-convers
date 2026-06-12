namespace Convers.Core;

/// <summary>
/// One entry in the network user table — a user present on the convers network, on some channel,
/// as seen by this leaf. Mirrors the user fields of <c>conversd.h struct connection</c> in the
/// <c>CT_USER</c> role (<c>name</c>, <c>host</c>, <c>channel</c>, <c>pers</c>, <c>nickname</c>,
/// <c>away</c>, <c>observer</c>). This is live presence state: it is rebuilt from the uplink on
/// reconnect and never persisted (design decision 7). A user keyed <c>(Name, Host)</c> is on
/// exactly one channel at a time, matching how <c>/..USER</c> moves a user between channels.
/// </summary>
public sealed record NetworkUser
{
    /// <summary>The user's convers name (callsign), canonicalised via <see cref="Callsigns.Normalize"/>.</summary>
    public required string Name { get; init; }

    /// <summary>The host (convers node) the user is logged in on, canonicalised.</summary>
    public required string Host { get; init; }

    /// <summary>The channel the user is currently on.</summary>
    public required int Channel { get; init; }

    /// <summary>
    /// The user's personal text (the <c>pers</c> field carried in <c>/..USER</c> / set by
    /// <c>/..UDAT</c>). Empty when the user has no personal note (the wire <c>'@'</c> sentinel).
    /// </summary>
    public string Personal { get; init; } = string.Empty;

    /// <summary>The user's TNOS-style nickname, if any (facility <c>n</c>); else the empty string.</summary>
    public string Nickname { get; init; } = string.Empty;

    /// <summary>
    /// The away message, if the user is away (<c>/..AWAY</c>); empty when present/back. Used to
    /// hint a sender doing a private message to an away user.
    /// </summary>
    public string Away { get; init; } = string.Empty;

    /// <summary>True when the user is away (<see cref="Away"/> is non-empty).</summary>
    public bool IsAway => Away.Length != 0;

    /// <summary>
    /// True when the user is an OBSERVER only (joined via <c>/..OBSV</c>): a subset of USER that
    /// produces no output to others. The leaf tracks it for an accurate <c>/who</c>.
    /// </summary>
    public bool IsObserver { get; init; }

    /// <summary>When the user last joined / switched channel (the <c>/..USER</c> timestamp).</summary>
    public DateTimeOffset JoinedAt { get; init; }
}
