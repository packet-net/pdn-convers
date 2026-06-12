using Convers.Console;
using Convers.Core;
using Convers.Host.Rhp;
using Convers.Host.Sessions;
using Convers.Host.Tests.Rhp;
using Convers.Host.Tests.Uplink;
using Convers.Host.Uplink;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Convers.Host.Tests.Sessions;

/// <summary>
/// The composed inbound path under test: real <see cref="RhpNodeLink"/> + <see cref="HostLink"/> +
/// <see cref="LocalSessionRegistry"/> + <see cref="InboundDemux"/> against a <see cref="FakeRhpServer"/>,
/// driving the shared <see cref="ConversHub"/>. The uplink is a <see cref="ScriptedUpstreamLink"/> so the
/// test can both observe what the node sends a parent (<c>/..USER</c>, <c>/..CMSG</c>) and push inbound
/// network traffic. Loops start in the constructor; dispose tears everything down.
/// </summary>
internal sealed class DemuxHarness : IAsyncDisposable
{
    public const string NodeCall = "M0LTE-4";
    public const string HostName = "M0LTE";
    public const int DefaultChannel = 3333;

    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _loops = [];

    public DemuxHarness(
        IConsolePreferences? preferences = null,
        ConversStore? store = null,
        string nodeCall = NodeCall,
        bool noUplink = false)
    {
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 12, 12, 0, 0, TimeSpan.Zero));
        Server = new FakeRhpServer();
        Server.Start();

        Oracle = new ScriptedUpstreamLink();
        IUpstreamLinkFactory factory = noUplink
            ? NullUpstreamLinkFactory.Instance
            : new ScriptedUpstreamLinkFactory().EnqueueLink(Oracle);
        Factory = factory;
        Registry = new LocalSessionRegistry();
        Store = store;
        ChatLog = store is null ? null : new ChatLogWriter(store, NullLogger<ChatLogWriter>.Instance);

        Hub = new ConversHub(HostName, Time, store);
        Link = new HostLink(
            new HostLinkOptions { HostName = HostName },
            Factory,
            Hub,
            Time,
            NullLogger<HostLink>.Instance,
            Registry,
            ChatLog);

        Node = new RhpNodeLink(
            new RhpLinkOptions { Host = "127.0.0.1", Port = Server.Port, PreferredCallsign = nodeCall },
            Time,
            NullLogger<RhpNodeLink>.Instance);

        Demux = new InboundDemux(
            Node,
            Link,
            Registry,
            preferences ?? new FixedPreferences(ConsoleInterface.Plain),
            ChatLog,
            new RfSessionConfig { NodeName = HostName, DefaultChannel = DefaultChannel, PageLength = 0 },
            NullLogger<InboundDemux>.Instance);

        _loops.Add(Node.RunAsync(_cts.Token));
        _loops.Add(Link.RunAsync(_cts.Token));
        _loops.Add(Demux.RunAsync(_cts.Token));
    }

    public FakeTimeProvider Time { get; }

    public FakeRhpServer Server { get; }

    public ScriptedUpstreamLink Oracle { get; }

    public IUpstreamLinkFactory Factory { get; }

    public LocalSessionRegistry Registry { get; }

    public ConversStore? Store { get; }

    public ChatLogWriter? ChatLog { get; }

    public ConversHub Hub { get; }

    public HostLink Link { get; }

    public RhpNodeLink Node { get; }

    public InboundDemux Demux { get; }

    public CancellationToken Token => _cts.Token;

    /// <summary>Completes the uplink handshake so the link is established (then <c>/..USER</c> etc. flow up).</summary>
    public async Task BringUplinkUpAsync()
    {
        _ = await Oracle.ReadSentAsync(TimeSpan.FromSeconds(5)); // our /..HOST
        Oracle.PushLine(ConversCommandLine("HOST ORACLE saupp1.62a Aadmpunfi"));
        await Link.WaitForUpAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
    }

    /// <summary>Builds a host-command wire line with the real 3-byte prefix.</summary>
    public static string ConversCommandLine(string body) => Convers.Protocol.ConversCommand.HostCommandPrefix + body;

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try
        {
            await Task.WhenAll(_loops).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // Best-effort teardown.
        }

        await Link.DisposeAsync();
        await Node.DisposeAsync();
        await Server.DisposeAsync();
        _cts.Dispose();
    }
}

/// <summary>A fixed per-callsign surface for the demux tests (no disk).</summary>
internal sealed class FixedPreferences(ConsoleInterface surface) : IConsolePreferences
{
    public ConsoleInterface GetInterface(string callsign) => surface;
}
