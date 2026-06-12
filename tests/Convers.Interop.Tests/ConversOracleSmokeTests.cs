using System.Net.Sockets;

namespace Convers.Interop.Tests;

/// <summary>
/// The W0 oracle lane: proves the conversd-saupp container (docker/compose.oracle.yml) is up and its
/// convers port is accepting connections — the stand-in parent node W4's HostLink will peer with.
/// Tagged <c>Interop</c> so it is excluded from the unit lane (<c>--filter Category!=Interop</c>) and
/// only runs once CI (or a local <c>docker compose … up -d --wait</c>) has stood the oracle up.
/// </summary>
[Trait("Category", "Interop")]
public class ConversOracleSmokeTests
{
    [Fact]
    public async Task Oracle_AcceptsConnectionsOnTheConversPort()
    {
        using var client = new TcpClient();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(OracleEndpoint.Host, OracleEndpoint.Port, cts.Token);

        Assert.True(client.Connected, $"conversd oracle not reachable at {OracleEndpoint.Host}:{OracleEndpoint.Port}");
    }
}
