using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops.Watchdog.Counters;
using ArcaneCore.Kernel.Ops.Watchdog.Heartbeat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ArcaneCore.Kernel.Ops.Watchdog;

/// <summary>
/// DI wiring shared by both daemons. The caller binds <see cref="WatchdogOptions"/> from its
/// configuration (<c>services.Configure&lt;WatchdogOptions&gt;(...)</c>, which lives in the hosting
/// packages the daemons reference) and then calls <see cref="AddOpsWatchdog"/>; calling it twice
/// is harmless. Registered: the clock, the shared counter registry, the memory monitor, the
/// thread-pool probe, the heartbeat writer, the counter dump, the options validator and the
/// <see cref="WatchdogHost"/> hosted service. The tick monitor is the world daemon's addition.
/// </summary>
public static class WatchdogServiceCollectionExtensions
{
    public static IServiceCollection AddOpsWatchdog(this IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(WatchdogRegistered)))
        {
            return services;
        }

        services.AddSingleton<WatchdogRegistered>();
        services.AddOptions<WatchdogOptions>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<WatchdogOptions>, WatchdogOptionsValidation>());
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<WatchdogOptions>>().Value);
        services.TryAddSingleton(WatchdogClock.System);
        services.TryAddSingleton(CounterRegistry.Default);
        services.TryAddSingleton<IMemoryProbe, GcMemoryProbe>();
        services.TryAddSingleton<IMemoryPressureActuator>(sp => new RuntimeMemoryPressureActuator(sp.GetService<IHostApplicationLifetime>()));
        services.TryAddSingleton<MemoryMonitor>();
        services.TryAddSingleton<ThreadPoolProbe>();
        services.TryAddSingleton<HeartbeatWriter>();
        services.TryAddSingleton<CounterDump>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWatchdogMonitor, MemoryMonitor>(sp => sp.GetRequiredService<MemoryMonitor>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWatchdogMonitor, ThreadPoolProbe>(sp => sp.GetRequiredService<ThreadPoolProbe>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWatchdogMonitor, HeartbeatWriter>(sp => sp.GetRequiredService<HeartbeatWriter>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWatchdogMonitor, CounterDump>(sp => sp.GetRequiredService<CounterDump>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, WatchdogHost>());
        return services;
    }

    /// <summary>
    /// The world daemon's tick monitor: registered as a monitor and as the liveness source that
    /// gates the heartbeat. The world feature that feeds it finds it through DI.
    /// </summary>
    public static IServiceCollection AddTickWatchdog(this IServiceCollection services)
    {
        services.AddOpsWatchdog();
        services.TryAddSingleton<TickMonitor>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWatchdogMonitor, TickMonitor>(sp => sp.GetRequiredService<TickMonitor>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILivenessSource, TickMonitor>(sp => sp.GetRequiredService<TickMonitor>()));
        return services;
    }

    private sealed class WatchdogRegistered;
}
