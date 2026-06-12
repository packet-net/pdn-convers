using System.Collections.Concurrent;
using Convers.Console;
using Convers.Core;
using Convers.Host.Uplink;

namespace Convers.Host.Sessions;

/// <summary>
/// The local-delivery fan-out: the <see cref="ILocalDelivery"/> sink the <see cref="HostLink"/> hands
/// the hub's <c>Deliver*</c> actions, routing each to the addressed local session's terminal (the
/// inbound half of the presence bridge — design decision 5). A session registers its line sink on
/// connect (<see cref="Register"/>) and removes it on disconnect (<see cref="Unregister"/>); the
/// registry renders each action with <see cref="ActionRenderer"/> and writes the resulting line to the
/// one session it is addressed to.
/// </summary>
/// <remarks>
/// Delivery is fire-and-forget from the hub's loop: each session owns a bounded outbound queue its own
/// task drains (so a slow RF link never blocks the hub). Writes happen off the hub loop. Thread-safe:
/// the session map is concurrent and each per-session queue is single-producer/single-consumer.
/// </remarks>
public sealed class LocalSessionRegistry : ILocalDelivery
{
    private readonly ConcurrentDictionary<string, Func<string, CancellationToken, ValueTask>> _sinks = new(StringComparer.Ordinal);

    /// <summary>The number of attached local sessions (diagnostics / tests).</summary>
    public int Count => _sinks.Count;

    /// <summary>
    /// Registers a session's line sink under <paramref name="sessionId"/>. The sink is invoked (off the
    /// hub loop) with each rendered line addressed to this session. Replacing an existing id is allowed
    /// (a reconnect with the same id).
    /// </summary>
    public void Register(string sessionId, Func<string, CancellationToken, ValueTask> writeLine)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(writeLine);
        _sinks[sessionId] = writeLine;
    }

    /// <summary>Removes a session's sink (on disconnect). Idempotent.</summary>
    public void Unregister(string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        _sinks.TryRemove(sessionId, out _);
    }

    /// <inheritdoc/>
    public void Deliver(ConversAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        string? sessionId = TargetSession(action);
        if (sessionId is null || !_sinks.TryGetValue(sessionId, out Func<string, CancellationToken, ValueTask>? sink))
        {
            return;
        }

        string? line = ActionRenderer.RenderOne(action, sessionId);
        if (line is null)
        {
            return;
        }

        // Fire-and-forget onto the session's own write path; a faulted sink (dead link) is the session's
        // problem to clean up, not the hub's.
        _ = WriteSafelyAsync(sink, line);
    }

    private static async Task WriteSafelyAsync(Func<string, CancellationToken, ValueTask> sink, string line)
    {
        try
        {
            await sink(line, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The session's transport died; the session task surfaces and cleans up. Swallow here so
            // the hub-loop dispatch never faults on one dead local link.
        }
    }

    /// <summary>The session a local-bound action is addressed to, or null for an action with no local target.</summary>
    private static string? TargetSession(ConversAction action) => action switch
    {
        ConversAction.DeliverChannelMessage a => a.SessionId,
        ConversAction.DeliverPrivateMessage a => a.SessionId,
        ConversAction.DeliverAwayNotice a => a.SessionId,
        ConversAction.DeliverInvite a => a.SessionId,
        ConversAction.DeliverJoinNotice a => a.SessionId,
        ConversAction.DeliverLeaveNotice a => a.SessionId,
        ConversAction.DeliverTopic a => a.SessionId,
        _ => null,
    };
}
