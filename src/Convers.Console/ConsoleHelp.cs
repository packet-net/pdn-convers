namespace Convers.Console;

/// <summary>
/// The help text for both surfaces. Plain help explains in sentences (design.md decision 9: "help
/// explains in sentences"); classic help lists the literal conversd <c>/</c>-commands for power and
/// legacy users. Returned as discrete lines so the session pages them.
/// </summary>
public static class ConsoleHelp
{
    /// <summary>
    /// Plain-language help. With no topic, a short sentence-style summary; with a topic, a one-line
    /// explanation of that verb (resolved by the same unambiguous-prefix rule the parser uses).
    /// </summary>
    public static List<string> Plain(string topic)
    {
        topic = topic.Trim();
        if (topic.Length != 0)
        {
            string? verb = ConsoleVerbs.Resolve(SplitFirstWord(topic));
            if (verb is not null && PlainTopics.TryGetValue(verb, out string? detail))
            {
                return [detail];
            }
        }

        return
        [
            "This is a convers round-table chat. Just type a line to say it to everyone on your channel.",
            "Commands (any unambiguous abbreviation works):",
            "  join <channel>     move to a chat channel (e.g. 'join 2723' or just 'j 2723')",
            "  say <text>         say something to your channel (or just type the text)",
            "  who [*]            see who is here ('who' for your channel, 'who *' for everyone)",
            "  msg <call> <text>  send a private message to one person",
            "  topic [<text>]     show or set this channel's topic",
            "  mode [ch] [+/-..]  show or set channel modes (operators only to set)",
            "  personal [<text>]  set your personal note (or 'pers'); empty clears it",
            "  away [<text>]      mark yourself away (no text means you're back)",
            "  oper <secret>      become an operator (sysop secret from convers.yaml)",
            "  invite <call> [ch] invite someone to a channel",
            "  leave              leave this channel (ends your session)",
            "  quit               sign off",
            "  help [<command>]   this help, or help on one command",
        ];
    }

    /// <summary>
    /// Classic (<c>/</c>-command) help — the literal conversd surface for power users and legacy
    /// automated clients. With a topic, the one-line summary of that command.
    /// </summary>
    public static List<string> Classic(string topic)
    {
        topic = topic.Trim();
        if (topic.Length != 0)
        {
            string head = SplitFirstWord(topic);
            string? verb = ClassicCommands.Resolve(head.StartsWith('/') ? head[1..] : head);
            if (verb is not null && ClassicTopics.TryGetValue(verb, out string? detail))
            {
                return [detail];
            }
        }

        return
        [
            "Classic convers commands (abbreviate by leaving out lower-case letters):",
            "  /Name <call> [ch]      (already logged in; switches channel)",
            "  /Join [n] /Channel [n] switch to channel n",
            "  /Who [*|n|...]         list users (/Users, /ONline are aliases)",
            "  /Msg <user> <text>     private message (/Send, /WRite, /QUEry)",
            "  /TOpic [text]          set/show channel topic ('@' clears)",
            "  /MOde [#ch] [+/-opts]  set/show channel modes (silptm; operators only to set)",
            "  /PErsonal [text]       set personal text (/NOTE); '@' clears",
            "  /Away [text]           mark away (no text = back)",
            "  /OPerator <secret>     become an operator (/SYsOp; sysop secret)",
            "  /Invite <user> [ch]    invite a user to a channel",
            "  /LEave                 leave the channel",
            "  /Quit [reason]         sign off (/BYe, /Exit)",
            "  /Help [command] or /?  this help",
        ];
    }

    private static string SplitFirstWord(string text)
    {
        int sp = text.IndexOf(' ', StringComparison.Ordinal);
        return sp < 0 ? text : text[..sp];
    }

    private static readonly Dictionary<string, string> PlainTopics =
        new(StringComparer.Ordinal)
        {
            ["join"] = "join <channel>: move to a numbered chat channel. Bare 'join' shows your current channel.",
            ["say"] = "say <text>: send a line to everyone on your channel. You can also just type the text.",
            ["who"] = "who: list who is on your channel. 'who *' (or 'who all') lists everyone on the network.",
            ["msg"] = "msg <call> <text>: send a private message to one person, wherever they are.",
            ["topic"] = "topic <text>: set this channel's topic. Bare 'topic' shows the current one.",
            ["mode"] = "mode [ch] [+/-silptm]: show or set channel modes. Only operators may set them; e.g. 'mode +mt' moderates and topic-locks the current channel.",
            ["personal"] = "personal <text>: set your personal note that others see in 'who'. Empty clears it.",
            ["away"] = "away <text>: mark yourself away with a note. Bare 'away' marks you back.",
            ["oper"] = "oper <secret>: log in as an operator using the node sysop secret (from convers.yaml). Operators can set channel modes and the topic on +t channels.",
            ["invite"] = "invite <call> [channel]: invite someone to a channel (the current one if omitted).",
            ["leave"] = "leave: leave your channel. As you are on one channel, this signs you off.",
            ["quit"] = "quit [reason]: sign off the convers session.",
            ["help"] = "help [command]: this overview, or one line of help on a single command.",
        };

    private static readonly Dictionary<string, string> ClassicTopics =
        new(StringComparer.Ordinal)
        {
            ["join"] = "/Join [n] (or /Channel, /Name <call> [n]): switch to channel n.",
            ["who"] = "/Who [*|n|...] (aliases /Users, /ONline): list users and their channels.",
            ["msg"] = "/Msg <user> <text> (aliases /Send, /WRite, /QUEry): send a private message.",
            ["topic"] = "/TOpic [text]: set or show the channel topic. Text '@' removes it.",
            ["mode"] = "/MOde [#channel] [+/-silptm]: set or show channel modes (operators only to set). Bare /MOde shows the current channel's modes.",
            ["personal"] = "/PErsonal [text] (alias /NOTE): set personal description. '@' clears it.",
            ["away"] = "/Away [text]: mark away. No text marks you back.",
            ["oper"] = "/OPerator <secret> (alias /SYsOp): become an operator with the node sysop secret.",
            ["invite"] = "/Invite <user> [channel]: invite a user to a channel.",
            ["leave"] = "/LEave [channel]: leave the channel (signs off if it is your last).",
            ["quit"] = "/Quit [reason] (aliases /BYe, /Exit): terminate the session.",
            ["help"] = "/Help [command] (or /?): this help, or help on one command.",
        };
}
