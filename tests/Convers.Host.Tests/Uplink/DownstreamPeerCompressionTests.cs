using System.Collections.Concurrent;
using Convers.Core;
using Convers.Host.Uplink;
using Convers.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Convers.Host.Tests.Uplink;

/// <summary>
/// End-to-end compression test for an accepted downstream peer: a real <see cref="DownstreamPeerSession"/>
/// over an <see cref="InMemoryCompressingLink"/> (byte-carrying, production <c>CompressingLineTransport</c>)
/// against a fake peer that speaks real Huffman frames. Proves the full negotiate → compress → decompress
/// path on the inbound host link without an oracle — the stock conversd oracle does not negotiate
/// <c>//COMP</c> on a HOST peer link (its <c>/comp</c> command is USER/OBSERVER-only), so the real-oracle
/// compressed round-trip is verified on a USER link in <c>HostLinkCompressionOracleInteropTests</c>.
/// </summary>
public class DownstreamPeerCompressionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static HostLinkOptions Options(bool offerCompression = true) => new()
    {
        HostName = "PDNCONV",
        PingInterval = TimeSpan.FromSeconds(60),
        SilenceTimeout = TimeSpan.FromSeconds(180),
        HandshakeTimeout = TimeSpan.FromSeconds(30),
        OfferCompression = offerCompression,
    };

    /// <summary>Drains the fake peer's inbound lines into a concurrent log on a background task.</summary>
    private static (ConcurrentQueue<string> Lines, Task Reader) DrainPeer(IUpstreamLink peer, CancellationToken ct)
    {
        var lines = new ConcurrentQueue<string>();
        Task reader = Task.Run(async () =>
        {
            while (true)
            {
                string? line = await peer.ReceiveLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                lines.Enqueue(line);
            }
        }, ct);
        return (lines, reader);
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
    public async Task PeerLink_NegotiatesComp_ThenContentRoundTripsCompressed()
    {
        var time = new FakeTimeProvider(T0);
        var hub = new ConversHub("PDNCONV", time);

        // The peer announced /..HOST first (the demux peeked it); hand it to "our" buffered side.
        string peerHandshake = Wire.Host("HOST GB7XYZ saupp1.62a Aadmpunfi");
        (IUpstreamLink ours, IUpstreamLink peer) = InMemoryCompressingLink.CreatePair([peerHandshake]);

        // HostLink running so the session's SnapshotAsync/SubmitPeerInbound are drained on its owning loop.
        // Fail the dial so the HostLink enters its backoff loop, which (with frozen time) parks in
        // DrainWhileWaitingAsync — the owning loop that drains the peer session's SubmitPeerInbound work.
        var factory = new ScriptedUpstreamLinkFactory().EnqueueFailure(new IOException("no uplink in this test"));
        var hostLink = new HostLink(Options(), factory, hub, time, NullLogger<HostLink>.Instance);
        using var cts = new CancellationTokenSource(Timeout);
        Task hostRun = hostLink.RunAsync(cts.Token);

        (ConcurrentQueue<string> peerLines, Task peerReader) = DrainPeer(peer, cts.Token);

        var session = new DownstreamPeerSession(
            "peer-1", ours, hostLink, hub, Options(), time, NullLogger<DownstreamPeerSession>.Instance);
        Task sessionRun = session.RunAsync(cts.Token);

        // 1) Our session replies with its /..HOST and offers //COMP 1 (swallowed by the peer's transport,
        //    which reciprocates → the peer engages compression). The /..HOST surfaces; the toggle never does.
        await WaitUntilAsync(
            () => peerLines.Any(l => HostCommandCodec.Parse(l) is HostHandshake { Hostname: "PDNCONV" }),
            "our /..HOST reply reached the peer");
        Assert.DoesNotContain(peerLines, l => l.Contains("COMP", StringComparison.OrdinalIgnoreCase));
        await WaitUntilAsync(() => peer.CompressionEngaged, "peer engaged compression on our //COMP offer");
        await WaitUntilAsync(() => ours.CompressionEngaged, "our transmit side engaged toward the peer");

        // 2) The peer announces a user — compressed on the wire (its tx armed). Our session decodes it (our
        //    rx armed on the peer's reciprocal //COMP 1) and relays it to the shared hub: a network user
        //    appears, proving the inbound compressed frame round-tripped through the codec into a plain line.
        await peer.SendLineAsync(
            HostCommandCodec.Format(new HostUser("g4abc", "GB7XYZ", 1700000000, -1, 3333, "")), cts.Token);
        await WaitUntilAsync(
            () => hub.NetworkUsers.Any(u => u.Name.Equals("g4abc", StringComparison.OrdinalIgnoreCase)),
            "peer's compressed /..USER decoded and applied to the hub");

        // 3) Reverse direction: a local user joins; the hub fans the join out to the peer, sent compressed,
        //    and the peer decodes it transparently into a plain /..USER line.
        await hostLink.SubmitLocalEventAsync(new ConversEvent.LocalJoin("s-local", "M0LTE", 3333), cts.Token);
        await WaitUntilAsync(
            () => peerLines.Any(l => HostCommandCodec.Parse(l) is HostUser { User: "M0LTE", ToChannel: 3333 }),
            "local join announced to the peer (decoded from a compressed frame)");

        await cts.CancelAsync();
        await SwallowAsync(sessionRun, peerReader, hostRun);
        await hostLink.DisposeAsync();
    }

    [Fact]
    public async Task PeerLink_WithCompressionDisabled_NeverOffers_AndStaysPlain()
    {
        var time = new FakeTimeProvider(T0);
        var hub = new ConversHub("PDNCONV", time);
        string peerHandshake = Wire.Host("HOST GB7XYZ saupp1.62a Aadmpunfi");
        (IUpstreamLink ours, IUpstreamLink peer) = InMemoryCompressingLink.CreatePair([peerHandshake]);

        // Fail the dial so the HostLink enters its backoff loop, which (with frozen time) parks in
        // DrainWhileWaitingAsync — the owning loop that drains the peer session's SubmitPeerInbound work.
        var factory = new ScriptedUpstreamLinkFactory().EnqueueFailure(new IOException("no uplink in this test"));
        var hostLink = new HostLink(
            Options(offerCompression: false), factory, hub, time, NullLogger<HostLink>.Instance);
        using var cts = new CancellationTokenSource(Timeout);
        Task hostRun = hostLink.RunAsync(cts.Token);

        (ConcurrentQueue<string> peerLines, Task peerReader) = DrainPeer(peer, cts.Token);
        var session = new DownstreamPeerSession(
            "peer-1", ours, hostLink, hub, Options(offerCompression: false), time,
            NullLogger<DownstreamPeerSession>.Instance);
        Task sessionRun = session.RunAsync(cts.Token);

        await WaitUntilAsync(
            () => peerLines.Any(l => HostCommandCodec.Parse(l) is HostHandshake { Hostname: "PDNCONV" }),
            "our /..HOST reply reached the peer (uncompressed)");
        Assert.DoesNotContain(peerLines, l => l.Contains("COMP", StringComparison.OrdinalIgnoreCase));
        Assert.False(ours.CompressionEngaged);
        Assert.False(peer.CompressionEngaged);

        // A plain /..USER round-trips with no //COMP ever exchanged — the no-regression default.
        await peer.SendLineAsync(
            HostCommandCodec.Format(new HostUser("g4abc", "GB7XYZ", 1700000000, -1, 3333, "")), cts.Token);
        await WaitUntilAsync(
            () => hub.NetworkUsers.Any(u => u.Name.Equals("g4abc", StringComparison.OrdinalIgnoreCase)),
            "peer's plain /..USER applied to the hub");
        Assert.False(ours.CompressionEngaged);

        await cts.CancelAsync();
        await SwallowAsync(sessionRun, peerReader, hostRun);
        await hostLink.DisposeAsync();
    }

    private static async Task SwallowAsync(params Task[] tasks)
    {
        foreach (Task t in tasks)
        {
            try
            {
                await t.WaitAsync(Timeout).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
            }
        }
    }
}
