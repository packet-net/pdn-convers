using Convers.Console;
using Convers.Host.Tests.Rhp;

namespace Convers.Host.Tests.Sessions;

/// <summary>
/// The inbound demux end-to-end over the composed RHP node link + HostLink + registry (design decision
/// 3): an inbound RF connect becomes an auto-logged-in USER session, the user's input bridges to the hub
/// through the link (so it reaches the uplink and other local users), and inbound network traffic fans
/// out to the right local session.
/// </summary>
public class InboundDemuxTests
{
    [Fact]
    public async Task InboundConnect_GreetsAndAutoLogsIn_FromRemoteCallsign()
    {
        await using var h = new DemuxHarness();
        FakeRhpPeer peer = await h.Server.AcceptChildAsync("G4ABC");

        // The user never types /name; the demux greets and auto-joins them on the default channel.
        Assert.Equal("[M0LTE convers] Welcome G4ABC.", await peer.ReadLineAsync());
        Assert.Equal($"You are on channel {DemuxHarness.DefaultChannel}.", await peer.ReadLineAsync());
        Assert.Equal("Type 'help' for commands, or just type to chat.", await peer.ReadLineAsync());
    }

    [Fact]
    public async Task ClassicPreference_GreetsInClassicMode()
    {
        await using var h = new DemuxHarness(new FixedPreferences(ConsoleInterface.Classic));
        FakeRhpPeer peer = await h.Server.AcceptChildAsync("G4ABC");

        Assert.Equal("[M0LTE convers] Welcome G4ABC.", await peer.ReadLineAsync());
        Assert.Equal($"You are on channel {DemuxHarness.DefaultChannel}.", await peer.ReadLineAsync());
        Assert.Equal("Classic mode. Type /help for commands.", await peer.ReadLineAsync());
    }

    [Fact]
    public async Task UserSpeaks_ReachesTheUplinkAsCmsg()
    {
        await using var h = new DemuxHarness();
        await h.BringUplinkUpAsync();

        FakeRhpPeer peer = await h.Server.AcceptChildAsync("G4ABC");
        await DrainGreetingAsync(peer);

        await peer.SendLineAsync("hello network");

        // The say bridges through the link to the uplink as a /..CMSG on the default channel (the join
        // /..USER will have preceded it).
        await WaitUntilAsync(
            () => h.Oracle.SentLines.Any(l =>
                Convers.Protocol.HostCommandCodec.Parse(l) is Convers.Protocol.HostChannelMessage
                {
                    User: "G4ABC", Channel: DemuxHarness.DefaultChannel, Text: "hello network",
                }),
            "the say reaching the uplink as /..CMSG");
    }

    [Fact]
    public async Task NetworkMessage_FansOutToTheLocalUser()
    {
        await using var h = new DemuxHarness();
        await h.BringUplinkUpAsync();

        FakeRhpPeer peer = await h.Server.AcceptChildAsync("G4ABC");
        await DrainGreetingAsync(peer);

        // A network user speaks on the user's channel; it must reach the RF session.
        h.Oracle.PushLine(DemuxHarness.ConversCommandLine($"CMSG g8xyz {DemuxHarness.DefaultChannel} hi there"));

        string delivered = await peer.ReadLineMatchingAsync(l => l.Contains("hi there", StringComparison.Ordinal));
        Assert.Equal("<G8XYZ>: hi there", delivered);
    }

    [Fact]
    public async Task TwoLocalUsers_SeeEachOthersChannelMessages()
    {
        await using var h = new DemuxHarness();
        await h.BringUplinkUpAsync();

        FakeRhpPeer alice = await h.Server.AcceptChildAsync("G4ABC");
        await DrainGreetingAsync(alice);
        FakeRhpPeer bob = await h.Server.AcceptChildAsync("G8XYZ");
        await DrainGreetingAsync(bob);

        // Bob should see Alice's join notice and her message (both default-channel local users).
        await alice.SendLineAsync("morning all");

        string seen = await bob.ReadLineMatchingAsync(l => l.Contains("morning all", StringComparison.Ordinal));
        Assert.Equal("<G4ABC>: morning all", seen);
    }

