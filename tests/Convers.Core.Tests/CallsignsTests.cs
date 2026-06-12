using Convers.Core;

namespace Convers.Core.Tests;

public class CallsignsTests
{
    [Theory]
    [InlineData("m0lte", "M0LTE")]
    [InlineData("  G4ABC  ", "G4ABC")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    public void Normalize_UppercasesAndTrims(string? input, string expected) =>
        Assert.Equal(expected, Callsigns.Normalize(input));

    [Theory]
    [InlineData("M0LTE", true)]
    [InlineData("DB0SAO", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("has\ttab", false)]
    public void IsValidName_RequiresNonEmptyGraphicToken(string input, bool expected) =>
        Assert.Equal(expected, Callsigns.IsValidName(input));

    [Theory]
    [InlineData("m0lte", "M0LTE", true)]
    [InlineData("M0LTE", "g4abc", false)]
    [InlineData(null, "", true)]
    public void Equal_IsCaseInsensitiveOnCanonicalForm(string? a, string? b, bool expected) =>
        Assert.Equal(expected, Callsigns.Equal(a, b));
}
