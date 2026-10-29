using ArcaneCore.Data;
using ArcaneCore.Data.Auth;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops.ServerConsole;
using ArcaneCore.Realm.Net;
using ArcaneCore.Realm.Ops.ServerConsole;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ArcaneCore.Realm;

/// <summary>The realm daemon's complete service registration, so the CI guard builds exactly what <c>Program.cs</c> builds (with scope and build validation on).</summary>
public static class RealmProductionServices
{
    public static IServiceCollection AddRealmProductionServices(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddArcaneCoreLogging(configuration);
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.SectionName));
        services.Configure<AutoPatchOptions>(configuration.GetSection(AutoPatchOptions.SectionName)); // logon auto-patcher, off by default
        services.Configure<RealmSeedOptions>(configuration.GetSection(RealmSeedOptions.SectionName));
        services.AddNetProtection(configuration);
        services.AddAuthDatabase(configuration);
        services.AddRealmResilience(configuration); // after AddAuthDatabase: guards the auth stores (docs/ops/resilience.md)
        services.AddSingleton<LogonServer>(); // one instance: the hosted service and the console (its session count) share it
        services.AddHostedService(sp => sp.GetRequiredService<LogonServer>());
        services.Configure<RealmConsoleOptions>(configuration.GetSection(RealmConsoleOptions.SectionName));
        services.TryAddSingleton(_ => ConsoleInputSource.Default());
        services.TryAddSingleton(TimeProvider.System);
        services.AddHostedService<SupervisedChildDrain>(); // a child of the cluster host supervisor stops gracefully on its stdin "drain" line or EOF
        services.AddHostedService<RealmConsole>(); // typed commands at the console window, off when stdin is redirected (docs/areas/server-console.md)
        services.AddRealmWatchdog(configuration);
        // Ops:Metrics, off by default (docs/ops/metrics.md). The Realm role binds Ops:Metrics:RealmPrometheusPrefix (default port 9465),
        // so a profile shared with the World (which keeps 9464) runs both endpoints.
        ArcaneCore.Kernel.Ops.Metrics.MetricsServiceCollectionExtensions.AddOpsMetrics(services, configuration, "arcanecore-realm", MetricsRole.Realm);
        return services;
    }
}
