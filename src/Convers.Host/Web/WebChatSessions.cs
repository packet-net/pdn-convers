using System.Collections.Concurrent;
using Convers.Core;
using Convers.Host.Sessions;
using Convers.Host.Uplink;

namespace Convers.Host.Web;

/// <summary>
/// The web users' presence into the shared <see cref="ConversHub"/> — the web analogue of an
/// <see cref="RfUserSession"/> (design decision 8: "web users join the same channels as RF users").
/// A web user is a <em>local session</em> like an RF user: it is registered in the
/// <see cref="LocalSessionRegistry"/> and joined to a channel through the <see cref="HostLink"/> seam,
/// so anything it says fans out to the RF and web users on that channel (and goes upstream), and RF
/// users see the web user present in <c>who</c>.
/// </summary>
/// <remarks>
/// <para>
/// HTTP is stateless and the tile is server-rendered (refresh / short-poll, pdn-bbs webmail style), so
/// this manager keeps one long-lived local session per callsign across requests. The session is created
/// lazily on the user's first mutating action (or first channel view) and kept joined so RF users keep
/// seeing the web user. Every mutation (<c>say</c> / <c>join</c> / <c>topic</c> / <c>msg</c> / <c>away</c>)
/// is submitted through <see cref="HostLink.SubmitLocalEventAsync"/> — the SAME hub seam RF users use —
/// and chat logging is centralised at that link's fan-out (design decision 7), so a web user's chat is
/// logged exactly once, with no per-session call to bypass, mirroring <see cref="RfUserSession"/>.
/// </para>
/// <para>
/// A web user's inbound deliveries (other people's messages) are rendered from the durable chat log
/// (<see cref="ConversStore.QueryChatLog"/>) on each page render, not pushed; the registered sink only
/// buffers a small recent tail so the contract with the registry is symmetric with an RF session (the
/// hub treats the web user as a real present local listener). Idle sessions are swept (a
/// <see cref="ConversEvent.LocalLeave"/> + unregister) after <see cref="IdleTimeout"/> with no activity,
/// driven lazily on access and at dispose — no background loop, so nothing new to schedule.
/// </para>
/// </remarks>
public sealed class WebChatSessions : IAsyncDisposable
{
    /// <summary>How long a web session may sit idle (no page view / action) before it is signed off.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(15);

    private readonly HostLink _link;
    private readonly LocalSessionRegistry _registry;
    private readonly TimeProvider _time;
    private readonly int _defaultChannel;
    private readonly string _operatorSecret;

