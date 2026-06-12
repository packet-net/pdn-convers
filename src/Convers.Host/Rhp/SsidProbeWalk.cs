using System.Globalization;

namespace Convers.Host.Rhp;

/// <summary>
/// The SSID probe-walk that resolves a bind clash (design decision 4): given a preferred callsign with
/// an SSID, produce the ordered candidate callsigns to try at RHP bind time — the preferred SSID first,
/// then the remaining SSIDs in <c>0..15</c> in increasing order, wrapping past 15 back to 0 so every
/// SSID is offered exactly once. <see cref="RhpNodeLink"/> tries each in turn until one binds.
/// </summary>
/// <remarks>
/// Pure string logic, no I/O — unit-testable on its own. A preferred callsign with no SSID is treated
/// as SSID 0 (the bare callsign). The base is taken verbatim before the first <c>-</c>; an SSID outside
/// <c>0..15</c> (or a non-numeric one) falls back to starting at SSID 0.
/// </remarks>
public static class SsidProbeWalk
{
    /// <summary>The valid SSID range (AX.25): 0–15 inclusive.</summary>
    public const int MaxSsid = 15;

    /// <summary>
    /// The ordered bind candidates for <paramref name="preferredCallsign"/>: the preferred callsign
    /// <em>verbatim</em> first (a bare callsign stays bare — the operator's exact form is tried before any
    /// SSID is appended), then every SSID in <c>0..15</c> in increasing order from the start, wrapping
    /// after 15, with the preferred form skipped on the second pass. So a bare <c>M0LTE</c> tries
    /// <c>M0LTE</c>, then <c>M0LTE-1 … M0LTE-15</c>, then <c>M0LTE-0</c>; an <c>M0LTE-4</c> tries
    /// <c>M0LTE-4</c>, <c>M0LTE-5 … M0LTE-15</c>, <c>M0LTE-0 … M0LTE-3</c>. Seventeen entries for a bare
    /// callsign (the bare form plus all 16 SSIDs), sixteen for one with an SSID.
    /// </summary>
    public static IEnumerable<string> Candidates(string preferredCallsign)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preferredCallsign);
        string verbatim = preferredCallsign.Trim().ToUpperInvariant();
        (string @base, int startSsid) = Split(preferredCallsign);

        // The exact configured form first (bare stays bare).
        yield return verbatim;

        for (int i = 0; i <= MaxSsid; i++)
        {
            int ssid = (startSsid + i) % (MaxSsid + 1);
            string candidate = $"{@base}-{ssid.ToString(CultureInfo.InvariantCulture)}";
            if (!string.Equals(candidate, verbatim, StringComparison.Ordinal))
            {
                yield return candidate;
            }
        }
    }

    /// <summary>
    /// Splits a callsign into its base and starting SSID. No <c>-</c>, an empty SSID, a non-numeric SSID,
    /// or an SSID outside 0–15 all start the walk at SSID 0.
    /// </summary>
    public static (string Base, int Ssid) Split(string callsign)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callsign);
        string trimmed = callsign.Trim().ToUpperInvariant();
        int dash = trimmed.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0)
        {
            return (trimmed, 0);
        }

        string @base = trimmed[..dash];
        string ssidText = trimmed[(dash + 1)..];
        if (@base.Length == 0)
        {
            return (trimmed, 0);
        }

        if (int.TryParse(ssidText, NumberStyles.None, CultureInfo.InvariantCulture, out int ssid) &&
            ssid is >= 0 and <= MaxSsid)
        {
            return (@base, ssid);
        }

        return (@base, 0);
    }
}
