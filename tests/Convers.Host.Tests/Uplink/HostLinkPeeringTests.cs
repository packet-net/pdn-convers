using Convers.Core;
using Convers.Host.Uplink;
using Convers.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Convers.Host.Tests.Uplink;

/// <summary>
/// Integration tests for downstream peering (W7c) over scripted transports: a <see cref="HostLink"/> (the
/// uplink) with an attached <see cref="DownstreamPeerSession"/>, exercising the SPECS golden rule (relay a
/// peer's traffic to the OTHER hosts), the loop guard (no echo back to a peer's origin), and the bridge to
/// local sessions. Time is a <see cref="FakeTimeProvider"/> so the link timers are deterministic.
/// </summary>
public class HostLinkPeeringTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly string OracleHandshake = Wire.Host("HOST ORACLE saupp1.62a Aadmpunfi");
    private static readonly string PeerHandshake = Wire.Host("HOST GB7XYZ saupp1.62a Aadmpunfi");

    private static HostLinkOptions Options() => new()
    {
        HostName = "PDNCONV",
        PingInterval = TimeSpan.FromSeconds(60),
        SilenceTimeout = TimeSpan.FromSeconds(180),
        HandshakeTimeout = TimeSpan.FromSeconds(30),
        InitialBackoff = TimeSpan.FromSeconds(1),
        MaxBackoff = TimeSpan.FromSeconds(60),
    };

    private sealed record Harness(
        HostLink Link,
        ConversHub Hub,
        RecordingLocalDelivery Local,
        ScriptedUpstreamLink Uplink,
        FakeTimeProvider Time,
        CancellationTokenSource Cts,
        Task LinkRun) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Cts.CancelAsync();
            try
            {
                await LinkRun.WaitAsync(Timeout);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
            }

            await Link.DisposeAsync();
            Cts.Dispose();
        }
    }

    private static async Task<Harness> StartUplinkUpAsync()
    {
        var time = new FakeTimeProvider(T0);
        var hub = new ConversHub("PDNCONV", time);
        var local = new RecordingLocalDelivery();
        var uplink = new ScriptedUpstreamLink();
        var factory = new ScriptedUpstreamLinkFactory().EnqueueLink(uplink);
        var link = new HostLink(Options(), factory, hub, time, NullLogger<HostLink>.Instance, local);
        var cts = new CancellationTokenSource();
        Task run = link.RunAsync(cts.Token);

        _ = await uplink.ReadSentAsync(Timeout); // our /..HOST up
        uplink.PushLine(OracleHandshake);
        await link.WaitForUpAsync(new CancellationTokenSource(Timeout).Token);
        return new Harness(link, hub, local, uplink, time, cts, run);
    }

    /// <summary>Attaches a downstream peer (its own scripted transport + session), brings it up, returns the transport + session task.</summary>
    private static async Task<(ScriptedUpstreamLink Transport, Task Run)> AttachPeerAsync(Harness h)
    {
        var transport = new ScriptedUpstreamLink();
        var session = new DownstreamPeerSession(
            "peer-1", transport, h.Link, h.Hub, Options(), h.Time, NullLogger<DownstreamPeerSession>.Instance);
        Task run = session.RunAsync(h.Cts.Token);

        // The peer announces first; we reply with our /..HOST and the peer is registered.
        transport.PushLine(PeerHandshake);
        await WaitUntilAsync(() => h.Link.DownstreamPeerCount == 1, "peer registered");
        await WaitUntilAsync(
            () => transport.SentLines.Any(l => HostCommandCodec.Parse(l) is HostHandshake { Hostname: "PDNCONV" }),
            "our /..HOST reply to the peer");
        return (transport, run);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string what)
    {
        using var cts = new CancellationTokenSource(Timeout);
        while (!predicate())
        {
            if (cts.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for: {what}");
            }

            await Task.Delay(10, cts.Token);
        }
    }

    [Fact]
    public async Task PeerHandshake_RegistersPeer_AndAnnouncesLocalPresence()
    {
        await using Harness h = await StartUplinkUpAsync();

        // A local user exists before the peer attaches → the peer should be told about them on join.
        await h.Link.SubmitLocalEventAsync(new ConversEvent.LocalJoin("s1", "M0LTE", 3333), CancellationToken.None);
        await WaitUntilAsync(
            () => h.Uplink.SentLines.Any(l => HostCommandCodec.Parse(l) is HostUser { User: "M0LTE", ToChannel: 3333 }),
            "local join applied");

        (ScriptedUpstreamLink peer, _) = await AttachPeerAsync(h);

        // The freshly-established peer is announced our current presence (the local user).
        await WaitUntilAsync(
            () => peer.SentLines.Any(l => HostCommandCodec.Parse(l) is HostUser { User: "M0LTE", ToChannel: 3333 }),
            "local presence announced to the new peer");
    }

    [Fact]
    public async Task PeerCmsg_ReachesUplink_AndLocalSession_ButNotEchoedBackToPeer()
    {
        await using Harness h = await StartUplinkUpAsync();

        // A local user on 3333 to receive the peer's message.
        await h.Link.SubmitLocalEventAsync(new ConversEvent.LocalJoin("s1", "M0LTE", 3333), CancellationToken.None);
        await WaitUntilAsync(
            () => h.Uplink.SentLines.Any(l => HostCommandCodec.Parse(l) is HostUser { User: "M0LTE", ToChannel: 3333 }),
            "local join applied");

        (ScriptedUpstreamLink peer, _) = await AttachPeerAsync(h);
        int uplinkBefore = h.Uplink.SentLines.Count;

        // The downstream peer's user speaks on 3333.
        peer.PushLine(Wire.Host("CMSG g4abc 3333 hi from downstream"));

        // Golden rule: it reaches the uplink (the OTHER host)...
        await WaitUntilAsync(
            () => h.Uplink.SentLines.Skip(uplinkBefore).Any(l =>
                HostCommandCodec.Parse(l) is HostChannelMessage { Channel: 3333, Text: "hi from downstream" }),
            "peer CMSG relayed to the uplink");

        // ...and the local session on that channel.
        await WaitUntilAsync(
            () => h.Local.Actions.OfType<ConversAction.DeliverChannelMessage>()
                .Any(a => a is { SessionId: "s1", Channel: 3333, Text: "hi from downstream" }),
            "peer CMSG delivered to the local session");

        // Loop guard: it is NOT echoed back to the originating peer.
        await Task.Delay(100);
        Assert.DoesNotContain(
            peer.SentLines,
            l => HostCommandCodec.Parse(l) is HostChannelMessage { Text: "hi from downstream" });
    }

    [Fact]
    public async Task UplinkCmsg_ReachesDownstreamPeer()
    {
        await using Harness h = await StartUplinkUpAsync();
        (ScriptedUpstreamLink peer, _) = await AttachPeerAsync(h);
        int peerBefore = peer.SentLines.Count;

        // The upstream network sends a channel message; the downstream peer must learn it (the golden rule
        // the other direction — uplink → the other host).
        h.Uplink.PushLine(Wire.Host("CMSG g8xyz 3333 hello downstream"));

        await WaitUntilAsync(
            () => peer.SentLines.Skip(peerBefore).Any(l =>
                HostCommandCodec.Parse(l) is HostChannelMessage { Channel: 3333, Text: "hello downstream" }),
            "uplink CMSG relayed to the downstream peer");
    }

    [Fact]
    public async Task LocalUserSay_ReachesBothUplinkAndPeer()
    {
        await using Harness h = await StartUplinkUpAsync();
        (ScriptedUpstreamLink peer, _) = await AttachPeerAsync(h);

        await h.Link.SubmitLocalEventAsync(new ConversEvent.LocalJoin("s1", "M0LTE", 3333), CancellationToken.None);
        await WaitUntilAsync(
            () => peer.SentLines.Any(l => HostCommandCodec.Parse(l) is HostUser { User: "M0LTE", ToChannel: 3333 }),
            "local join fanned to the peer");

        await h.Link.SubmitLocalEventAsync(new ConversEvent.LocalSay("s1", "evening all"), CancellationToken.None);

        // A local say goes BOTH up the uplink and down to the peer.
        await WaitUntilAsync(
            () => h.Uplink.SentLines.Any(l => HostCommandCodec.Parse(l) is HostChannelMessage { Text: "evening all" }),
            "local say up the uplink");
        await WaitUntilAsync(
            () => peer.SentLines.Any(l => HostCommandCodec.Parse(l) is HostChannelMessage { Text: "evening all" }),
            "local say down to the peer");
    }

    [Fact]
    public async Task UplinkPing_DoesNotLeakAPongToDownstreamPeers()
    {
        // The uplink's keepalive /..PING becomes a hub SendPong answered up the UPLINK only — it must NOT be
        // fanned out to downstream peers (each peer's keepalive is answered by its own session engine). A
        // regression guard for the per-link PONG leak.
        await using Harness h = await StartUplinkUpAsync();
        (ScriptedUpstreamLink peer, _) = await AttachPeerAsync(h);
        int peerBefore = peer.SentLines.Count;

        h.Uplink.PushLine(Wire.Host("PING"));

        // The uplink gets a PONG back...
        await WaitUntilAsync(
            () => h.Uplink.SentLines.Any(l => HostCommandCodec.Parse(l) is HostPong), "PONG answered up the uplink");

        // ...but the downstream peer never receives a PONG (nor any new line from this).
        await Task.Delay(100);
        Assert.DoesNotContain(peer.SentLines.Skip(peerBefore), l => HostCommandCodec.Parse(l) is HostPong);
    }

    [Fact]
    public async Task PeerDisconnect_Unregisters()
    {
        await using Harness h = await StartUplinkUpAsync();
        (ScriptedUpstreamLink peer, Task run) = await AttachPeerAsync(h);

        peer.Close(); // the peer hangs up
        await WaitUntilAsync(() => h.Link.DownstreamPeerCount == 0, "peer unregistered on disconnect");
        await run.WaitAsync(Timeout);
    }
}
