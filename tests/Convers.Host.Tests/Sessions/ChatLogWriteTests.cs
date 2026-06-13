using Convers.Core;
using Convers.Host.Tests.Rhp;
using Microsoft.Extensions.Time.Testing;

namespace Convers.Host.Tests.Sessions;

/// <summary>
/// The chat-log write-wiring (design decision 7): all chat the node sees — local users' channel
/// messages / PMs / presence and inbound network <c>/..CMSG</c> / <c>/..UMSG</c> / <c>/..USER</c> — is
/// persisted to the append-only <c>chatlog</c> from the Host's vantage, with origin local vs network.
/// Driven end-to-end through the composed demux + link.
/// </summary>
public sealed class ChatLogWriteTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("convers-chatlog-test-");
    private readonly ConversStore _store;

    public ChatLogWriteTests()
    {
        _store = ConversStore.Open(
            Path.Combine(_dir.FullName, "convers.db"),
            new FakeTimeProvider(new DateTimeOffset(2026, 6, 12, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task LocalUser_ChannelMessageAndJoin_AreLoggedAsLocal()
    {
        await using var h = new DemuxHarness(store: _store);
        await h.BringUplinkUpAsync();

        FakeRhpPeer peer = await h.Server.AcceptChildAsync("G4ABC");
        await DrainGreetingAsync(peer);
        await peer.SendLineAsync("hello channel");

        await WaitUntilAsync(() => _store.CountChatLog(kind: ChatLogKind.Channel) >= 1, "the local channel message logged");

        ChatLogEntry channel = _store.QueryChatLog(kind: ChatLogKind.Channel).Single();
        Assert.Equal(ChatLogOrigin.Local, channel.Origin);
        Assert.Equal("G4ABC", channel.FromCall);
        Assert.Equal(DemuxHarness.DefaultChannel, channel.Channel);
        Assert.Equal("hello channel", channel.Text);

        // The join was logged as a local presence event.
        Assert.Contains(
            _store.QueryChatLog(kind: ChatLogKind.Presence),
            p => p is { Origin: ChatLogOrigin.Local, FromCall: "G4ABC", Text: "joined" });
    }

    [Fact]
    public async Task LocalChannelMessage_IsLoggedExactlyOnce_EvenWithOtherLocalListeners()
    {
        // Two local users on the same channel: a channel message must be logged ONCE (at ingestion), not
        // once per recipient — the centralisation guarantee (no per-session call to multiply it).
        await using var h = new DemuxHarness(store: _store);
        await h.BringUplinkUpAsync();

        FakeRhpPeer speaker = await h.Server.AcceptChildAsync("G4ABC");
        await DrainGreetingAsync(speaker);
        FakeRhpPeer listener = await h.Server.AcceptChildAsync("G8XYZ");
        await DrainGreetingAsync(listener);

        await speaker.SendLineAsync("one message");

        await WaitUntilAsync(
            () => _store.CountChatLog(kind: ChatLogKind.Channel) >= 1, "the channel message logged");
        // Give any erroneous duplicate a chance to land, then assert exactly one.
        await Task.Delay(100);

        ChatLogEntry only = Assert.Single(_store.QueryChatLog(kind: ChatLogKind.Channel));
        Assert.Equal(ChatLogOrigin.Local, only.Origin);
        Assert.Equal("G4ABC", only.FromCall);
        Assert.Equal("one message", only.Text);
    }

    [Fact]
    public async Task LocalPrivateMessage_ToAnotherLocalUser_IsLoggedExactlyOnce()
    {
        await using var h = new DemuxHarness(store: _store);
        await h.BringUplinkUpAsync();

        FakeRhpPeer from = await h.Server.AcceptChildAsync("G4ABC");
        await DrainGreetingAsync(from);
        FakeRhpPeer to = await h.Server.AcceptChildAsync("G8XYZ");
        await DrainGreetingAsync(to);

        await from.SendLineAsync("msg G8XYZ secret hello");

        await WaitUntilAsync(
            () => _store.CountChatLog(kind: ChatLogKind.PrivateMessage) >= 1, "the PM logged");
        await Task.Delay(100);

        ChatLogEntry only = Assert.Single(_store.QueryChatLog(kind: ChatLogKind.PrivateMessage));
        Assert.Equal(ChatLogOrigin.Local, only.Origin);
        Assert.Equal("G4ABC", only.FromCall);
        Assert.Equal("G8XYZ", only.ToCall);
        Assert.Equal("secret hello", only.Text);
    }

    [Fact]
    public async Task NetworkTraffic_CmsgAndUmsg_AreLoggedAsNetwork()
    {
        await using var h = new DemuxHarness(store: _store);
        await h.BringUplinkUpAsync();

        FakeRhpPeer peer = await h.Server.AcceptChildAsync("G4ABC");
        await DrainGreetingAsync(peer);

        // Inbound network channel message + a PM addressed to the local user.
        h.Oracle.PushLine(DemuxHarness.ConversCommandLine($"CMSG g8xyz {DemuxHarness.DefaultChannel} from the net"));
        h.Oracle.PushLine(DemuxHarness.ConversCommandLine("UMSG g8xyz G4ABC psst"));

        await WaitUntilAsync(
            () => _store.CountChatLog(kind: ChatLogKind.Channel, channel: DemuxHarness.DefaultChannel) >= 1 &&
                  _store.CountChatLog(kind: ChatLogKind.PrivateMessage) >= 1,
            "the network CMSG and UMSG logged");

        ChatLogEntry cmsg = _store.QueryChatLog(kind: ChatLogKind.Channel, channel: DemuxHarness.DefaultChannel).Single();
        Assert.Equal(ChatLogOrigin.Network, cmsg.Origin);
        Assert.Equal("G8XYZ", cmsg.FromCall);
        Assert.Equal("from the net", cmsg.Text);

        ChatLogEntry pm = _store.QueryChatLog(kind: ChatLogKind.PrivateMessage).Single();
        Assert.Equal(ChatLogOrigin.Network, pm.Origin);
        Assert.Equal("G8XYZ", pm.FromCall);
        Assert.Equal("G4ABC", pm.ToCall);
        Assert.Equal("psst", pm.Text);
    }

    [Fact]
    public async Task ModeRefusedSay_IsNotLogged()
    {
        // A non-operator's say to a moderated (+m) channel is refused by the hub (only a notice, no
        // broadcast), so the centralised logger must NOT record it as a channel message.
        await using var h = new DemuxHarness(store: _store);
        await h.BringUplinkUpAsync();

        FakeRhpPeer peer = await h.Server.AcceptChildAsync("G4ABC");
        await DrainGreetingAsync(peer);

        // Become operator, moderate the channel, then drop operator by switching to a fresh non-op user.
        await peer.SendLineAsync($"oper {DemuxHarness.OperatorSecret}");
        await peer.ReadLineMatchingAsync(l => l.Contains("operator", StringComparison.OrdinalIgnoreCase));
        await peer.SendLineAsync("mode +m");
        await peer.ReadLineMatchingAsync(l => l.Contains("modes:", StringComparison.OrdinalIgnoreCase));

        // A separate non-operator user joins the moderated channel and tries to speak.
        FakeRhpPeer plain = await h.Server.AcceptChildAsync("G8XYZ");
        await DrainGreetingAsync(plain);
        await plain.SendLineAsync("can I speak?");
        await plain.ReadLineMatchingAsync(l => l.Contains("moderated", StringComparison.OrdinalIgnoreCase));

        // Give any erroneous log a chance to land, then assert the refused say was NOT logged.
        await Task.Delay(150);
        Assert.DoesNotContain(
            _store.QueryChatLog(kind: ChatLogKind.Channel), e => e.Text == "can I speak?");
    }

    [Fact]
    public async Task NoOpSwitch_ToTheSameChannel_DoesNotLogAPhantomJoin()
    {
        // Joining the channel you are already on is a no-op in the hub (no actions); the centralised writer
        // must NOT record a phantom "joined" presence for it (only real, announced presence is logged).
        await using var h = new DemuxHarness(store: _store);
        await h.BringUplinkUpAsync();

        FakeRhpPeer peer = await h.Server.AcceptChildAsync("G4ABC");
        await DrainGreetingAsync(peer);

        // Wait for the genuine join (on connect) to be logged: exactly one presence row so far.
        await WaitUntilAsync(
            () => _store.CountChatLog(kind: ChatLogKind.Presence) >= 1, "the real join logged");

        // Re-join the same channel — a no-op.
        await peer.SendLineAsync($"join {DemuxHarness.DefaultChannel}");
        await peer.ReadLineMatchingAsync(l => l.Contains("You are on channel", StringComparison.Ordinal));
        await Task.Delay(150); // let any erroneous phantom row land

        // Still exactly one "joined" presence row for G4ABC (the genuine one), no phantom.
        Assert.Single(_store.QueryChatLog(kind: ChatLogKind.Presence),
            p => p is { FromCall: "G4ABC", Text: "joined" });
    }

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            _dir.Delete(recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private static async Task DrainGreetingAsync(FakeRhpPeer peer)
    {
        await peer.ReadLineAsync();
        await peer.ReadLineAsync();
        await peer.ReadLineAsync();
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
