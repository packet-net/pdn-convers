using Convers.Host.Sessions;

namespace Convers.Host.Tests.Sessions;

/// <summary>
/// Unit tests for the <see cref="PeeringPolicy"/> admission rules (W7c — design decisions 1 and 4): the
/// off-by-default strict-leaf posture, the explicit callsign allowlist (the conversd <c>Access … HOST</c>
/// mirror, SSID-agnostic), and the optional shared link password (the host.c <c>check_password</c>
/// substring convention).
/// </summary>
public class PeeringPolicyTests
{
    [Fact]
    public void Disabled_AdmitsNoOne()
    {
        Assert.False(PeeringPolicy.Disabled.Enabled);
        Assert.False(PeeringPolicy.Disabled.IsAllowed("GB7XYZ"));
    }

    [Fact]
    public void Enabled_EmptyAllowlist_FailsClosed()
    {
        var policy = new PeeringPolicy(enabled: true, allow: [], password: null);

        Assert.True(policy.Enabled);
        Assert.False(policy.IsAllowed("GB7XYZ"));
    }

    [Fact]
    public void Allowlist_AdmitsExactCallsign_CaseInsensitively()
    {
        var policy = new PeeringPolicy(enabled: true, allow: ["GB7XYZ-1"], password: null);

        Assert.True(policy.IsAllowed("gb7xyz-1"));
        Assert.False(policy.IsAllowed("GB7XYZ-2"));
    }

    [Fact]
    public void BareBaseAllowlistEntry_AdmitsAnySsidOfThatBase()
    {
        // conversd Access is per-host: a base entry admits any SSID of that station.
        var policy = new PeeringPolicy(enabled: true, allow: ["GB7XYZ"], password: null);

        Assert.True(policy.IsAllowed("GB7XYZ"));
        Assert.True(policy.IsAllowed("GB7XYZ-1"));
        Assert.True(policy.IsAllowed("gb7xyz-7"));
        Assert.False(policy.IsAllowed("GB7ABC"));
    }

    [Fact]
    public void NoPassword_AlwaysPasses()
    {
        var policy = new PeeringPolicy(enabled: true, allow: ["GB7XYZ"], password: null);

        Assert.False(policy.RequiresPassword);
        Assert.True(policy.PasswordOk(null));
        Assert.True(policy.PasswordOk("anything"));
    }

    [Fact]
    public void Password_RequiresSubstringMatch_AndRejectsEmpty()
    {
        var policy = new PeeringPolicy(enabled: true, allow: ["GB7XYZ"], password: "s3cr3t");

        Assert.True(policy.RequiresPassword);
        Assert.False(policy.PasswordOk(null));
        Assert.False(policy.PasswordOk(""));
        Assert.False(policy.PasswordOk("wrong"));
        Assert.True(policy.PasswordOk("s3cr3t"));
        Assert.True(policy.PasswordOk("prefix s3cr3t suffix")); // verify_password uses strstr()
    }
}
