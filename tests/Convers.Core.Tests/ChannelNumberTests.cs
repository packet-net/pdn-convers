using Convers.Core;

namespace Convers.Core.Tests;

public class ChannelNumberTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(3333, true)]
    [InlineData(32767, true)]
    [InlineData(-1, false)]
    [InlineData(32768, false)]
    public void IsValid_HonoursTheZeroTo32767Range(int channel, bool expected) =>
        Assert.Equal(expected, ChannelNumber.IsValid(channel));

    [Theory]
    [InlineData(0, false)]       // the random/special channel is not a public default
    [InlineData(255, false)]     // below the reserved public floor
    [InlineData(256, true)]
    [InlineData(3333, true)]     // the shipped placeholder default
    [InlineData(32767, true)]
    [InlineData(40000, false)]
    public void IsPublicDefault_RequiresThe256To32767Range(int channel, bool expected) =>
        Assert.Equal(expected, ChannelNumber.IsPublicDefault(channel));
}
