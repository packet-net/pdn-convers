using Convers.Host;
using Convers.Host.Rhp;
using Convers.Host.Uplink;
using Convers.Host.Tests.Rhp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Convers.Host.Tests.Uplink;

/// <summary>
/// The concrete <see cref="IUpstreamLink"/> providers (design decision 6) and the composition selector:
/// the RF-via-RHP provider dials a neighbour over the node link and round-trips Latin-1 lines; the
/// selector picks rf / tcp / none from <c>config.Uplink.Provider</c>.
/// </summary>
public class UpstreamProviderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RfUpstreamLink_DialsNeighbourAndRoundTripsLines()
    {
        await using var server = new FakeRhpServer();
        server.Start();
        await using var node = new RhpNodeLink(
            new RhpLinkOptions { Host = "127.0.0.1", Port = server.Port, PreferredCallsign = "M0LTE-4" },
            new FakeTimeProvider(T0),
            NullLogger<RhpNodeLink>.Instance);
        using var cts = new CancellationTokenSource(Timeout);
        Task run = node.RunAsync(cts.Token);
        await node.WaitForUpAsync(cts.Token);

        var factory = new RfUpstreamLinkFactory(node, "GB7XYZ");
        await using IUpstreamLink link = await factory.ConnectAsync(cts.Token);

        // The node saw an open(Active) from our bound callsign to the neighbour.
        FakeRhpPeer neighbour = await server.NextOpenAsync();
        Assert.Equal("GB7XYZ", neighbour.Remote);
        Assert.Equal("M0LTE-4", neighbour.Local);

        // Outbound line reaches the neighbour (CR-terminated, AX.25 discipline).
        await link.SendLineAsync("/ÿHOST M0LTE pdnconv1 Aampun", cts.Token);
        string sent = await neighbour.ReadLineAsync();
        Assert.StartsWith("/ÿHOST", sent, StringComparison.Ordinal);

        // Inbound line from the neighbour is received, terminator stripped.
        await neighbour.SendLineAsync("/ÿHOST ORACLE saupp1.62a Aadmpunfi");
        string? received = await link.ReceiveLineAsync(cts.Token);
        Assert.Equal("/ÿHOST ORACLE saupp1.62a Aadmpunfi", received);

        await cts.CancelAsync();
        try
        {
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
        }
    }

    [Fact]
    public async Task SelectUpstreamFactory_PicksProviderByConfig()
    {
        await using var node = new RhpNodeLink(
            new RhpLinkOptions { Host = "127.0.0.1", Port = 1, PreferredCallsign = "M0LTE-4" },
            TimeProvider.System,
            NullLogger<RhpNodeLink>.Instance);

        // null/unset → no uplink.
        Assert.IsType<NullUpstreamLinkFactory>(
            HostComposition.SelectUpstreamFactory(new ConversHostConfig(), node));

        // tcp.
        var tcp = new ConversHostConfig
        {
            Uplink = new UplinkConfig { Provider = "tcp", Tcp = new TcpUplinkConfig { Host = "44.68.41.2", Port = 3600 } },
        };
        Assert.IsType<TcpUpstreamLinkFactory>(HostComposition.SelectUpstreamFactory(tcp, node));

        // rf.
        var rf = new ConversHostConfig
        {
            Uplink = new UplinkConfig { Provider = "rf", Rf = new RfUplinkConfig { Call = "GB7XYZ" } },
        };
        Assert.IsType<RfUpstreamLinkFactory>(HostComposition.SelectUpstreamFactory(rf, node));

        // A provider named but missing its target falls back to no uplink (safe default).
        var rfNoTarget = new ConversHostConfig { Uplink = new UplinkConfig { Provider = "rf" } };
        Assert.IsType<NullUpstreamLinkFactory>(HostComposition.SelectUpstreamFactory(rfNoTarget, node));
    }

    [Theory]
    [InlineData("", "M0LTE-4", "M0LTE")]                 // no hostname configured → base of callsign
    [InlineData("LEAFNODE9X", "M0LTE-4", "LEAFNODE9")]   // configured but >9 chars → truncated
    [InlineData("RELAY", "M0LTE-4", "RELAY")]            // configured short name used verbatim
    public void HostNameFor_DerivesTheHandshakeHostName(string configured, string callsign, string expected)
    {
        var config = new ConversHostConfig { Uplink = new UplinkConfig { Hostname = configured } };
        Assert.Equal(expected, HostComposition.HostNameFor(config, callsign));
    }
}
