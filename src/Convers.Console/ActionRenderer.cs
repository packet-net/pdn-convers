using Convers.Core;

namespace Convers.Console;

/// <summary>
/// Renders the Core <see cref="ConversAction"/> fan-out into plain text lines for one local session.
/// Only the "deliver to a local session" actions addressed to <c>mySessionId</c> produce output here;
/// the uplink actions (<c>Send*</c>, <c>DropUplink</c>) are the Host's concern and are skipped. This
/// is pure and side-effect-free so the action→text mapping is unit-testable on its own.
///
/// <para>Lines are returned without a terminator; the session applies CR discipline when it writes.
/// Channel text uses the conversd <c>&lt;call&gt;: text</c> shape; private messages use the
/// <c>&lt;*call*&gt;: text</c> shape so the recipient sees a PM is private; presence/topic notices are
/// plain-language sentences (the surface is plain by default, decision 9).</para>
/// </summary>
public static class ActionRenderer
{
    /// <summary>Renders every action addressed to <paramref name="mySessionId"/> into display lines (in order).</summary>
    public static List<string> Render(IEnumerable<ConversAction> actions, string mySessionId)
    {
        var lines = new List<string>();
        foreach (ConversAction action in actions)
        {
            string? line = RenderOne(action, mySessionId);
            if (line is not null)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    /// <summary>Renders a single action, or <see langword="null"/> when it is not for this session (or is an uplink action).</summary>
    public static string? RenderOne(ConversAction action, string mySessionId) => action switch
    {
        ConversAction.DeliverChannelMessage a when Mine(a.SessionId, mySessionId) =>
            Inv($"<{a.FromUser}>: {a.Text}"),

        ConversAction.DeliverPrivateMessage a when Mine(a.SessionId, mySessionId) =>
            Inv($"<*{a.FromUser}*>: {a.Text}"),

        ConversAction.DeliverAwayNotice a when Mine(a.SessionId, mySessionId) =>
            Inv($"*** {a.User} is away: {a.Away}"),

        ConversAction.DeliverInvite a when Mine(a.SessionId, mySessionId) =>
            Inv($"*** {a.FromUser} invites you to channel {a.Channel}"),

        ConversAction.DeliverJoinNotice a when Mine(a.SessionId, mySessionId) =>
            Inv($"*** {a.User} joined channel {a.Channel}"),

        ConversAction.DeliverLeaveNotice a when Mine(a.SessionId, mySessionId) =>
            a.Reason.Length == 0
                ? Inv($"*** {a.User} left channel {a.Channel}")
                : Inv($"*** {a.User} left channel {a.Channel} ({a.Reason})"),

        ConversAction.DeliverTopic a when Mine(a.SessionId, mySessionId) =>
            RenderTopic(a),

        ConversAction.DeliverModeChange a when Mine(a.SessionId, mySessionId) =>
            Inv($"*** Channel {a.Channel} modes: {ChannelModes.ToWire(a.Modes)}"),

        ConversAction.DeliverModeNotice a when Mine(a.SessionId, mySessionId) =>
            Inv($"*** {a.Reason}"),

        _ => null,
    };

    private static string RenderTopic(ConversAction.DeliverTopic a)
    {
        if (a.Topic.Length == 0)
        {
            return Inv($"*** Topic of channel {a.Channel} cleared");
        }

        return a.SetBy.Length == 0
            ? Inv($"*** Topic of channel {a.Channel}: {a.Topic}")
            : Inv($"*** Topic of channel {a.Channel}: {a.Topic} (set by {a.SetBy})");
    }

    private static bool Mine(string sessionId, string mySessionId) =>
        string.Equals(sessionId, mySessionId, StringComparison.Ordinal);

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);
}
