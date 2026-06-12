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
