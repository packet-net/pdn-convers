using Convers.Protocol;

namespace Convers.Host.Uplink;

/// <summary>
/// The sans-IO relay policy for a node that has accepted a downstream peer (W7c): given one inbound
/// <c>/..</c> command and the peer it arrived from, decide what to forward to the <em>other</em> connected
/// hosts — the SPECS "golden rule" ("relay unrecognised <c>/..</c> to all other connected hosts"), made
/// live once a downstream peer exists. For a strict leaf (no downstream peer) there is no "other host", so
/// nothing is relayed and this is never exercised — the node stays a leaf (design decision 1).
/// </summary>
/// <remarks>
/// <para>
/// The loop guard (design decisions 1 and 4): traffic is relayed to every peer <em>except its origin</em>
/// (no echo back), and link-local control verbs never transit:
/// <list type="bullet">
///   <item><c>/..HOST</c> — the handshake, link-local and role-specific; never relayed.</item>
///   <item><c>/..PING</c> / <c>/..PONG</c> — keepalive measured per-link; never relayed.</item>
///   <item><c>/..LOOP</c> — a loop signal; the receiving link is dropped, not forwarded.</item>
///   <item><c>/..SYSI</c> and <c>/..ROUT</c> — answered by each node for its own view (the engine's
///         SysInfo / Route reply) and not transited: we are no transit for these queries, and a leaf gives
///         its own single-hop route answer rather than fanning the query across the tree (loop-safe).</item>
/// </list>
/// </para>
/// <para>
/// Everything else (presence, messages, topics, modes, away, invites, destinations, unknown verbs) is
/// relayed verbatim to the other hosts.
/// </para>
/// </remarks>
public static class PeerRelay
{
    /// <summary>
    /// The command to relay to every peer other than the origin, or <see langword="null"/> when this verb
    /// is link-local (answered per-link, not transited).
    /// </summary>
    public static HostCommand? Forwarded(HostCommand inbound)
    {
        ArgumentNullException.ThrowIfNull(inbound);
        return inbound switch
        {
            // Link-local control: answered per-link by each node's own engine and never transited between
            // hosts (design decisions 1/4). HOST/PING/PONG are link keepalive/handshake; LOOP drops the
            // link; SYSI and ROUT are answered by each node for its own view (the engine's SysInfo/Route
            // replies) and NOT forwarded — keeping the tree loop-safe (we are no transit for these queries,
            // and a leaf gives its own single-hop route answer rather than fanning the query across peers).
            HostHandshake => null,
            HostPing => null,
            HostPong => null,
            HostLoop => null,
            HostSysInfo => null,
            HostRoute => null,

            // Everything else (presence, messages, topics, modes, away, invites, dest, unknown) relays
            // verbatim to the other connected hosts — the golden rule.
            _ => inbound,
        };
    }
}
