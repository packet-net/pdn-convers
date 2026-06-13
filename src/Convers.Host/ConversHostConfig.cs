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
    /// <summary>The packet.net convers home channel — where local users land at connect (Tom's pick, 2026-06-13).</summary>
    public const int DefaultChannelNumber = 2723;

    /// <summary>
    /// Explicit callsign override. Normally <b>blank</b> — the callsign is derived automatically from
    /// the node (<see cref="ConversIdentity"/>: <c>&lt;node-callsign&gt;-&lt;ssid&gt;</c>). Set this only
    /// to force a specific callsign, which then wins verbatim (including any SSID).
    /// </summary>
    public string Callsign { get; init; } = "";

    /// <summary>Preferred SSID (0–15) for the auto-derived callsign; ignored when <see cref="Callsign"/> is set.</summary>
    public int Ssid { get; init; } = ConversIdentity.DefaultSsid;

    /// <summary>Sysop callsign (console sysop rights; sysop view in the web tile).</summary>
    public string Sysop { get; init; } = "";

    /// <summary>
    /// The operator secret a local user presents with <c>oper &lt;secret&gt;</c> to gain operator status
    /// (mirrors conversd <c>SecretPass</c>/<c>SecretNum</c>: a configured secret/password). Blank disables
    /// operator login entirely (conversd's <c>SecretNum 0</c> behaviour) — no secret can be matched.
    /// </summary>
    public string OperatorSecret { get; init; } = "";

    /// <summary>
    /// The system-information string answered to an inbound <c>/..SYSI</c> (SPECS line 136). Typically the
    /// sysop's email / a one-line node description. Blank returns only the identity/version line.
    /// </summary>
    public string Sysinfo { get; init; } = "";

    /// <summary>
    /// The channel packet.net users land on at connect without choosing (design.md decision: a fixed
    /// default channel; the handover reserves 256–32767, avoiding collisions).
    /// </summary>
    public int DefaultChannel { get; init; } = DefaultChannelNumber;

    /// <summary>Web-chat bind (loopback per the app-gateway contract).</summary>
    public WebConfig Web { get; init; } = new();

    /// <summary>The node's RHPv2 endpoint (the packet plane our callsign binds on).</summary>
    public RhpConfig Rhp { get; init; } = new();

    /// <summary>The single upstream convers host link (design.md decision 6). Unset until a parent exists.</summary>
    public UplinkConfig Uplink { get; init; } = new();

    /// <summary>
    /// Downstream-peering toggle (W7c — design decisions 1 and 4): whether the node accepts an inbound
    /// <c>/..HOST</c> from an allowlisted callsign and becomes a small hub instead of a strict leaf.
    /// <b>Off by default</b>; leave it that way unless you intend to host downstream convers peers.
    /// </summary>
    public PeeringConfig Peering { get; init; } = new();
}

/// <summary>
/// Inbound downstream-peering configuration (W7c). Mirrors conversd <c>Access … HOST</c> + the optional
/// host-link password: an explicit callsign allowlist gates who may join as a downstream peer, and an
/// optional shared password is required when set (host.c <c>check_password</c>). Disabled by default — the
/// node stays a strict leaf (design decision 1).
/// </summary>
public sealed record PeeringConfig
{
    /// <summary>Whether to accept inbound downstream HOST peers at all. Default false (strict leaf).</summary>
    public bool Enabled { get; init; }

    /// <summary>The callsigns allowed to join as downstream peers (the <c>Access … HOST</c> allowlist).</summary>
    public List<string> Allow { get; init; } = [];

    /// <summary>Optional shared link password a downstream peer must present (via <c>/..PASS</c>) to be admitted.</summary>
    public string? Password { get; init; }
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

    /// <summary>
    /// Whether <em>we</em> initiate the conversd-saupp Huffman compression offer (<c>//COMP 1</c>) on the host
    /// link (both the uplink and accepted downstream peers) once the <c>/..HOST</c> handshake completes.
    /// <b>Off by default</b> — the safe, no-regression posture: initiating arms our transmit side, so it only
    /// belongs toward a peer known to honour host-link compression (stock conversd honours <c>/comp</c> on
    /// USER links only). Regardless of this flag we always reciprocate a peer's own <c>//COMP 1</c>, so a
    /// peer that offers compression first still gets a compressed link both ways. Set true only toward a
    /// known-supporting parent/peer.
    /// </summary>
    public bool Compression { get; init; }

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
        # callsign: USUALLY LEAVE BLANK. pdn-convers derives its on-air callsign automatically from
        #           the node it runs under — the pdn convention is that an app lives at an SSID of the
        #           node callsign, so the callsign is <node-callsign>-<ssid>, probe-walking to the next
        #           free SSID if that one is already taken. Set this ONLY to force a specific callsign;
        #           it then wins verbatim (including any SSID you put here).
        callsign: ""

        # ssid: preferred SSID (0-15) for the auto-derived callsign. Ignored when callsign is set above.
        ssid: 4

        # sysop: the sysop's callsign (sysop rights on the console; sysop view in the web tile).
        sysop: ""

        # operatorSecret: the secret a local user types as `oper <secret>` to gain operator status
        #                 (mirrors conversd SecretPass/SecretNum). Operators can set channel modes and the
        #                 topic on +t (topic-locked) channels. Leave blank to disable operator login.
        operatorSecret: ""

        # sysinfo: one line of system information answered to a network /..SYSI query (e.g. the sysop's
        #          email or a short node description). Leave blank for just the identity/version line.
        sysinfo: ""

        # defaultChannel: the channel packet.net users land on at connect without choosing — the
        #                 packet.net convers "home" channel. Must be 0..32767; 2723 is the chosen public
        #                 number (change it only if it collides with something on your branch of the net).
        defaultChannel: 2723

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
          compression: false        # initiate the conversd //COMP Huffman compression offer after the
                                    # handshake. OFF by default (safe): initiating compresses our output, so
                                    # only enable it toward a peer KNOWN to support host-link compression
                                    # (stock conversd does //comp on USER links only). We always RECIPROCATE a
                                    # peer's own //COMP offer regardless, so a supporting peer still gets a
                                    # compressed link. Applies to the uplink AND accepted downstream peers.
          # rf:
          #   call: GB7XYZ          # the neighbour convers node to open over RF
          # tcp:
          #   host: 44.68.41.2      # e.g. HubNA (NA); find/confirm a UK/EU hub
          #   port: 3600

        # peering: accept INBOUND downstream convers peers (the net is a TREE — one primary uplink, and
        #          downstream peers below us). OFF by default: pdn-convers is a strict leaf. Enable this
        #          only to host downstream peers. `allow` is the explicit callsign allowlist (mirrors
        #          conversd `Access … HOST`); a non-allowlisted /..HOST is treated as ordinary user input.
        #          `password`, if set, must be presented by the peer (via /..PASS) before its /..HOST.
        peering:
          enabled: false
          allow: []                 # e.g. [GB7XYZ, M0ABC-1]
          password: null
        """;
}
