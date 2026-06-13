using Convers.Host.Uplink;
using Convers.Protocol;

namespace Convers.Host.Tests.Uplink;

/// <summary>
/// Unit tests for the sans-IO <see cref="PeerRelay"/> policy (W7c): the SPECS "golden rule" of relaying
/// content to the other connected hosts, the link-local control verbs that are never transited, and the
/// <c>/..ROUT</c> TTL decrement (the loop/TTL guard — design decision 1).
/// </summary>
public class PeerRelayTests
{
    [Theory]
    [InlineData("CMSG g4abc 3333 hello")]
    [InlineData("USER bob REMOTE 1700000000 -1 3333 @")]
    [InlineData("UMSG g4abc m0lte private")]
    [InlineData("TOPI g4abc REMOTE 1700000000 3333 the topic")]
    [InlineData("MODE 3333 +m")]
    [InlineData("AWAY bob REMOTE 1700000000 lunch")]
    [InlineData("INVI g4abc bob 3333")]
    public void Content_IsRelayedVerbatim(string body)
    {
        HostCommand inbound = HostCommandCodec.Parse(Wire.Host(body));

        HostCommand? forwarded = PeerRelay.Forwarded(inbound);

        Assert.NotNull(forwarded);
        Assert.Equal(inbound, forwarded);
    }

    [Theory]
    [InlineData("PING")]
    [InlineData("PONG 3")]
    [InlineData("HOST GB7XYZ saupp1.62a Aadmpunfi")]
    [InlineData("LOOP ORACLE PDNCONV bob HOST")]
    [InlineData("SYSI g4abc PDNCONV")]
    [InlineData("ROUT FARHOST g4abc 5")] // route queries are answered per-node, never transited (loop-safe)
    [InlineData("ROUT FARHOST g4abc 0")]
    public void LinkLocalControl_IsNotRelayed(string body)
    {
        HostCommand inbound = HostCommandCodec.Parse(Wire.Host(body));

        Assert.Null(PeerRelay.Forwarded(inbound));
    }

    [Fact]
    public void UnknownVerb_IsRelayedVerbatim()
    {
        HostCommand inbound = HostCommandCodec.Parse(Wire.Host("WIBBLE some payload here"));

        Assert.Equal(inbound, PeerRelay.Forwarded(inbound));
    }
}