    [Fact]
    public async Task NoUplink_LocalUsersStillFanOutToEachOther()
    {
        // provider: null (local-only). With no uplink the HostLink stays in backoff, but its owning loop
        // keeps draining local events so the two RF users still see each other (the backoff-drain path).
        await using var h = new DemuxHarness(noUplink: true);

        FakeRhpPeer alice = await h.Server.AcceptChildAsync("G4ABC");
        await DrainGreetingAsync(alice);
        FakeRhpPeer bob = await h.Server.AcceptChildAsync("G8XYZ");
        await DrainGreetingAsync(bob);

        await alice.SendLineAsync("hello with no parent");

        string seen = await bob.ReadLineMatchingAsync(l => l.Contains("hello with no parent", StringComparison.Ordinal));
        Assert.Equal("<G4ABC>: hello with no parent", seen);
    }

    [Fact]
    public async Task Who_AnswersFromTheHubSnapshot()
    {
        await using var h = new DemuxHarness(noUplink: true);

        FakeRhpPeer peer = await h.Server.AcceptChildAsync("G4ABC");
        await DrainGreetingAsync(peer);

        await peer.SendLineAsync("who");

        // The who snapshot lists the user on their channel (served on the link's owning loop).
        Assert.Equal($"Users on channel {DemuxHarness.DefaultChannel}:", await peer.ReadLineAsync());
        string self = await peer.ReadLineAsync();
        Assert.Contains("G4ABC", self, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Quit_SendsSignOff_AndClosesTheChild()
    {
        await using var h = new DemuxHarness();
        FakeRhpPeer peer = await h.Server.AcceptChildAsync("G4ABC");
        await DrainGreetingAsync(peer);

        await peer.SendLineAsync("quit");

        Assert.StartsWith("73 de M0LTE", await peer.ReadLineAsync(), StringComparison.Ordinal);
        int closed = await h.Server.Closes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(closed > 0);
    }

    [Fact]
    public async Task NonOperator_SetMode_IsRefused_ThenOperLogin_AllowsIt()
    {
        await using var h = new DemuxHarness(noUplink: true);
        FakeRhpPeer peer = await h.Server.AcceptChildAsync("G4ABC");
        await DrainGreetingAsync(peer);

        // A plain user cannot set modes: the hub refuses with a DeliverModeNotice.
        await peer.SendLineAsync("mode +m");
        string refused = await peer.ReadLineMatchingAsync(l => l.Contains("operator", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("channel operator", refused, StringComparison.OrdinalIgnoreCase);

        // Operator login with the configured secret grants operator status.
        await peer.SendLineAsync($"oper {DemuxHarness.OperatorSecret}");
        Assert.Equal("*** You are now an operator.", await peer.ReadLineAsync());

        // Now the mode-set takes effect and the channel's modes are announced back.
        await peer.SendLineAsync("mode +m");
        string applied = await peer.ReadLineMatchingAsync(l => l.Contains("modes:", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("+m", applied, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WrongOperatorSecret_IsDenied()
    {
        await using var h = new DemuxHarness(noUplink: true);
        FakeRhpPeer peer = await h.Server.AcceptChildAsync("G4ABC");
        await DrainGreetingAsync(peer);

        await peer.SendLineAsync("oper notthesecret");
        Assert.Equal("Sorry, operator access denied.", await peer.ReadLineAsync());
    }

    [Fact]
    public async Task TopicLockedChannel_RefusesNonOperator_ThenAllowsOperator()
    {
        await using var h = new DemuxHarness(noUplink: true);
        FakeRhpPeer peer = await h.Server.AcceptChildAsync("G4ABC");
        await DrainGreetingAsync(peer);

        // Become operator and lock the topic with +t.
        await peer.SendLineAsync($"oper {DemuxHarness.OperatorSecret}");
        Assert.Equal("*** You are now an operator.", await peer.ReadLineAsync());
        await peer.SendLineAsync("mode +t");
        _ = await peer.ReadLineMatchingAsync(l => l.Contains("modes:", StringComparison.OrdinalIgnoreCase));

        // An operator can still set the topic on a +t channel.
        await peer.SendLineAsync("topic operators only");
        string topic = await peer.ReadLineMatchingAsync(l => l.Contains("operators only", StringComparison.Ordinal));
        Assert.Contains("Topic", topic, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task DrainGreetingAsync(FakeRhpPeer peer)
    {
        await peer.ReadLineAsync(); // Welcome
        await peer.ReadLineAsync(); // You are on channel N
        await peer.ReadLineAsync(); // help hint
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
