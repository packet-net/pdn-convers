namespace Convers.Core;

/// <summary>
/// A convers channel number and its validity rules (conversd.conf <c>DefaultChan</c>: "must be
/// &gt;= 0 and &lt;= 32767"). Seed domain primitive for wave W2's <c>ConversHub</c>; channel
/// membership, modes and the network destination table are built on top of it.
/// </summary>
public static class ChannelNumber
{
    /// <summary>Lowest valid channel.</summary>
    public const int Min = 0;

    /// <summary>Highest valid channel (a signed 16-bit ceiling, per the spec).</summary>
    public const int Max = 32767;

    /// <summary>
    /// Channel 0 is special: conversd treats it as the "random channel" sentinel and still forwards
    /// its traffic to links. The packet.net public default lives in the ordinary range instead.
    /// </summary>
    public const int Random = 0;

    /// <summary>
    /// The lowest channel the handover reserves for public, owner-pickable defaults (256–32767 —
    /// below this collides with well-known low channels). The shipped placeholder default is 3333.
    /// </summary>
    public const int PublicFloor = 256;

    /// <summary>True when <paramref name="channel"/> is within the valid <see cref="Min"/>..<see cref="Max"/> range.</summary>
    public static bool IsValid(int channel) => channel is >= Min and <= Max;

    /// <summary>
    /// True when <paramref name="channel"/> is a sensible owner-pickable public default
    /// (<see cref="PublicFloor"/>..<see cref="Max"/>) — i.e. valid and clear of the low/random range.
    /// </summary>
    public static bool IsPublicDefault(int channel) => channel is >= PublicFloor and <= Max;
}
