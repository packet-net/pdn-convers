namespace Convers.Host.Uplink;

/// <summary>
/// The transport seam to the single upstream parent node — an abstract, line-oriented byte stream the
/// <see cref="HostLink"/> dials, speaks <c>/..HOST</c> over, and re-dials on loss. It is deliberately
/// transport-agnostic: the concrete providers (RF via an RHP <c>open</c>, or a direct TCP socket to an
/// internet hub — design decision 6) land in W5; W4 owns only this contract and a scripted fake for the
/// sans-IO FSM tests.
/// </summary>
/// <remarks>
/// <para>
/// The convers wire is line-based, CR/LF tolerant and Latin-1 (see <see cref="Convers.Protocol.ConversWire"/>).
/// This seam works in whole lines so the FSM never sees a socket: <see cref="SendLineAsync"/> takes a
/// terminator-stripped line (the provider frames it), and <see cref="ReceiveLineAsync"/> yields one
/// terminator-stripped line, or <see langword="null"/> when the link has closed (peer hang-up, transport
/// loss, or local dispose) — the same end-of-stream signal the pdn-bbs <c>RhpChildConnection.ReceiveAsync</c>
/// uses to drive reconnect.
/// </para>
/// <para>
/// One link instance models one dial attempt's stream. The <see cref="HostLink"/> obtains a fresh link
/// from an <see cref="IUpstreamLinkFactory"/> for every (re)connect, so providers need not be reusable
/// across outages.
/// </para>
/// </remarks>
public interface IUpstreamLink : IAsyncDisposable
{
    /// <summary>
    /// Send one logical line to the parent. The line is the wire body <em>without</em> a terminator; the
    /// provider appends the transport's terminator (<c>\n</c> for TCP, <c>\r</c> for AX.25) and Latin-1
    /// encodes it. Throws if the link is no longer connected.
    /// </summary>
    Task SendLineAsync(string line, CancellationToken cancellationToken);

    /// <summary>
    /// Await the next inbound line from the parent (terminator stripped), or <see langword="null"/> when
    /// the stream has ended (the link is gone). After a null, no further lines arrive on this instance.
    /// </summary>
    Task<string?> ReceiveLineAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Offer host-link compression to the peer (the conversd <c>//COMP 1</c> exchange — W7-deferred plumbing):
    /// send <c>//COMP 1</c> uncompressed and arm our transmit side so subsequent writes are Huffman-coded,
    /// then both directions compress once the peer arms its side. A peer that does not understand or accept
    /// <c>//COMP</c> simply runs uncompressed, so this is always safe to call. Drivers call it once, right
    /// after the <c>/..HOST</c> handshake completes. The default is a no-op — a transport that does not carry
    /// compression (the scripted test fake, or a provider opting out) ignores the offer and runs as today.
    /// </summary>
    Task OfferCompressionAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Whether compression is currently engaged on the transmit side (we are Huffman-coding outbound bytes).
    /// Surfaced for status/diagnostics; <see langword="false"/> on a transport without compression.
    /// </summary>
    bool CompressionEngaged => false;
}

/// <summary>
/// Creates a fresh <see cref="IUpstreamLink"/> for each (re)connect attempt — the dial. The
/// <see cref="HostLink"/> calls this once per loop iteration so a dropped link is replaced by a clean one
/// (mirroring how <c>RhpNodeLink</c> builds a new <c>RhpClient</c> per reconnect). The concrete factories
/// (RF/RHP, TCP) are W5; W4 ships a scripted factory for tests.
/// </summary>
public interface IUpstreamLinkFactory
{
    /// <summary>
    /// Dial the parent and return a connected <see cref="IUpstreamLink"/>. Throws on failure to connect —
    /// the <see cref="HostLink"/> treats a throw as a failed attempt and backs off before retrying.
    /// </summary>
    Task<IUpstreamLink> ConnectAsync(CancellationToken cancellationToken);
}
