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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Ops.Watchdog;

/// <summary>
/// Feeds the Kernel <see cref="TickMonitor"/> from the world thread (docs/ops/watchdog.md). It is
/// the one consumer of <see cref="WorldRuntime.TickStarting"/> here: the handler
/// runs as the first step of every tick and does nothing but <see cref="TickMonitor.OnTick(long)"/>, a
/// clock read and a few stores (plus one small record when the frame it closes was a hang). The body-duration percentiles of <see cref="WorldRuntime.Stats"/>
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
                FaultLedger.Shared.Record("watchdog.body-stats", ex);
                return default;
            }
        };
        monitor.AttachLatency(world.Stats.FrameLatency, world.Stats.BodyLatency, world.Stats.PhaseLatencies);
        monitor.HangDetail = inProgress => SafeDescribe(world, inProgress);
        monitor.CompletedHangDetail = SafeDescribeCompleted;
        monitor.HangClosed = frameMicros => RecordHungFrame(world, frameMicros);
        if (monitor.StackCapture is { } stackCapture)
        {
            stackCapture.Captured = ReportHangStackToQa;
        }

        if (services.GetService<HostLoadSampler>() is { Enabled: true } hostLoad)
        {
            monitor.HostLoad = hostLoad;
            world.HostLoad = hostLoad;
        }

        IOpsAnnouncer? announcer = services.GetService<IOpsAnnouncer>();
        // Bot QA's hang sink is read from the registry at report time: the bot QA feature registers it while it is attached (and
        // the sink is on), so a detach or a pack swap of bot QA needs nothing from the watchdog.
        _bots = services.GetService<ManagedBotRegistry>();
        if (announcer is not null || _bots is not null)
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
                    FaultLedger.Shared.Record("watchdog.alert", ex);
                }
            };
        }

        world.TickStarting += OnTickStarting;
        logger.LogInformation("tick watchdog attached: budget {BudgetMs} ms, hang {HangMs} ms, ring {Ring} frames",
            monitor.BudgetMicros / 1000, monitor.HangMicros == long.MaxValue ? 0 : monitor.HangMicros / 1000, monitor.Ring.Capacity);
    }

    // Watchdog thread only (Describe, the alert handler and the report all run inside TickMonitor.Check): Describe leaves the hang's facts here just before the
    // monitor raises the alert, and the handler reads them. They are identity strings for bot QA's fingerprint (a phase name and a kind of work), never a duration.
    private ManagedBotRegistry? _bots;
    private HangFacts _facts = HangFacts.Unknown;

    /// <summary>The identity the last completed-hang description left for bot QA (watchdog thread, so tests read it from the thread that called Check).</summary>
    internal HangFacts LastHangFacts => _facts;
    private string? _lastInProgress;
    private int _inProgressDistinct;
    private long _attachedMicros;
    private volatile bool _stopping;

    /// <summary>What a hang is, for bot QA: the tick phase, the kind of work in it and whether the garbage collector took the tick.</summary>
    internal readonly record struct HangFacts(string Phase, string Item, bool GcDominated)
    {
        internal static HangFacts Unknown { get; } = new("Unknown", "none", false);

        /// <summary>No description was made (an earlier report already consumed the hung frame): nothing is sent to bot QA.</summary>
        internal static HangFacts NotDescribed { get; } = new(string.Empty, string.Empty, false);
    }

    /// <summary>The share of a completed tick that GC pauses must take for it to be the host's doing rather than the code's (the host sampler's own threshold).</summary>
    private const double GcShare = HostLoadSampler.GcShareThreshold;

    /// <summary>The facts of a hang still running: where the world thread is. Autosave has a fixed token (its work is the save pass, not a handler).</summary>
    internal static HangFacts RunningFacts(TickPosition position)
        => new(position.Phase.ToString(), position.Phase == TickPhase.Autosave ? "autosave" : WorkKind(position.Work), false);

    /// <summary>
    /// The facts of a hang that ended, from the tick body that ended the hung frame: its dominant phase, and the work that belongs to that phase (<c>map</c> for the maps phase, a
    /// fixed token for commands, world-tick handlers and autosave, the slowest feature only when the feature phase dominates), so the item never names code that
    /// was not the slow part. No body: the stall was outside the tick body.
    /// </summary>
    internal static HangFacts CompletedFacts(SlowTickInfo? worst)
        => worst is null ? new HangFacts("OutsideTick", "none", false) : CompletedFacts(worst.Phases, worst.TopFeatures, worst.DurationMicros);

    /// <summary>The facts of a tick body with this phase split, slowest features and length.</summary>
    internal static HangFacts CompletedFacts(TickPhases phases, IReadOnlyList<(string Name, long Micros)> topFeatures, long bodyMicros)
    {
        string phase = DominantPhase(phases);
        string item = phase switch
        {
            nameof(TickPhase.Maps) => "map",
            nameof(TickPhase.Autosave) => "autosave",
            nameof(TickPhase.Commands) => "commands",
            nameof(TickPhase.WorldTick) => "worldtick-handlers",
            _ => topFeatures.Count > 0 ? topFeatures[0].Name : "none",
        };
        bool gc = phases.GcCaptured && phases.Gc.PauseTicks / 10.0 >= bodyMicros * GcShare;
        return new HangFacts(phase, item, gc);
    }

    private void ReportHangToQa(TickAlert alert)
    {
        if (_facts.Phase.Length == 0) return; // the hung frame was described in an earlier report: do not invent a cause
        ReportHang(alert, _facts);
    }

    /// <summary>
    /// Watchdog thread, once per hang alert (a rate-limited event, never per tick): report the hang to bot QA unless it was not the code's doing. Never reported:
    /// after shutdown began, in the start-up warm-up (<see cref="IBotTickHangSink.Warmup"/>, bot QA's <c>TickHangWarmupMinutes</c>), when the host-load verdict blames the machine or
    /// the GC, or when GC pauses took most of the tick. A hang still running is reported once per (phase, work); the completion alert reports only the hangs that
    /// no running report stood for (several can end between two checks). Never throws: the watchdog thread keeps checking whatever the sink does.
    /// </summary>
    internal void ReportHang(TickAlert alert, in HangFacts facts)
    {
        if (_bots?.HangSink is not { } qa || alert.Kind != TickAlertKind.Hang) return;
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

            if (_stopping || alert.HostVerdict is HostLoadVerdict.HostStarved or HostLoadVerdict.GcPauses or HostLoadVerdict.GcPaging or HostLoadVerdict.ProcessStalled || facts.GcDominated
                || _clock.NowMicros - _attachedMicros < (long)qa.Warmup.TotalMicroseconds) return;
            string detail = alert.Detail.Length <= 400 ? alert.Detail : alert.Detail[..400];
            qa.ReportHang(facts.Phase, facts.Item, alert.Text + ": " + detail, occurrences);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("watchdog.qa", ex);
            logger.LogDebug(ex, "the tick hang could not be reported to bot QA");
        }
    }

    /// <summary>
    /// Thread-pool thread, after a hang stack capture finished: hand the world thread's top frames to bot QA (a world-hang fault, fingerprinted by those frames). Same gates as
    /// <see cref="ReportHang"/> that can be known here (shutdown, the start-up warm-up); the host-load verdict is not, so a hang the machine caused still files under the
    /// frames it was caught in. Never throws.
    /// </summary>
    private void ReportHangStackToQa(HangStackReport report)
    {
        if (_bots?.HangSink is not { } qa || report.WorldThreadFrames.Count == 0) return;
        try
        {
            if (_stopping || _clock.NowMicros - _attachedMicros < (long)qa.Warmup.TotalMicroseconds) return;
            qa.ReportHangStack(report.RunningMs, report.WorldThreadFrames);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("watchdog.qa-stack", ex);
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
            FaultLedger.Shared.Record("watchdog.describe", ex);
            return "watchdog detail unavailable: " + ex.GetType().Name;
        }
    }

    /// <summary>
    /// A hung frame, snapshotted on the world thread the moment the next tick starts (while the closing tick's phases, duration and slowest features are complete):
    /// <paramref name="FrameMicros"/> the frame's length, <paramref name="BodyMicros"/> the tick body that ended it.
    /// </summary>
    internal sealed record HungFrame(long FrameMicros, long BodyMicros, TickPhases Phases, IReadOnlyList<(string Name, long Micros)> TopFeatures);

    private readonly object _hungGate = new();
    private HungFrame? _worstHung;
    private int _hungCount;

    /// <summary>
    /// World thread, inside <see cref="TickMonitor.OnTick(long)"/> before the hang is counted: snapshot the closing tick for the watchdog thread. Allocates one small record
    /// (and two five-entry buffers) per hang, an event that costs seconds anyway; keeps the longest frame since the last report.
    /// </summary>
    private void RecordHungFrame(WorldRuntime world, long frameMicros)
    {
        var names = new string?[TickStats.TopFeatureCount];
        var micros = new long[TickStats.TopFeatureCount];
        int count = world.CopyLastTickTopFeatures(names, micros);
        var top = new (string Name, long Micros)[count];
        for (int i = 0; i < count; i++)
        {
            top[i] = (names[i] ?? "?", micros[i]);
        }

        var frame = new HungFrame(frameMicros, world.LastTickDurationMicros, world.LastTickPhases, top);
        lock (_hungGate)
        {
            _hungCount++;
            if (_worstHung is null || frameMicros >= _worstHung.FrameMicros)
            {
                _worstHung = frame;
            }
        }
    }

    /// <summary>Watchdog thread, once per completed-hang line: the worst hung frame recorded since the last report (and how many there were), then forget them.</summary>
    private CompletedHangText SafeDescribeCompleted()
    {
        try
        {
            HungFrame? frame;
            int count;
            lock (_hungGate)
            {
                frame = _worstHung;
                count = _hungCount;
                _worstHung = null;
                _hungCount = 0;
            }

            if (frame is null)
            {
                // The record and its count are taken together under the lock, so none left means an earlier report already described these hangs.
                _facts = HangFacts.NotDescribed;
                return new CompletedHangText("hung frame described in the previous report", 0);
            }

            return new CompletedHangText(AppendSaveQueue(FormatHungFrame(frame, count, out _facts)), count);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _facts = HangFacts.Unknown;
            FaultLedger.Shared.Record("watchdog.describe", ex);
            return new CompletedHangText("watchdog detail unavailable: " + ex.GetType().Name, 0);
        }
    }

    /// <summary>
    /// The completed-hang detail from the hung frame's own tick: how long the frame was, how much of it the tick body (commands to autosave) filled and the rest as time
    /// outside tick code (a wait between ticks, a GC pause or paging that hit the idle wait). <paramref name="facts"/> are the hang's identity for bot QA: <c>OutsideTick</c> when
    /// the time outside the body dominates, else the body's dominant phase and work.
    /// </summary>
    internal static string FormatHungFrame(HungFrame frame, int count, out HangFacts facts)
    {
        long outside = Math.Max(0, frame.FrameMicros - frame.BodyMicros);
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"hung frame {frame.FrameMicros / 1000} ms: tick body {frame.BodyMicros / 1000} ms: {frame.Phases.Format()}");
        if (frame.TopFeatures.Count > 0)
        {
            text.Append("; top features: ");
            for (int i = 0; i < frame.TopFeatures.Count; i++)
            {
                text.Append(CultureInfo.InvariantCulture, $"{(i > 0 ? ", " : string.Empty)}{frame.TopFeatures[i].Name} {frame.TopFeatures[i].Micros / 1000.0:F1} ms");
            }
        }

        text.Append(CultureInfo.InvariantCulture, $"; {outside / 1000} ms outside tick code");
        if (count > 1)
        {
            text.Append(CultureInfo.InvariantCulture, $"; {count} hung frames since the last report, worst shown");
        }

        facts = outside > frame.BodyMicros
            ? new HangFacts("OutsideTick", "none", false)
            : CompletedFacts(frame.Phases, frame.TopFeatures, frame.BodyMicros);
        return text.ToString();
    }

    private string AppendSaveQueue(string text)
    {
        if (services.GetService<CharacterSaveQueue>() is { } saves)
        {
            return text + string.Create(CultureInfo.InvariantCulture, $"; save queue depth {saves.Pending}, oldest pending {saves.OldestPendingAge.TotalMilliseconds:F0} ms");
        }

        return text;
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

    // World thread, once per tick, before the tick's commands: no allocation, no lock, no log (a hung frame is recorded by TickMonitor.HangClosed).
    private void OnTickStarting(uint diffMs)
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
            FaultLedger.Shared.Record("watchdog.tick", ex);
        }
    }
}
