using Convers.Protocol;

namespace Convers.Host.Uplink;

/// <summary>
/// Static configuration for the upstream <see cref="HostLink"/>: our identity in the <c>/..HOST</c>
/// handshake, the facility set we advertise, and the keepalive / reconnect timings. Defaults match the
/// conversd-saupp behaviour captured from the oracle (see <c>docker/compose.oracle.yml</c>).
/// </summary>
public sealed record HostLinkOptions
{
    /// <summary>
    /// Our convers host name, sent as the first field of <c>/..HOST</c>. conversd caps the host name at
    /// 9 characters; <see cref="Validate"/> enforces it.
    /// </summary>
    public required string HostName { get; init; }

    /// <summary>
    /// The software string (second <c>/..HOST</c> field, ≤ 8 chars). Identifies pdn-convers to the parent.
    /// </summary>
    public string Software { get; init; } = "pdnconv1";

    /// <summary>
    /// The facilities we advertise in the handshake. The oracle answers <c>Aadmpunfi</c>; we offer the
    /// subset a strict leaf honours — away (new+old), modes, ping-pong, udat/user, and nicknames. We do
    /// <em>not</em> advertise <c>d</c> (destination forwarding) or <c>f</c>/<c>i</c> (saupp internals) as a
    /// leaf never transits or forwards destinations (design decision 1).
    /// </summary>
    public Facilities Facilities { get; init; } =
        Facilities.AwayNew | Facilities.AwayOld | Facilities.ChannelModes |
        Facilities.PingPong | Facilities.Udat | Facilities.Nicknames;

    /// <summary>
    /// The optional host-link password (the parent's <c>Access … HOST</c> + password requirement). When
    /// set, it is presented after the handshake. The oracle requires none, so this is empty by default.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// The system-information string answered to an inbound <c>/..SYSI</c> for us (SPECS line 136: "at the
    /// MINIMUM, the email address of the convers sysop should be available"). The Host fills this from
    /// <c>convers.yaml</c>; empty means only the version/identity line is returned.
    /// </summary>
    public string SysInfo { get; init; } = "";

    /// <summary>
    /// How long to wait for the parent's <c>/..HOST</c> reply before treating the attempt as failed and
    /// reconnecting.
    /// </summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often we send an unsolicited <c>/..PING</c> to measure and hold the link once established.
    /// </summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Drop and reconnect if no line of any kind arrives from the parent for this long (silence = dead
    /// link). Must exceed <see cref="PingInterval"/> so a healthy PONG resets it before it fires.
    /// </summary>
    public TimeSpan SilenceTimeout { get; init; } = TimeSpan.FromSeconds(180);

    /// <summary>First reconnect delay after a link loss (doubled each failed attempt).</summary>
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The reconnect backoff cap.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Validates the options, normalising and enforcing the wire's host-name limit. Returns the same
    /// record with a normalised host name. Throws <see cref="ArgumentException"/> on an empty or too-long
    /// host name, or on incoherent timings.
    /// </summary>
    public HostLinkOptions Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(HostName);
        string host = HostName.Trim();
        if (host.Length > MaxHostNameLength)
        {
            throw new ArgumentException(
                $"Convers host name '{host}' exceeds {MaxHostNameLength} characters.", nameof(HostName));
        }

        if (SilenceTimeout <= PingInterval)
        {
            throw new ArgumentException(
                "SilenceTimeout must exceed PingInterval so a healthy link is not torn down.", nameof(SilenceTimeout));
        }

        if (InitialBackoff <= TimeSpan.Zero || MaxBackoff < InitialBackoff)
        {
            throw new ArgumentException("Backoff timings are incoherent.", nameof(InitialBackoff));
        }

        return this with { HostName = host };
    }

    /// <summary>conversd's host-name length cap (<c>HOSTNAMESIZE</c>); the handshake field is ≤ 9 chars.</summary>
    public const int MaxHostNameLength = 9;
}
