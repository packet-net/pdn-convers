namespace Convers.Console;

/// <summary>
/// A parsed user intent — the surface-independent result of reading one input line (plain-language or
/// classic <c>/</c>-command). The session translates these into Core
/// <see cref="Convers.Core.ConversEvent"/>s and/or renders an immediate reply. Keeping the parse a
/// pure value (no I/O, no Core types) makes the plain-vs-classic grammar trivially unit-testable and
/// keeps the two surfaces converging on one set of intents.
/// </summary>
public abstract record ConsoleIntent
{
    private ConsoleIntent()
    {
    }

    /// <summary>An empty / whitespace-only line: do nothing.</summary>
    public sealed record Empty : ConsoleIntent;

    /// <summary>Say <paramref name="Text"/> to the current channel (bare text, or <c>say</c>/<c>/me</c>-less).</summary>
    public sealed record Say(string Text) : ConsoleIntent;

    /// <summary>Join / switch to a channel. Null = a bare query (no number given).</summary>
    public sealed record Join(int? Channel) : ConsoleIntent;

    /// <summary>Private message <paramref name="Text"/> to <paramref name="To"/>.</summary>
    public sealed record Msg(string To, string Text) : ConsoleIntent;

    /// <summary>Set (or, with empty <paramref name="Text"/>, query) the current channel topic.</summary>
    public sealed record Topic(string Text) : ConsoleIntent;

    /// <summary>Set (or, with empty <paramref name="Text"/>, clear) personal text.</summary>
    public sealed record Personal(string Text) : ConsoleIntent;

    /// <summary>List who is present (optional scope/argument carried verbatim).</summary>
    public sealed record Who(string Argument) : ConsoleIntent;

    /// <summary>Invite <paramref name="User"/> to a channel (null = the current channel).</summary>
    public sealed record Invite(string User, int? Channel) : ConsoleIntent;

    /// <summary>Mark away with <paramref name="Text"/> (empty = back).</summary>
    public sealed record Away(string Text) : ConsoleIntent;

    /// <summary>
    /// Show or set channel modes (<c>/..MODE</c>). <paramref name="Channel"/> null = the current channel.
    /// <paramref name="Options"/> empty = show the current modes; otherwise it is the verbatim toggle
    /// string (e.g. <c>+mt</c>, <c>-s</c>) to apply.
    /// </summary>
    public sealed record Mode(int? Channel, string Options) : ConsoleIntent;

    /// <summary>
    /// Operator login (<c>/..OPER</c> semantics): authenticate with the node sysop
    /// <paramref name="Secret"/> to gain operator status. Empty secret is a request the session refuses
    /// with a usage hint.
    /// </summary>
    public sealed record Oper(string Secret) : ConsoleIntent;

    /// <summary>Leave the current channel (and sign off when it is the only one).</summary>
    public sealed record Leave(string Reason) : ConsoleIntent;

    /// <summary>Sign off the whole session.</summary>
    public sealed record Quit(string Reason) : ConsoleIntent;

    /// <summary>Show help (optionally for a specific <paramref name="Subject"/> command).</summary>
    public sealed record Help(string Subject) : ConsoleIntent;

    /// <summary>
    /// The line could not be understood (an unknown word, an unknown <c>/</c>-command, or an
    /// ambiguous prefix). <paramref name="Raw"/> is the verbatim line for the error message.
    /// </summary>
    public sealed record Unknown(string Raw) : ConsoleIntent;
}
