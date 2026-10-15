using System.Diagnostics;
using System.Globalization;
using System.Text;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Ops.Watchdog;
using ArcaneCore.World.Features;
using ArcaneCore.World.Ops.Announce;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Playerbots.Qa;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
        _attachedMicros = _clock.NowMicros;
        _warmupMicros = (services.GetService<IOptions<PlayerbotQaOptions>>()?.Value.TickHangWarmupMinutes ?? 5) * 60_000_000L;
        _monitor = monitor;
        monitor.StartTicking(world.Options.TickIntervalMs);
        monitor.BodyStats = () =>
        {
            try
            {
                TickStatsSnapshot stats = world.Stats.Snapshot();
                return new TickBodySummary(stats.Samples, stats.P50Micros, stats.P99Micros, stats.MaxMicros, stats.Overruns);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("watchdog-body-stats", ex);
                return default;
            }
        };
        monitor.AttachLatency(world.Stats.FrameLatency, world.Stats.BodyLatency, world.Stats.PhaseLatencies);
        monitor.HangDetail = inProgress => SafeDescribe(world, inProgress);
        if (services.GetService<HostLoadSampler>() is { Enabled: true } hostLoad)
        {
            monitor.HostLoad = hostLoad;
            world.HostLoad = hostLoad;
        }

        IOpsAnnouncer? announcer = services.GetService<IOpsAnnouncer>();
        _qa = services.GetService<IPlayerbotQaSink>() is { Enabled: true } qa ? qa : null;
        if (announcer is not null || _qa is not null)
        {
            // Staff only (World:Announce:Faults), one per kind per dedup window. A hang still running and the same hang completed have keys of
            // their own, so the final-duration line is not swallowed by the "is hung" line sent a moment before. Runs on the watchdog thread; a hung world thread delays only the delivery.
            // Bot QA gets the hang first and straight from the alert (never from the log line), so a failing announcer cannot hide it.
            monitor.Alerted = alert =>
            {
                ReportHangToQa(alert);
                try
                {
                    announcer?.Announce(
                        AnnounceCategory.Faults,
                        alert.Kind == TickAlertKind.Hang ? AnnounceSeverity.Critical : AnnounceSeverity.Warning,
                        alert.Text,
                        alert.Detail,
                        gmOnly: true,
                        dedupKey: alert.Kind == TickAlertKind.Hang ? (alert.InProgress ? "tick-Hang-running" : "tick-Hang-done") : "tick-" + alert.Kind);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("watchdog-alert", ex);
                }
            };
        }

        world.WorldTick += OnTick;
        logger.LogInformation("tick watchdog attached: budget {BudgetMs} ms, hang {HangMs} ms, ring {Ring} frames",
            monitor.BudgetMicros / 1000, monitor.HangMicros == long.MaxValue ? 0 : monitor.HangMicros / 1000, monitor.Ring.Capacity);
    }

    // Watchdog thread only (Describe, the alert handler and the report all run inside TickMonitor.Check): Describe leaves the hang's facts here just before the
    // monitor raises the alert, and the handler reads them. They are identity strings for bot QA's fingerprint (a phase name and a kind of work), never a duration.
    private IPlayerbotQaSink? _qa;
    private HangFacts _facts = HangFacts.Unknown;
    private string? _lastInProgress;
    private int _inProgressDistinct;
    private long _attachedMicros;
    private long _warmupMicros;
    private volatile bool _stopping;

    /// <summary>What a hang is, for bot QA: the tick phase, the kind of work in it and whether the garbage collector took the tick.</summary>
    internal readonly record struct HangFacts(string Phase, string Item, bool GcDominated)
    {
        internal static HangFacts Unknown { get; } = new("Unknown", "none", false);
    }

    /// <summary>The share of a completed tick that GC pauses must take for it to be the host's doing rather than the code's (the host sampler's own threshold).</summary>
    private const double GcShare = HostLoadSampler.GcShareThreshold;

    /// <summary>The facts of a hang still running: where the world thread is. Autosave has a fixed token (its work is the save pass, not a handler).</summary>
    internal static HangFacts RunningFacts(TickPosition position)
        => new(position.Phase.ToString(), position.Phase == TickPhase.Autosave ? "autosave" : WorkKind(position.Work), false);

    /// <summary>
    /// The facts of a hang that ended, from the slowest recent tick: its dominant phase, and the work that belongs to that phase (<c>map</c> for the maps phase, a
    /// fixed token for commands, world-tick handlers and autosave, the slowest feature only when the feature phase dominates), so the item never names code that
    /// was not the slow part. No slow tick recorded: the stall was outside the tick body.
    /// </summary>
    internal static HangFacts CompletedFacts(SlowTickInfo? worst)
    {
        if (worst is null) return new HangFacts("OutsideTick", "none", false);
        string phase = DominantPhase(worst.Phases);
        string item = phase switch
        {
            nameof(TickPhase.Maps) => "map",
            nameof(TickPhase.Autosave) => "autosave",
            nameof(TickPhase.Commands) => "commands",
            nameof(TickPhase.WorldTick) => "worldtick-handlers",
            _ => worst.TopFeatures.Count > 0 ? worst.TopFeatures[0].Name : "none",
        };
        bool gc = worst.Phases.GcCaptured && worst.Phases.Gc.PauseTicks / 10.0 >= worst.DurationMicros * GcShare;
        return new HangFacts(phase, item, gc);
    }

    private void ReportHangToQa(TickAlert alert) => ReportHang(alert, _facts);

    /// <summary>
    /// Watchdog thread, once per hang alert (a rate-limited event, never per tick): report the hang to bot QA unless it was not the code's doing. Never reported:
    /// after shutdown began, in the start-up warm-up (<see cref="PlayerbotQaOptions.TickHangWarmupMinutes"/>), when the host-load verdict blames the machine or
    /// the GC, or when GC pauses took most of the tick. A hang still running is reported once per (phase, work); the completion alert reports only the hangs that
    /// no running report stood for (several can end between two checks). Never throws: the watchdog thread keeps checking whatever the sink does.
    /// </summary>
    internal void ReportHang(TickAlert alert, in HangFacts facts)
    {
        if (_qa is not { } qa || alert.Kind != TickAlertKind.Hang) return;
        try
        {
            long occurrences = 1;
            if (alert.InProgress)
            {
                string seen = facts.Phase + "|" + facts.Item;
                if (string.Equals(_lastInProgress, seen, StringComparison.Ordinal)) return;
                _lastInProgress = seen;
                _inProgressDistinct++;
            }
            else
            {
                occurrences = alert.Hangs - _inProgressDistinct;
                _lastInProgress = null;
                _inProgressDistinct = 0;
                if (occurrences <= 0) return;
            }

            if (_stopping || alert.HostVerdict is HostLoadVerdict.HostStarved or HostLoadVerdict.GcPauses || facts.GcDominated
                || _clock.NowMicros - _attachedMicros < _warmupMicros) return;
            string detail = alert.Detail.Length <= 400 ? alert.Detail : alert.Detail[..400];
            qa.Report(new QaSignal
            {
                Kind = QaSignalKind.TickHang, AtUtc = DateTime.UtcNow, Stage = facts.Phase, Reason = facts.Item, Message = alert.Text + ": " + detail, Occurrences = occurrences,
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("watchdog-qa", ex);
            logger.LogDebug(ex, "the tick hang could not be reported to bot QA");
        }
    }

    /// <summary>The kind of work a tick position names: <c>map</c>, the declaring class of a handler, or the work's type name; <c>none</c> without work.</summary>
    private static string WorkKind(object? work) => work switch
    {
        null => "none",
        Map => "map",
        Delegate handler => TickWorkNames.Of(handler),
        _ => work.GetType().Name,
    };

    /// <summary>The phase that took the longest in a completed slow tick (the names of <see cref="TickPhase"/>).</summary>
    private static string DominantPhase(TickPhases phases)
    {
        long features = Math.Max(0, phases.FeaturesMicros - phases.WorldTickMicros);
        string name = nameof(TickPhase.Commands);
        long best = phases.CommandsMicros;
        if (phases.WorldTickMicros > best) (name, best) = (nameof(TickPhase.WorldTick), phases.WorldTickMicros);
        if (features > best) (name, best) = (nameof(TickPhase.Features), features);
        if (phases.MapsMicros > best) (name, best) = (nameof(TickPhase.Maps), phases.MapsMicros);
        if (phases.AutosaveMicros > best) name = nameof(TickPhase.Autosave);
        return name;
    }

    public Task StopAsync()
    {
        _stopping = true;
        _monitor?.StopTicking();
        return Task.CompletedTask;
    }

    /// <summary>How far back a recorded slow tick still explains a hang or overrun line.</summary>
    private static readonly TimeSpan DetailWindow = TickStats.SlowTickRetention;

    private string SafeDescribe(WorldRuntime world, bool inProgress)
    {
        try
        {
            return Describe(world, inProgress);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _facts = HangFacts.Unknown;
            FaultLedger.Shared.Record("watchdog-describe", ex);
            return "watchdog detail unavailable: " + ex.GetType().Name;
        }
    }

    /// <summary>
    /// Watchdog thread, only when a hang or overrun line is about to be logged: where the tick is (in progress), or the phase split
    /// and slowest features of the slowest recent slow tick (after the fact), then the character save queue's backlog.
    /// </summary>
    private string Describe(WorldRuntime world, bool inProgress)
    {
        _facts = HangFacts.Unknown; // a Describe that throws must not leave the previous hang's facts to be reported again
        var text = new StringBuilder();
        if (inProgress)
        {
            TickPosition position = world.CurrentPosition;
            _facts = RunningFacts(position);
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
                _facts = CompletedFacts(null);
                text.Append(CultureInfo.InvariantCulture, $"no tick body over the interval was recorded in the last {DetailWindow.TotalSeconds:F0} s (the stall was outside the tick body)");
            }
            else
            {
                SlowTickInfo worst = slow[0];
                _facts = CompletedFacts(worst);
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
    private void OnTick(uint diffMs)
    {
        try
        {
            if (_monitor is { } monitor)
            {
                monitor.OnTick(_clock.NowMicros);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("watchdog-tick", ex);
        }
    }
}
