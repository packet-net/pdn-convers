using Convers.Core;

namespace Convers.Host.Sessions;

/// <summary>
/// The downstream-peering admission policy (W7c — design decisions 1 and 4): whether the node accepts an
/// inbound <c>/..HOST</c> as a downstream peer at all, the explicit callsign allowlist that gates it
/// (mirroring conversd <c>Access … HOST</c>), and the optional shared link password (host.c
/// <c>check_password</c>). <b>Off by default</b> — with peering disabled the node stays a strict leaf and a
/// leading <c>/..HOST</c> is treated as ordinary (invalid) user input, exactly as before (design decision
/// 3). Sans-IO and immutable; the admission decisions are pure functions of the connecting callsign and
/// any presented password.
/// </summary>
public sealed class PeeringPolicy
{
    private readonly HashSet<string> _allow;
    private readonly string? _password;

    /// <summary>The disabled policy — no inbound peers (the strict-leaf default).</summary>
    public static readonly PeeringPolicy Disabled = new(enabled: false, allow: [], password: null);

    /// <summary>
    /// Creates a policy. When <paramref name="enabled"/> is false the rest is ignored. The
    /// <paramref name="allow"/> callsigns are normalised (case/whitespace) for matching; an empty allowlist
    /// admits no one even when enabled (fail-closed). <paramref name="password"/> blank/null means no
    /// password is required.
    /// </summary>
    public PeeringPolicy(bool enabled, IEnumerable<string> allow, string? password)
    {
        ArgumentNullException.ThrowIfNull(allow);
        Enabled = enabled;
        _allow = new HashSet<string>(StringComparer.Ordinal);
        foreach (string call in allow)
        {
            if (!string.IsNullOrWhiteSpace(call))
            {
                _allow.Add(Callsigns.Normalize(call));
            }
        }

        _password = string.IsNullOrEmpty(password) ? null : password;
    }

    /// <summary>Whether downstream peering is enabled at all.</summary>
    public bool Enabled { get; }

    /// <summary>Whether a shared link password is required.</summary>
    public bool RequiresPassword => _password is not null;

    /// <summary>
    /// True when <paramref name="remoteCallsign"/> is on the explicit allowlist (the <c>Access … HOST</c>
    /// mirror). Matches on the base+SSID callsign the RHP layer authenticated. Always false when disabled.
    /// </summary>
    public bool IsAllowed(string remoteCallsign)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(remoteCallsign))
        {
            return false;
        }

        string call = Callsigns.Normalize(remoteCallsign);
        if (_allow.Contains(call))
        {
            return true;
        }

        // Also accept a bare-base allowlist entry matching the connecting station's base callsign, so an
        // entry of "GB7XYZ" admits "GB7XYZ-1" (the conversd Access model is per-host, SSID-agnostic).
        string? @base = ConversIdentity.BaseCallsign(call);
        return @base is not null && _allow.Contains(@base);
    }

    /// <summary>
    /// True when <paramref name="presented"/> satisfies the configured password. No password configured →
    /// always true. Mirrors conversd <c>verify_password</c>: the configured secret must be a substring of
    /// what the peer presented (the "hide password in a long answer" convention), and an empty presentation
    /// fails when a password is required.
    /// </summary>
    public bool PasswordOk(string? presented)
    {
        if (_password is null)
        {
            return true;
        }

        return !string.IsNullOrEmpty(presented) && presented.Contains(_password, StringComparison.Ordinal);
    }
}
