using System.Diagnostics;
using System.Globalization;
using System.Text;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Ops.Watchdog;
using ArcaneCore.World.Features;
using ArcaneCore.World.Persistence;
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
        monitor.HangDetail = inProgress => Describe(world, inProgress);
        if (services.GetService<HostLoadSampler>() is { Enabled: true } hostLoad)
        {
            monitor.HostLoad = hostLoad;
            world.HostLoad = hostLoad;
        }

        world.WorldTick += OnTick;
        logger.LogInformation("tick watchdog attached: budget {BudgetMs} ms, hang {HangMs} ms, ring {Ring} frames",
            monitor.BudgetMicros / 1000, monitor.HangMicros == long.MaxValue ? 0 : monitor.HangMicros / 1000, monitor.Ring.Capacity);
    }

    public Task StopAsync()
    {
        _monitor?.StopTicking();
        return Task.CompletedTask;
    }

    /// <summary>How far back a recorded slow tick still explains a hang or overrun line.</summary>
    private static readonly TimeSpan DetailWindow = TickStats.SlowTickRetention;

    /// <summary>
    /// Watchdog thread, only when a hang or overrun line is about to be logged: where the tick is (in progress), or the phase split
    /// and slowest features of the slowest recent slow tick (after the fact), then the character save queue's backlog.
    /// </summary>
    private string Describe(WorldRuntime world, bool inProgress)
    {
        var text = new StringBuilder();
        if (inProgress)
        {
            TickPosition position = world.CurrentPosition;
            if (position.Phase == TickPhase.Idle)
            {
                text.Append("world thread is between ticks (not inside a tick body)");
            }
            else
            {
                double ms = position.SinceTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(position.SinceTimestamp).TotalMilliseconds;
                text.Append(CultureInfo.InvariantCulture, $"phase={position.Phase}, running={position.DescribeWork()} for {ms:F0} ms");
            }
        }
        else
        {
            IReadOnlyList<SlowTickInfo> slow = world.Stats.SlowTicks(DetailWindow);
            if (slow.Count == 0)
            {
                text.Append(CultureInfo.InvariantCulture, $"no tick body over the interval was recorded in the last {DetailWindow.TotalSeconds:F0} s (the stall was outside the tick body)");
            }
            else
            {
                SlowTickInfo worst = slow[0];
                text.Append(CultureInfo.InvariantCulture, $"slowest tick body {worst.DurationMicros / 1000} ms: {worst.Phases.Format()}");
                if (worst.TopFeatures.Count > 0)
                {
                    text.Append("; top features: ");
                    for (int i = 0; i < worst.TopFeatures.Count; i++)
                    {
                        text.Append(CultureInfo.InvariantCulture, $"{(i > 0 ? ", " : string.Empty)}{worst.TopFeatures[i].Name} {worst.TopFeatures[i].Micros / 1000.0:F1} ms");
                    }
                }
            }
        }

        if (services.GetService<CharacterSaveQueue>() is { } saves)
        {
            text.Append(CultureInfo.InvariantCulture, $"; save queue depth {saves.Pending}, oldest pending {saves.OldestPendingAge.TotalMilliseconds:F0} ms");
        }

        return text.ToString();
    }

    // World thread, once per tick: no allocation, no lock, no log.
    private void OnTick(uint diffMs) => _monitor!.OnTick(_clock.NowMicros);
}