    // One live session per callsign. Keyed on the canonical callsign so case never splits a user.
    private readonly ConcurrentDictionary<string, WebSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>Creates the manager. <paramref name="defaultChannel"/> is where a new web user lands.</summary>
    public WebChatSessions(
        HostLink link,
        LocalSessionRegistry registry,
        TimeProvider timeProvider,
        int defaultChannel,
        string operatorSecret = "")
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _link = link;
        _registry = registry;
        _time = timeProvider;
        _defaultChannel = defaultChannel;
        _operatorSecret = operatorSecret ?? "";
    }

    /// <summary>The number of live web sessions (diagnostics / tests).</summary>
    public int Count => _sessions.Count;

    /// <summary>
    /// The channel a web user is currently on — their live session's channel, or the default when they
    /// have no session yet (a first-time visitor who has not acted). Does not create a session.
    /// </summary>
    public async ValueTask<int> CurrentChannelAsync(string callsign, CancellationToken cancellationToken)
    {
        SweepIdle(cancellationToken);
        string call = Callsigns.Normalize(callsign);
        if (_sessions.TryGetValue(call, out WebSession? session))
        {
            session.Touch(_time.GetUtcNow());
            return await _link.SnapshotAsync(
                hub => hub.GetSession(session.Id)?.Channel ?? session.Channel, cancellationToken).ConfigureAwait(false);
        }

        return _defaultChannel;
    }

    /// <summary>
    /// A read-only snapshot of a channel's live presence and topic, taken on the hub's owning loop via the
    /// link seam (the hub is never read off-loop — design decision 2). Used by the channel view.
    /// </summary>
    public ValueTask<(IReadOnlyList<NetworkUser> Present, string Topic, string TopicBy, ChannelMode Modes)> SnapshotChannelAsync(
        int channel, CancellationToken cancellationToken) =>
        _link.SnapshotAsync(hub =>
        {
            Channel ch = hub.GetChannel(channel);
            return ((IReadOnlyList<NetworkUser>)ch.Users, ch.Topic, ch.TopicSetBy, ch.Modes);
        }, cancellationToken);

    /// <summary>
    /// A read-only snapshot for the <c>who</c> page: the users on <paramref name="channel"/> and the whole
    /// network table, taken on the hub's owning loop (never off-loop).
    /// </summary>
    public ValueTask<(IReadOnlyList<NetworkUser> Here, IReadOnlyList<NetworkUser> Network)> SnapshotWhoAsync(
        int channel, CancellationToken cancellationToken) =>
        _link.SnapshotAsync(hub =>
            ((IReadOnlyList<NetworkUser>)hub.GetChannel(channel).Users, hub.NetworkUsers), cancellationToken);

    /// <summary>
    /// Ensures the web user has a live joined session and returns its current channel. Use this before a
    /// mutating action so the user is present on the hub (and visible to RF users) when it lands.
    /// </summary>
    public async ValueTask<int> EnsureJoinedAsync(string callsign, CancellationToken cancellationToken)
    {
        WebSession session = await GetOrCreateAsync(callsign, cancellationToken).ConfigureAwait(false);
        return await _link.SnapshotAsync(
            hub => hub.GetSession(session.Id)?.Channel ?? session.Channel, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Says <paramref name="text"/> to the user's current channel (fans out to RF + web, goes upstream).</summary>
    public async ValueTask SayAsync(string callsign, string text, CancellationToken cancellationToken)
    {
        if (text.Length == 0)
        {
            return;
        }

        WebSession session = await GetOrCreateAsync(callsign, cancellationToken).ConfigureAwait(false);
        await _link.SubmitLocalEventAsync(new ConversEvent.LocalSay(session.Id, text), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Switches the user to <paramref name="channel"/> (joining it like an RF <c>join</c>).</summary>
    public async ValueTask JoinChannelAsync(string callsign, int channel, CancellationToken cancellationToken)
    {
        if (!ChannelNumber.IsValid(channel))
        {
            return;
        }

        WebSession session = await GetOrCreateAsync(callsign, cancellationToken).ConfigureAwait(false);
        await _link.SubmitLocalEventAsync(
            new ConversEvent.LocalSwitchChannel(session.Id, channel), cancellationToken).ConfigureAwait(false);
        session.Channel = channel;
    }

    /// <summary>Sets the topic of the user's current channel (the hub enforces <c>+t</c> for non-operators).</summary>
    public async ValueTask SetTopicAsync(string callsign, string topic, CancellationToken cancellationToken)
    {
        WebSession session = await GetOrCreateAsync(callsign, cancellationToken).ConfigureAwait(false);
        await _link.SubmitLocalEventAsync(
            new ConversEvent.LocalSetTopic(session.Id, topic), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a private message from the web user to another user (local or remote).</summary>
    public async ValueTask PrivateMessageAsync(string callsign, string toUser, string text, CancellationToken cancellationToken)
    {
        if (text.Length == 0 || toUser.Length == 0)
        {
            return;
        }

        WebSession session = await GetOrCreateAsync(callsign, cancellationToken).ConfigureAwait(false);
        await _link.SubmitLocalEventAsync(
            new ConversEvent.LocalPrivateMessage(session.Id, toUser, text), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets the modes of the user's current channel (the hub enforces operator status; a non-operator's
    /// request is refused). <paramref name="options"/> is the verbatim toggle string (e.g. <c>+mt</c>).
    /// </summary>
    public async ValueTask SetModeAsync(string callsign, string options, CancellationToken cancellationToken)
    {
        if (options.Trim().Length == 0)
        {
            return;
        }

        WebSession session = await GetOrCreateAsync(callsign, cancellationToken).ConfigureAwait(false);
        int channel = await _link.SnapshotAsync(
            hub => hub.GetSession(session.Id)?.Channel ?? session.Channel, cancellationToken).ConfigureAwait(false);
        await _link.SubmitLocalEventAsync(
            new ConversEvent.LocalSetMode(session.Id, channel, options.Trim()), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Operator login for a web user (<c>/..OPER</c> semantics): grants operator status when
    /// <paramref name="secret"/> matches the node operator secret. Returns true on success. A blank
    /// configured secret disables operator login entirely (conversd <c>SecretNum 0</c>).
    /// </summary>
    public async ValueTask<bool> TryOperAsync(string callsign, string secret, CancellationToken cancellationToken)
    {
        if (_operatorSecret.Length == 0 || !FixedTimeEquals(secret, _operatorSecret))
        {
            return false;
        }

        WebSession session = await GetOrCreateAsync(callsign, cancellationToken).ConfigureAwait(false);
        await _link.SubmitLocalEventAsync(
            new ConversEvent.LocalSetOperator(session.Id, -1, true), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>True when the web user currently has operator status (read from the live hub session).</summary>
    public async ValueTask<bool> IsOperatorAsync(string callsign, CancellationToken cancellationToken)
    {
        string call = Callsigns.Normalize(callsign);
        if (!_sessions.TryGetValue(call, out WebSession? session))
        {
            return false;
        }

        return await _link.SnapshotAsync(
            hub => hub.GetSession(session.Id)?.IsOperator ?? false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sets (empty clears) the web user's away message.</summary>
    public async ValueTask SetAwayAsync(string callsign, string away, CancellationToken cancellationToken)
    {
        WebSession session = await GetOrCreateAsync(callsign, cancellationToken).ConfigureAwait(false);
        await _link.SubmitLocalEventAsync(
            new ConversEvent.LocalSetAway(session.Id, away), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Constant-time secret comparison so the operator secret cannot be guessed by timing.</summary>
    private static bool FixedTimeEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));

    private async ValueTask<WebSession> GetOrCreateAsync(string callsign, CancellationToken cancellationToken)
    {
        SweepIdle(cancellationToken);
        string call = Callsigns.Normalize(callsign);
        if (_sessions.TryGetValue(call, out WebSession? existing))
        {
            existing.Touch(_time.GetUtcNow());
            return existing;
        }

        var session = new WebSession(call, _defaultChannel, _time.GetUtcNow());
        // Race: if another request created it first, adopt theirs and drop ours (it never joined).
        WebSession winner = _sessions.GetOrAdd(call, session);
        if (!ReferenceEquals(winner, session))
        {
            winner.Touch(_time.GetUtcNow());
            return winner;
        }

        // Register the session's (buffering) sink BEFORE the join so any immediate fan-out is captured,
        // exactly as RfUserSession does. The web tile renders scrollback from the chat log; the buffer
        // is a small symmetric tail so the hub treats the web user as a real present local listener.
        _registry.Register(session.Id, session.WriteLineAsync);
        await _link.SubmitLocalEventAsync(
            new ConversEvent.LocalJoin(session.Id, call, _defaultChannel), cancellationToken).ConfigureAwait(false);
        return session;
    }

    /// <summary>Signs off any web session idle longer than <see cref="IdleTimeout"/>. Cheap when there are none.</summary>
    private void SweepIdle(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();
        foreach (WebSession session in _sessions.Values)
        {
            if (now - session.LastSeen >= IdleTimeout && _sessions.TryRemove(
                    new KeyValuePair<string, WebSession>(session.Callsign, session)))
            {
                // Fire-and-forget the sign-off; a faulted submit during shutdown must not wedge a request.
                _ = SignOffAsync(session, "web session idle", cancellationToken);
            }
        }
    }

    private async Task SignOffAsync(WebSession session, string reason, CancellationToken cancellationToken)
    {
        try
        {
            await _link.SubmitLocalEventAsync(
                new ConversEvent.LocalLeave(session.Id, reason), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort sign-off; nothing to do if the link is gone.
        }
        finally
        {
            _registry.Unregister(session.Id);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        foreach (WebSession session in _sessions.Values)
        {
            if (_sessions.TryRemove(new KeyValuePair<string, WebSession>(session.Callsign, session)))
            {
                await SignOffAsync(session, "shutdown", CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>One live web user as a local hub session: its id, callsign, last-known channel and a small recent tail.</summary>
    private sealed class WebSession
    {
        private const int TailCapacity = 50;
        private readonly object _gate = new();
        private readonly Queue<string> _tail = new();
        private long _lastSeenTicks;

        public WebSession(string callsign, int channel, DateTimeOffset now)
        {
            Callsign = callsign;
            Channel = channel;
            Id = $"web-{callsign}";
            _lastSeenTicks = now.UtcTicks;
        }

        /// <summary>The opaque hub session id (stable per callsign so a reconnect reuses the same identity).</summary>
        public string Id { get; }

        /// <summary>The web user's canonical callsign.</summary>
        public string Callsign { get; }

        /// <summary>Last channel we asked the hub to put this user on (a cache; the hub is authoritative).</summary>
        public int Channel { get; set; }

        /// <summary>When this session was last touched by a request.</summary>
        public DateTimeOffset LastSeen => new(Interlocked.Read(ref _lastSeenTicks), TimeSpan.Zero);

        /// <summary>Marks the session active as of <paramref name="now"/>.</summary>
        public void Touch(DateTimeOffset now) => Interlocked.Exchange(ref _lastSeenTicks, now.UtcTicks);

        /// <summary>The registry sink: buffers the most recent delivered lines (bounded).</summary>
        public ValueTask WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _tail.Enqueue(line);
                while (_tail.Count > TailCapacity)
                {
                    _tail.Dequeue();
                }
            }

            return ValueTask.CompletedTask;
        }
    }
}
