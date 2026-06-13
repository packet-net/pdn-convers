using Convers.Core;
using Convers.Host.Uplink;
using Convers.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Convers.Host.Tests.Uplink;

/// <summary>
/// Integration tests for the I/O-owning <see cref="HostLink"/> driven over the
/// <see cref="ScriptedUpstreamLink"/> fake transport: the full <c>/..HOST</c> handshake, presence bridging
/// both ways, keepalive, and reconnect/backoff with presence replay and fault-on-loss (mirroring pdn-bbs
/// <c>RhpNodeLink</c>). Time is a <see cref="FakeTimeProvider"/> so backoff and keepalive are deterministic.
/// </summary>
public class HostLinkTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly string OracleHandshake = Wire.Host("HOST ORACLE saupp1.62a Aadmpunfi");

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
        ScriptedUpstreamLinkFactory Factory,
        RecordingLocalDelivery Local,
        FakeTimeProvider Time,
        CancellationTokenSource Cts,
        Task Run) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Cts.CancelAsync();
            try
            {
                await Run.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                // expected on teardown
            }

            await Link.DisposeAsync();
            Cts.Dispose();
        }
    }

    private static Harness Start(ScriptedUpstreamLinkFactory factory)
    {
        var time = new FakeTimeProvider(T0);
        var hub = new ConversHub("PDNCONV", time);
        var local = new RecordingLocalDelivery();
        var link = new HostLink(Options(), factory, hub, time, NullLogger<HostLink>.Instance, local);
        var cts = new CancellationTokenSource();
        Task run = link.RunAsync(cts.Token);
        return new Harness(link, hub, factory, local, time, cts, run);
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

    /// <summary>
    /// Advances fake time in <paramref name="step"/>s until <paramref name="predicate"/> holds, yielding
    /// real time between steps. A single <c>Time.Advance</c> can race the background
    /// <see cref="HostLink.RunAsync"/> loop reaching its <c>Task.Delay(backoff, time)</c> await — under CPU
    /// contention the advance fires before the delay registers, so the delay (set relative to the already-
    /// advanced clock) never releases and the loop wedges. Poll-advancing closes that race: whenever the
    /// loop does reach the delay, a later step releases it (mirrors pdn-bbs <c>HostHarness.AdvanceUntilAsync</c>).
    /// </summary>
    private static async Task AdvanceUntilAsync(FakeTimeProvider time, Func<bool> predicate, string what, TimeSpan step)
    {
        DateTime realDeadline = DateTime.UtcNow + Timeout;
        while (!predicate())
        {
            if (DateTime.UtcNow > realDeadline)
            {
                throw new TimeoutException($"Timed out advancing until: {what}");
            }

            time.Advance(step);
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Handshake_SendsHostThenComesUp_OnOracleReply()
    {
        var oracle = new ScriptedUpstreamLink();
        var factory = new ScriptedUpstreamLinkFactory().EnqueueLink(oracle);
        await using Harness h = Start(factory);

        // The link dials and sends our /..HOST.
        string sent = await oracle.ReadSentAsync(Timeout);
        Assert.StartsWith(ConversCommand.HostCommandPrefix, sent);
        var hs = Assert.IsType<HostHandshake>(HostCommandCodec.Parse(sent));
        Assert.Equal("PDNCONV", hs.Hostname);
        Assert.False(h.Link.IsUp);

        // Parent replies → link comes up.
        oracle.PushLine(OracleHandshake);
        await h.Link.WaitForUpAsync(new CancellationTokenSource(Timeout).Token);
        Assert.True(h.Link.IsUp);
    }

    [Fact]
    public async Task InboundCmsg_FansOutToLocalSession()
    {
        var oracle = new ScriptedUpstreamLink();
        await using Harness h = Start(new ScriptedUpstreamLinkFactory().EnqueueLink(oracle));

        // A local user is on channel 3333 (W5's demux would do this; here we drive the hub directly).
        await h.Link.SubmitLocalEventAsync(
            new ConversEvent.LocalJoin("s1", "M0LTE", 3333), CancellationToken.None);

        oracle.PushLine(OracleHandshake);
        await h.Link.WaitForUpAsync(new CancellationTokenSource(Timeout).Token);

        // The network sends a channel message; it must reach the local session.
        oracle.PushLine(Wire.Host("CMSG g4abc 3333 hello leaf"));

        await WaitUntilAsync(
            () => h.Local.Actions.OfType<ConversAction.DeliverChannelMessage>().Any(),
            "DeliverChannelMessage to local session");

        var delivered = h.Local.Actions.OfType<ConversAction.DeliverChannelMessage>().Single();
        Assert.Equal("s1", delivered.SessionId);
        // The hub canonicalises callsigns (upper-cased), so the sender comes back normalised.
        Assert.Equal("G4ABC", delivered.FromUser);
        Assert.Equal("hello leaf", delivered.Text);
    }

    [Fact]
    public async Task LocalJoin_AnnouncesUserUpstream()
    {
        var oracle = new ScriptedUpstreamLink();
        await using Harness h = Start(new ScriptedUpstreamLinkFactory().EnqueueLink(oracle));

        // Complete the handshake first.
        _ = await oracle.ReadSentAsync(Timeout);
        oracle.PushLine(OracleHandshake);
        await h.Link.WaitForUpAsync(new CancellationTokenSource(Timeout).Token);

        // A local user joins → a /..USER goes upstream.
        await h.Link.SubmitLocalEventAsync(
            new ConversEvent.LocalJoin("s1", "M0LTE", 3333), CancellationToken.None);

        await WaitUntilAsync(
            () => oracle.SentLines.Any(l => HostCommandCodec.Parse(l) is HostUser { ToChannel: 3333 }),
            "a /..USER join sent upstream");

        var user = oracle.SentLines
            .Select(HostCommandCodec.Parse).OfType<HostUser>()
            .First(u => u.ToChannel == 3333);
        Assert.Equal("M0LTE", user.User);
        Assert.Equal("PDNCONV", user.Host);
        Assert.Equal(-1, user.FromChannel);
    }

    [Fact]
    public async Task KeepalivePing_IsSent_AfterPingInterval()
    {
        var oracle = new ScriptedUpstreamLink();
        await using Harness h = Start(new ScriptedUpstreamLinkFactory().EnqueueLink(oracle));

        _ = await oracle.ReadSentAsync(Timeout);
        oracle.PushLine(OracleHandshake);
        await h.Link.WaitForUpAsync(new CancellationTokenSource(Timeout).Token);

        // Advance past the ping interval; the periodic tick (TimeProvider-backed) fires a /..PING.
        h.Time.Advance(TimeSpan.FromSeconds(61));

        await WaitUntilAsync(
            () => oracle.SentLines.Any(l => HostCommandCodec.Parse(l) is HostPing),
            "a keepalive /..PING after the interval");
    }

    [Fact]
    public async Task SilenceTimeout_DropsAndReconnects_WithFreshHandshake()
    {
        var first = new ScriptedUpstreamLink();
        var second = new ScriptedUpstreamLink();
        var factory = new ScriptedUpstreamLinkFactory().EnqueueLink(first).EnqueueLink(second);
        await using Harness h = Start(factory);

        _ = await first.ReadSentAsync(Timeout);
        first.PushLine(OracleHandshake);
        await h.Link.WaitForUpAsync(new CancellationTokenSource(Timeout).Token);

        // No traffic for the whole silence window → drop, then backoff, then redial.
        h.Time.Advance(TimeSpan.FromSeconds(181)); // trip the silence timeout
        await WaitUntilAsync(() => first.Disposed, "first link torn down on silence");

        // The link is now down; advance past the initial backoff to let it redial the second link.
        await WaitUntilAsync(() => !h.Link.IsUp, "link marked down");
        await AdvanceUntilAsync(h.Time, () => factory.ConnectAttempts >= 2, "redial after silence", TimeSpan.FromSeconds(1));

        // The second connection re-runs the handshake.
        string sent = await second.ReadSentAsync(Timeout);
        Assert.IsType<HostHandshake>(HostCommandCodec.Parse(sent));
        Assert.True(factory.ConnectAttempts >= 2);
    }

    [Fact]
    public async Task PeerHangup_FaultsAndReconnects()
    {
        var first = new ScriptedUpstreamLink();
        var second = new ScriptedUpstreamLink();
        var factory = new ScriptedUpstreamLinkFactory().EnqueueLink(first).EnqueueLink(second);
        await using Harness h = Start(factory);

        _ = await first.ReadSentAsync(Timeout);
        first.PushLine(OracleHandshake);
        await h.Link.WaitForUpAsync(new CancellationTokenSource(Timeout).Token);

        // Parent hangs up (the receive loop sees a null line).
        first.Close();
        await WaitUntilAsync(() => first.Disposed, "first link disposed after hangup");
        await WaitUntilAsync(() => !h.Link.IsUp, "link marked down after hangup");

        await AdvanceUntilAsync(h.Time, () => factory.ConnectAttempts >= 2, "redial after hangup", TimeSpan.FromSeconds(1));
        string sent = await second.ReadSentAsync(Timeout);
        Assert.IsType<HostHandshake>(HostCommandCodec.Parse(sent));
    }

    [Fact]
    public async Task Reconnect_ReplaysLocalPresenceUpstream()
    {
        var first = new ScriptedUpstreamLink();
        var second = new ScriptedUpstreamLink();
        var factory = new ScriptedUpstreamLinkFactory().EnqueueLink(first).EnqueueLink(second);
        await using Harness h = Start(factory);

        // Up, with a local user joined.
        _ = await first.ReadSentAsync(Timeout);
        first.PushLine(OracleHandshake);
        await h.Link.WaitForUpAsync(new CancellationTokenSource(Timeout).Token);
        await h.Link.SubmitLocalEventAsync(
            new ConversEvent.LocalJoin("s1", "M0LTE", 3333), CancellationToken.None);
        await WaitUntilAsync(
            () => first.SentLines.Any(l => HostCommandCodec.Parse(l) is HostUser { ToChannel: 3333 }),
            "initial join announced");

        // Drop the link; reconnect.
        first.Close();
        await WaitUntilAsync(() => !h.Link.IsUp, "down after hangup");
        await AdvanceUntilAsync(h.Time, () => factory.ConnectAttempts >= 2, "redial for replay", TimeSpan.FromSeconds(1));
        _ = await second.ReadSentAsync(Timeout); // the new handshake
        second.PushLine(OracleHandshake);
        await h.Link.WaitForUpAsync(new CancellationTokenSource(Timeout).Token);

        // On the new connection the link replays the still-present local user as a /..USER join.
        await WaitUntilAsync(
            () => second.SentLines.Any(l =>
                HostCommandCodec.Parse(l) is HostUser { User: "M0LTE", ToChannel: 3333, FromChannel: -1 }),
            "local presence replayed on the new link");
    }

    [Fact]
    public async Task FailedDial_BacksOff_ThenSucceeds()
    {
        var oracle = new ScriptedUpstreamLink();
        var factory = new ScriptedUpstreamLinkFactory()
            .EnqueueFailure(new IOException("connection refused"))
            .EnqueueLink(oracle);
        await using Harness h = Start(factory);

        // First dial throws; the loop backs off (InitialBackoff = 1s) then redials.
        await WaitUntilAsync(() => factory.ConnectAttempts >= 1, "first (failing) dial attempted");
        await AdvanceUntilAsync(h.Time, () => factory.ConnectAttempts >= 2, "redial after failure", TimeSpan.FromSeconds(1));

        string sent = await oracle.ReadSentAsync(Timeout);
        Assert.IsType<HostHandshake>(HostCommandCodec.Parse(sent));
        oracle.PushLine(OracleHandshake);
        await h.Link.WaitForUpAsync(new CancellationTokenSource(Timeout).Token);
        Assert.True(factory.ConnectAttempts >= 2);
    }

    [Fact]
    public async Task InboundLoop_DropsTheLink()
    {
        var first = new ScriptedUpstreamLink();
        var second = new ScriptedUpstreamLink();
        var factory = new ScriptedUpstreamLinkFactory().EnqueueLink(first).EnqueueLink(second);
        await using Harness h = Start(factory);

        _ = await first.ReadSentAsync(Timeout);
        first.PushLine(OracleHandshake);
        await h.Link.WaitForUpAsync(new CancellationTokenSource(Timeout).Token);

        // A /..LOOP must drop the link (SPECS) and trigger a reconnect.
        first.PushLine(Wire.Host("LOOP ORACLE PDNCONV g4abc HOST"));
        await WaitUntilAsync(() => first.Disposed, "link dropped on /..LOOP");
    }

    [Fact]
    public async Task LinkTimeP_RoundTrip_IsMeasuredAndSurfaced()
    {
        var oracle = new ScriptedUpstreamLink();
        await using Harness h = Start(new ScriptedUpstreamLinkFactory().EnqueueLink(oracle));

        _ = await oracle.ReadSentAsync(Timeout);
        oracle.PushLine(OracleHandshake);
        await h.Link.WaitForUpAsync(new CancellationTokenSource(Timeout).Token);
        Assert.Null(h.Link.LastRoundTripMs); // no measurement yet
        Assert.Equal("ORACLE", h.Link.PeerHostName);

        // Provoke a keepalive ping, let a little time pass, then answer with a pong.
        h.Time.Advance(TimeSpan.FromSeconds(61));
        await WaitUntilAsync(
            () => oracle.SentLines.Any(l => HostCommandCodec.Parse(l) is HostPing), "a keepalive ping");
        h.Time.Advance(TimeSpan.FromMilliseconds(200));
        oracle.PushLine(Wire.Host("PONG 3"));

        // The measured round-trip (the link-time `p` facility) is surfaced on the link.
        await WaitUntilAsync(() => h.Link.LastRoundTripMs is not null, "round-trip measured and surfaced");
        Assert.InRange(h.Link.LastRoundTripMs!.Value, 0, 5000);
    }

    [Fact]
    public async Task LocalModeSet_ByOperator_GoesUpstreamAsMode()
    {
        var oracle = new ScriptedUpstreamLink();
        await using Harness h = Start(new ScriptedUpstreamLinkFactory().EnqueueLink(oracle));

        _ = await oracle.ReadSentAsync(Timeout);
        oracle.PushLine(OracleHandshake);
        await h.Link.WaitForUpAsync(new CancellationTokenSource(Timeout).Token);

        // A local user joins and is granted operator, then sets modes; the hub enforces and emits /..MODE.
        await h.Link.SubmitLocalEventAsync(new ConversEvent.LocalJoin("s1", "M0LTE", 3333), CancellationToken.None);
        await h.Link.SubmitLocalEventAsync(new ConversEvent.LocalSetOperator("s1", 3333, true), CancellationToken.None);
        await h.Link.SubmitLocalEventAsync(new ConversEvent.LocalSetMode("s1", 3333, "+mt"), CancellationToken.None);

        await WaitUntilAsync(
            () => oracle.SentLines.Any(l => HostCommandCodec.Parse(l) is HostMode { Channel: 3333 }),
            "a /..MODE went upstream");

        var mode = oracle.SentLines.Select(HostCommandCodec.Parse).OfType<HostMode>().First(m => m.Channel == 3333);
        Assert.Equal("+tm", mode.Options); // canonical full mode string after the toggle (s-p-t-i-m-l order)
    }

    [Fact]
    public async Task InboundMode_DeliversModeChange_ToLocalSessionOnTheChannel()
    {
        var oracle = new ScriptedUpstreamLink();
        await using Harness h = Start(new ScriptedUpstreamLinkFactory().EnqueueLink(oracle));

        await h.Link.SubmitLocalEventAsync(new ConversEvent.LocalJoin("s1", "M0LTE", 3333), CancellationToken.None);
        oracle.PushLine(OracleHandshake);
        await h.Link.WaitForUpAsync(new CancellationTokenSource(Timeout).Token);

        // The uplink (authoritative) sets the channel's modes; the local listener is told.
        oracle.PushLine(Wire.Host("MODE 3333 +m"));

        await WaitUntilAsync(
            () => h.Local.Actions.OfType<ConversAction.DeliverModeChange>().Any(a => a.SessionId == "s1"),
            "DeliverModeChange to the local session");

        var change = h.Local.Actions.OfType<ConversAction.DeliverModeChange>().First(a => a.SessionId == "s1");
        Assert.Equal(3333, change.Channel);
        Assert.True((change.Modes & ChannelMode.Moderated) != 0);
    }
}
