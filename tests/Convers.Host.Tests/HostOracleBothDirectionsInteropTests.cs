using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using Convers.Core;
using Convers.Host.Sessions;
using Convers.Host.Tests.Rhp;
using Convers.Host.Tests.Uplink;
using Convers.Host.Uplink;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Convers.Host.Tests;

/// <summary>
/// The W6 interop lane (design decision 10, the "docker lane green <b>both directions</b>" build-wave
/// line): compose the production host with the <b>TCP uplink</b> pointed at the live conversd-saupp oracle
/// (<c>docker/compose.oracle.yml</c>) and prove the convers conversation flows <b>both ways</b> through it.
/// <para>
/// W5a (<see cref="HostOracleInteropTests"/>) proved the <b>uplink</b> direction — a local user's join and
/// message reach a convers channel observed by a second plain TCP USER on the oracle. This wave adds the
/// new proof: the <b>inbound</b> direction — a real convers-network user's channel message comes back down
/// our uplink and is <i>delivered to our local session</i> (the <see cref="ILocalDelivery"/> sink the
/// <see cref="HostLink"/> fans inbound deliveries to) <b>and</b> persisted in the chat log (kind=channel,
/// origin=network) — and a single-session <b>full bidirectional</b> test that drives both directions at
/// once. The complete web-user ↔ real-conversd round trip was demonstrated live (both ways, with chat
/// logging) while building W5/W6; these tests codify it so CI runs it.
/// </para>
/// Tagged <c>Interop</c> so the unit lane skips it (<c>--filter Category!=Interop</c>); it runs once the
/// oracle is up (<c>docker compose -f docker/compose.oracle.yml up -d --build --wait</c>). Each test uses a
/// distinct host callsign and channel and closes its oracle TCP clients cleanly on teardown, so conversd's
/// one-session-per-identity reaper never trips a sibling test.
/// </summary>
[Trait("Category", "Interop")]
public sealed class HostOracleBothDirectionsInteropTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(25);

    /// <summary>
    /// INBOUND direction (the new proof). Put a local user on a channel so the oracle routes that channel
    /// to our uplink, then have a second plain TCP user on the oracle send a channel message; assert our
    /// local session receives it through the production <see cref="ILocalDelivery"/> fan-out (the rendered
    /// <c>DeliverChannelMessage</c> — network sender + text) and that it is persisted in
    /// <see cref="ConversStore.QueryChatLog"/> as a network-origin channel row.
    /// </summary>
    [Fact]
    public async Task ComposedHost_TcpUplink_NetworkUserChannelMessageReachesLocalSessionAndChatLog()
    {
        const int channel = 4101;
        const string networkUser = "G7NET";
        using var cts = new CancellationTokenSource(Timeout);

        // Distinct callsign base → distinct convers host name, so this composed host's uplink never
        // collides with the W5a uplink test (or the sibling tests below) on the oracle's
        // one-host-link-per-identity rule when the interop classes run in parallel.
        await using ComposedHost host = await ComposedHost.BuildWithTcpUplinkAsync(
            OracleEndpoint.Host, OracleEndpoint.Port, callsign: "M6INB-1");
        await WaitUntilAsync(
            () => host.App.Services.GetUplinkIsUp(), "the composed host's uplink establishing to the oracle");

        LocalSessionRegistry registry = host.App.Services.GetRequiredService<LocalSessionRegistry>();
        HostLink link = host.App.Services.GetRequiredService<HostLink>();
        ConversStore store = host.App.Services.GetRequiredService<ConversStore>();

        // A local session attaches its line sink (exactly as RfUserSession does) and joins the channel, so
        // the oracle starts routing that channel's traffic down our uplink to this session.
        var delivered = new RecordingSink();
        const string sessionId = "local-rx-1";
        registry.Register(sessionId, delivered.WriteAsync);
        await link.SubmitLocalEventAsync(new ConversEvent.LocalJoin(sessionId, "M0LCL", channel), cts.Token);

        // Let the join propagate to the oracle before the network user speaks.
        await DelayAsync(TimeSpan.FromSeconds(2));

        // A real convers-network user (a second plain TCP client) joins the same channel and sends a
        // message. conversd relays it down our uplink as a /..CMSG.
        await using OracleClient netUser = await OracleClient.NameOntoAsync(networkUser, channel);
        await netUser.SendLineAsync("hello from the network");

        // INBOUND assertion 1: the message was delivered to our local session via the ILocalDelivery sink,
        // rendered with the network sender + text (the DeliverChannelMessage the HostLink fanned out).
        string line = await delivered.WaitForLineAsync(
            l => l.Contains("hello from the network", StringComparison.OrdinalIgnoreCase), Timeout);
        Assert.Equal($"<{networkUser}>: hello from the network", line);

        // INBOUND assertion 2: it is persisted in the chat log as a network-origin channel message.
        ChatLogEntry row = await WaitForChatLogAsync(
            store, channel, ChatLogKind.Channel,
            e => e.Text.Equals("hello from the network", StringComparison.Ordinal));
        Assert.Equal(ChatLogKind.Channel, row.Kind);
        Assert.Equal(ChatLogOrigin.Network, row.Origin);
        Assert.Equal(networkUser, row.FromCall);
        Assert.Equal(channel, row.Channel);
    }

    /// <summary>
    /// FULL BIDIRECTIONAL. In one composed session prove BOTH directions: our local user's message reaches
    /// the oracle (seen by a second plain TCP USER — the uplink direction), and that same oracle user's
    /// reply reaches our local user (the inbound direction). This is the both-ways live demo, codified.
    /// </summary>
    [Fact]
    public async Task ComposedHost_TcpUplink_FullBidirectional_LocalAndNetworkUsersChatBothWays()
    {
        const int channel = 4202;
        const string networkUser = "G8NET";
        const string localUserCall = "M0BID";
        using var cts = new CancellationTokenSource(Timeout);

        // Distinct callsign base (see the inbound test) so this host's uplink host name is unique on the
        // oracle and never collides with the parallel interop tests.
        await using ComposedHost host = await ComposedHost.BuildWithTcpUplinkAsync(
            OracleEndpoint.Host, OracleEndpoint.Port, callsign: "M7BID-1");
        await WaitUntilAsync(
            () => host.App.Services.GetUplinkIsUp(), "the composed host's uplink establishing to the oracle");

        LocalSessionRegistry registry = host.App.Services.GetRequiredService<LocalSessionRegistry>();
        HostLink link = host.App.Services.GetRequiredService<HostLink>();
        ConversStore store = host.App.Services.GetRequiredService<ConversStore>();

        var delivered = new RecordingSink();
        const string sessionId = "local-bi-1";
        registry.Register(sessionId, delivered.WriteAsync);
        await link.SubmitLocalEventAsync(new ConversEvent.LocalJoin(sessionId, localUserCall, channel), cts.Token);

        // A network user (plain TCP USER on the oracle) joins the same channel and watches the wire.
        await using OracleClient netUser = await OracleClient.NameOntoAsync(networkUser, channel);
        await DelayAsync(TimeSpan.FromSeconds(2));

        // --- Uplink direction: our local user speaks; the network user must see it on the channel. ---
        await link.SubmitLocalEventAsync(new ConversEvent.LocalSay(sessionId, "ping from the leaf"), cts.Token);

        string seenByNetwork = await netUser.ReadUntilAsync(
            t => t.Contains("ping from the leaf", StringComparison.OrdinalIgnoreCase), Timeout);
        Assert.Contains(localUserCall, seenByNetwork, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ping from the leaf", seenByNetwork, StringComparison.OrdinalIgnoreCase);

        // --- Inbound direction: the network user replies; our local user must receive it. ---
        await netUser.SendLineAsync("pong from the network");

        string seenByLocal = await delivered.WaitForLineAsync(
            l => l.Contains("pong from the network", StringComparison.OrdinalIgnoreCase), Timeout);
        Assert.Equal($"<{networkUser}>: pong from the network", seenByLocal);

        // The inbound (network-origin) message is in the durable chat log. (The local user's own message
        // is logged at the RF/web session layer — RfUserSession/WebChatSessions call ChatLogWriter — not at
        // the HostLink.SubmitLocalEventAsync seam this test drives, so we don't assert it here; the
        // RF-path test below exercises a real session, and the uplink direction is proven by the network
        // user above seeing our message.)
        ChatLogEntry inbound = await WaitForChatLogAsync(
            store, channel, ChatLogKind.Channel,
            e => e.Text.Equals("pong from the network", StringComparison.Ordinal));
        Assert.Equal(ChatLogOrigin.Network, inbound.Origin);
        Assert.Equal(networkUser, inbound.FromCall);
    }

    /// <summary>
    /// FULL BIDIRECTIONAL through the <b>real RF path</b> (the most faithful codification of the live demo).
    /// Rather than synthesising the local session with <see cref="HostLink.SubmitLocalEventAsync"/>, drive
    /// an actual RF user in through the composed <see cref="FakeRhpServer"/> + production
    /// demux/<c>RfUserSession</c>, on the node's default channel 3333. Prove BOTH ways end to end as an
    /// on-air user actually experiences them:
    /// <list type="number">
    /// <item><b>Uplink:</b> the RF user types a line → a network user on the same oracle channel sees it,
    /// and it is chat-logged <i>local-origin</i> (the session layer's <c>ChatLogWriter</c> call).</item>
    /// <item><b>Inbound:</b> the network user replies → it arrives <i>down the RF wire</i> as
    /// <c>&lt;sender&gt;: text</c>, and is chat-logged <i>network-origin</i>.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task ComposedHost_RfUser_ChatsWithNetworkUserBothWays_OverTheAir()
    {
        const int channel = 3333; // the node's default channel — the RF user auto-joins it on connect.
        const string rfUser = "G4ABC";
        const string networkUser = "G9NET";

        // Distinct callsign base (see the inbound test) so this host's uplink host name is unique on the
        // oracle and never collides with the parallel interop tests.
        await using ComposedHost host = await ComposedHost.BuildWithTcpUplinkAsync(
            OracleEndpoint.Host, OracleEndpoint.Port, callsign: "M8RFI-1");
        await WaitUntilAsync(
            () => host.App.Services.GetUplinkIsUp(), "the composed host's uplink establishing to the oracle");
        ConversStore store = host.App.Services.GetRequiredService<ConversStore>();

        // A real RF user connects through the production RhpNodeLink + InboundDemux and is auto-joined to
        // the node's default channel — the same channel the oracle uses by default.
        FakeRhpPeer peer = await host.Server.AcceptChildAsync(rfUser);
        await DrainGreetingAsync(peer);

        // A network user joins the same channel on the oracle and watches the wire.
        await using OracleClient netUser = await OracleClient.NameOntoAsync(networkUser, channel);
        await DelayAsync(TimeSpan.FromSeconds(2));

        // --- Uplink direction: the RF user types a line (a Say); the network user must see it. ---
        await peer.SendLineAsync("over to the network");
        string seenByNetwork = await netUser.ReadUntilAsync(
            t => t.Contains("over to the network", StringComparison.OrdinalIgnoreCase), Timeout);
        Assert.Contains(rfUser, seenByNetwork, StringComparison.OrdinalIgnoreCase);

        // The RF user's own message is chat-logged local-origin by the RfUserSession session layer.
        ChatLogEntry outbound = await WaitForChatLogAsync(
            store, channel, ChatLogKind.Channel,
            e => e.Text.Equals("over to the network", StringComparison.Ordinal));
        Assert.Equal(ChatLogOrigin.Local, outbound.Origin);
        Assert.Equal(rfUser, outbound.FromCall);

        // --- Inbound direction: the network user replies; it must reach the RF user down the air. ---
        await netUser.SendLineAsync("over to the RF side");
        string overTheAir = await peer.ReadLineMatchingAsync(
            l => l.Contains("over to the RF side", StringComparison.OrdinalIgnoreCase), Timeout);
        Assert.Equal($"<{networkUser}>: over to the RF side", overTheAir.TrimEnd('\r'));

        // ...and is chat-logged network-origin.
        ChatLogEntry inbound = await WaitForChatLogAsync(
            store, channel, ChatLogKind.Channel,
            e => e.Text.Equals("over to the RF side", StringComparison.Ordinal));
        Assert.Equal(ChatLogOrigin.Network, inbound.Origin);
        Assert.Equal(networkUser, inbound.FromCall);
    }

    private static async Task DrainGreetingAsync(FakeRhpPeer peer)
    {
        await peer.ReadLineAsync();
        await peer.ReadLineAsync();
        await peer.ReadLineAsync();
    }

    private static Task DelayAsync(TimeSpan delay) => Task.Delay(delay);

    private static async Task WaitUntilAsync(Func<bool> predicate, string what)
    {
        using var cts = new CancellationTokenSource(Timeout);
        while (!predicate())
        {
            if (cts.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for: {what}");
            }

            await Task.Delay(50, cts.Token);
        }
    }

    private static async Task<ChatLogEntry> WaitForChatLogAsync(
        ConversStore store, int channel, ChatLogKind kind, Func<ChatLogEntry, bool> match)
    {
        DateTime deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            ChatLogEntry? hit = store.QueryChatLog(channel: channel, kind: kind).FirstOrDefault(match);
            if (hit is not null)
            {
                return hit;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Timed out waiting for a chat-log row on channel {channel}.");
            }

            await Task.Delay(100);
        }
    }
}

