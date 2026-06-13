namespace Convers.Protocol;

/// <summary>
/// The out-of-band <c>//COMP</c> negotiation that toggles the host-link Huffman <see cref="Compression"/>
/// (W7c). Unlike a <c>/..HOST</c> facility letter, compression is turned on per-link with a literal
/// <c>//COMP 1</c> (enable) / <c>//COMP 0</c> (disable) line, always sent <em>uncompressed</em> so the
/// toggle is readable on a link that may already be 8-bit-clean-but-not-yet-compressed (conversd
/// <c>comp_command</c> / <c>fast_write(..., -1)</c>). The convention: a side that <em>can</em> compress
/// answers a request with <c>//COMP 1</c> and then both sides compress; a side that cannot answers
/// <c>//COMP 0</c>. This helper recognises and builds those lines; it performs no I/O.
/// </summary>
public static class CompressionNegotiation
{
    /// <summary>The literal request/acknowledge prefix (conversd writes <c>"\r//COMP n\r"</c>).</summary>
    public const string Prefix = "//COMP";

    /// <summary>The line enabling compression on the link (sent uncompressed).</summary>
    public const string Enable = "//COMP 1";

    /// <summary>The line disabling compression on the link (sent uncompressed).</summary>
    public const string Disable = "//COMP 0";

    /// <summary>
    /// True when <paramref name="line"/> is a <c>//COMP</c> negotiation line; <paramref name="enabled"/>
    /// then carries the requested state (<c>//COMP 1</c> → true, <c>//COMP 0</c> → false). CR/whitespace
    /// tolerant, matching how conversd brackets the token with carriage returns.
    /// </summary>
    public static bool TryParse(string? line, out bool enabled)
    {
        enabled = false;
        if (line is null)
        {
            return false;
        }

        string canonical = line.Trim();
        if (!canonical.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string arg = canonical[Prefix.Length..].Trim();
        if (arg == "1")
        {
            enabled = true;
            return true;
        }

        if (arg == "0")
        {
            enabled = false;
            return true;
        }

        return false;
    }
}
