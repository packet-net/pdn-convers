using Convers.Host.Sessions;
using Convers.Host.Tests.Rhp;
using Convers.Protocol;

namespace Convers.Host.Tests.Sessions;

/// <summary>
/// The inbound demux's downstream-peering gate (W7c — design decisions 1, 3, 4): with peering enabled, an
/// allowlisted <c>/..HOST</c> becomes a downstream peer; a non-allowlisted <c>/..HOST</c>, or peering
/// disabled, stays an ordinary USER session (the strict-leaf default — the peeked line is replayed, not
/// lost). End-to-end over the composed RHP node link + HostLink + demux against a <see cref="FakeRhpServer"/>.
/// </summary>
public class InboundDemuxPeeringTests
{
    private const string PeerCall = "GB7XYZ";

    private static string HostLine(string body) => ConversCommand.HostCommandPrefix + body;

    private static PeeringPolicy AllowPeer(string? password = null) =>
        new(enabled: true, allow: [PeerCall], password: password);

    [Fact]
    public async Task AllowlistedHostHandshake_BecomesADownstreamPeer()
    {
        await using var h = new DemuxHarness(peering: AllowPeer());
        await h.BringUplinkUpAsync();

        FakeRhpPeer peer = await h.Server.AcceptChildAsync(PeerCall);
        await peer.SendLineAsync(HostLine("HOST GB7XYZ saupp1.62a Aadmpunfi"));

        // The node accepts it as a peer (not a user) and replies with its own /..HOST.
        await WaitUntilAsync(() => h.Link.DownstreamPeerCount == 1, "peer registered");
        string reply = await peer.ReadLineMatchingAsync(l => l.Contains("HOST", StringComparison.Ordinal));
        Assert.IsType<HostHandshake>(HostCommandCodec.Parse(reply));
    }

    [Fact]
    public async Task PeerUserAndMessage_ReachTheUplink()
    {
        await using var h = new DemuxHarness(peering: AllowPeer());
        await h.BringUplinkUpAsync();

        FakeRhpPeer peer = await h.Server.AcceptChildAsync(PeerCall);
        await peer.SendLineAsync(HostLine("HOST GB7XYZ saupp1.62a Aadmpunfi"));
        await WaitUntilAsync(() => h.Link.DownstreamPeerCount == 1, "peer registered");

        // A user behind the downstream peer joins and speaks; both reach the uplink (the golden rule).
        await peer.SendLineAsync(HostLine("USER bob GB7XYZ 1700000000 -1 3333 @"));
        await peer.SendLineAsync(HostLine("CMSG bob 3333 hello from behind the peer"));

        await WaitUntilAsync(
            () => h.Oracle.SentLines.Any(l => HostCommandCodec.Parse(l) is HostChannelMessage
            {
                Channel: 3333, Text: "hello from behind the peer",
            }),
            "the downstream user's CMSG reaching the uplink");
    }

    [Fact]
    public async Task PipelinedContentAfterHostInOneFrame_IsNotLost()
    {
        // A peer that sends /..HOST and a /..CMSG in a SINGLE frame: the demux peeks only the HOST line, but
        // the pipelined CMSG (buffered by the peek) must still be replayed to the peer session and relayed.
        await using var h = new DemuxHarness(peering: AllowPeer());
        await h.BringUplinkUpAsync();

        FakeRhpPeer peer = await h.Server.AcceptChildAsync(PeerCall);
        await peer.SendTextAsync(
            HostLine("HOST GB7XYZ saupp1.62a Aadmpunfi") + "\r" +
            HostLine("USER bob GB7XYZ 1700000000 -1 3333 @") + "\r" +
            HostLine("CMSG bob 3333 pipelined hi") + "\r");

        await WaitUntilAsync(() => h.Link.DownstreamPeerCount == 1, "peer registered");
        await WaitUntilAsync(
            () => h.Oracle.SentLines.Any(l => HostCommandCodec.Parse(l) is HostChannelMessage
            {
                Channel: 3333, Text: "pipelined hi",
            }),
            "the pipelined CMSG reaching the uplink despite arriving in the same frame as /..HOST");
    }

    [Fact]
    public async Task NonAllowlistedHostHandshake_FallsBackToUserSession()
    {
        // Peering enabled but this caller is not on the allowlist → it is treated as an ordinary user, and
        // the leading /..HOST line is just (invalid) user input (no peer is registered).
        await using var h = new DemuxHarness(peering: AllowPeer());

        FakeRhpPeer peer = await h.Server.AcceptChildAsync("M0NOPE");
        await peer.SendLineAsync(HostLine("HOST M0NOPE saupp1.62a Aadmpunfi"));

        // It is greeted as a user (the auto-login welcome), and never becomes a peer.
        Assert.Equal("[M0LTE convers] Welcome M0NOPE.", await peer.ReadLineAsync());
        Assert.Equal(0, h.Link.DownstreamPeerCount);
    }

    [Fact]
    public async Task PeeringDisabled_HostHandshakeFromAllowlistedCall_IsStillAUser()
    {
        // The default: peering off → no peek, no peer; a /..HOST is just user input.
        await using var h = new DemuxHarness();

        FakeRhpPeer peer = await h.Server.AcceptChildAsync(PeerCall);

        // Greeted immediately as a user (no first-line peek when peering is disabled).
        Assert.Equal($"[M0LTE convers] Welcome {PeerCall}.", await peer.ReadLineAsync());
        Assert.Equal(0, h.Link.DownstreamPeerCount);
    }

    [Fact]
    public async Task WrongPassword_FallsBackToUserSession()
    {
        await using var h = new DemuxHarness(peering: AllowPeer(password: "s3cr3t"));

        FakeRhpPeer peer = await h.Server.AcceptChildAsync(PeerCall);
        // No /..PASS presented (or wrong one) → not admitted as a peer.
        await peer.SendLineAsync(HostLine("HOST GB7XYZ saupp1.62a Aadmpunfi"));

        Assert.Equal($"[M0LTE convers] Welcome {PeerCall}.", await peer.ReadLineAsync());
        Assert.Equal(0, h.Link.DownstreamPeerCount);
    }

    [Fact]
    public async Task CorrectPassword_ViaPassThenHost_IsAdmitted()
    {
        await using var h = new DemuxHarness(peering: AllowPeer(password: "s3cr3t"));
        await h.BringUplinkUpAsync();

        FakeRhpPeer peer = await h.Server.AcceptChildAsync(PeerCall);
        // conversd-style: /..PASS before the /..HOST.
        await peer.SendLineAsync(HostLine("PASS s3cr3t"));
        await peer.SendLineAsync(HostLine("HOST GB7XYZ saupp1.62a Aadmpunfi"));

        await WaitUntilAsync(() => h.Link.DownstreamPeerCount == 1, "peer admitted with the password");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string what)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            if (cts.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for: {what}");
            }

            await Task.Delay(10, cts.Token);
        }
    }
}