/// <summary>
/// Records the rendered lines the <see cref="LocalSessionRegistry"/> writes to a local session's sink, so
/// a test can assert what a local (RF/web) user would have received from the inbound fan-out — without a
/// live terminal. This is exactly the seam <see cref="RfUserSession"/> registers, minus the transport.
/// </summary>
internal sealed class RecordingSink
{
    private readonly ConcurrentQueue<string> _lines = new();

    public ValueTask WriteAsync(string line, CancellationToken cancellationToken)
    {
        _lines.Enqueue(line);
        return ValueTask.CompletedTask;
    }

    public async Task<string> WaitForLineAsync(Func<string, bool> match, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            string? hit = _lines.FirstOrDefault(match);
            if (hit is not null)
            {
                return hit;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for a line delivered to the local session.");
            }

            await Task.Delay(50);
        }
    }
}

/// <summary>
/// A plain TCP convers USER on the oracle (no host link) — the network side of an interop round trip. It
/// <c>/name</c>s onto a channel and can send/observe channel traffic, mirroring how
/// <see cref="HostOracleInteropTests"/> drives its observer. Disposing DISConnects and closes the socket
/// cleanly so conversd's one-session-per-identity reaper never trips a sibling test.
/// </summary>
internal sealed class OracleClient : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;

    private OracleClient(TcpClient client, NetworkStream stream)
    {
        _client = client;
        _stream = stream;
    }

    /// <summary>Connects a plain TCP USER and <c>/name</c>s it onto <paramref name="channel"/>.</summary>
    public static async Task<OracleClient> NameOntoAsync(string call, int channel)
    {
        var client = new TcpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await client.ConnectAsync(OracleEndpoint.Host, OracleEndpoint.Port, cts.Token);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(Encoding.Latin1.GetBytes($"/NAME {call} {channel}\n"), cts.Token);
        // Let the oracle register the name + channel before the caller drives traffic.
        await Task.Delay(TimeSpan.FromMilliseconds(500), cts.Token);
        return new OracleClient(client, stream);
    }

    public async Task SendLineAsync(string text)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await _stream.WriteAsync(Encoding.Latin1.GetBytes(text + "\n"), cts.Token);
    }

    /// <summary>Reads from the oracle until the accumulated transcript satisfies <paramref name="done"/>.</summary>
    public async Task<string> ReadUntilAsync(Func<string, bool> done, TimeSpan timeout)
    {
        var sb = new StringBuilder();
        using var cts = new CancellationTokenSource(timeout);
        var buffer = new byte[4096];
        while (!done(sb.ToString()))
        {
            int read = await _stream.ReadAsync(buffer, cts.Token);
            if (read == 0)
            {
                break;
            }

            sb.Append(Encoding.Latin1.GetString(buffer, 0, read));
        }

        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _stream.WriteAsync(Encoding.Latin1.GetBytes("/quit\n"), cts.Token);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
        {
            // Best-effort clean DISC; the socket close below is what conversd actually reaps on.
        }

        _stream.Dispose();
        _client.Dispose();
    }
}
