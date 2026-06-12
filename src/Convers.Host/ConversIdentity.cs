namespace Convers.Host;

/// <summary>
/// Resolves the convers node's on-air callsign. pdn's convention (packet.net
/// <c>AppServiceSupervisor</c>; the DAPPS precedent) is that <b>an app lives at an SSID of the node
/// callsign</b>: the supervisor injects <c>PDN_NODE_CALLSIGN</c>, and the app derives
/// <c>&lt;node-base&gt;-&lt;ssid&gt;</c> automatically (DAPPS uses <c>&lt;nodecall&gt;-7</c>), so the
/// owner never hand-edits a callsign. An explicit override still wins, verbatim.
/// </summary>
/// <remarks>
/// This resolves the <em>preferred</em> identity. The clash handling — <b>probe-walking to the next
/// free SSID on a duplicate-socket refusal</b> at RHP bind time — lands in W5 with the RHP bind loop;
/// this resolver only picks the starting SSID. Pure string logic, no I/O.
/// </remarks>
public static class ConversIdentity
{
    /// <summary>Default preferred SSID for the auto-derived callsign (DAPPS took 7; convers uses 4).</summary>
    public const int DefaultSsid = 4;

    /// <summary>The base callsign used when neither an override nor a node callsign is available.</summary>
    public const string PlaceholderBase = "N0CALL";

    /// <summary>
    /// Resolves the effective callsign.
    /// <list type="bullet">
    /// <item>A non-blank <paramref name="overrideCallsign"/> wins and is used verbatim (normalised) —
    /// including any SSID the owner put in it.</item>
    /// <item>Otherwise the callsign is <c>&lt;base-of(<paramref name="nodeCallsign"/>)&gt;-&lt;ssid&gt;</c>.</item>
    /// <item>If no node callsign is available either (running outside the supervisor), the base is
    /// <see cref="PlaceholderBase"/> and <c>IsPlaceholder</c> is <see langword="true"/>.</item>
    /// </list>
    /// <paramref name="ssid"/> outside 0–15 falls back to <see cref="DefaultSsid"/>.
    /// </summary>
    public static (string Callsign, bool IsPlaceholder) Resolve(string? overrideCallsign, string? nodeCallsign, int ssid)
    {
        if (!string.IsNullOrWhiteSpace(overrideCallsign))
        {
            return (Normalise(overrideCallsign), false);
        }

        int effectiveSsid = ssid is >= 0 and <= 15 ? ssid : DefaultSsid;

        string? nodeBase = BaseCallsign(nodeCallsign);
        if (nodeBase is { Length: > 0 })
        {
            return ($"{nodeBase}-{effectiveSsid}", false);
        }

        return ($"{PlaceholderBase}-{effectiveSsid}", true);
    }

    /// <summary>Upper-cases and trims a callsign (the wire form is upper-case ASCII).</summary>
    public static string Normalise(string callsign) =>
        callsign.Trim().ToUpperInvariant();

    /// <summary>The base callsign: everything before the first <c>-</c>, normalised. Null/blank in → null.</summary>
    public static string? BaseCallsign(string? callsign)
    {
        if (string.IsNullOrWhiteSpace(callsign))
        {
            return null;
        }

        string trimmed = callsign.Trim();
        int dash = trimmed.IndexOf('-', StringComparison.Ordinal);
        string @base = dash >= 0 ? trimmed[..dash] : trimmed;
        return @base.Length == 0 ? null : @base.ToUpperInvariant();
    }
}
