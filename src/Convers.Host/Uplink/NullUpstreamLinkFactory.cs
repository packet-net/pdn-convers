namespace Convers.Host.Uplink;

/// <summary>
/// The "no uplink" provider (design decision 6: <c>provider: null</c> — local-only, the develop/test-
/// against-the-oracle configuration). It never connects, so the <see cref="HostLink"/> stays in its
/// backoff-and-drain state: local sessions still fan out among each other through the hub (the link's
/// owning loop keeps draining local events while it waits), but nothing is sent or received upstream.
/// </summary>
public sealed class NullUpstreamLinkFactory : IUpstreamLinkFactory
{
    /// <summary>The shared instance.</summary>
    public static readonly NullUpstreamLinkFactory Instance = new();

    private NullUpstreamLinkFactory()
    {
    }

    /// <inheritdoc/>
    public Task<IUpstreamLink> ConnectAsync(CancellationToken cancellationToken) =>
        Task.FromException<IUpstreamLink>(new InvalidOperationException("No uplink provider configured (local-only)."));
}
