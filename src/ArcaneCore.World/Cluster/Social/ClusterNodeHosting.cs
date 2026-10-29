using ArcaneCore.Data.Characters.Cluster;
using ArcaneCore.Kernel.Cluster.Social;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ArcaneCore.World.Cluster.Social;

/// <summary>Registration of the node role (the one call in <c>AddWorldDaemon</c>).</summary>
public static class ClusterNodeHosting
{
    /// <summary>
    /// Bind <see cref="ClusterNodeOptions"/> and, only when Cluster:Node:Role is not Combined, register the durable lease store (as
    /// <see cref="IRealmAuthorityLeaseStore"/>, over the characters database), the <see cref="ClusterRoleFeatureFilter"/> and the
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
            services.TryAddSingleton<IRealmAuthorityLeaseStore>(sp => sp.GetRequiredService<EfRealmAuthorityLeaseStore>());
            services.TryAddSingleton<ClusterRoleFeatureFilter>();
            services.TryAddSingleton<RealmAuthorityLeaseKeeper>();
        }

        return services;
    }
}
