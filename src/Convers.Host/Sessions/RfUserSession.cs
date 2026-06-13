using Convers.Console;
using Convers.Core;
using Convers.Host.Uplink;

namespace Convers.Host.Sessions;

/// <summary>
/// Drives one connected RF user's whole lifetime as a convers USER session (design decision 3), bridging
/// it to the shared <see cref="ConversHub"/> <em>through the <see cref="HostLink"/></em>: every mutation a
/// user makes (join / say / msg / topic / personal / away / invite / leave) is submitted as a Core
/// <see cref="ConversEvent"/> via <see cref="HostLink.SubmitLocalEventAsync"/>, so the hub (driven on the
/// link's single owning loop) turns it into the uplink <c>/..</c> commands and the local fan-out to the
/// other sessions — which arrives back here through the <see cref="LocalSessionRegistry"/>. The user is
/// auto-logged-in from <see cref="IConverseTerminal.RemoteCallsign"/> (the RHP-authenticated callsign);
/// they never type <c>/name</c> (decision 4).
/// </summary>
/// <remarks>
/// <para>
/// This is the Host-side bridge driver. It reuses <c>Convers.Console</c>'s public surface primitives —
/// <see cref="ConsoleParser"/> (plain/classic), <see cref="ConsoleIntent"/>, <see cref="ActionRenderer"/>
/// and <see cref="ConsoleHelp"/> — so the RF UX is identical to <c>ConverseConsoleSession</c>. It does not
/// use <c>ConverseConsoleSession.RunAsync</c> directly because that drives a <c>ConversHub</c> in-process
/// and only renders the actions addressed to its own session, discarding the uplink-bound <c>Send*</c> and
/// the cross-session deliveries; routing through the link is what makes a real convers channel work.
/// </para>
/// <para>
/// Read queries (<c>who</c>, a bare <c>topic</c>) are answered from a read-only snapshot of the live hub,
/// taken on the link's owning loop via <see cref="HostLink.SnapshotAsync"/> so the hub is never read
/// off-loop. Inbound deliveries are written here through the registry sink registered for this session.
/// </para>
/// </remarks>
public sealed class RfUserSession
{
    private readonly IConverseTerminal _terminal;
    private readonly HostLink _link;
    private readonly LocalSessionRegistry _registry;
    private readonly RfSessionConfig _config;
    private readonly string _sessionId;
    private readonly string _call;

    private RfUserSession(
        IConverseTerminal terminal,
        HostLink link,
        LocalSessionRegistry registry,
        RfSessionConfig config,
        string sessionId)
    {
        _terminal = terminal;
        _link = link;
        _registry = registry;
        _config = config;
        _sessionId = sessionId;
        _call = Callsigns.Normalize(terminal.RemoteCallsign);
    }

