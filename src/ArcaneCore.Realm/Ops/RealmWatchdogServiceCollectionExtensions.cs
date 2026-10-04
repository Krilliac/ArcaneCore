using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops.Watchdog;
using Microsoft.Extensions.Configuration;

// The conventional namespace for AddXxx extensions, so Program.cs needs one added line and no new using.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>The realm daemon's watchdog wiring (docs/ops/watchdog.md): memory, thread pool, heartbeat and counters; there is no tick to watch.</summary>
public static class RealmWatchdogServiceCollectionExtensions
{
    /// <summary>Bind <c>Ops:Watchdog</c> and register the watchdog thread and its monitors.</summary>
    public static IServiceCollection AddRealmWatchdog(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WatchdogOptions>(configuration.GetSection(WatchdogOptions.SectionName));
        services.AddOpsWatchdog();
        return services;
    }
}
