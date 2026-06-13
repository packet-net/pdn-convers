using Convers.Console;

namespace Convers.Console.Tests;

/// <summary>
/// Parsing of the W7b commands — <c>mode</c> (show/set channel modes) and <c>oper</c> (operator login)
/// — across both the plain-language and classic surfaces. Both converge on the same intents.
/// </summary>
public class ConsoleParserModeOperTests
{
    [Fact]
    public void Plain_BareMode_ShowsCurrentChannel()
    {
        var intent = Assert.IsType<ConsoleIntent.Mode>(ConsoleParser.ParsePlain("mode"));
        Assert.Null(intent.Channel);
        Assert.Equal(string.Empty, intent.Options);
    }

    [Theory]
    [InlineData("mode +mt", "+mt")]
    [InlineData("mode -s", "-s")]
    [InlineData("mode +i-l", "+i-l")]
    public void Plain_ModeToggle_OnCurrentChannel(string line, string options)
    {
        var intent = Assert.IsType<ConsoleIntent.Mode>(ConsoleParser.ParsePlain(line));
        Assert.Null(intent.Channel);
        Assert.Equal(options, intent.Options);
    }

    [Theory]
    [InlineData("mode 3333 +mt", 3333, "+mt")]
    [InlineData("mode #256 -s", 256, "-s")]
    public void Plain_ModeWithChannel_TargetsThatChannel(string line, int channel, string options)
    {
        var intent = Assert.IsType<ConsoleIntent.Mode>(ConsoleParser.ParsePlain(line));
        Assert.Equal(channel, intent.Channel);
        Assert.Equal(options, intent.Options);
    }

    [Fact]
    public void Plain_ModePrefix_IsUnambiguous_BecauseMsgSharesM()
    {
        // 'm' alone is ambiguous (msg/mode) so it falls through to chat; 'mo' resolves to mode.
        Assert.IsType<ConsoleIntent.Say>(ConsoleParser.ParsePlain("m +mt"));
        Assert.IsType<ConsoleIntent.Mode>(ConsoleParser.ParsePlain("mo +mt"));
    }

    [Theory]
    [InlineData("/mode 3333 +mt", 3333, "+mt")]
    [InlineData("/MOde #256 -s", 256, "-s")]
    [InlineData("/mo +m", null, "+m")]
    public void Classic_Mode_Parses(string line, int? channel, string options)
    {
        var intent = Assert.IsType<ConsoleIntent.Mode>(ConsoleParser.ParseClassic(line));
        Assert.Equal(channel, intent.Channel);
        Assert.Equal(options, intent.Options);
    }

    [Theory]
    [InlineData("oper letmein", "letmein")]
    [InlineData("o s3cret", "s3cret")]   // 'o' is unique to oper in plain mode
    public void Plain_Oper_CarriesSecret(string line, string secret)
    {
        var intent = Assert.IsType<ConsoleIntent.Oper>(ConsoleParser.ParsePlain(line));
        Assert.Equal(secret, intent.Secret);
    }

    [Fact]
    public void Plain_BareOper_HasEmptySecret()
    {
        var intent = Assert.IsType<ConsoleIntent.Oper>(ConsoleParser.ParsePlain("oper"));
        Assert.Equal(string.Empty, intent.Secret);
    }

    [Theory]
    [InlineData("/oper letmein")]
    [InlineData("/op letmein")]
    [InlineData("/operator letmein")]
    [InlineData("/sysop letmein")]
    [InlineData("/sy letmein")]
    public void Classic_OperAliases_CarrySecret(string line)
    {
        var intent = Assert.IsType<ConsoleIntent.Oper>(ConsoleParser.ParseClassic(line));
        Assert.Equal("letmein", intent.Secret);
    }
}