    /// <summary>
    /// Runs one RF session to completion over <paramref name="terminal"/>, bridged to the hub through
    /// <paramref name="link"/>. Returns why it ended so the demux can close the child. Chat logging is
    /// <b>not</b> done here: every channel/PM/presence action the hub emits is logged centrally at the
    /// <see cref="HostLink"/> fan-out (design decision 7), so nothing can bypass it and nothing
    /// double-logs.
    /// </summary>
    public static async Task<ConverseSessionEndReason> RunAsync(
        IConverseTerminal terminal,
        HostLink link,
        LocalSessionRegistry registry,
        RfSessionConfig config,
        string sessionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrEmpty(sessionId);

        var session = new RfUserSession(terminal, link, registry, config, sessionId);
        return await session.RunCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ConverseSessionEndReason> RunCoreAsync(CancellationToken cancellationToken)
    {
        // Register this session's line sink BEFORE the join so any immediate fan-out reaches it.
        _registry.Register(_sessionId, (line, ct) => _terminal.WriteAsync(line + "\r", ct));
        try
        {
            await GreetAndJoinAsync(cancellationToken).ConfigureAwait(false);

            while (true)
            {
                string? raw = await _terminal.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (raw is null)
                {
                    await SignOffAsync("link lost", cancellationToken).ConfigureAwait(false);
                    return ConverseSessionEndReason.Drop;
                }

                ConsoleIntent intent = Parse(raw);
                if (intent is ConsoleIntent.Quit quit)
                {
                    await SignOffAsync(quit.Reason, cancellationToken).ConfigureAwait(false);
                    await WriteLineAsync(SignOffLine(quit.Reason), cancellationToken).ConfigureAwait(false);
                    return ConverseSessionEndReason.Quit;
                }

                if (intent is ConsoleIntent.Leave leave)
                {
                    string reason = leave.Reason.Length == 0 ? "leaving" : leave.Reason;
                    await SignOffAsync(reason, cancellationToken).ConfigureAwait(false);
                    await WriteLineAsync(SignOffLine(leave.Reason), cancellationToken).ConfigureAwait(false);
                    return ConverseSessionEndReason.Quit;
                }

                await HandleAsync(intent, raw, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ConverseTerminalClosedException)
        {
            await SignOffAsync("link lost", cancellationToken).ConfigureAwait(false);
            return ConverseSessionEndReason.Drop;
        }
        finally
        {
            _registry.Unregister(_sessionId);
        }
    }

    // ---------------------------------------------------------------- lifetime

    private async Task GreetAndJoinAsync(CancellationToken ct)
    {
        await WriteLineAsync($"[{_config.NodeName} convers] Welcome {_call}.", ct).ConfigureAwait(false);
        await SubmitAsync(new ConversEvent.LocalJoin(_sessionId, _call, _config.DefaultChannel), ct).ConfigureAwait(false);

        // Render the join's own-session notices (e.g. the persisted topic) from a snapshot.
        await ShowTopicAsync(_config.DefaultChannel, announceNone: false, ct).ConfigureAwait(false);

        await WriteLineAsync($"You are on channel {_config.DefaultChannel}.", ct).ConfigureAwait(false);
        await WriteLineAsync(_config.Interface == ConsoleInterface.Classic
            ? "Classic mode. Type /help for commands."
            : "Type 'help' for commands, or just type to chat.", ct).ConfigureAwait(false);
    }

    private async Task SignOffAsync(string reason, CancellationToken ct) =>
        await SubmitAsync(new ConversEvent.LocalLeave(_sessionId, reason), ct).ConfigureAwait(false);

    private string SignOffLine(string reason) =>
        reason.Trim().Length == 0
            ? $"73 de {_config.NodeName}"
            : $"73 de {_config.NodeName} ({reason.Trim()})";

    private ConsoleIntent Parse(string line) => _config.Interface == ConsoleInterface.Classic
        ? ConsoleParser.ParseClassic(line)
        : ConsoleParser.ParsePlain(line);

    // ---------------------------------------------------------------- per-intent handling

    private async Task HandleAsync(ConsoleIntent intent, string raw, CancellationToken ct)
    {
        switch (intent)
        {
            case ConsoleIntent.Empty:
                return;

            case ConsoleIntent.Say say:
                await HandleSayAsync(say, ct).ConfigureAwait(false);
                return;

            case ConsoleIntent.Join join:
                await HandleJoinAsync(join, ct).ConfigureAwait(false);
                return;

            case ConsoleIntent.Msg msg:
                await SubmitAsync(new ConversEvent.LocalPrivateMessage(_sessionId, msg.To, msg.Text), ct).ConfigureAwait(false);
                return;

            case ConsoleIntent.Topic topic:
                await HandleTopicAsync(topic, ct).ConfigureAwait(false);
                return;

            case ConsoleIntent.Personal personal:
                await SubmitAsync(new ConversEvent.LocalSetPersonal(_sessionId, personal.Text), ct).ConfigureAwait(false);
                await WriteLineAsync(personal.Text.Trim().Length == 0
                    ? "Personal text cleared."
                    : $"Personal text set: {personal.Text.Trim()}", ct).ConfigureAwait(false);
                return;

            case ConsoleIntent.Away away:
                await SubmitAsync(new ConversEvent.LocalSetAway(_sessionId, away.Text), ct).ConfigureAwait(false);
                await WriteLineAsync(away.Text.Trim().Length == 0
                    ? "You are back."
                    : $"You are away: {away.Text.Trim()}", ct).ConfigureAwait(false);
                return;

            case ConsoleIntent.Mode mode:
                await HandleModeAsync(mode, ct).ConfigureAwait(false);
                return;

            case ConsoleIntent.Oper oper:
                await HandleOperAsync(oper, ct).ConfigureAwait(false);
                return;

            case ConsoleIntent.Who who:
                await HandleWhoAsync(who, ct).ConfigureAwait(false);
                return;

            case ConsoleIntent.Invite invite:
                await HandleInviteAsync(invite, ct).ConfigureAwait(false);
                return;

            case ConsoleIntent.Help help:
                await WritePagedAsync(_config.Interface == ConsoleInterface.Classic
                    ? ConsoleHelp.Classic(help.Subject)
                    : ConsoleHelp.Plain(help.Subject), ct).ConfigureAwait(false);
                return;

            case ConsoleIntent.Unknown:
                await WriteLineAsync(UnknownHint(raw), ct).ConfigureAwait(false);
                return;

            default:
                return;
        }
    }

    private async Task HandleSayAsync(ConsoleIntent.Say say, CancellationToken ct)
    {
        if (say.Text.Length == 0)
        {
            return;
        }

        await SubmitAsync(new ConversEvent.LocalSay(_sessionId, say.Text), ct).ConfigureAwait(false);
    }

    private async Task HandleJoinAsync(ConsoleIntent.Join join, CancellationToken ct)
    {
        if (join.Channel is null)
        {
            int current = await CurrentChannelAsync(ct).ConfigureAwait(false);
            await WriteLineAsync($"You are on channel {current}.", ct).ConfigureAwait(false);
            return;
        }

        await SubmitAsync(new ConversEvent.LocalSwitchChannel(_sessionId, join.Channel.Value), ct).ConfigureAwait(false);
        await ShowTopicAsync(join.Channel.Value, announceNone: false, ct).ConfigureAwait(false);
        await WriteLineAsync($"You are on channel {join.Channel.Value}.", ct).ConfigureAwait(false);
    }

    private async Task HandleTopicAsync(ConsoleIntent.Topic topic, CancellationToken ct)
    {
        if (topic.Text.Trim().Length == 0)
        {
            int current = await CurrentChannelAsync(ct).ConfigureAwait(false);
            await ShowTopicAsync(current, announceNone: true, ct).ConfigureAwait(false);
            return;
        }

        await SubmitAsync(new ConversEvent.LocalSetTopic(_sessionId, topic.Text), ct).ConfigureAwait(false);
    }

    private async Task HandleModeAsync(ConsoleIntent.Mode mode, CancellationToken ct)
    {
        int current = await CurrentChannelAsync(ct).ConfigureAwait(false);
        int channel = mode.Channel ?? current;
        if (mode.Options.Trim().Length == 0)
        {
            // Show: read the current modes from the live hub snapshot.
            ChannelMode modes = await _link.SnapshotAsync(hub => hub.GetChannel(channel).Modes, ct).ConfigureAwait(false);
            await WriteLineAsync($"*** Channel {channel} modes: {ChannelModes.ToWire(modes)}", ct).ConfigureAwait(false);
            return;
        }

        // Set: the hub enforces operator status and answers a refusal as a DeliverModeNotice, and on
        // success delivers a DeliverModeChange to the sessions ON that channel (rendered through the
        // registry). When the target is a DIFFERENT channel than the one we are on, that DeliverModeChange
        // would not reach us, so we confirm the resulting modes from a snapshot taken AFTER the submit
        // (FIFO on the link's owning loop, so the snapshot observes the applied change).
        await SubmitAsync(new ConversEvent.LocalSetMode(_sessionId, channel, mode.Options), ct).ConfigureAwait(false);
        if (channel != current)
        {
            ChannelMode after = await _link.SnapshotAsync(hub => hub.GetChannel(channel).Modes, ct).ConfigureAwait(false);
            await WriteLineAsync($"*** Channel {channel} modes: {ChannelModes.ToWire(after)}", ct).ConfigureAwait(false);
        }
    }

    private async Task HandleOperAsync(ConsoleIntent.Oper oper, CancellationToken ct)
    {
        if (oper.Secret.Length == 0)
        {
            await WriteLineAsync("Usage: oper <secret>", ct).ConfigureAwait(false);
            return;
        }

        // Mirror conversd SecretPass/SecretNum: a blank configured secret disables operator login.
        if (_config.OperatorSecret.Length == 0 ||
            !FixedTimeEquals(oper.Secret, _config.OperatorSecret))
        {
            await WriteLineAsync("Sorry, operator access denied.", ct).ConfigureAwait(false);
            return;
        }

        await SubmitAsync(new ConversEvent.LocalSetOperator(_sessionId, -1, true), ct).ConfigureAwait(false);
        await WriteLineAsync("*** You are now an operator.", ct).ConfigureAwait(false);
    }

    /// <summary>Constant-time secret comparison so the operator secret cannot be guessed by timing.</summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        byte[] x = System.Text.Encoding.UTF8.GetBytes(a);
        byte[] y = System.Text.Encoding.UTF8.GetBytes(b);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(x, y);
    }

    private async Task HandleInviteAsync(ConsoleIntent.Invite invite, CancellationToken ct)
    {
        int channel = invite.Channel ?? await CurrentChannelAsync(ct).ConfigureAwait(false);
        await SubmitAsync(new ConversEvent.LocalInvite(_sessionId, invite.User, channel), ct).ConfigureAwait(false);
        await WriteLineAsync($"Invited {invite.User} to channel {channel}.", ct).ConfigureAwait(false);
    }

    private async Task HandleWhoAsync(ConsoleIntent.Who who, CancellationToken ct)
    {
        string arg = who.Argument.Trim();
        bool wholeNetwork = arg is "*" or "all" or "ALL" or "n";

        List<string> lines = await _link.SnapshotAsync(hub =>
        {
            var result = new List<string>();
            if (wholeNetwork)
            {
                result.Add("Users on the network:");
                foreach (NetworkUser u in hub.NetworkUsers)
                {
                    result.Add(FormatUser(u));
                }
            }
            else
            {
                int current = hub.GetSession(_sessionId)?.Channel ?? _config.DefaultChannel;
                result.Add($"Users on channel {current}:");
                foreach (NetworkUser u in hub.GetChannel(current).Users)
                {
                    result.Add(FormatUser(u));
                }
            }

            if (result.Count == 1)
            {
                result.Add("  (nobody)");
            }

            return result;
        }, ct).ConfigureAwait(false);

        await WritePagedAsync(lines, ct).ConfigureAwait(false);
    }

    private async Task ShowTopicAsync(int channel, bool announceNone, CancellationToken ct)
    {
        (string topic, string setBy) = await _link.SnapshotAsync(hub =>
        {
            Channel ch = hub.GetChannel(channel);
            return (ch.Topic, ch.TopicSetBy);
        }, ct).ConfigureAwait(false);

        if (topic.Length == 0)
        {
            if (announceNone)
            {
                await WriteLineAsync($"No topic set on channel {channel}.", ct).ConfigureAwait(false);
            }

            return;
        }

        await WriteLineAsync(setBy.Length == 0
            ? $"*** Topic of channel {channel}: {topic}"
            : $"*** Topic of channel {channel}: {topic} (set by {setBy})", ct).ConfigureAwait(false);
    }

    private async Task<int> CurrentChannelAsync(CancellationToken ct) =>
        await _link.SnapshotAsync(
            hub => hub.GetSession(_sessionId)?.Channel ?? _config.DefaultChannel, ct).ConfigureAwait(false);

    private static string FormatUser(NetworkUser u)
    {
        string flags = u.IsAway ? " (away)" : string.Empty;
        string pers = u.Personal.Length == 0 ? string.Empty : $" - {u.Personal}";
        return $"  {u.Name}@{u.Host} ch {u.Channel}{flags}{pers}";
    }

    private string UnknownHint(string raw) => _config.Interface == ConsoleInterface.Classic
        ? $"Unknown command. Type /help for a list. ({raw.Trim()})"
        : $"Sorry, I didn't understand that. Type 'help' for commands. ({raw.Trim()})";

    // ---------------------------------------------------------------- plumbing

    private ValueTask SubmitAsync(ConversEvent @event, CancellationToken ct) =>
        _link.SubmitLocalEventAsync(@event, ct);

    private ValueTask WriteLineAsync(string line, CancellationToken ct) => _terminal.WriteAsync(line + "\r", ct);

    /// <summary>Paclen-friendly pager (mirrors <c>ConverseConsoleSession.WritePagedAsync</c>).</summary>
    private async Task WritePagedAsync(List<string> lines, CancellationToken ct)
    {
        int pageLength = _config.PageLength;
        int sincePrompt = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            await WriteLineAsync(lines[i], ct).ConfigureAwait(false);
            sincePrompt++;

            if (pageLength > 0 && sincePrompt >= pageLength && i < lines.Count - 1)
            {
                await WriteLineAsync("<A>bort, <CR> Continue..>", ct).ConfigureAwait(false);
                string? response = await _terminal.ReadLineAsync(ct).ConfigureAwait(false);
                if (response is null)
                {
                    throw new ConverseTerminalClosedException();
                }

                response = response.Trim();
                if (response.Equals("A", StringComparison.OrdinalIgnoreCase) ||
                    response.Equals("abort", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteLineAsync("Output aborted", ct).ConfigureAwait(false);
                    return;
                }

                sincePrompt = 0;
            }
        }
    }
}

/// <summary>Static per-session configuration for an <see cref="RfUserSession"/> (the demux supplies it).</summary>
public sealed record RfSessionConfig
{
    /// <summary>The leaf's own convers node name, shown in the greeting (the bound callsign).</summary>
    public string NodeName { get; init; } = "convers";

    /// <summary>The fixed default channel a user lands on at connect (design decision: a configured public default).</summary>
    public int DefaultChannel { get; init; } = 3333;

    /// <summary>Lines per page for the pager; 0 disables paging.</summary>
    public int PageLength { get; init; } = 20;

    /// <summary>The input surface this session presents (plain default / classic — design decision 9).</summary>
    public ConsoleInterface Interface { get; init; } = ConsoleInterface.Plain;

    /// <summary>
    /// The operator secret a user presents with <c>oper &lt;secret&gt;</c> to gain operator status
    /// (conversd <c>SecretPass</c>/<c>SecretNum</c>). Blank disables operator login on this node.
    /// </summary>
    public string OperatorSecret { get; init; } = "";
}
