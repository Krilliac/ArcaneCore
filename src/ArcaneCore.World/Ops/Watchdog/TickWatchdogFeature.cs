using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Ops.Watchdog;
using ArcaneCore.World.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Ops.Watchdog;

/// <summary>
/// Feeds the Kernel <see cref="TickMonitor"/> from the world thread (docs/ops/watchdog.md). It is
/// the one consumer of the existing <see cref="WorldRuntime.WorldTick"/> seam here: the handler
/// runs at the start of every tick and does nothing but <see cref="TickMonitor.OnTick(long)"/>, a
/// clock read and a few stores. The body-duration percentiles of <see cref="WorldRuntime.Stats"/>
/// are read by the watchdog thread only (its snapshot takes the recorder's short lock, never the
/// world thread's time beyond that). When the watchdog is not registered (a test host, or
/// <c>AddWorldWatchdog</c> not called) the feature attaches nothing.
/// </summary>
public sealed class TickWatchdogFeature(IServiceProvider services, ILogger<TickWatchdogFeature> logger) : IWorldFeature
{
    private TickMonitor? _monitor;
    private WatchdogClock _clock = WatchdogClock.System;

    /// <summary>The monitor this feature feeds, or null when the watchdog is not registered.</summary>
    public TickMonitor? Monitor => _monitor;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        TickMonitor? monitor = services.GetService<TickMonitor>();
        if (monitor is null)
        {
            return;
        }

        if (!monitor.Enabled)
        {
            logger.LogInformation("tick watchdog disabled (Ops:Watchdog:TickMonitor:Enabled=false)");
            return;
        }

        _clock = services.GetService<WatchdogClock>() ?? WatchdogClock.System;
        _monitor = monitor;
        monitor.StartTicking(world.Options.TickIntervalMs);
        monitor.BodyStats = () =>
        {
            TickStatsSnapshot stats = world.Stats.Snapshot();
            return new TickBodySummary(stats.Samples, stats.P50Micros, stats.P99Micros, stats.MaxMicros, stats.Overruns);
        };
        world.WorldTick += OnTick;
        logger.LogInformation("tick watchdog attached: budget {BudgetMs} ms, hang {HangMs} ms, ring {Ring} frames",
            monitor.BudgetMicros / 1000, monitor.HangMicros == long.MaxValue ? 0 : monitor.HangMicros / 1000, monitor.Ring.Capacity);
    }

    public Task StopAsync()
    {
        _monitor?.StopTicking();
        return Task.CompletedTask;
    }

    // World thread, once per tick: no allocation, no lock, no log.
    private void OnTick(uint diffMs) => _monitor!.OnTick(_clock.NowMicros);
}
