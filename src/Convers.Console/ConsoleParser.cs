using Convers.Core;

namespace Convers.Console;

/// <summary>
/// The pure, sans-IO parser for both user-input surfaces. <see cref="ParsePlain"/> reads the
/// plain-language vocabulary (canonical words, any unambiguous prefix, bare text = say);
/// <see cref="ParseClassic"/> reads the literal conversd <c>/</c>-command surface. Both converge on
/// one <see cref="ConsoleIntent"/> set, so the session does not care which surface produced it and
/// the grammars are tested in isolation. No Core mutation happens here — parsing is a value
/// transformation only.
/// </summary>
public static class ConsoleParser
{
    /// <summary>
    /// Parses a line in plain-language mode. A leading <c>/</c> is tolerated (so a classic-minded
    /// user is still understood: <c>/who</c> behaves like <c>who</c>); a bare line of text with no
    /// recognised leading verb is said to the current channel.
    /// </summary>
    public static ConsoleIntent ParsePlain(string? line)
    {
        string raw = line ?? string.Empty;
        string trimmed = raw.TrimStart();
        if (trimmed.Length == 0)
        {
            return new ConsoleIntent.Empty();
        }

        // A leading slash in plain mode is forgiving: treat "/who" as "who". But a bare "/" or
        // "/<text>" that is not a known verb is just chat, so we only strip when a verb follows.
        bool hadSlash = trimmed[0] == '/';
        string body = hadSlash ? trimmed[1..].TrimStart() : trimmed;
        if (body.Length == 0)
        {
            return new ConsoleIntent.Say(raw.TrimStart());
        }

        (string head, string rest) = SplitFirstWord(body);
        string? verb = ConsoleVerbs.Resolve(head);

        if (verb is null)
        {
            // Unknown leading word. In plain mode that is simply something the user said to the
            // channel (the whole original line, sans a forgiving leading slash). The one exception
            // is an explicit slash with a non-verb head, which we still treat as said text so chat
            // like "/me waves" is never lost.
            return new ConsoleIntent.Say(hadSlash ? body : trimmed);
        }

        return BuildIntent(verb, rest, raw);
    }

    /// <summary>
    /// Parses a line in classic (<c>/</c>-command) mode. Lines beginning with <c>/</c> are commands,
    /// resolved by the conversd capital-letter abbreviation rule; any other line is ordinary chat
    /// said to the current channel (matching conversd, where non-<c>/</c> input is channel text).
    /// </summary>
    public static ConsoleIntent ParseClassic(string? line)
    {
        string raw = line ?? string.Empty;
        if (raw.Length == 0 || raw.TrimEnd().Length == 0)
        {
            return new ConsoleIntent.Empty();
        }

        if (raw[0] != '/')
        {
            return new ConsoleIntent.Say(raw);
        }

        string body = raw[1..];
        if (body.Length == 0)
        {
            // A bare "/" is meaningless; surface it as unknown so the user gets a hint.
            return new ConsoleIntent.Unknown(raw);
        }

        (string head, string rest) = SplitFirstWord(body);
        string? verb = ClassicCommands.Resolve(head);
        if (verb is null)
        {
            return new ConsoleIntent.Unknown(raw);
        }

        return BuildIntent(verb, rest, raw);
    }

    /// <summary>
    /// Shared intent construction once a canonical verb is known — the per-verb argument grammar is
    /// identical for both surfaces, so the plain and classic parsers funnel through here.
    /// </summary>
    private static ConsoleIntent BuildIntent(string verb, string rest, string raw)
    {
        rest = rest.Trim();
        switch (verb)
        {
            case "say":
                return new ConsoleIntent.Say(rest);

            case "join":
                return ParseJoin(rest, raw);

            case "name":
                // /NAME <call> [channel]: the user is already authenticated (decision 4), so the
                // callsign is ignored; only an optional channel matters (a (re)join to it).
                return ParseName(rest);

            case "msg":
            {
                (string to, string text) = SplitFirstWord(rest);
                if (to.Length == 0 || text.Trim().Length == 0)
                {
                    return new ConsoleIntent.Unknown(raw);
                }

                return new ConsoleIntent.Msg(Callsigns.Normalize(StripChannelHash(to)), text.Trim());
            }

            case "topic":
                return new ConsoleIntent.Topic(NormaliseTopic(rest));

            case "personal":
                return new ConsoleIntent.Personal(NormaliseTopic(rest));

            case "away":
                return new ConsoleIntent.Away(rest);

            case "mode":
                return ParseMode(rest);

            case "oper":
                return new ConsoleIntent.Oper(rest.Trim());

            case "who":
                return new ConsoleIntent.Who(rest);

            case "invite":
                return ParseInvite(rest, raw);

            case "leave":
                return new ConsoleIntent.Leave(rest);

            case "quit":
                return new ConsoleIntent.Quit(rest);

            case "help":
                return new ConsoleIntent.Help(rest);

            default:
                return new ConsoleIntent.Unknown(raw);
        }
    }

