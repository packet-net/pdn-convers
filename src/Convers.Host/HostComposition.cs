using System.Net;
using System.Reflection;
using Convers.Console;
using Convers.Core;
using Convers.Host.Rhp;
using Convers.Host.Sessions;
using Convers.Host.Uplink;
using Convers.Protocol;

namespace Convers.Host;

/// <summary>
/// Builds the composed convers host — the RHPv2 node link (callsign bind + inbound demux), the upstream
/// <see cref="HostLink"/> over the selected <see cref="IUpstreamLinkFactory"/> (RF/RHP, direct-TCP, or
/// none), the <see cref="ConversHub"/> and the <see cref="ConversStore"/> (saupp differentiators + the
/// append-only chat log) over the Protocol/Core/Console libraries (design.md src/Convers.Host). Extracted
/// from <c>Program</c> so the tests can boot the exact production wiring (see <c>HostCompositionTests</c>).
/// </summary>
/// <remarks>
/// Each component loop registers as its own <c>ComponentService&lt;T&gt;</c> hosted service: distinct
/// closed generics so <c>AddHostedService</c> (which de-duplicates by implementation type via
/// <c>TryAddEnumerable</c>) keeps one service per loop — the pdn-bbs footgun, pinned by
/// <c>HostCompositionTests</c>. The web chat tile is W5b (pdn-bbs webmail style); W5a keeps only
/// <c>/healthz</c>.
/// </remarks>
public static class HostComposition
{
    /// <summary>
    /// Composes the application. State (db + config) lives in <c>$PDN_APP_STATE</c>; the RHP endpoint
    /// falls back to <c>PDN_RHP_HOST</c>/<c>PDN_RHP_PORT</c>. The caller owns the returned app (<c>Run</c>
    /// in Program, <c>StartAsync</c>/<c>DisposeAsync</c> in tests).
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

        // Auto-derive the on-air callsign from the node (pdn convention: <node-callsign>-<ssid>). An
        // explicit config callsign wins verbatim. The RHP node link probe-walks the SSID at bind time
        // if this one is taken (design decision 4).
        string? nodeCallsign = Environment.GetEnvironmentVariable("PDN_NODE_CALLSIGN");
        (string callsign, bool placeholderIdentity) =
            ConversIdentity.Resolve(config.Callsign, nodeCallsign, config.Ssid);

        string version = typeof(HostComposition).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { Length: > 0 } iv
            ? iv.Split('+')[0]
            : "0.1.0";

        var time = TimeProvider.System;

