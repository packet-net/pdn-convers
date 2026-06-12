namespace Convers.Core;

/// <summary>
/// The persisted, restart-surviving profile of a local user — the saupp differentiators
/// <c>conversd-saupp</c> keeps on disk (personal text, nickname, password). Live presence
/// (which channel, away state) is NOT here; it is in-memory only and rebuilt from the uplink
/// (design decision 7). The key is the user's canonical callsign.
/// </summary>
public sealed record UserProfile
{
    /// <summary>The user's callsign (canonical form; the primary key).</summary>
    public required string Callsign { get; init; }

    /// <summary>The user's personal text (the convers <c>pers</c> field). Empty when unset.</summary>
    public string Personal { get; init; } = string.Empty;

    /// <summary>The user's TNOS-style nickname (facility <c>n</c>). Empty when unset.</summary>
    public string Nickname { get; init; } = string.Empty;

    /// <summary>
    /// The salted password hash for this user, or <see langword="null"/> when no password is set.
    /// The store never holds a plaintext password — hashing is the caller's responsibility; the
    /// store persists and round-trips the opaque verifier string only.
    /// </summary>
    public string? PasswordHash { get; init; }

    /// <summary>When this profile was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}
