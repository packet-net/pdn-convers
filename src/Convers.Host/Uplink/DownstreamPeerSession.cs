using System.Threading.Channels;
using Convers.Core;
using Convers.Protocol;
using Microsoft.Extensions.Logging;
using CoreChannel = Convers.Core.Channel;

namespace Convers.Host.Uplink;

/// <summary>
/// One accepted <em>downstream</em> host peer (W7c, the post-v1 peering toggle — design decisions 1 and 4):
/// the inbound mirror of the uplink <see cref="HostLink"/>. A peer dialled our bound callsign and its first
/// line was an allowlisted <c>/..HOST</c>; the demux hands the connection here instead of starting a USER
/// console session. This drives the inbound <see cref="HostLinkEngine"/> (so the handshake reply,
/// PING/PONG keepalive, and ROUT/SYSI/LOOP answers are shared with the uplink role), announces current
/// presence to the new peer, then bridges the peer to the shared hub <em>through</em> the
/// <see cref="HostLink"/>: content commands are submitted via <see cref="HostLink.SubmitPeerInboundAsync"/>
/// (relay to the other hosts + local fan-out on the hub's single owning loop), and the hub's local-origin
/// traffic is fanned back to this peer because the session registers itself as an <see cref="IPeerSink"/>.
/// </summary>
/// <remarks>
/// <para>
/// The net is a TREE: this node has one primary uplink and accepts downstream peers, never transiting in a
/// way that forms a loop (the relay's loop guard, <see cref="PeerRelay"/>, drops the echo back to a peer
/// and never relays the link-local control verbs). PING/PONG/HOST/ROUT/SYSI/LOOP are answered by this
/// session's own engine and are NOT submitted to the shared hub, so they stay per-link.
/// </para>
/// <para>
/// I/O-owning, like <see cref="HostLink"/>: the engine is sans-IO. The session reads peer lines on its own
/// task and writes through a small outbound queue (<see cref="IPeerSink.Enqueue"/> from the hub loop is
/// non-blocking — it just queues). Never throws out of its lifetime; on any loss it unregisters and closes.
/// </para>
/// </remarks>
public sealed class DownstreamPeerSession : IPeerSink
{
    private readonly IUpstreamLink _transport;
    private readonly HostLink _hostLink;
    private readonly ConversHub _hub;
    private readonly HostLinkOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly TimeSpan _tickInterval;

    // Outbound queue: Enqueue (called from the hub's owning loop) must never block, so it writes here and a
    // dedicated writer task drains it onto the transport in order.
    private readonly Channel<HostCommand> _outbound =
        System.Threading.Channels.Channel.CreateUnbounded<HostCommand>(
            new UnboundedChannelOptions { SingleReader = true });

    private volatile string _peerHostName = string.Empty;