        // The web tile binds exactly what the config says (loopback per the app-gateway contract).
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Parse(config.Web.Bind), config.Web.Port));

        builder.Services.AddSingleton(config);

        // State store — saupp differentiators + the append-only chat log. Owned by the host (disposed
        // on shutdown). Singleton so the hub (hydrate/write-through) and the chat-log writer share it.
        var store = ConversStore.Open(Path.Combine(stateDir, "convers.db"), time);
        builder.Services.AddSingleton(store);

        // The single shared presence model, hydrated from the store. The HostLink drives it on its loop.
        var hub = new ConversHub(callsign, time, store);
        builder.Services.AddSingleton(hub);

        // Local fan-out sink (inbound deliveries → the right RF session) and the chat-log writer
        // (inbound network events → the chatlog).
        var registry = new LocalSessionRegistry();
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton(sp =>
            new ChatLogWriter(store, sp.GetRequiredService<ILogger<ChatLogWriter>>()));

        // Per-callsign console-surface preference (plain default / classic).
        var preferences = ConsolePreferenceStore.Open(Path.Combine(stateDir, "console-prefs.json"));
        builder.Services.AddSingleton<IConsolePreferences>(preferences);

        // RHP node link — binds the convers callsign (probe-walking the SSID on a clash).
        var linkOptions = new RhpLinkOptions
        {
            Host = config.Rhp.Host!,
            Port = config.Rhp.Port!.Value,
            PreferredCallsign = callsign,
            User = config.Rhp.User,
            Pass = config.Rhp.Pass,
        };
        builder.Services.AddSingleton(sp =>
            new RhpNodeLink(linkOptions, time, sp.GetRequiredService<ILogger<RhpNodeLink>>()));

        // The upstream HostLink over the selected provider. The RF provider dials through the node link,
        // so resolve it from the container; TCP/null need no node link.
        var hostLinkOptions = new HostLinkOptions
        {
            HostName = HostNameFor(config, callsign),
            Password = string.IsNullOrEmpty(config.Uplink.Password) ? null : config.Uplink.Password,
        };
        builder.Services.AddSingleton(sp => new HostLink(
            hostLinkOptions,
            SelectUpstreamFactory(config, sp.GetRequiredService<RhpNodeLink>()),
            sp.GetRequiredService<ConversHub>(),
            time,
            sp.GetRequiredService<ILogger<HostLink>>(),
            sp.GetRequiredService<LocalSessionRegistry>(),
            sp.GetRequiredService<ChatLogWriter>()));

        var sessionConfig = new RfSessionConfig
        {
            NodeName = callsign,
            DefaultChannel = config.DefaultChannel,
        };
        builder.Services.AddSingleton(sp => new InboundDemux(
            sp.GetRequiredService<RhpNodeLink>(),
            sp.GetRequiredService<HostLink>(),
            sp.GetRequiredService<LocalSessionRegistry>(),
            sp.GetRequiredService<IConsolePreferences>(),
            sp.GetRequiredService<ChatLogWriter>(),
            sessionConfig,
            sp.GetRequiredService<ILogger<InboundDemux>>()));

        // One ComponentService<T> per loop — distinct closed generics so AddHostedService (which
        // de-duplicates by implementation type) registers every loop. A single non-generic component
        // service would collapse to the first registration (the pdn-bbs footgun; pinned by tests).
        builder.Services.AddHostedService(sp => new ComponentService<RhpNodeLink>("rhp-link",
            sp.GetRequiredService<RhpNodeLink>(), static (link, ct) => link.RunAsync(ct)));
        builder.Services.AddHostedService(sp => new ComponentService<HostLink>("host-link",
            sp.GetRequiredService<HostLink>(), static (link, ct) => link.RunAsync(ct)));
        builder.Services.AddHostedService(sp => new ComponentService<InboundDemux>("demux",
            sp.GetRequiredService<InboundDemux>(), static (demux, ct) => demux.RunAsync(ct)));

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

        log.Starting(version, callsign, config.DefaultChannel, config.Uplink.Provider ?? "none",
            config.Web.Bind, config.Web.Port);
        return app;
    }

    /// <summary>
    /// The convers host name announced in the <c>/..HOST</c> handshake (≤ 9 chars, no domain): the
    /// configured <c>uplink.hostname</c> if set, else the base of the bound callsign (the natural node
    /// name).
    /// </summary>
    internal static string HostNameFor(ConversHostConfig config, string callsign)
    {
        if (config.Uplink.Hostname is { Length: > 0 } configured)
        {
            return Truncate(configured.Trim());
        }

        string @base = ConversIdentity.BaseCallsign(callsign) ?? callsign;
        return Truncate(@base);
    }

    private static string Truncate(string value) =>
        value.Length > HostLinkOptions.MaxHostNameLength ? value[..HostLinkOptions.MaxHostNameLength] : value;

    /// <summary>
    /// Selects the upstream provider by <c>config.Uplink.Provider</c> (<c>rf</c> | <c>tcp</c> | null):
    /// the RF provider dials a neighbour over RHP <c>open</c>, the TCP provider opens a direct socket,
    /// and an unset/unknown provider means no uplink (local-only — develop/test against the oracle).
    /// </summary>
    internal static IUpstreamLinkFactory SelectUpstreamFactory(ConversHostConfig config, RhpNodeLink nodeLink)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(nodeLink);

        return config.Uplink.Provider?.Trim().ToLowerInvariant() switch
        {
            "rf" when config.Uplink.Rf is { Call.Length: > 0 } rf =>
                new RfUpstreamLinkFactory(nodeLink, rf.Call),
            "tcp" when config.Uplink.Tcp is { Host.Length: > 0 } tcp =>
                new TcpUpstreamLinkFactory(tcp.Host, tcp.Port),
            _ => NullUpstreamLinkFactory.Instance,
        };
    }
}

/// <summary>
/// Hosts one named component loop as an <see cref="IHostedService"/>. Generic over the component because
/// <c>AddHostedService</c> de-duplicates registrations by implementation type (it uses
/// <c>TryAddEnumerable</c>) — distinct closed generics keep one service per component where a shared
/// non-generic class would collapse to the first registration (the pdn-bbs footgun).
/// </summary>
internal sealed class ComponentService<TComponent>(
    string name, TComponent component, Func<TComponent, CancellationToken, Task> run) : BackgroundService
    where TComponent : class
{
    /// <summary>The component name (diagnostics).</summary>
    public string Name { get; } = name;

    /// <inheritdoc/>
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => run(component, stoppingToken);
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
        Message = "pdn-convers {Version}: callsign {Callsign}, default channel {DefaultChannel}, "
            + "uplink {Uplink}, web {WebBind}:{WebPort}")]
    public static partial void Starting(
        this ILogger logger, string version, string callsign, int defaultChannel, string uplink,
        string webBind, int webPort);
}
