namespace Convers.Console;

/// <summary>
/// The literal conversd <c>/</c>-command vocabulary for <c>classic</c> mode, with conversd's
/// capital-letter abbreviation rule (<c>etc/convers.help</c>: "Commands can be abbreviated by leaving
/// out any non-capitalized letters"). Each entry pairs a command name with its minimum abbreviation
/// (the capitalized prefix) and the canonical verb the parser builds an intent from. This is a
/// <b>user-input surface we parse ourselves</b> into Core events — it is deliberately NOT the
/// <c>Convers.Protocol</c> host wire (design dependency rule); the grammar overlap with Protocol's
/// <c>UserCommandCodec</c> is intentional and not resolved by a cross-project reference.
/// </summary>
public static class ClassicCommands
{
    /// <summary>One classic command: its full name, its minimum (capitalised) abbreviation, and the canonical verb.</summary>
    private readonly record struct Entry(string Name, string Abbrev, string Verb);

    /// <summary>
    /// The classic commands W3 maps, in conversd table order so prefix resolution is deterministic.
    /// Only the user-surface commands in scope are modelled; anything else falls through to
    /// "unknown" (a leaf may ignore the long tail of conversd power-commands — W7).
    /// </summary>
    private static readonly Entry[] Table =
    [
        // name        abbrev    canonical verb
        new("name",     "n",      "name"),   // /NAME <call> [chan] — auto-login; the call is ignored (decision 4), the optional channel joins.
        new("channel",  "c",      "join"),   // /Channel [n]
        new("join",     "j",      "join"),   // /Join [n]
        new("personal", "pe",     "personal"), // /PErsonal [text]
        new("note",     "no",     "personal"), // /NOTE [text] (alias for personal)
        new("away",     "a",      "away"),   // /Away [text]
        new("msg",      "m",      "msg"),    // /Msg user|#chan text
        new("send",     "s",      "msg"),    // /Send user text
        new("write",    "wr",     "msg"),    // /WRite user text
        new("query",    "que",    "msg"),    // /QUEry user (we map to a one-shot msg target form via args)
        new("topic",    "to",     "topic"),  // /TOpic [text]
        new("mode",     "mo",     "mode"),   // /MOde [#channel] options — set/show channel modes
        new("oper",     "op",     "oper"),   // /OPerator [secret] — become operator
        new("operator", "op",     "oper"),   // /OPerator [secret] (full spelling)
        new("sysop",    "sy",     "oper"),   // /SYsOp [secret] — become operator (alias)
        new("who",      "wh",     "who"),    // /Who [...]
        new("users",    "u",      "who"),    // /USers [...]
        new("online",   "o",      "who"),    // /ONline
        new("invite",   "i",      "invite"), // /Invite user [channel]
        new("leave",    "le",     "leave"),  // /LEave [channel]
        new("quit",     "qu",     "quit"),   // /Quit [arg]
        new("bye",      "by",     "quit"),   // /BYe [arg]
        new("exit",     "e",      "quit"),   // /Exit [arg]
        new("help",     "h",      "help"),   // /Help [command]
    ];

    /// <summary>
    /// Resolves a typed classic command word to its canonical verb (case-insensitive), honouring the
    /// capital-letter abbreviation rule: the typed word must be at least the command's minimum
    /// abbreviation and a prefix of the full name. Returns the first matching table row's verb, or
    /// <see langword="null"/> when nothing matches. <c>?</c> is accepted as help (conversd's <c>/?</c>).
    /// </summary>
    public static string? Resolve(string? word)
    {
        if (string.IsNullOrEmpty(word))
        {
            return null;
        }

        if (word == "?")
        {
            return "help";
        }

        foreach (Entry e in Table)
        {
            if (word.Length >= e.Abbrev.Length &&
                word.Length <= e.Name.Length &&
                e.Name.StartsWith(word, StringComparison.OrdinalIgnoreCase))
            {
                return e.Verb;
            }
        }

        return null;
    }
}
