namespace Convers.Core;

/// <summary>
/// The in-memory convers presence model for a strict-leaf node, exposed sans-IO. It holds channels,
/// local user sessions, the network user table, topics, personal text and away state, plus a
/// defensive loop/TTL guard, and bridges the two sides the leaf connects: local sessions ⇄ the one
/// uplink. Drive it with <see cref="Advance(ConversEvent)"/> — a local user joins/speaks/leaves, or a
/// host-level presence/message event arrives — and it returns the <see cref="ConversAction"/> fan-out
/// to perform. It performs no I/O, owns no threads and touches no socket (design decision 2).
///
/// <para>Live presence is in-memory only and rebuilt from the uplink on reconnect; only the saupp
/// differentiators (personal text, nicknames, passwords, topics) are persisted, in
/// <see cref="ConversStore"/> (design decision 7). The hub takes an optional store to hydrate
/// persisted topics/personal text and to write through topic and personal-text changes.</para>
///
/// <para>Not thread-safe: the Host drives it from a single owning loop. <see cref="TimeProvider"/>
/// is injected; the hub never reads the wall clock directly.</para>
/// </summary>
public sealed class ConversHub
{
    private readonly TimeProvider _time;
    private readonly ConversStore? _store;

    /// <summary>Local sessions keyed by their opaque session id.</summary>
    private readonly Dictionary<string, LocalSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>The network user table, keyed by canonical (name, host). Includes our own local users.</summary>
    private readonly Dictionary<(string Name, string Host), NetworkUser> _users = [];

    /// <summary>Per-channel topic + modes. Channels with no metadata are implied by membership.</summary>
    private readonly Dictionary<int, ChannelState> _channels = [];

    /// <summary>Callsigns with global-operator status (<c>/..OPER</c> with channel == -1).</summary>
    private readonly HashSet<string> _globalOps = new(StringComparer.Ordinal);

    /// <summary>Channel-operator grants, keyed by channel, holding the set of operator callsigns.</summary>
    private readonly Dictionary<int, HashSet<string>> _channelOps = [];

    /// <summary>Standing channel invitations, keyed by callsign, holding the set of invited channels.</summary>
    private readonly Dictionary<string, HashSet<int>> _invites = new(StringComparer.Ordinal);

    /// <summary>
    /// Constructs a hub for a leaf whose own host name is <paramref name="hostName"/> (how local
    /// users are presented upstream). When a <paramref name="store"/> is supplied, persisted topics
    /// are hydrated immediately and topic / personal-text changes are written through.
    /// </summary>
    public ConversHub(string hostName, TimeProvider timeProvider, ConversStore? store = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
        ArgumentNullException.ThrowIfNull(timeProvider);

        HostName = Callsigns.Normalize(hostName);
        _time = timeProvider;
        _store = store;

        if (store is not null)
        {
            foreach (StoredTopic topic in store.ListTopics())
            {
                _channels[topic.Channel] = new ChannelState
                {
                    Topic = topic.Topic,
                    TopicSetBy = topic.SetBy,
                    TopicSetAt = topic.SetAt,
                };
            }
        }
    }

    /// <summary>This leaf's own convers host name; local users appear in the network table under it.</summary>
    public string HostName { get; }

    /// <summary>The number of local sessions currently attached.</summary>
    public int LocalSessionCount => _sessions.Count;

    /// <summary>A snapshot of all users in the network table (local + remote), ordered by name then host.</summary>
    public IReadOnlyList<NetworkUser> NetworkUsers =>
        _users.Values.OrderBy(u => u.Name, StringComparer.Ordinal)
            .ThenBy(u => u.Host, StringComparer.Ordinal).ToList();

    /// <summary>Looks up a local session by id, or <see langword="null"/> when not attached.</summary>
    public LocalSession? GetSession(string sessionId) =>
        _sessions.TryGetValue(sessionId, out LocalSession? s) ? s : null;

