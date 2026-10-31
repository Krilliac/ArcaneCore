using ArcaneCore.World.Ops.Accounts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Ops.RemoteAdmin;

/// <summary>Registers the world daemon's remote administration listener (docs/ops/remote-admin.md).</summary>
public static class RemoteAdminServiceCollectionExtensions
{
    /// <summary>Bind <c>World:RemoteAdmin</c> and add the RA hosted service. Call after <c>AddWorldDaemon</c> (it stops before the world).</summary>
    public static IServiceCollection AddRemoteAdmin(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RemoteAdminOptions>(configuration.GetSection(RemoteAdminOptions.SectionName));
        services.AddSecondFactorChecker();
        services.AddHostedService<RemoteAdminServer>();
        return services;
    }
}