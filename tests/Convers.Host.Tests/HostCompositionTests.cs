using Convers.Host.Tests.Rhp;
using Convers.Host.Uplink;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Convers.Host.Tests;

/// <summary>
/// Pins the composed host itself — the exact production wiring via <see cref="HostComposition.Build"/> —
/// against the wire-faithful <see cref="FakeRhpServer"/>. The W0 liveness contract (<c>/healthz</c>) is
/// preserved, every component loop is registered as a distinct closed generic (the pdn-bbs
/// <c>ComponentService&lt;T&gt;</c> dedup footgun), and the loops actually run: an inbound RF connect
/// through the real <c>RhpNodeLink</c> + <c>InboundDemux</c> as Program composes them reaches a greeting
/// over the wire. The web chat tile is W5b, so only <c>/healthz</c> is served here.
/// </summary>
public sealed class HostCompositionTests
{
    [Fact]
    public async Task Build_ComposedHost_Serves_Healthz()
    {
        await using var host = await ComposedHost.BuildAsync(start: true);

        string baseUrl = host.App.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();

        using var client = new HttpClient();
        HttpResponseMessage resp = await client.GetAsync(new Uri($"{baseUrl}/healthz"));

        Assert.True(resp.IsSuccessStatusCode);
        Assert.Equal("ok", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ComposedHost_RegistersEveryComponentLoop()
    {
        await using var host = await ComposedHost.BuildAsync(start: false);

        List<IHostedService> components = [.. host.App.Services.GetServices<IHostedService>()
            .Where(s => s.GetType().Name.StartsWith("ComponentService", StringComparison.Ordinal))];

        // rhp-link + host-link + demux. Distinct closed generics, so each loop registers (a single
        // non-generic ComponentService would collapse to the first — the pdn-bbs footgun).
        Assert.Equal(3, components.Count);
        Assert.Equal(3, components.Select(s => s.GetType()).Distinct().Count());
    }

    [Fact]
    public async Task ComposedHost_InboundConnect_GreetingReachesPeerOverTheWire()
    {
        await using var host = await ComposedHost.BuildAsync(start: true);

        // The real lab flow: accept push + child Connected status push through the production
        // RhpNodeLink + InboundDemux. If any loop failed to register/run, nothing would come back.
        FakeRhpPeer peer = await host.Server.AcceptChildAsync("G4ABC");

        Assert.Equal("[G0ABC convers] Welcome G4ABC.", await peer.ReadLineAsync());
        Assert.Equal("You are on channel 3333.", await peer.ReadLineAsync());
        Assert.Equal("Type 'help' for commands, or just type to chat.", await peer.ReadLineAsync());
    }
}

/// <summary>
/// The production composition booted for a test: a temp state dir with a <c>convers.yaml</c> pointing at
/// a <see cref="FakeRhpServer"/> and no uplink (local-only), built through
/// <see cref="HostComposition.Build"/> on an ephemeral web port. Dispose stops the host and cleans up.
/// </summary>
internal sealed class ComposedHost : IAsyncDisposable
{
    private readonly DirectoryInfo _dir;
    private readonly bool _started;

    private ComposedHost(FakeRhpServer server, WebApplication app, DirectoryInfo dir, bool started)
    {
        Server = server;
        App = app;
        _dir = dir;
        _started = started;
    }

    public FakeRhpServer Server { get; }

    public WebApplication App { get; }

    /// <summary>Builds (and with <paramref name="start"/>, starts) the composed host with no uplink.</summary>
    public static Task<ComposedHost> BuildAsync(bool start) =>
        BuildCoreAsync(start, uplinkYaml: "uplink:\n  provider: null", callsign: "G0ABC");

    /// <summary>
    /// Builds and starts the composed host with the <b>TCP uplink</b> pointed at
    /// <paramref name="host"/>:<paramref name="port"/> — the interop lane's conversd oracle.
    /// </summary>
    public static Task<ComposedHost> BuildWithTcpUplinkAsync(string host, int port, string callsign) =>
        BuildCoreAsync(
            start: true,
            uplinkYaml: $"uplink:\n  provider: tcp\n  tcp:\n    host: {host}\n    port: {port}",
            callsign: callsign);

    private static async Task<ComposedHost> BuildCoreAsync(bool start, string uplinkYaml, string callsign)
    {
        var server = new FakeRhpServer();
        server.Start();
        DirectoryInfo dir = Directory.CreateTempSubdirectory("convers-composed-test-");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, ConversHostConfigFile.FileName), $"""
            callsign: {callsign}
            defaultChannel: 3333
            web:
              bind: 127.0.0.1
              port: 0
            rhp:
              host: 127.0.0.1
              port: {server.Port}
            {uplinkYaml}
            """);

        // Pass the state dir explicitly so parallel composed-host tests never race on the process-global
        // PDN_APP_STATE env var (that race made two hosts open one convers.db → SQLite "disk I/O error").
        WebApplication app = HostComposition.Build([], dir.FullName);

        var host = new ComposedHost(server, app, dir, start);
        if (start)
        {
            await app.StartAsync();
            await server.WaitForListenAsync();
        }

        return host;
    }

    public async ValueTask DisposeAsync()
    {
        if (_started)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await App.StopAsync(cts.Token);
        }

        await App.DisposeAsync();
        await Server.DisposeAsync();
        try
        {
            _dir.Delete(recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Service-provider helpers for the composed-host tests.</summary>
internal static class ComposedHostServices
{
    /// <summary>Whether the composed host's upstream <see cref="HostLink"/> is currently established.</summary>
    public static bool GetUplinkIsUp(this IServiceProvider services) =>
        services.GetRequiredService<HostLink>().IsUp;
}
