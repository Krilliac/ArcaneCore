using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops.Watchdog;
using Microsoft.Extensions.Configuration;

// The conventional namespace for AddXxx extensions, so Program.cs needs one added line and no new using.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>The world daemon's watchdog wiring (docs/ops/watchdog.md): the shared monitors plus the tick monitor.</summary>
public static class WorldWatchdogServiceCollectionExtensions
{
    /// <summary>Bind <c>Ops:Watchdog</c> and register the watchdog thread, the memory, thread-pool, heartbeat and counter monitors and the world-tick monitor.</summary>
    public static IServiceCollection AddWorldWatchdog(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WatchdogOptions>(configuration.GetSection(WatchdogOptions.SectionName));
        services.AddTickWatchdog();
        return services;
    }
}