    /// <summary>
    /// A read-only snapshot of a channel: its topic, modes and the users present on it (local +
    /// remote), ordered by name. Returns an empty channel (no users, no topic) for a number that
    /// has neither members nor metadata, so callers always get a usable view.
    /// </summary>
    public Channel GetChannel(int channel)
    {
        _channels.TryGetValue(channel, out ChannelState? state);
        List<NetworkUser> users = _users.Values
            .Where(u => u.Channel == channel)
            .OrderBy(u => u.Name, StringComparer.Ordinal)
            .ThenBy(u => u.Host, StringComparer.Ordinal)
            .ToList();

        return new Channel
        {
            Number = channel,
            Topic = state?.Topic ?? string.Empty,
            TopicSetBy = state?.TopicSetBy ?? string.Empty,
            TopicSetAt = state?.TopicSetAt,
            Modes = state?.Modes ?? ChannelMode.None,
            Users = users,
        };
    }

    /// <summary>
    /// A <c>/who</c>-style listing of channels that have members and/or metadata, ordered by number.
    /// Channels flagged secret (<c>+s</c>) or invisible (<c>+i</c>) are <b>hidden</b> from this
    /// listing (their number/existence is not displayed, SPECS lines 97/101) — unless
    /// <paramref name="includeHidden"/> is set, which a channel-operator/sysop view passes. A direct
    /// <see cref="GetChannel"/> lookup is unaffected: a user who knows a channel can still query it.
    /// </summary>
    public IReadOnlyList<Channel> ListChannels(bool includeHidden = false)
    {
        IEnumerable<int> numbers = _users.Values.Select(u => u.Channel)
            .Concat(_channels.Keys)
            .Distinct();

        return numbers
            .Where(n => includeHidden || !ChannelModes.IsHidden(ModesOf(n)))
            .OrderBy(n => n)
            .Select(GetChannel)
            .ToList();
    }

    /// <summary>
    /// Advances the model by one event and returns the fan-out actions to perform, in order. Pure
    /// with respect to I/O: the only side effects are the hub's own in-memory state and (when a
    /// store is configured) write-through of persisted topics / personal text. An event that has no
    /// effect (e.g. a message on a channel with no local listeners, or a sign-off for an unknown
    /// user) returns an empty list, never throws.
    /// </summary>
    public IReadOnlyList<ConversAction> Advance(ConversEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        return @event switch
        {
            ConversEvent.LocalJoin e => LocalJoin(e),
            ConversEvent.LocalSwitchChannel e => LocalSwitchChannel(e),
            ConversEvent.LocalSay e => LocalSay(e),
            ConversEvent.LocalPrivateMessage e => LocalPrivateMessage(e),
            ConversEvent.LocalSetPersonal e => LocalSetPersonal(e),
            ConversEvent.LocalSetAway e => LocalSetAway(e),
            ConversEvent.LocalSetTopic e => LocalSetTopic(e),
            ConversEvent.LocalInvite e => LocalInvite(e),
            ConversEvent.LocalSetMode e => LocalSetMode(e),
            ConversEvent.LocalSetOperator e => LocalSetOperator(e),
            ConversEvent.LocalLeave e => LocalLeave(e),
            ConversEvent.HostUser e => HostUser(e),
            ConversEvent.HostChannelMessage e => HostChannelMessage(e),
            ConversEvent.HostPrivateMessage e => HostPrivateMessage(e),
            ConversEvent.HostPersonal e => HostPersonal(e),
            ConversEvent.HostAway e => HostAway(e),
            ConversEvent.HostTopic e => HostTopic(e),
            ConversEvent.HostInvite e => HostInvite(e),
            ConversEvent.HostMode e => HostMode(e),
            ConversEvent.HostOper e => HostOper(e),
            ConversEvent.HostPing => [new ConversAction.SendPong(PongSentinelNoMeasurement)],
            ConversEvent.HostPong => [],
            ConversEvent.HostLoop e => [new ConversAction.DropUplink($"loop via {e.Host}")],
            ConversEvent.HostUnknown => [], // strict leaf: nothing else to relay to (decision 1)
            _ => [],
        };
    }

    // ---------------------------------------------------------------- local events