    private static ConsoleIntent.Join ParseName(string rest)
    {
        // Drop the leading callsign token (auto-login ignores it); a following token may be a channel.
        (_, string after) = SplitFirstWord(rest);
        after = after.Trim();
        if (after.Length == 0)
        {
            return new ConsoleIntent.Join(null);
        }

        (string chanToken, _) = SplitFirstWord(after);
        return TryParseChannel(chanToken, out int channel)
            ? new ConsoleIntent.Join(channel)
            : new ConsoleIntent.Join(null);
    }

    private static ConsoleIntent ParseJoin(string rest, string raw)
    {
        if (rest.Length == 0)
        {
            return new ConsoleIntent.Join(null);
        }

        // Only the first token is the channel; conversd ignores trailing words on /join.
        (string first, _) = SplitFirstWord(rest);
        if (TryParseChannel(first, out int channel))
        {
            return new ConsoleIntent.Join(channel);
        }

        return new ConsoleIntent.Unknown(raw);
    }

    private static ConsoleIntent.Mode ParseMode(string rest)
    {
        rest = rest.Trim();
        if (rest.Length == 0)
        {
            // Bare 'mode' / '/mode' shows the current channel's modes.
            return new ConsoleIntent.Mode(null, string.Empty);
        }

        // An optional leading channel token (bare number or #number) targets a specific channel; anything
        // else (a +/- toggle string, or a bare letter set) applies to the current channel.
        (string first, string after) = SplitFirstWord(rest);
        if (TryParseChannel(first, out int channel))
        {
            return new ConsoleIntent.Mode(channel, after.Trim());
        }

        return new ConsoleIntent.Mode(null, rest);
    }

    private static ConsoleIntent ParseInvite(string rest, string raw)
    {
        if (rest.Length == 0)
        {
            return new ConsoleIntent.Unknown(raw);
        }

        (string user, string after) = SplitFirstWord(rest);
        int? channel = null;
        after = after.Trim();
        if (after.Length > 0)
        {
            (string chanToken, _) = SplitFirstWord(after);
            if (!TryParseChannel(chanToken, out int c))
            {
                return new ConsoleIntent.Unknown(raw);
            }

            channel = c;
        }

        return new ConsoleIntent.Invite(Callsigns.Normalize(StripChannelHash(user)), channel);
    }

    /// <summary>conversd's <c>@</c> sentinel clears a topic / personal text; otherwise keep verbatim.</summary>
    private static string NormaliseTopic(string text) => text.Trim() == "@" ? string.Empty : text;

    /// <summary>Splits the first whitespace-delimited word from the remainder (remainder un-trimmed at the head).</summary>
    private static (string Head, string Tail) SplitFirstWord(string text)
    {
        string s = text.TrimStart();
        int sp = IndexOfWhitespace(s);
        if (sp < 0)
        {
            return (s, string.Empty);
        }

        return (s[..sp], s[(sp + 1)..]);
    }

    private static int IndexOfWhitespace(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsWhiteSpace(s[i]))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>A leading <c>#</c> on a channel/target token is accepted and stripped (conversd convention).</summary>
    private static string StripChannelHash(string token) =>
        token.StartsWith('#') ? token[1..] : token;

    private static bool TryParseChannel(string token, out int channel)
    {
        channel = 0;
        string t = StripChannelHash(token);
        if (!int.TryParse(t, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int value))
        {
            return false;
        }

        if (!ChannelNumber.IsValid(value))
        {
            return false;
        }

        channel = value;
        return true;
    }
}