    /// <summary>Creates a downstream-peer session over an accepted transport. <paramref name="peerId"/> is unique per node.</summary>
    public DownstreamPeerSession(
        string peerId,
        IUpstreamLink transport,
        HostLink hostLink,
        ConversHub hub,
        HostLinkOptions options,
        TimeProvider timeProvider,
        ILogger logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerId);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(hostLink);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        PeerId = peerId;
        _transport = transport;
        _hostLink = hostLink;
        _hub = hub;
        _options = options.Validate();
        _time = timeProvider;
        _logger = logger;
        _tickInterval = TimeSpan.FromMilliseconds(Math.Max(50, _options.PingInterval.TotalMilliseconds / 4));
    }

    /// <inheritdoc/>
    public string PeerId { get; }

    /// <inheritdoc/>
    public string PeerHostName => _peerHostName;

    /// <inheritdoc/>
    public void Enqueue(HostCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _outbound.Writer.TryWrite(command);
    }

    /// <summary>
    /// Runs the peer's whole lifetime to completion: handshake, presence announce, then the steady-state
    /// receive/keepalive loop, unregistering and closing on loss. Never throws (failures are logged).
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var engine = new HostLinkEngine(_options, _time, inbound: true);
        Task writer = WriterLoopAsync(cancellationToken);
        bool registered = false;

        // Content lines a (misbehaving) peer sends before its /..HOST completes the handshake: buffered and
        // replayed to the hub right after we register + announce, so nothing the peer sent is lost.
        var earlyContent = new List<string>();
        try
        {
            ApplyStep(engine.OnAccepted());

            Task<string?> receive = _transport.ReceiveLineAsync(cancellationToken);
            using var ticker = new PeriodicTimer(_tickInterval, _time);
            Task<bool> tick = ticker.WaitForNextTickAsync(cancellationToken).AsTask();

            while (true)
            {
                Task completed = await Task.WhenAny(receive, tick).ConfigureAwait(false);
                if (completed == receive)
                {
                    string? line = await receive.ConfigureAwait(false);
                    if (line is null)
                    {
                        LogPeerClosed(_logger, PeerId, null);
                        break;
                    }

                    EngineStep step = engine.OnLineReceived(line);
                    if (!ApplyStep(step))
                    {
                        break; // engine asked to drop
                    }

                    if (step.HandshakeCompleted)
                    {
                        _peerHostName = engine.PeerHostName;
                        _hostLink.RegisterPeer(this);
                        registered = true;
                        LogEstablished(_logger, PeerId, engine.PeerHostName,
                            FacilitiesCodec.Format(engine.NegotiatedFacilities), null);
                        await AnnouncePresenceAsync(cancellationToken).ConfigureAwait(false);
                        foreach (string early in earlyContent)
                        {
                            await SubmitContentAsync(early, cancellationToken).ConfigureAwait(false);
                        }

                        earlyContent.Clear();
                    }
                    else if (registered)
                    {
                        await SubmitContentAsync(line, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        earlyContent.Add(line); // content before the handshake completed — replay it later
                    }

                    receive = _transport.ReceiveLineAsync(cancellationToken);
                }
                else
                {
                    if (!await tick.ConfigureAwait(false))
                    {
                        break;
                    }

                    if (!ApplyStep(engine.OnTick()))
                    {
                        break;
                    }

                    tick = ticker.WaitForNextTickAsync(cancellationToken).AsTask();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // shutdown
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPeerFailed(_logger, PeerId, ex.Message, null);
        }
        finally
        {
            if (registered)
            {
                _hostLink.UnregisterPeer(PeerId);
            }

            _outbound.Writer.TryComplete();
            await SwallowAsync(writer).ConfigureAwait(false);
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Carries out an engine step against the peer: send its outbound commands, then if it surfaced a hub
    /// event for PING (answered via the hub's PONG policy) emit the PONG to the peer. Returns false on a
    /// drop decision. Hub <em>content</em> (presence/messages/…) is NOT applied here — it is submitted to
    /// the shared hub from the receive loop, after the handshake, so the single-owning-loop invariant holds.
    /// </summary>
    private bool ApplyStep(EngineStep step)
    {
        foreach (HostCommand command in step.OutboundCommands)
        {
            Enqueue(command);
        }

        // The inbound engine surfaces a peer PING as a hub event; answer it per the hub's policy (PONG with
        // the no-measurement sentinel — matching the uplink path) directly back to this peer, not relayed.
        foreach (ConversEvent @event in step.HubEvents)
        {
            if (@event is ConversEvent.HostPing)
            {
                Enqueue(new HostPong(PongNoMeasurement));
            }
        }

        return step.DropReason is null;
    }

    /// <summary>
    /// Submits one inbound content line to the shared hub for relay + local fan-out, on the hub's owning
    /// loop. Only commands that carry network content are submitted; the link-local control verbs
    /// (HOST/PING/PONG/LOOP/ROUT/SYSI) are handled by the engine and never reach the hub or the relay.
    /// </summary>
    private async Task SubmitContentAsync(string line, CancellationToken cancellationToken)
    {
        if (!HostCommandCodec.TryParse(line, out HostCommand? command) || command is null)
        {
            return; // a greeting / human-readable notice; nothing to relay
        }

        if (IsLinkLocal(command))
        {
            return;
        }

        await _hostLink.SubmitPeerInboundAsync(command, PeerId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Announces current presence to the freshly-established peer so it has the full network picture
    /// (mirroring conversd <c>h_host_command</c>, which sends topics and every user after accepting a host).
    /// A read-only hub snapshot taken on the owning loop; we send every known user as a <c>/..USER</c> join
    /// plus their personal/away, and each channel's topic and modes.
    /// </summary>
    private async Task AnnouncePresenceAsync(CancellationToken cancellationToken)
    {
        List<HostCommand> announce = await _hostLink.SnapshotAsync(hub =>
        {
            var commands = new List<HostCommand>();
            foreach (NetworkUser user in hub.NetworkUsers)
            {
                long joined = HostBridge.ToUnix(user.JoinedAt);
                commands.Add(new HostUser(user.Name, user.Host, joined, -1, user.Channel, user.Personal));
                if (user.Personal.Length != 0)
                {
                    commands.Add(new HostUserData(user.Name, user.Host, user.Personal));
                }

                if (user.Away.Length != 0)
                {
                    commands.Add(new HostAway(user.Name, user.Host, joined, user.Away));
                }
            }

            foreach (CoreChannel channel in hub.ListChannels(includeHidden: true))
            {
                if (channel.Topic.Length != 0)
                {
                    long at = channel.TopicSetAt is { } t ? HostBridge.ToUnix(t) : 0;
                    commands.Add(new HostTopic(
                        channel.TopicSetBy.Length == 0 ? "conversd" : channel.TopicSetBy,
                        hub.HostName, at, channel.Number, channel.Topic));
                }

                if (channel.Modes != ChannelMode.None)
                {
                    commands.Add(new HostMode(channel.Number, ChannelModes.ToWire(channel.Modes)));
                }
            }

            return commands;
        }, cancellationToken).ConfigureAwait(false);

        foreach (HostCommand command in announce)
        {
            Enqueue(command);
        }
    }

    /// <summary>Drains the outbound queue onto the transport in order; ends when the queue completes or the link drops.</summary>
    private async Task WriterLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (HostCommand command in _outbound.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await _transport.SendLineAsync(HostCommandCodec.Format(command), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException or IOException or InvalidOperationException)
        {
            // The transport died or we are shutting down; the receive loop's null/loss handles teardown.
        }
    }

    /// <summary>The link-local control verbs the engine owns — never submitted to the hub or relayed.</summary>
    private static bool IsLinkLocal(HostCommand command) =>
        command is HostHandshake or HostPing or HostPong or HostLoop or HostRoute or HostSysInfo;

    private static async Task SwallowAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // best-effort teardown
        }
    }

    /// <summary>PONG sentinel: "I do not make link measurements" (SPECS /..PONG -1), matching the hub's policy.</summary>
    private const long PongNoMeasurement = -1;

    private static readonly Action<ILogger, string, string, string, Exception?> LogEstablished =
        LoggerMessage.Define<string, string, string>(LogLevel.Information, new EventId(1, "PeerEstablished"),
            "Downstream peer {PeerId} ({Peer}) established (facilities {Facilities})");

    private static readonly Action<ILogger, string, Exception?> LogPeerClosed =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(2, "PeerClosed"),
            "Downstream peer {PeerId} closed the connection");

    private static readonly Action<ILogger, string, string, Exception?> LogPeerFailed =
        LoggerMessage.Define<string, string>(LogLevel.Warning, new EventId(3, "PeerFailed"),
            "Downstream peer {PeerId} failed: {Reason}");
}
