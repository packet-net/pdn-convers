using Convers.Protocol;

namespace Convers.Host.Uplink;

/// <summary>
/// A connected host peer the node can relay a <c>/..</c> line to — the uplink (one, upstream) or an
/// accepted downstream peer (W7c, when peering is enabled). Each sink has a stable <see cref="PeerId"/>
/// so the relay can fan a peer's traffic out to every <em>other</em> peer (the SPECS "golden rule":
/// relay unrecognised <c>/..</c> to all other connected hosts) while never echoing it back to its origin
/// (the loop guard — design decisions 1 and 4). The uplink's sink is the <see cref="HostLink"/> itself;
/// a downstream peer's sink is its <c>DownstreamPeerSession</c>.
/// </summary>
/// <remarks>
/// Sends are made on the <see cref="HostLink"/>'s single owning loop (the hub is not thread-safe), so an
/// implementation must be cheap and non-blocking from that loop — queue the line and return. A faulted
/// send is the peer's own concern (its session task surfaces the loss and unregisters); the relay never
/// faults on one dead peer.
/// </remarks>
public interface IPeerSink
{
    /// <summary>A stable identity for this peer (e.g. <c>"uplink"</c> or <c>"peer-3"</c>), unique per node.</summary>
    string PeerId { get; }

    /// <summary>
    /// The peer's convers host name once its handshake has completed (empty while connecting). Used by the
    /// relay's loop guard to avoid reflecting a host's own presence back toward it.
    /// </summary>
    string PeerHostName { get; }

    /// <summary>
    /// Enqueue one host command to send to this peer (best-effort, non-blocking). The implementation
    /// formats and frames it onto the peer's transport off the owning loop.
    /// </summary>
    void Enqueue(HostCommand command);
}
