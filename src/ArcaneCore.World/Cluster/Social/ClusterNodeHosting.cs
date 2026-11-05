using ArcaneCore.Data;
using ArcaneCore.Data.Characters.Cluster;
using ArcaneCore.Kernel.Cluster.Social;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Cluster.Social;

/// <summary>Registration of the node role (the one call in <c>AddWorldDaemon</c>).</summary>
public static class ClusterNodeHosting
{
    /// <summary>The Npgsql application name of the lease pool (pg_stat_activity shows it).</summary>
    public const string LeaseApplicationName = "arcane-lease";

    /// <summary>Connections the lease pool of one process may hold.</summary>
    public const int LeasePoolSize = 2;

    /// <summary>
    /// Bind <see cref="ClusterNodeOptions"/> and, only when Cluster:Node:Role is not Combined, register the durable lease store (as
    /// <see cref="IRealmAuthorityLeaseStore"/>, over the characters database, with the concrete store), the <see cref="ClusterRoleFeatureFilter"/> and the
    /// <see cref="RealmAuthorityLeaseKeeper"/> that <see cref="WorldHost"/> consults. Combined (the default) registers none of them, so the
    /// world runs every feature and `.cluster leases` queries nothing.
    /// </summary>
    public static IServiceCollection AddClusterNodeRole(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        IConfigurationSection section = configuration.GetSection(ClusterNodeOptions.SectionName);
        services.Configure<ClusterNodeOptions>(section);
        var options = new ClusterNodeOptions();
        section.Bind(options);
        if (options.Role != ClusterNodeRole.Combined)
        {
            // Every lease command is bounded to a third of a renewal interval (at least a second), well under the half interval the keeper
            // needs to decide before a lease lapses, so a hung database fails the renewal instead of hiding it.
            TimeSpan commandTimeout = TimeSpan.FromSeconds(Math.Max(1, options.AuthorityLeaseRenewSeconds / 3));
            // On PostgreSQL the leases connect through their own small Npgsql pool (own connection string: application name, MaxPoolSize 2, connect timeout = the command timeout), so a saturated or stalled
            // general pool cannot starve a renewal. One round is sequential (renew, release and the validation calls are rare), so 2 connections per process is the ceiling: a split cluster of 5 workers
            // adds at most 10 of the server's max_connections. Other providers keep the general pool.
            services.TryAddSingleton(sp => new EfRealmAuthorityLeaseStore(
                sp.GetRequiredService<IServiceScopeFactory>(),
                commandTimeout,
                EfRealmAuthorityLeaseStore.DedicatedContextFactory(
                    sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.Resolve(DatabaseComponent.Characters), LeaseApplicationName, LeasePoolSize, commandTimeout)));
            services.TryAddSingleton<IRealmAuthorityLeaseStore>(sp => sp.GetRequiredService<EfRealmAuthorityLeaseStore>());
            services.TryAddSingleton<ClusterRoleFeatureFilter>();
            services.TryAddSingleton<RealmAuthorityLeaseKeeper>();
            // The stores of the economy, social and instance singletons verify this node's lease at commit (LeaseFencedWrite).
            services.TryAddSingleton<IRealmLeaseFenceSource>(sp => sp.GetRequiredService<RealmAuthorityLeaseKeeper>());
        }

        return services;
    }
}
