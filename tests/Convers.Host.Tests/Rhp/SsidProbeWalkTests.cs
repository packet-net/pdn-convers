using Convers.Host.Rhp;

namespace Convers.Host.Tests.Rhp;

/// <summary>
/// The SSID probe-walk that resolves a bind clash (design decision 4): the ordered bind candidates for a
/// preferred callsign — the preferred SSID first, then every other SSID 0–15 in increasing order
/// (wrapping past 15), so every SSID is offered exactly once.
/// </summary>
public class SsidProbeWalkTests
{
    [Fact]
    public void Candidates_StartsAtPreferredSsid_ThenWrapsThrough0To15()
    {
        string[] walk = [.. SsidProbeWalk.Candidates("M0LTE-4")];

        // The preferred form M0LTE-4 is the verbatim first entry; the SSID sweep then skips it.
        Assert.Equal(16, walk.Length);
        Assert.Equal("M0LTE-4", walk[0]);          // verbatim preferred first
        Assert.Equal("M0LTE-5", walk[1]);          // then increasing
        Assert.Equal("M0LTE-15", walk[11]);        // up to 15
        Assert.Equal("M0LTE-0", walk[12]);         // then wrap to 0
        Assert.Equal("M0LTE-3", walk[15]);         // ending just below the start

        // Every SSID 0–15 appears exactly once.
        Assert.Equal(16, walk.Distinct().Count());
    }

    [Fact]
    public void Candidates_BareCallsign_TriesBareFormFirst_ThenSsids()
    {
        string[] walk = [.. SsidProbeWalk.Candidates("GB7XYZ")];

        // The operator's exact bare form is tried before any SSID is appended.
        Assert.Equal(17, walk.Length);
        Assert.Equal("GB7XYZ", walk[0]);
        Assert.Equal("GB7XYZ-0", walk[1]);
        Assert.Equal("GB7XYZ-1", walk[2]);
        Assert.Equal("GB7XYZ-15", walk[16]);
        Assert.Equal(17, walk.Distinct().Count());
    }

    [Fact]
    public void Candidates_NormalisesToUpperCase()
    {
        string[] walk = [.. SsidProbeWalk.Candidates("m0lte-7")];
        Assert.Equal("M0LTE-7", walk[0]);
    }

    [Theory]
    [InlineData("M0LTE-99", "M0LTE", 0)]   // out-of-range SSID falls back to 0
    [InlineData("M0LTE-x", "M0LTE", 0)]    // non-numeric SSID falls back to 0
    [InlineData("M0LTE-15", "M0LTE", 15)]
    [InlineData("M0LTE", "M0LTE", 0)]
    public void Split_ParsesBaseAndStartSsid(string input, string expectedBase, int expectedSsid)
    {
        (string @base, int ssid) = SsidProbeWalk.Split(input);
        Assert.Equal(expectedBase, @base);
        Assert.Equal(expectedSsid, ssid);
    }
}
