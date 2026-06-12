namespace Convers.Protocol;

/// <summary>
/// The convers '/'-grammar prefixes (see <c>reference/SPECS.txt</c> and <c>user.c</c>/<c>host.c</c>).
/// The full line codec, the USER and HOST command sets, and the
/// UNKNOWN→USER|OBSERVER|HOST connection FSM are wave W1 — this is the seed primitive the
/// scaffold (and the first golden vectors) build on.
/// </summary>
public static class ConversCommand
{
    /// <summary>Every command is introduced by a forward slash.</summary>
    public const string CommandPrefix = "/";

    /// <summary>
    /// Host-to-host commands carry the doubled-dot prefix (<c>/..USER</c>, <c>/..CMSG</c>,
    /// <c>/..PING</c>, …). The SPECS "golden rule" — relay unrecognised <c>/..</c> commands to all
    /// <em>other</em> connected hosts — is a no-op for a strict leaf (one uplink, no other host).
    /// </summary>
    public const string HostCommandPrefix = "/..";

    /// <summary>
    /// True when <paramref name="line"/> is a host-to-host command (<c>/..</c>-prefixed) rather than
    /// a user command or chat text. A leaf bridges these between its single uplink and local users.
    /// </summary>
    public static bool IsHostCommand(string? line) =>
        line is not null && line.StartsWith(HostCommandPrefix, StringComparison.Ordinal);

    /// <summary>
    /// True when <paramref name="line"/> begins a command (starts with <c>/</c>) — as opposed to
    /// ordinary chat text destined for the current channel.
    /// </summary>
    public static bool IsCommand(string? line) =>
        line is not null && line.StartsWith(CommandPrefix, StringComparison.Ordinal);
}