    private List<ConversAction> LocalJoin(ConversEvent.LocalJoin e)
    {
        if (!ChannelNumber.IsValid(e.Channel))
        {
            return [];
        }

        string call = Callsigns.Normalize(e.Callsign);
        DateTimeOffset now = _time.GetUtcNow();

        // +p/+i invite-only: a fresh joiner with no standing invitation is refused. A user who was
        // invited earlier (held in _invites) — or a global operator — is let in.
        if (ChannelModes.RequiresInvite(ModesOf(e.Channel)) &&
            !IsInvitedTo(call, e.Channel) && !IsGlobalOperator(call))
        {
            return [new ConversAction.DeliverModeNotice(e.SessionId, e.Channel, "That channel is invitation-only.")];
        }

        // Hydrate persisted personal text / nickname so a returning user keeps their identity.
        UserProfile? profile = _store?.GetProfile(call);
        string personal = profile?.Personal ?? string.Empty;
        string nickname = profile?.Nickname ?? string.Empty;

        var session = new LocalSession
        {
            Id = e.SessionId,
            Callsign = call,
            Channel = e.Channel,
            Personal = personal,
            Nickname = nickname,
            IsOperator = IsGlobalOperator(call),
            InvitedChannels = InvitesFor(call),
            JoinedAt = now,
        };
        _sessions[e.SessionId] = session;
        UpsertLocalUser(session);

        var actions = new List<ConversAction>
        {
            // Tell the world (upstream) this user joined: fromchan -1 = fresh join.
            new ConversAction.SendUser(call, HostName, now, -1, e.Channel, personal),
        };
        actions.AddRange(NotifyChannelJoin(e.Channel, call, exceptSession: e.SessionId));
        actions.AddRange(TopicNoticeFor(e.SessionId, e.Channel));
        return actions;
    }

    private List<ConversAction> LocalSwitchChannel(ConversEvent.LocalSwitchChannel e)
    {
        if (!_sessions.TryGetValue(e.SessionId, out LocalSession? session) || !ChannelNumber.IsValid(e.Channel))
        {
            return [];
        }

        if (session.Channel == e.Channel)
        {
            return [];
        }

        // +p/+i invite-only also gates a switch into the channel.
        if (ChannelModes.RequiresInvite(ModesOf(e.Channel)) &&
            !session.IsOperator && !session.InvitedChannels.Contains(e.Channel))
        {
            return [new ConversAction.DeliverModeNotice(e.SessionId, e.Channel, "That channel is invitation-only.")];
        }

        int from = session.Channel;
        DateTimeOffset now = _time.GetUtcNow();
        var actions = new List<ConversAction>();
        actions.AddRange(NotifyChannelLeave(from, session.Callsign, "switched channel", exceptSession: e.SessionId));

        // Channel-op status is per-channel: it does not follow the user to a new channel (unless a
        // global operator). Recompute against the destination channel's op roster.
        bool opOnNew = IsGlobalOperator(session.Callsign) || IsChannelOperator(session.Callsign, e.Channel);
        session = session with { Channel = e.Channel, IsOperator = opOnNew, JoinedAt = now };
        _sessions[e.SessionId] = session;
        UpsertLocalUser(session);

        actions.Add(new ConversAction.SendUser(session.Callsign, HostName, now, from, e.Channel, session.Personal));
        actions.AddRange(NotifyChannelJoin(e.Channel, session.Callsign, exceptSession: e.SessionId));
        actions.AddRange(TopicNoticeFor(e.SessionId, e.Channel));
        return actions;
    }

    private List<ConversAction> LocalSay(ConversEvent.LocalSay e)
    {
        if (!_sessions.TryGetValue(e.SessionId, out LocalSession? session) || e.Text.Length == 0)
        {
            return [];
        }

        ChannelMode modes = ModesOf(session.Channel);

        // +m moderated: only a channel-operator may write. Drop the message and tell the speaker.
        if ((modes & ChannelMode.Moderated) != 0 && !session.IsOperator)
        {
            return
            [
                new ConversAction.DeliverModeNotice(
                    e.SessionId, session.Channel,
                    "This is a moderated channel. Only channel operators may write."),
            ];
        }

        var actions = new List<ConversAction>();

        // +l local channel: text is not forwarded to links (SPECS line 98). Suppress the upstream copy.
        if ((modes & ChannelMode.Local) == 0)
        {
            actions.Add(new ConversAction.SendChannelMessage(session.Callsign, session.Channel, e.Text));
        }

        // Local copies to every other local user on the channel (the speaker echoes locally too via
        // the Host's own transport; the hub does not re-deliver to the originator).
        actions.AddRange(DeliverToChannel(session.Channel, session.Callsign, e.Text, exceptSession: e.SessionId));
        return actions;
    }

