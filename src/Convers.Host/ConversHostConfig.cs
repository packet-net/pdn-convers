using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Convers.Host;

/// <summary>
/// The host configuration, loaded from <c>$PDN_APP_STATE/convers.yaml</c> (design.md "YAML config").
/// A commented default is written on first run. The RHP endpoint defaults come from the supervisor
/// environment (<c>PDN_RHP_HOST</c>/<c>PDN_RHP_PORT</c>) with a 127.0.0.1:9000 fallback.
/// </summary>
/// <remarks>
/// W0 parses, persists and lightly validates this; the uplink providers (decision 6) and the RHP
/// node link are wired in W4/W5. The shape is fixed now so later waves only add behaviour.
/// </remarks>
public sealed record ConversHostConfig
{
    /// <summary>The callsign placeholder a fresh default config carries until the owner edits it.</summary>
    public const string PlaceholderCallsign = "N0CALL";

    /// <summary>The shipped placeholder default channel — Tom picks the real public number before go-live.</summary>
    public const int PlaceholderDefaultChannel = 3333;

    /// <summary>Convers node callsign (+ optional SSID) — the RHP bind identity.</summary>
    public string Callsign { get; init; } = PlaceholderCallsign;

    /// <summary>Sysop callsign (console sysop rights; sysop view in the web tile).</summary>
    public string Sysop { get; init; } = "";

    /// <summary>
    /// The channel packet.net users land on at connect without choosing (design.md decision: a fixed
    /// default channel; the handover reserves 256–32767, avoiding collisions).
    /// </summary>
    public int DefaultChannel { get; init; } = PlaceholderDefaultChannel;

    /// <summary>Web-chat bind (loopback per the app-gateway contract).</summary>
    public WebConfig Web { get; init; } = new();

    /// <summary>The node's RHPv2 endpoint (the packet plane our callsign binds on).</summary>
    public RhpConfig Rhp { get; init; } = new();

    /// <summary>The single upstream convers host link (design.md decision 6). Unset until a parent exists.</summary>
    public UplinkConfig Uplink { get; init; } = new();
}

/// <summary>Web-chat bind configuration. MUST stay loopback (app-gateway contract).</summary>
public sealed record WebConfig
{
    /// <summary>Bind address; the gateway identity headers are only trustworthy on loopback.</summary>
    public string Bind { get; init; } = "127.0.0.1";

    /// <summary>Port; must match <c>ui.upstream</c> in pdn-app.yaml.</summary>
    public int Port { get; init; } = 18091;
}

/// <summary>RHPv2 endpoint + credentials. Null host/port defer to the supervisor environment.</summary>
public sealed record RhpConfig
{
    /// <summary>RHP host; null → <c>PDN_RHP_HOST</c> → 127.0.0.1.</summary>
    public string? Host { get; init; }

    /// <summary>RHP port; null → <c>PDN_RHP_PORT</c> → 9000.</summary>
    public int? Port { get; init; }

    /// <summary>RHP auth user (only when the node sets <c>rhp.requireAuth</c>).</summary>
    public string? User { get; init; }

    /// <summary>RHP auth password.</summary>
    public string? Pass { get; init; }
}

/// <summary>
/// The single upstream host link (design.md decision 1 + 6). Exactly one provider once a parent is
/// arranged: <see cref="Rf"/> dials a neighbouring convers node over RHP <c>open</c> (pdn-native), or
/// <see cref="Tcp"/> opens a direct socket to an internet hub. Both are built behind one
/// <c>IUpstreamLink</c> in W5; the default is left unset until the external prerequisite is met.
/// </summary>
public sealed record UplinkConfig
{
    /// <summary>Active provider: <c>"rf"</c>, <c>"tcp"</c>, or null (no uplink — develop against the local oracle).</summary>
    public string? Provider { get; init; }

    /// <summary>Our convers hostname announced in the <c>/..HOST</c> handshake (max 9 chars, no domain).</summary>
    public string Hostname { get; init; } = "";

    /// <summary>Password the parent's <c>Access … HOST</c> requires, if any (host.c check_password).</summary>
    public string? Password { get; init; }

    /// <summary>RF provider: the neighbour convers node callsign to RHP-<c>open</c>.</summary>
    public RfUplinkConfig? Rf { get; init; }

    /// <summary>TCP provider: the internet hub to dial directly (e.g. HubNA 44.68.41.2:3600).</summary>
    public TcpUplinkConfig? Tcp { get; init; }
}

/// <summary>RF-via-RHP-open uplink target.</summary>
public sealed record RfUplinkConfig
{
    /// <summary>The neighbouring convers node's callsign (+ optional SSID).</summary>
    public string Call { get; init; } = "";
}

