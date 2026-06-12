namespace Convers.Core;

/// <summary>
/// A local user session attached to this leaf (an RF or web user). The hub tracks one of these per
/// connected local user; <see cref="Id"/> is an opaque token the Host assigns so fan-out actions
/// can be routed back to the right transport. The session's identity is the RHP-authenticated
/// callsign — never spoofable, never the conversd <c>~</c>-branded unauthenticated form
/// (design decision 4).
/// </summary>
public sealed record LocalSession
{
    /// <summary>
    /// The opaque session id the Host assigns and the hub echoes back in actions. The hub treats
    /// it as an arbitrary key; uniqueness is the caller's contract.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>The authenticated callsign of the local user (canonical form).</summary>
    public required string Callsign { get; init; }

    /// <summary>The channel this local user is currently on.</summary>
    public int Channel { get; init; }

    /// <summary>The user's current personal text (mirrors the persisted <see cref="UserProfile.Personal"/>).</summary>
    public string Personal { get; init; } = string.Empty;

    /// <summary>The user's current nickname.</summary>
    public string Nickname { get; init; } = string.Empty;

    /// <summary>The user's away message; empty when present.</summary>
    public string Away { get; init; } = string.Empty;

    /// <summary>True when the local user is away.</summary>
    public bool IsAway => Away.Length != 0;

    /// <summary>When the local user joined / last switched channel.</summary>
    public DateTimeOffset JoinedAt { get; init; }
}