    private List<ConversAction> LocalPrivateMessage(ConversEvent.LocalPrivateMessage e)
    {
        if (!_sessions.TryGetValue(e.SessionId, out LocalSession? session) || e.Text.Length == 0)
        {
            return [];
        }

        string to = Callsigns.Normalize(e.ToUser);
        var actions = new List<ConversAction>();

        // If the addressee is a local session, deliver directly; otherwise (or additionally, when a
        // same-named remote also exists) hand it to the uplink. A leaf prefers local delivery and
        // still mirrors upstream so the network presence/echo is consistent.
        bool deliveredLocally = false;
        foreach (LocalSession target in _sessions.Values)
        {
            if (Callsigns.Equal(target.Callsign, to))
            {
                actions.Add(new ConversAction.DeliverPrivateMessage(target.Id, session.Callsign, e.Text));
                if (target.IsAway)
                {
                    actions.Add(new ConversAction.DeliverAwayNotice(e.SessionId, target.Callsign, target.Away));
                }

                deliveredLocally = true;
            }
        }

        // Away hint for a remote away user.
        if (!deliveredLocally)
        {
            NetworkUser? remote = FindRemoteUser(to);
            if (remote is { IsAway: true })
            {
                actions.Add(new ConversAction.DeliverAwayNotice(e.SessionId, remote.Name, remote.Away));
            }
        }

        actions.Add(new ConversAction.SendPrivateMessage(session.Callsign, to, e.Text));
        return actions;
    }

    private List<ConversAction> LocalSetPersonal(ConversEvent.LocalSetPersonal e)
    {
        if (!_sessions.TryGetValue(e.SessionId, out LocalSession? session))
        {
            return [];
        }

        string personal = MakePersonalConsistent(e.Personal);
        session = session with { Personal = personal };
        _sessions[e.SessionId] = session;
        UpsertLocalUser(session);
        PersistProfile(session);

        return [new ConversAction.SendPersonal(session.Callsign, HostName, personal)];
    }

    private List<ConversAction> LocalSetAway(ConversEvent.LocalSetAway e)
    {
        if (!_sessions.TryGetValue(e.SessionId, out LocalSession? session))
        {
            return [];
        }

        string away = e.Away.Trim();
        DateTimeOffset now = _time.GetUtcNow();
        session = session with { Away = away };
        _sessions[e.SessionId] = session;
        UpsertLocalUser(session);

        return [new ConversAction.SendAway(session.Callsign, HostName, now, away)];
    }

    private List<ConversAction> LocalSetTopic(ConversEvent.LocalSetTopic e)
    {
        if (!_sessions.TryGetValue(e.SessionId, out LocalSession? session))
        {
            return [];
        }

        // +t topic-locked: only a channel-operator may set the topic (SPECS line 102).
        if ((ModesOf(session.Channel) & ChannelMode.TopicLocked) != 0 && !session.IsOperator)
        {
            return
            [
                new ConversAction.DeliverModeNotice(
                    e.SessionId, session.Channel,
                    "The topic on this channel may be set by channel operators only."),
            ];
        }

        DateTimeOffset now = _time.GetUtcNow();
        string topic = e.Topic.Trim();
        ApplyTopic(session.Channel, topic, session.Callsign, now);

        var actions = new List<ConversAction>
        {
            new ConversAction.SendTopic(session.Callsign, HostName, now, session.Channel, topic),
        };
        actions.AddRange(NotifyTopic(session.Channel, topic, session.Callsign));
        return actions;
    }

    private List<ConversAction> LocalInvite(ConversEvent.LocalInvite e)
    {
        if (!_sessions.TryGetValue(e.SessionId, out LocalSession? session) || !ChannelNumber.IsValid(e.Channel))
        {
            return [];
        }

        string to = Callsigns.Normalize(e.ToUser);
        RecordInvite(to, e.Channel);
        var actions = new List<ConversAction>();
        foreach (LocalSession target in _sessions.Values)
        {
            if (Callsigns.Equal(target.Callsign, to))
            {
                actions.Add(new ConversAction.DeliverInvite(target.Id, session.Callsign, e.Channel));
            }
        }

        actions.Add(new ConversAction.SendInvite(session.Callsign, to, e.Channel));
        return actions;
    }

