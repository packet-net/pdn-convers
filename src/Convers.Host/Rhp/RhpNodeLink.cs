using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RhpV2.Client;
using RhpV2.Client.Protocol;

namespace Convers.Host.Rhp;

/// <summary>Static configuration for the node link.</summary>
public sealed record RhpLinkOptions
{
    /// <summary>RHP server host.</summary>
    public required string Host { get; init; }

    /// <summary>RHP server port.</summary>
    public required int Port { get; init; }

    /// <summary>
    /// The preferred callsign to bind (with its SSID). On a duplicate/in-use refusal the link
    /// probe-walks the SSID (0–15) from this one until a free SSID binds (design decision 4).
    /// </summary>
    public required string PreferredCallsign { get; init; }

    /// <summary>Auth user, when the node requires auth.</summary>
    public string? User { get; init; }

    /// <summary>Auth password.</summary>
    public string? Pass { get; init; }

    /// <summary>First reconnect delay after a connection loss.</summary>
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Reconnect backoff cap.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// The resilient RHPv2 attachment to the node (design.md src/Convers.Host: "RHPv2 client … binding the
/// convers callsign — inbound accept → a local USER session"). Owns one <see cref="RhpClient"/> at a
/// time: connect → optional <c>auth</c> → <c>socket</c>/<c>bind</c>(all ports)/<c>listen</c> for the
/// convers callsign, then surfaces accepted children on <see cref="Accepted"/> and routes
/// <c>recv</c>/server <c>close</c> pushes to their <see cref="RhpChildConnection"/>s. When the node
/// restarts every child faults, and the link reconnects with TimeProvider-driven exponential backoff,
/// re-binding on every reconnect (mirroring pdn-bbs <c>RhpNodeLink</c>).
/// </summary>
/// <remarks>
/// The SSID probe-walk (design decision 4, deferred to this wave): when the preferred callsign is
/// refused as a <see cref="RhpErrorCode.DuplicateSocket"/> (or an in-use local address) the link
/// increments the SSID (staying within 0–15) and retries the bind until one binds, then listens on it.
/// The callsign actually bound is published on <see cref="BoundCallsign"/> and logged.
/// </remarks>
public sealed class RhpNodeLink : IAsyncDisposable
{
    private readonly RhpLinkOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    private readonly Channel<RhpChildConnection> _accepted = Channel.CreateUnbounded<RhpChildConnection>();
    private readonly ConcurrentDictionary<int, RhpChildConnection> _children = new();
    private readonly Dictionary<int, List<byte[]>> _pendingRecv = [];
    private readonly object _gate = new();

    private volatile RhpClient? _client;
    private volatile TaskCompletionSource _up = NewTcs();

    /// <summary>Creates the link (call <see cref="RunAsync"/> to start it).</summary>
    public RhpNodeLink(RhpLinkOptions options, TimeProvider time, ILogger<RhpNodeLink> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PreferredCallsign);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _time = time;
        _logger = logger;
        BoundCallsign = options.PreferredCallsign;
    }

    /// <summary>Inbound connections accepted for the bound convers callsign.</summary>
    public ChannelReader<RhpChildConnection> Accepted => _accepted.Reader;

    /// <summary>
    /// The callsign actually bound on the node — the preferred one, or a probe-walked SSID when the
    /// preferred one was in use. Updated on each (re)bind; defaults to the preferred callsign.
    /// </summary>
    public string BoundCallsign { get; private set; }

    /// <summary>Whether the link is currently connected and bound.</summary>
    public bool IsUp => _up.Task.IsCompletedSuccessfully;

    /// <summary>Completes when the link is connected and bound (a new wait per outage).</summary>
    public Task WaitForUpAsync(CancellationToken cancellationToken) => _up.Task.WaitAsync(cancellationToken);

