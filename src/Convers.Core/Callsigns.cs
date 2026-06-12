namespace Convers.Core;

/// <summary>
/// Callsign / convers-name normalisation. The convers wire is Latin-1 and case-insensitive on
/// user and host names (<c>host.c</c> uses <c>strcasecmp</c> throughout); the canonical form we
/// store and compare on is upper-cased and trimmed. A convers "name" is a single graphic token
/// (<c>h_user_command</c> rejects any name containing a non-<c>isgraph</c> byte), so normalisation
/// also collapses internal whitespace away by rejecting it at validation time rather than mangling.
/// </summary>
public static class Callsigns
{
    /// <summary>
    /// The canonical comparison form: trimmed and upper-cased with the invariant culture.
    /// A null or all-whitespace input normalises to the empty string.
    /// </summary>
    public static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();

    /// <summary>
    /// True when <paramref name="value"/> is a valid convers name token: non-empty and every
    /// character a graphic (printable, non-space) one — the <c>isgraph</c> rule
    /// <c>h_user_command</c> enforces on both the user and host fields.
    /// </summary>
    public static bool IsValidName(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (char c in value)
        {
            // isgraph: any printable char that is not whitespace. Latin-1, so control bytes and
            // the 0x80..0x9F / 0xA0 (NBSP) range are also excluded.
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c == ' ')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Case-insensitive equality on the canonical form (the <c>strcasecmp</c> the C reference uses).</summary>
    public static bool Equal(string? a, string? b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);
}