    private List<ConversAction> LocalSetMode(ConversEvent.LocalSetMode e)
    {
        if (!_sessions.TryGetValue(e.SessionId, out LocalSession? session) || !ChannelNumber.IsValid(e.Channel))
        {
            return [];
        }

        // Only a channel-operator on the target channel (or a global operator) may change its modes
        // (conversd mode_command()). Reject anyone else with a notice, change nothing.
        if (!IsGlobalOperator(session.Callsign) && !IsChannelOperator(session.Callsign, e.Channel))
        {
            return
            [
                new ConversAction.DeliverModeNotice(
                    e.SessionId, e.Channel, "You must be a channel operator to set modes."),
            ];
        }

        ChannelMode before = ModesOf(e.Channel);
        ChannelMode after = ChannelModes.Apply(before, e.Options, isChannelZero: e.Channel == ChannelNumber.Random);
        if (after == before)
        {
            return [];
        }

        SetModes(e.Channel, after);

        var actions = new List<ConversAction>
        {
            new ConversAction.SendMode(e.Channel, ChannelModes.ToWire(after)),
        };
        actions.AddRange(NotifyModeChange(e.Channel, after));
        return actions;
    }

    private List<ConversAction> LocalSetOperator(ConversEvent.LocalSetOperator e)
    {
        if (!_sessions.TryGetValue(e.SessionId, out LocalSession? session))
        {
            return [];
        }

        ApplyOperator(session.Callsign, e.Channel, e.Grant);
        RefreshSessionOpFlags(session.Callsign);
        return [];
    }

    private List<ConversAction> LocalLeave(ConversEvent.LocalLeave e)
    {
        if (!_sessions.TryGetValue(e.SessionId, out LocalSession? session))
        {
            return [];
        }

        int channel = session.Channel;
        DateTimeOffset now = _time.GetUtcNow();
        _sessions.Remove(e.SessionId);
        RemoveLocalUser(session.Callsign);

        var actions = new List<ConversAction>
        {
            // Sign-off upstream: tochan -1, reason in the personal slot.
            new ConversAction.SendUser(session.Callsign, HostName, now, channel, -1, e.Reason),
        };
        actions.AddRange(NotifyChannelLeave(channel, session.Callsign, e.Reason, exceptSession: e.SessionId));
        return actions;
    }

    // ---------------------------------------------------------------- host (uplink) events

    private List<ConversAction> HostUser(ConversEvent.HostUser e)
    {
        string user = Callsigns.Normalize(e.User);
        string host = Callsigns.Normalize(e.Host);
        if (!Callsigns.IsValidName(user) || !Callsigns.IsValidName(host))
        {
            return [];
        }

        // Loop/echo guard: a presence claiming to originate at *our* host name, for a user we did
        // not put on the wire, is bogus — ignore it (we are the authority for our own users).
        if (Callsigns.Equal(host, HostName))
        {
            return [];
        }

        var key = (user, host);
        string personal = ResolvePersonal(e.Personal);

        if (e.ToChannel == -1)
        {
            // Sign-off. The personal field carries the reason.
            if (!_users.TryGetValue(key, out NetworkUser? leaving))
            {
                return [];
            }

            _users.Remove(key);
            return NotifyChannelLeave(leaving.Channel, user, e.Personal, exceptSession: null);
        }

        if (!ChannelNumber.IsValid(e.ToChannel))
        {
            return [];
        }

        _users.TryGetValue(key, out NetworkUser? existing);
        int fromChannel = existing?.Channel ?? e.FromChannel;

        _users[key] = new NetworkUser
        {
            Name = user,
            Host = host,
            Channel = e.ToChannel,
            Personal = personal.Length == 0 ? (existing?.Personal ?? string.Empty) : personal,
            Nickname = existing?.Nickname ?? string.Empty,
            Away = existing?.Away ?? string.Empty,
            IsObserver = e.Observer,
            JoinedAt = e.Timestamp,
        };

        var actions = new List<ConversAction>();
        if (existing is not null && fromChannel >= 0 && fromChannel != e.ToChannel)
        {
            actions.AddRange(NotifyChannelLeave(fromChannel, user, "switched channel", exceptSession: null));
        }

        actions.AddRange(NotifyChannelJoin(e.ToChannel, user, exceptSession: null));
        return actions;
    }

    private List<ConversAction> HostChannelMessage(ConversEvent.HostChannelMessage e)
    {
        if (e.Text.Length == 0 || !ChannelNumber.IsValid(e.Channel))
        {
            return [];
        }

        return DeliverToChannel(e.Channel, Callsigns.Normalize(e.User), e.Text, exceptSession: null);
    }

