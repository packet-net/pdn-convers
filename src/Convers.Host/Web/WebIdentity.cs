using Convers.Core;

namespace Convers.Host.Web;

/// <summary>
/// Maps a pdn username to a convers callsign for the web chat tile (design decision 8: "pdn usernames
/// map ↔ callsigns … so the owner and web users join the same channels as RF users").
/// </summary>
/// <remarks>
/// <para>
/// <b>Mapping (kept simple and documented):</b> the pdn username <em>is</em> the convers callsign when
/// it is itself a valid amateur callsign — the dominant case, since a pdn account is a ham's account, so
/// the owner signing in as <c>m0lte</c> becomes convers user <c>M0LTE</c> with no extra step. This
/// mirrors how an RF user auto-logs-in from their RHP-authenticated callsign (decision 4) — identity is
/// the callsign, never a chosen name. A username that is not a valid callsign has no mapping (the tile
/// shows a short explanation rather than inventing one); a richer username→callsign table is out of W5b
/// scope.
/// </para>
/// <para>
/// "Valid callsign" here is deliberately conservative: a single graphic token (Core's
/// <see cref="Callsigns.IsValidName"/>), 3–9 characters, alphanumeric, containing at least one letter and
/// one digit — enough to reject a free-form username like "tom" while accepting real callsigns.
/// </para>
/// </remarks>
public static class WebIdentity
{
    /// <summary>Shortest plausible callsign (e.g. <c>W1X</c>).</summary>
    public const int MinCallsignLength = 3;

    /// <summary>Longest callsign we accept (callsign + SSID, matching the claim-form ceiling).</summary>
    public const int MaxCallsignLength = 9;

    /// <summary>
    /// The convers callsign for <paramref name="pdnUser"/>, or <see langword="null"/> when the username
    /// is not a valid callsign (and so has no mapping). The result is in canonical (upper, trimmed) form.
    /// </summary>
    public static string? CallsignFor(string? pdnUser)
    {
        if (string.IsNullOrWhiteSpace(pdnUser))
        {
            return null;
        }

        string call = Callsigns.Normalize(pdnUser);
        return LooksLikeCallsign(call) ? call : null;
    }

    /// <summary>
    /// A conservative callsign shape check: 3–9 chars, a single graphic token, all ASCII alphanumeric,
    /// with at least one letter and one digit. Accepts real callsigns (with or without an SSID dash is
    /// not in scope — convers names are a single token) and rejects free-form usernames.
    /// </summary>
    public static bool LooksLikeCallsign(string? value)
    {
        if (string.IsNullOrEmpty(value)
            || value.Length is < MinCallsignLength or > MaxCallsignLength
            || !Callsigns.IsValidName(value))
        {
            return false;
        }

        bool hasLetter = false;
        bool hasDigit = false;
        foreach (char c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }

            hasLetter |= char.IsAsciiLetter(c);
            hasDigit |= char.IsAsciiDigit(c);
        }

        return hasLetter && hasDigit;
    }
}