/// <summary>Direct-TCP-to-hub uplink target.</summary>
public sealed record TcpUplinkConfig
{
    /// <summary>Hub host / FQDN.</summary>
    public string Host { get; init; } = "";

    /// <summary>Hub convers port (3600 by convention).</summary>
    public int Port { get; init; } = 3600;
}

/// <summary>Loads <c>convers.yaml</c> from the state dir, creating a commented default on first run.</summary>
public static class ConversHostConfigFile
{
    /// <summary>The config file name inside <c>$PDN_APP_STATE</c>.</summary>
    public const string FileName = "convers.yaml";

    /// <summary>
    /// Loads the config, writing <see cref="DefaultYaml"/> first when the file does not exist.
    /// <paramref name="getEnv"/> supplies <c>PDN_RHP_HOST</c>/<c>PDN_RHP_PORT</c> defaults for an
    /// unset RHP endpoint.
    /// </summary>
    public static (ConversHostConfig Config, bool CreatedDefault) LoadOrCreate(string stateDir, Func<string, string?> getEnv)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        ArgumentNullException.ThrowIfNull(getEnv);

        string path = Path.Combine(stateDir, FileName);
        bool created = false;
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(stateDir);
            File.WriteAllText(path, DefaultYaml);
            created = true;
        }

        ConversHostConfig config = Parse(File.ReadAllText(path));
        return (ApplyEnvironment(config, getEnv), created);
    }

    /// <summary>Parses a YAML config document (camelCase keys; unknown keys ignored).</summary>
    public static ConversHostConfig Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        IDeserializer deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        return deserializer.Deserialize<ConversHostConfig>(yaml) ?? new ConversHostConfig();
    }

    /// <summary>Resolves the RHP endpoint: explicit config → supervisor env → 127.0.0.1:9000.</summary>
    public static ConversHostConfig ApplyEnvironment(ConversHostConfig config, Func<string, string?> getEnv)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(getEnv);

        string host = config.Rhp.Host is { Length: > 0 } h ? h
            : getEnv("PDN_RHP_HOST") is { Length: > 0 } envHost ? envHost
            : "127.0.0.1";
        int port = config.Rhp.Port
            ?? (int.TryParse(getEnv("PDN_RHP_PORT"), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int envPort)
                ? envPort
                : 9000);
        return config with { Rhp = config.Rhp with { Host = host, Port = port } };
    }

    /// <summary>The commented default written on first run.</summary>
    public const string DefaultYaml = """
        # pdn-convers configuration — created on first run; edit and restart the app.
        #
        # callsign: the convers node callsign (+ optional SSID). This is the callsign the node
        #           binds over RHPv2 — RF users connect to it and it is the identity presented to
        #           the upstream convers network.
        callsign: N0CALL

        # sysop: the sysop's callsign (sysop rights on the console; sysop view in the web tile).
        sysop: ""

        # defaultChannel: the channel packet.net users land on at connect without choosing.
        #                 Must be 0..32767; pick a public number 256..32767 that is not already in
        #                 use on the network. 3333 is a PLACEHOLDER — change it before go-live.
        defaultChannel: 3333

        # web: the web-chat bind. MUST stay loopback — pdn's app gateway is the trust boundary for
        #      the X-Pdn-* identity headers. The port must match ui.upstream in pdn-app.yaml.
        web:
          bind: 127.0.0.1
          port: 18091

        # rhp: the node's RHPv2 endpoint. When omitted (or null) the supervisor environment
        #      (PDN_RHP_HOST / PDN_RHP_PORT) is used, falling back to 127.0.0.1:9000. user/pass only
        #      matter when the node sets rhp.requireAuth.
        rhp:
          host: null
          port: null
          user: null
          pass: null

        # uplink: the SINGLE upstream convers host link (the net is a tree — exactly one parent).
        #         Leave provider unset until a parent node is arranged (see HANDOVER.md §4); the app
        #         runs fine with no uplink, serving local users only. Two providers, pick one:
        #           rf  — dial a neighbouring convers node over RHP `open` (pdn-native, no internet)
        #           tcp — open a direct socket to an internet hub (needs the hub op to allowlist us
        #                 as `Access … HOST`, or a 44Net source address)
        uplink:
          provider: null            # rf | tcp | null
          hostname: ""              # our convers hostname in the /..HOST handshake (<= 9 chars, no domain)
          password: null            # if the parent's `Access HOST` requires one
          # rf:
          #   call: GB7XYZ          # the neighbour convers node to open over RF
          # tcp:
          #   host: 44.68.41.2      # e.g. HubNA (NA); find/confirm a UK/EU hub
          #   port: 3600
        """;
}