    private List<ConversAction> HostPrivateMessage(ConversEvent.HostPrivateMessage e)
    {
        if (e.Text.Length == 0)
        {
            return [];
        }

        string to = Callsigns.Normalize(e.To);
        string from = Callsigns.Normalize(e.From);
        var actions = new List<ConversAction>();
        foreach (LocalSession target in _sessions.Values)
        {
            if (Callsigns.Equal(target.Callsign, to))
            {
                actions.Add(new ConversAction.DeliverPrivateMessage(target.Id, from, e.Text));
            }
        }

        return actions;
    }

    private List<ConversAction> HostPersonal(ConversEvent.HostPersonal e)
    {
        string user = Callsigns.Normalize(e.User);
        string host = Callsigns.Normalize(e.Host);
        var key = (user, host);
        if (!_users.TryGetValue(key, out NetworkUser? existing))
        {
            return [];
        }

        _users[key] = existing with { Personal = ResolvePersonal(e.Text) };
        return [];
    }

    private List<ConversAction> HostAway(ConversEvent.HostAway e)
    {
        string user = Callsigns.Normalize(e.User);
        string host = Callsigns.Normalize(e.Host);
        var key = (user, host);
        if (!_users.TryGetValue(key, out NetworkUser? existing))
        {
            return [];
        }

        _users[key] = existing with { Away = e.Text.Trim() };
        return [];
    }

    private List<ConversAction> HostTopic(ConversEvent.HostTopic e)
    {
        if (!ChannelNumber.IsValid(e.Channel))
        {
            return [];
        }

        // SPECS /..TOPI: "If your host holds a newer topic, it should not be changed and forwarded."
        if (_channels.TryGetValue(e.Channel, out ChannelState? state) &&
            state.TopicSetAt is { } existingAt && existingAt > e.Timestamp)
        {
            return [];
        }

        string user = Callsigns.Normalize(e.User);
        string topic = e.Text.Trim();
        ApplyTopic(e.Channel, topic, user, e.Timestamp);
        return NotifyTopic(e.Channel, topic, user);
    }

    private List<ConversAction> HostInvite(ConversEvent.HostInvite e)
    {
        if (!ChannelNumber.IsValid(e.Channel))
        {
            return [];
        }

        string user = Callsigns.Normalize(e.User);
        string from = Callsigns.Normalize(e.From);
        RecordInvite(user, e.Channel);
        var actions = new List<ConversAction>();
        foreach (LocalSession target in _sessions.Values)
        {
            if (Callsigns.Equal(target.Callsign, user))
            {
                actions.Add(new ConversAction.DeliverInvite(target.Id, from, e.Channel));
            }
        }

        return actions;
    }

    private List<ConversAction> HostMode(ConversEvent.HostMode e)
    {
        if (!ChannelNumber.IsValid(e.Channel))
        {
            return [];
        }

        // The uplink is authoritative: apply the modes verbatim, no operator check. Channel 0's
        // letter restriction still holds (matching conversd's own mode_command()).
        ChannelMode before = ModesOf(e.Channel);
        ChannelMode after = ChannelModes.Apply(before, e.Options, isChannelZero: e.Channel == ChannelNumber.Random);
        if (after == before)
        {
            return [];
        }

        SetModes(e.Channel, after);
        return NotifyModeChange(e.Channel, after);
    }

