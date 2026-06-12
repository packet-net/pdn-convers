using Convers.Console;

namespace Convers.Console.Tests;

public class ConsoleVerbsTests
{
    [Theory]
    [InlineData("join", "join")]
    [InlineData("j", "join")]      // unambiguous single-letter prefix
    [InlineData("JO", "join")]     // case-insensitive
    [InlineData("w", "who")]
    [InlineData("t", "topic")]
    [InlineData("quit", "quit")]
    public void Resolve_AcceptsUnambiguousPrefixes(string input, string expected) =>
        Assert.Equal(expected, ConsoleVerbs.Resolve(input));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("x")]              // matches nothing
    [InlineData("xyz")]
    public void Resolve_ReturnsNullForNoMatch(string? input) =>
        Assert.Null(ConsoleVerbs.Resolve(input));

    [Fact]
    public void All_VerbsAreEachIndividuallyResolvable()
    {
        foreach (string verb in ConsoleVerbs.All)
        {
            Assert.Equal(verb, ConsoleVerbs.Resolve(verb));
        }
    }
}
