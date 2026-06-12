using Convers.Host;

namespace Convers.Host.Tests;

public class ConversIdentityTests
{
    [Theory]
    [InlineData("M0LTE-1", 4, "M0LTE-4")]    // strip the node SSID, append ours
    [InlineData("M0LTE", 4, "M0LTE-4")]      // node has no SSID
    [InlineData("g0abc-9", 7, "G0ABC-7")]    // upper-cased
    [InlineData("M0LTE-1", 0, "M0LTE-0")]
    [InlineData("M0LTE-1", 15, "M0LTE-15")]
    public void Resolve_DerivesNodeBasePlusSsid(string nodeCallsign, int ssid, string expected)
    {
        (string callsign, bool placeholder) = ConversIdentity.Resolve(null, nodeCallsign, ssid);

        Assert.Equal(expected, callsign);
        Assert.False(placeholder);
    }

    [Theory]
    [InlineData("G7XYZ-2")]
    [InlineData(" g7xyz-2 ")]   // trimmed + upper-cased to G7XYZ-2
    public void Resolve_ExplicitOverrideWinsVerbatim(string overrideCallsign)
    {
        (string callsign, bool placeholder) = ConversIdentity.Resolve(overrideCallsign, "M0LTE-1", 4);

        Assert.Equal("G7XYZ-2", callsign);   // node callsign + ssid ignored
        Assert.False(placeholder);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_NoNodeAndNoOverride_IsPlaceholder(string? nodeCallsign)
    {
        (string callsign, bool placeholder) = ConversIdentity.Resolve(null, nodeCallsign, 4);

        Assert.Equal("N0CALL-4", callsign);
        Assert.True(placeholder);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(16)]
    [InlineData(99)]
    public void Resolve_OutOfRangeSsid_FallsBackToDefault(int ssid)
    {
        (string callsign, _) = ConversIdentity.Resolve(null, "M0LTE-1", ssid);

        Assert.Equal($"M0LTE-{ConversIdentity.DefaultSsid}", callsign);
    }

    [Theory]
    [InlineData("M0LTE-1", "M0LTE")]
    [InlineData("M0LTE", "M0LTE")]
    [InlineData("g0abc-12", "G0ABC")]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void BaseCallsign_StripsSsidAndNormalises(string? input, string? expected) =>
        Assert.Equal(expected, ConversIdentity.BaseCallsign(input));
}