    private List<ConversAction> HostOper(ConversEvent.HostOper e)
    {
        string user = Callsigns.Normalize(e.User);
        if (!Callsigns.IsValidName(user))
        {
            return [];
        }

        ApplyOperator(user, e.Channel, e.Grant);
        RefreshSessionOpFlags(user);

        // Reflect op status on the remote-user snapshot too, when we know the user. A user is shown
        // as an operator if they are a global op or a channel-op on the channel they are currently on.
        string host = Callsigns.Normalize(e.Host);
        if (_users.TryGetValue((user, host), out NetworkUser? remote))
        {
            bool isOp = IsGlobalOperator(user) || IsChannelOperator(user, remote.Channel);
            _users[(user, host)] = remote with { IsOperator = isOp };
        }

        return [];
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Sentinel pong value: "I do not make link measurements" (SPECS /..PONG -1).</summary>
    private const long PongSentinelNoMeasurement = -1;

    private void UpsertLocalUser(LocalSession session)
    {
        _users[(session.Callsign, HostName)] = new NetworkUser
        {
            Name = session.Callsign,
            Host = HostName,
            Channel = session.Channel,
            Personal = session.Personal,
            Nickname = session.Nickname,
            Away = session.Away,
            IsObserver = false,
            IsOperator = session.IsOperator,
            JoinedAt = session.JoinedAt,
        };
    }

    private void RemoveLocalUser(string callsign) => _users.Remove((callsign, HostName));

    // ---------------------------------------------------------------- channel modes

    /// <summary>The current modes of a channel (<see cref="ChannelMode.None"/> when it has no metadata).</summary>
    private ChannelMode ModesOf(int channel) =>
        _channels.TryGetValue(channel, out ChannelState? state) ? state.Modes : ChannelMode.None;

    private void SetModes(int channel, ChannelMode modes)
    {
        _channels.TryGetValue(channel, out ChannelState? state);
        state ??= new ChannelState();
        _channels[channel] = state with { Modes = modes };
    }

    private List<ConversAction> NotifyModeChange(int channel, ChannelMode modes)
    {
        var actions = new List<ConversAction>();
        foreach (LocalSession s in _sessions.Values)
        {
            if (s.Channel == channel)
            {
                actions.Add(new ConversAction.DeliverModeChange(s.Id, channel, modes));
            }
        }

        return actions;
    }

    // ---------------------------------------------------------------- operator / invite state

    private bool IsGlobalOperator(string callsign) => _globalOps.Contains(Callsigns.Normalize(callsign));

    private bool IsChannelOperator(string callsign, int channel) =>
        _channelOps.TryGetValue(channel, out HashSet<string>? ops) && ops.Contains(Callsigns.Normalize(callsign));

    /// <summary>Grants or revokes operator status. <paramref name="channel"/> == -1 is the global role.</summary>
    private void ApplyOperator(string callsign, int channel, bool grant)
    {
        string call = Callsigns.Normalize(callsign);
        if (channel < 0)
        {
            if (grant)
            {
                _globalOps.Add(call);
            }
            else
            {
                _globalOps.Remove(call);
            }

            return;
        }

        if (grant)
        {
            if (!_channelOps.TryGetValue(channel, out HashSet<string>? ops))
            {
                ops = new HashSet<string>(StringComparer.Ordinal);
                _channelOps[channel] = ops;
            }

            ops.Add(call);
        }
        else if (_channelOps.TryGetValue(channel, out HashSet<string>? ops))
        {
            ops.Remove(call);
        }
    }

    /// <summary>Recomputes the op flag on a callsign's live session (and snapshot) after an op change.</summary>
    private void RefreshSessionOpFlags(string callsign)
    {
        string call = Callsigns.Normalize(callsign);
        foreach (LocalSession s in _sessions.Values.ToList())
        {
            if (!Callsigns.Equal(s.Callsign, call))
            {
                continue;
            }

            bool isOp = IsGlobalOperator(call) || IsChannelOperator(call, s.Channel);
            if (isOp != s.IsOperator)
            {
                LocalSession updated = s with { IsOperator = isOp };
                _sessions[s.Id] = updated;
                UpsertLocalUser(updated);
            }
        }
    }

    private void RecordInvite(string callsign, int channel)
    {
        string call = Callsigns.Normalize(callsign);
        if (!_invites.TryGetValue(call, out HashSet<int>? channels))
        {
            channels = [];
            _invites[call] = channels;
        }

        channels.Add(channel);

        // Reflect the invite on any live session for that callsign immediately.
        foreach (LocalSession s in _sessions.Values.ToList())
        {
            if (Callsigns.Equal(s.Callsign, call))
            {
                _sessions[s.Id] = s with { InvitedChannels = InvitesFor(call) };
            }
        }
    }

    private bool IsInvitedTo(string callsign, int channel) =>
        _invites.TryGetValue(Callsigns.Normalize(callsign), out HashSet<int>? channels) && channels.Contains(channel);

    private IReadOnlySet<int> InvitesFor(string callsign) =>
        _invites.TryGetValue(Callsigns.Normalize(callsign), out HashSet<int>? channels)
            ? channels.ToHashSet()
            : System.Collections.Immutable.ImmutableHashSet<int>.Empty;

    private NetworkUser? FindRemoteUser(string name)
    {
        foreach (NetworkUser u in _users.Values)
        {
            if (Callsigns.Equal(u.Name, name) && !Callsigns.Equal(u.Host, HostName))
            {
                return u;
            }
        }

        return null;
    }

    /// <summary>Deliver a channel message to every local session on the channel except one (the originator).</summary>
    private List<ConversAction> DeliverToChannel(int channel, string fromUser, string text, string? exceptSession)
    {
        var actions = new List<ConversAction>();
        foreach (LocalSession s in _sessions.Values)
        {
            if (s.Channel == channel && !string.Equals(s.Id, exceptSession, StringComparison.Ordinal))
            {
                actions.Add(new ConversAction.DeliverChannelMessage(s.Id, channel, fromUser, text));
            }
        }

        return actions;
    }

    private List<ConversAction> NotifyChannelJoin(int channel, string user, string? exceptSession)
    {
        var actions = new List<ConversAction>();
        foreach (LocalSession s in _sessions.Values)
        {
            if (s.Channel == channel && !string.Equals(s.Id, exceptSession, StringComparison.Ordinal))
            {
                actions.Add(new ConversAction.DeliverJoinNotice(s.Id, channel, user));
            }
        }

        return actions;
    }

    private List<ConversAction> NotifyChannelLeave(int channel, string user, string reason, string? exceptSession)
    {
        var actions = new List<ConversAction>();
        foreach (LocalSession s in _sessions.Values)
        {
            if (s.Channel == channel && !string.Equals(s.Id, exceptSession, StringComparison.Ordinal))
            {
                actions.Add(new ConversAction.DeliverLeaveNotice(s.Id, channel, user, reason));
            }
        }

        return actions;
    }

    private List<ConversAction> NotifyTopic(int channel, string topic, string setBy)
    {
        var actions = new List<ConversAction>();
        foreach (LocalSession s in _sessions.Values)
        {
            if (s.Channel == channel)
            {
                actions.Add(new ConversAction.DeliverTopic(s.Id, channel, topic, setBy));
            }
        }

        return actions;
    }

    private List<ConversAction> TopicNoticeFor(string sessionId, int channel)
    {
        if (_channels.TryGetValue(channel, out ChannelState? state) && state.Topic.Length != 0)
        {
            return [new ConversAction.DeliverTopic(sessionId, channel, state.Topic, state.TopicSetBy)];
        }

        return [];
    }

    private void ApplyTopic(int channel, string topic, string setBy, DateTimeOffset setAt)
    {
        _channels.TryGetValue(channel, out ChannelState? state);
        state ??= new ChannelState();
        _channels[channel] = state with { Topic = topic, TopicSetBy = setBy, TopicSetAt = setAt };

        _store?.UpsertTopic(new StoredTopic
        {
            Channel = channel,
            Topic = topic,
            SetBy = setBy,
            SetAt = setAt,
        });
    }

    private void PersistProfile(LocalSession session)
    {
        if (_store is null)
        {
            return;
        }

        UserProfile existing = _store.GetProfile(session.Callsign) ?? new UserProfile
        {
            Callsign = session.Callsign,
        };
        _store.UpsertProfile(existing with
        {
            Personal = session.Personal,
            Nickname = session.Nickname,
            UpdatedAt = _time.GetUtcNow(),
        });
    }

    /// <summary>
    /// The wire personal slot: a single <c>'@'</c> means "no personal note" (SPECS /..USER), which
    /// we store as the empty string. Anything else is the personal text, trimmed.
    /// </summary>
    private static string ResolvePersonal(string raw)
    {
        string trimmed = raw.Trim();
        return trimmed is "@" or "" ? string.Empty : trimmed;
    }

    /// <summary>
    /// Local-user personal text is always presented as authenticated: the conversd <c>~</c> brand
    /// (unauthenticated user) is never applied to an RHP-authenticated ham (design decision 4).
    /// </summary>
    private static string MakePersonalConsistent(string personal)
    {
        string trimmed = personal.Trim();
        return trimmed.StartsWith('~') ? trimmed.TrimStart('~').TrimStart() : trimmed;
    }

    /// <summary>Mutable internal per-channel metadata (topic + modes). Local presence lives in the user table.</summary>
    private sealed record ChannelState
    {
        public string Topic { get; init; } = string.Empty;

        public string TopicSetBy { get; init; } = string.Empty;

        public DateTimeOffset? TopicSetAt { get; init; }

        public ChannelMode Modes { get; init; } = ChannelMode.None;
    }
}
