using System.Net.Sockets;
using System.Text;
using Convers.Host.Tests.Rhp;
using Convers.Host.Tests.Uplink;
using Microsoft.AspNetCore.Builder;

namespace Convers.Host.Tests;

/// <summary>
/// The W5a interop lane (design decision 10): compose the production host with the <b>TCP uplink</b>
/// pointed at the live conversd-saupp oracle (<c>docker/compose.oracle.yml</c>) and prove the <b>uplink
/// direction</b> end-to-end — a local RF user (injected via the composed <see cref="FakeRhpServer"/>)
/// joins and speaks, and their presence + message show up on convers channel 3333 as observed by a
/// second plain TCP client connected to the oracle as a USER. The full bidirectional live demo is W6;
/// this proves the composed host links to conversd and a local user's activity reaches the network.
/// Tagged <c>Interop</c> so the unit lane skips it (<c>--filter Category!=Interop</c>); it runs once the
/// oracle is up (<c>docker compose -f docker/compose.oracle.yml up -d --build --wait</c>).
/// </summary>
[Trait("Category", "Interop")]
public sealed class HostOracleInteropTests
{
    private const string UserCall = "M0LTE";
    private const int Channel = 3333;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task ComposedHost_TcpUplink_LocalUserPresenceAndMessageReachTheOracle()
    {
        // A plain TCP observer joins channel 3333 on the oracle and watches the wire.
        using var observer = new TcpClient();
        using var connectCts = new CancellationTokenSource(Timeout);
        await observer.ConnectAsync(OracleEndpoint.Host, OracleEndpoint.Port, connectCts.Token);
        NetworkStream obs = observer.GetStream();
        await obs.WriteAsync(Encoding.Latin1.GetBytes("/NAME OBS 3333\n"), connectCts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(500), connectCts.Token);

        // Compose the host with the TCP uplink to the oracle; the bound callsign's base is the host name
        // the user is presented under upstream.
        await using ComposedHost host = await ComposedHost.BuildWithTcpUplinkAsync(
            OracleEndpoint.Host, OracleEndpoint.Port, callsign: "M0LTE-5");

        // Wait for the uplink to come up against the real oracle.
        await WaitUntilAsync(
            () => host.App.Services.GetUplinkIsUp(), "the composed host's uplink establishing to the oracle");

        // An inbound RF user joins (auto-login from the accepted callsign) and speaks.
        FakeRhpPeer peer = await host.Server.AcceptChildAsync(UserCall);
        await DrainGreetingAsync(peer);
        await peer.SendLineAsync("hello from the leaf");

        // The observer must see the user's presence + message on channel 3333 (the uplink direction).
        string transcript = await ReadUntilAsync(obs,
            text => text.Contains("hello from the leaf", StringComparison.OrdinalIgnoreCase),
            Timeout);

        Assert.Contains(UserCall, transcript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("joined channel 3333", transcript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hello from the leaf", transcript, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task DrainGreetingAsync(FakeRhpPeer peer)
    {
        await peer.ReadLineAsync();
        await peer.ReadLineAsync();
        await peer.ReadLineAsync();
    }

    private static async Task<string> ReadUntilAsync(NetworkStream stream, Func<string, bool> done, TimeSpan timeout)
    {
        var sb = new StringBuilder();
        using var cts = new CancellationTokenSource(timeout);
        var buffer = new byte[4096];
        while (!done(sb.ToString()))
        {
            int read = await stream.ReadAsync(buffer, cts.Token);
            if (read == 0)
            {
                break;
            }

            sb.Append(Encoding.Latin1.GetString(buffer, 0, read));
        }

        return sb.ToString();
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

            await Task.Delay(50, cts.Token);
        }
    }
}
