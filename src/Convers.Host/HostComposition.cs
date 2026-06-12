using System.Net;
using System.Reflection;

namespace Convers.Host;

/// <summary>
/// Builds the composed convers host. Extracted from <c>Program</c> so the tests can boot the exact
/// production wiring (design.md src/Convers.Host).
/// </summary>
/// <remarks>
/// W0 wires config, the loopback web bind and a liveness endpoint. The RHPv2 client + inbound demux
/// (W5), the upstream host link (W4) and the web chat UI (W5) register here as later waves land —
/// each as its own <c>ComponentService&lt;T&gt;</c> hosted loop (the closed-generic dedup footgun
/// pdn-bbs documents). Keeping composition in one place keeps it test-coverable.
/// </remarks>
public static class HostComposition
{
    /// <summary>
    /// Composes the application. State (db + config) lives in <c>$PDN_APP_STATE</c>; the RHP
    /// endpoint falls back to <c>PDN_RHP_HOST</c>/<c>PDN_RHP_PORT</c>. The caller owns the returned
    /// app (<c>Run</c> in Program, <c>StartAsync</c>/<c>DisposeAsync</c> in tests).
    /// </summary>
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        string stateDir = Environment.GetEnvironmentVariable("PDN_APP_STATE") is { Length: > 0 } s
            ? s
            : Directory.GetCurrentDirectory();
        Directory.CreateDirectory(stateDir);

        (ConversHostConfig config, bool createdDefault) =
            ConversHostConfigFile.LoadOrCreate(stateDir, Environment.GetEnvironmentVariable);

        // Auto-derive the on-air callsign from the node (pdn convention: an app lives at an SSID of
        // the node callsign — <node-callsign>-<ssid>). An explicit config callsign wins verbatim.
        // W5's RHP bind probe-walks to the next free SSID if this one is taken.
        string? nodeCallsign = Environment.GetEnvironmentVariable("PDN_NODE_CALLSIGN");
        (string callsign, bool placeholderIdentity) =
            ConversIdentity.Resolve(config.Callsign, nodeCallsign, config.Ssid);

        string version = typeof(HostComposition).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { Length: > 0 } iv
            ? iv.Split('+')[0]
            : "0.1.0";

        // The web tile binds exactly what the config says (loopback per the app-gateway contract).
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Parse(config.Web.Bind), config.Web.Port));

        builder.Services.AddSingleton(config);

        WebApplication app = builder.Build();

        // Liveness for scripts/deploy-convers.sh and the node supervisor.
        app.MapGet("/healthz", () => Results.Text("ok"));

        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Convers.Host");
        if (createdDefault)
        {
            log.CreatedDefaultConfig(Path.Combine(stateDir, ConversHostConfigFile.FileName));
        }

        if (placeholderIdentity)
        {
            log.PlaceholderCallsign(ConversHostConfigFile.FileName);
        }

        log.Starting(version, callsign, config.DefaultChannel, config.Web.Bind, config.Web.Port);
        return app;
    }
}

/// <summary>Startup log messages.</summary>
internal static partial class ProgramLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "Created a default {Path} — edit it (callsign, defaultChannel, uplink) and restart")]
    public static partial void CreatedDefaultConfig(this ILogger logger, string path);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "No node callsign (PDN_NODE_CALLSIGN) and no callsign override — using a setup "
            + "placeholder identity; run under a pdn node, or set callsign in {File}")]
    public static partial void PlaceholderCallsign(this ILogger logger, string file);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information,
        Message = "pdn-convers {Version}: callsign {Callsign}, default channel {DefaultChannel}, web {WebBind}:{WebPort}")]
    public static partial void Starting(
        this ILogger logger, string version, string callsign, int defaultChannel, string webBind, int webPort);
}
