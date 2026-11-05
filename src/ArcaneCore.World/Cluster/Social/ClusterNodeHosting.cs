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
    /// <summary>The Npgsql application name of the pool that acquires, renews and releases the leases (pg_stat_activity shows it).</summary>
    public const string LeaseApplicationName = "arcane-lease";

    /// <summary>Connections the lease writer pool of one process may hold (the keeper's calls are sequential: one at a time, plus a second for the release at stop).</summary>
    public const int LeasePoolSize = 2;

    /// <summary>The Npgsql application name of the pool of the lease readers (the peer locator's poll, the fencing-token validations).</summary>
    public const string LeaseReadApplicationName = "arcane-lease-read";

    /// <summary>Connections the lease reader pool of one process may hold.</summary>
    public const int LeaseReadPoolSize = 3;

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
            // On PostgreSQL the leases connect through two small Npgsql pools of their own (own connection strings: application name, max pool size, connect timeout and Command Timeout = the command timeout above), so a
            // saturated or stalled general pool cannot starve a renewal. The writer pool (acquire, renew, release) is only the keeper's; a renewal round and a takeover pass run under separate guards and may overlap (a takeover of a lapsed scope can still be waiting on a row lock while the next renewal tick starts), so up to 2 calls at once and 2 connections; the reader pool (PeerHolderLocator polls
            // GetCurrent every 1000 ms, ClusterRoleFeatureFilter validates fencing tokens, both from any thread) is separate so that a burst of readers can never queue a renewal, and has 3. At most 5 connections per
            // process, 25 for a split cluster of 5 workers; that is well under PostgreSQL's default max_connections of 100, which is an assumption (the setting is not read from the server). MariaDB and SQLite keep the general pool (null factories), and MariaDB keeps the coupling of the fence's shared lock
            // with the renewal's UPDATE (see EfRealmAuthorityLeaseStore).
            services.TryAddSingleton(sp =>
            {
                DatabaseConnectionOptions characters = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.Resolve(DatabaseComponent.Characters);
                return new EfRealmAuthorityLeaseStore(
                    sp.GetRequiredService<IServiceScopeFactory>(),
                    commandTimeout,
                    EfRealmAuthorityLeaseStore.DedicatedContextFactory(characters, LeaseApplicationName, LeasePoolSize, commandTimeout),
                    EfRealmAuthorityLeaseStore.DedicatedContextFactory(characters, LeaseReadApplicationName, LeaseReadPoolSize, commandTimeout));
            });
            services.TryAddSingleton<IRealmAuthorityLeaseStore>(sp => sp.GetRequiredService<EfRealmAuthorityLeaseStore>());
            services.TryAddSingleton<ClusterRoleFeatureFilter>();
            services.TryAddSingleton<RealmAuthorityLeaseKeeper>();
            // The stores of the economy, social and instance singletons verify this node's lease at commit (LeaseFencedWrite); the other realm writers ask the keeper (Holds).
            services.TryAddSingleton<IRealmLeaseFenceSource>(sp => sp.GetRequiredService<RealmAuthorityLeaseKeeper>());
        }

        return services;
    }
}
