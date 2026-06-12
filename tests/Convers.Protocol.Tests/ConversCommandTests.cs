using Convers.Protocol;

namespace Convers.Protocol.Tests;

public class ConversCommandTests
{
    [Theory]
    [InlineData("/..USER g4abc HUB 123 -1 3333", true)]
    [InlineData("/..PING", true)]
    [InlineData("/..CMSG conversd 3333 hello", true)]
    [InlineData("/name g4abc 3333", false)]
    [InlineData("/who", false)]
    [InlineData("hello channel", false)]
    [InlineData("", false)]
    public void IsHostCommand_DetectsDoubledDotPrefix(string line, bool expected) =>
        Assert.Equal(expected, ConversCommand.IsHostCommand(line));

    [Theory]
    [InlineData("/name g4abc", true)]
    [InlineData("/..USER x", true)]
    [InlineData("plain chat text", false)]
    [InlineData("", false)]
    public void IsCommand_DetectsLeadingSlash(string line, bool expected) =>
        Assert.Equal(expected, ConversCommand.IsCommand(line));

    [Fact]
    public void IsHostCommand_NullIsNotACommand() =>
        Assert.False(ConversCommand.IsHostCommand(null));
}
