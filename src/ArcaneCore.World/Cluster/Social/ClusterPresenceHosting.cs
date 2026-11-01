using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Peer;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Cluster.Social;

/// <summary>
/// Registration of realm presence (M15.2): the in-memory <see cref="IRealmPresenceDirectory"/> and <see cref="IClusterSocialRouter"/>, the <see cref="PresenceHolderService"/> over
/// them, one <see cref="LeaseRoutedPresence"/> answering for the directory, router and <see cref="IPresenceAuthority"/>, and the <see cref="ClusterPresencePublisher"/> the social
/// feature feeds. In Combined mode (no <see cref="RealmAuthorityLeaseKeeper"/>) the local service is always the authority: the in-process fast path, no peer link. Otherwise this
/// node is the authority while it holds the Social lease and writes through the <see cref="PeerLinkClient"/> to the holder: the client and the holder's listener are
/// built by <see cref="PeerLinkHostedService"/> (<see cref="ClusterPeerLinkHosting.AddClusterPeerLink"/>), published through <see cref="PeerLinkRuntime"/>.
/// </summary>
public static class ClusterPresenceHosting
{
    /// <summary>How often the publisher sends pending presence operations and drains this worker's kicks.</summary>
    public static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>The gateway id stamped on presence entries of sessions served in this process.</summary>
    public const string LocalGatewayId = "gateway";

    public static IServiceCollection AddClusterPresence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<InMemoryRealmPresenceDirectory>();
        services.TryAddSingleton(sp => new InMemoryClusterSocialRouter(sp.GetRequiredService<InMemoryRealmPresenceDirectory>()));
        services.TryAddSingleton(sp => new PresenceHolderService(
            sp.GetRequiredService<InMemoryRealmPresenceDirectory>(),
            sp.GetRequiredService<InMemoryClusterSocialRouter>()));
        services.TryAddSingleton(sp =>
        {
            RealmAuthorityLeaseKeeper? keeper = sp.GetService<RealmAuthorityLeaseKeeper>();
            PeerLinkRuntime? runtime = sp.GetService<PeerLinkRuntime>();
            PeerLinkClient? link = sp.GetService<PeerLinkClient>();
            PeerPresenceClient? fixedRemote = link is null ? null : new PeerPresenceClient(link);
            Func<bool> holdsSocial = keeper is null ? static () => true : () => keeper.TokenFor(RealmAuthorityScopes.Social) is not null;
            return new LeaseRoutedPresence(sp.GetRequiredService<PresenceHolderService>(), holdsSocial, () => runtime?.Presence ?? fixedRemote);
        });
        services.TryAddSingleton<IRealmPresenceDirectory>(sp => sp.GetRequiredService<LeaseRoutedPresence>());
        services.TryAddSingleton<IClusterSocialRouter>(sp => sp.GetRequiredService<LeaseRoutedPresence>());
        services.TryAddSingleton<IPresenceAuthority>(sp => sp.GetRequiredService<LeaseRoutedPresence>());
        services.TryAddSingleton(sp =>
        {
            string workerId = sp.GetService<RealmAuthorityLeaseKeeper>()?.NodeId
                ?? sp.GetService<IOptions<GatewayWorkerOptions>>()?.Value.NodeId
                ?? "world";
            SessionRegistry sessions = sp.GetRequiredService<SessionRegistry>();
            return new ClusterPresencePublisher(
                sp.GetRequiredService<IPresenceAuthority>(),
                workerId,
                LocalGatewayId,
                account => sessions.KickSuperseded(account),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<ClusterPresencePublisher>());
        });
        services.AddHostedService(sp => new PresencePublisherStarter(sp.GetRequiredService<ClusterPresencePublisher>()));
        return services;
    }

    /// <summary>Starts the publisher's flush loop with the host; the publisher itself is disposed by the container.</summary>
    private sealed class PresencePublisherStarter(ClusterPresencePublisher publisher) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            publisher.Start(FlushInterval);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
