namespace Convers.Console;

/// <summary>
/// Static, per-session console configuration the Host supplies at connect. Everything here is an
/// input to the session; the console never reads config from disk or the wall clock.
/// </summary>
public sealed record ConverseConsoleConfig
{
    /// <summary>The leaf's own convers node name, shown in the greeting (e.g. the node callsign).</summary>
    public string NodeName { get; init; } = "convers";

    /// <summary>
    /// The fixed default channel a user lands on at connect when they pick none (the packet.net
    /// convers home channel; overridden by config at composition time).
    /// </summary>
    public int DefaultChannel { get; init; } = 2723;

    /// <summary>
    /// Lines per page for the paclen-friendly pager (mirrors the BBS <c>OP</c> setting). 0 disables
    /// paging (one continuous stream — useful for automated/Winpack clients). Default 20.
    /// </summary>
    public int PageLength { get; init; } = 20;

    /// <summary>The which-surface input for this session (plain by default). See <see cref="ConsoleInterface"/>.</summary>
    public ConsoleInterface Interface { get; init; } = ConsoleInterface.Plain;
}