    /// <summary>The connect/bind/dispatch loop; runs until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        TimeSpan backoff = _options.InitialBackoff;
        while (!cancellationToken.IsCancellationRequested)
        {
            RhpClient? client = null;
            var lost = NewTcs();
            try
            {
                client = await RhpClient.ConnectAsync(_options.Host, _options.Port, cancellationToken).ConfigureAwait(false);
                client.Received += (_, e) => OnReceived(e.Message);
                client.Accepted += (_, e) => OnAccepted(e.Message);
                client.Closed += (_, e) => OnClosed(e.Handle);
                client.Disconnected += (_, _) => lost.TrySetResult();

                if (_options.User is { Length: > 0 } user)
                {
                    await client.AuthenticateAsync(user, _options.Pass ?? "", cancellationToken).ConfigureAwait(false);
                }

                int listener = await client.SocketAsync(ProtocolFamily.Ax25, SocketMode.Stream, cancellationToken).ConfigureAwait(false);

                // SSID probe-walk: bind the preferred callsign, stepping the SSID on a duplicate/in-use
                // refusal until one binds (design decision 4 — the part earlier waves deferred to here).
                string bound = await BindWalkingSsidAsync(client, listener, cancellationToken).ConfigureAwait(false);
                await client.ListenAsync(listener, OpenFlags.Passive, cancellationToken).ConfigureAwait(false);

                BoundCallsign = bound;
                _client = client;
                _up.TrySetResult();
                backoff = _options.InitialBackoff;
                LogBound(_logger, bound, _options.Host, _options.Port, null);

                await using CancellationTokenRegistration registration =
                    cancellationToken.Register(() => lost.TrySetResult());
                await lost.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogConnectFailed(_logger, _options.Host, _options.Port, ex.Message, null);
            }
            finally
            {
                if (_up.Task.IsCompleted)
                {
                    _up = NewTcs();
                }

                _client = null;
                FaultAllChildren();
                if (client is not null)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            LogReconnectWait(_logger, backoff.TotalSeconds, null);
            try
            {
                await Task.Delay(backoff, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = backoff * 2 > _options.MaxBackoff ? _options.MaxBackoff : backoff * 2;
        }

        _accepted.Writer.TryComplete();
    }

    /// <summary>
    /// Binds <paramref name="listener"/> to the preferred callsign, probe-walking the SSID (0–15) on a
    /// duplicate/in-use refusal until one binds. Returns the callsign that bound. Throws the original
    /// refusal once every SSID 0–15 has been tried, or immediately for a non-in-use error.
    /// </summary>
    private async Task<string> BindWalkingSsidAsync(RhpClient client, int listener, CancellationToken cancellationToken)
    {
        RhpServerException? last = null;
        foreach (string candidate in SsidProbeWalk.Candidates(_options.PreferredCallsign))
        {
            try
            {
                await client.BindAsync(listener, candidate, port: null, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(candidate, _options.PreferredCallsign, StringComparison.OrdinalIgnoreCase))
                {
                    LogProbeWalked(_logger, _options.PreferredCallsign, candidate, null);
                }

                return candidate;
            }
            catch (RhpServerException ex) when (IsCallsignInUse(ex.ErrorCode))
            {
                LogBindInUse(_logger, candidate, ex.ErrorCode, null);
                last = ex;
            }
        }

        // Exhausted SSID 0–15 without a free one.
        throw last ?? new RhpServerException(
            RhpErrorCode.DuplicateSocket, $"No free SSID to bind {_options.PreferredCallsign}.");
    }

    /// <summary>
    /// A bind refusal we treat as "this callsign is already in use" — the trigger for the SSID
    /// probe-walk: <see cref="RhpErrorCode.DuplicateSocket"/> (the canonical duplicate-bind code) and
    /// <see cref="RhpErrorCode.InvalidLocalAddress"/> (some nodes report an in-use local address so).
    /// </summary>
    private static bool IsCallsignInUse(int errorCode) =>
        errorCode is RhpErrorCode.DuplicateSocket or RhpErrorCode.InvalidLocalAddress;

    /// <summary>
    /// Opens an outbound connection (RHP <c>open</c>, Active) from the bound callsign to
    /// <paramref name="remote"/> — the RF uplink's dial path. <paramref name="port"/> pins the node
    /// port; null lets the node choose.
    /// </summary>
    /// <exception cref="InvalidOperationException">The link is down.</exception>
    public async Task<RhpChildConnection> OpenAsync(string remote, string? port, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remote);
        RhpClient client = _client ?? throw new InvalidOperationException("The RHP link is down.");
        int handle = await client.OpenAsync(
            ProtocolFamily.Ax25,
            SocketMode.Stream,
            port,
            local: BoundCallsign,
            remote: remote,
            OpenFlags.Active,
            cancellationToken).ConfigureAwait(false);
        var child = new RhpChildConnection(this, handle, remote);
        RegisterChild(child);
        return child;
    }

    internal async Task SendOnChildAsync(int handle, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        RhpClient client = _client ?? throw new InvalidOperationException("The RHP link is down.");
        SendReplyMessage reply = await client.SendOnHandleAsync(handle, data.Span, cancellationToken).ConfigureAwait(false);
        if (reply.ErrCode != 0)
        {
            throw new IOException($"RHP send on handle {handle} failed: {reply.ErrCode} {reply.ErrText}");
        }
    }

    internal async Task CloseChildAsync(int handle, CancellationToken cancellationToken)
    {
        if (_children.TryRemove(handle, out RhpChildConnection? child))
        {
            child.MarkClosed();
        }

        RhpClient? client = _client;
        if (client is null)
        {
            return;
        }

        try
        {
            await client.CloseAsync(handle, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCloseFailed(_logger, handle, ex.Message, null);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        RhpClient? client = _client;
        _client = null;
        FaultAllChildren();
        _accepted.Writer.TryComplete();
        if (client is not null)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnAccepted(AcceptMessage message)
    {
        var child = new RhpChildConnection(this, message.Child, message.Remote ?? "");
        RegisterChild(child);
        LogAccepted(_logger, child.RemoteCallsign, child.Handle, null);
        _accepted.Writer.TryWrite(child);
    }

    private void OnReceived(RecvMessage message)
    {
        byte[] data = RhpDataEncoding.FromWireString(message.Data ?? "");
        lock (_gate)
        {
            if (_children.TryGetValue(message.Handle, out RhpChildConnection? child))
            {
                child.Deliver(data);
                return;
            }

            // recv raced ahead of our handle registration — stash until the child exists.
            if (!_pendingRecv.TryGetValue(message.Handle, out List<byte[]>? pending))
            {
                pending = [];
                _pendingRecv[message.Handle] = pending;
            }

            pending.Add(data);
        }
    }

    private void OnClosed(int handle)
    {
        lock (_gate)
        {
            _pendingRecv.Remove(handle);
        }

        if (_children.TryRemove(handle, out RhpChildConnection? child))
        {
            child.MarkClosed();
        }
    }

    private void RegisterChild(RhpChildConnection child)
    {
        lock (_gate)
        {
            _children[child.Handle] = child;
            if (_pendingRecv.Remove(child.Handle, out List<byte[]>? pending))
            {
                foreach (byte[] chunk in pending)
                {
                    child.Deliver(chunk);
                }
            }
        }
    }

    private void FaultAllChildren()
    {
        lock (_gate)
        {
            _pendingRecv.Clear();
        }

        foreach (int handle in _children.Keys)
        {
            if (_children.TryRemove(handle, out RhpChildConnection? child))
            {
                child.MarkClosed();
            }
        }
    }

    private static TaskCompletionSource NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static readonly Action<ILogger, string, string, int, Exception?> LogBound =
        LoggerMessage.Define<string, string, int>(LogLevel.Information, new EventId(1, "RhpBound"),
            "Bound {Callsign} on RHP at {Host}:{Port}");

    private static readonly Action<ILogger, string, int, string, Exception?> LogConnectFailed =
        LoggerMessage.Define<string, int, string>(LogLevel.Warning, new EventId(2, "RhpConnectFailed"),
            "RHP connection to {Host}:{Port} failed: {Reason}");

    private static readonly Action<ILogger, double, Exception?> LogReconnectWait =
        LoggerMessage.Define<double>(LogLevel.Information, new EventId(3, "RhpReconnectWait"),
            "Reconnecting to RHP in {Seconds}s");

    private static readonly Action<ILogger, string, int, Exception?> LogAccepted =
        LoggerMessage.Define<string, int>(LogLevel.Information, new EventId(4, "RhpAccepted"),
            "Inbound connection from {Remote} (handle {Handle})");

    private static readonly Action<ILogger, int, string, Exception?> LogCloseFailed =
        LoggerMessage.Define<int, string>(LogLevel.Debug, new EventId(5, "RhpCloseFailed"),
            "RHP close of handle {Handle} failed: {Reason}");

    private static readonly Action<ILogger, string, int, Exception?> LogBindInUse =
        LoggerMessage.Define<string, int>(LogLevel.Information, new EventId(6, "RhpBindInUse"),
            "Callsign {Callsign} is in use (errCode {ErrCode}); probe-walking the SSID");

    private static readonly Action<ILogger, string, string, Exception?> LogProbeWalked =
        LoggerMessage.Define<string, string>(LogLevel.Warning, new EventId(7, "RhpProbeWalked"),
            "Preferred callsign {Preferred} was in use; bound {Bound} instead");
}
