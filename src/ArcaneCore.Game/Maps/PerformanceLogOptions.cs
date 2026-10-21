using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Maps;

/// <summary>
/// Slow-update thresholds in milliseconds, 0 disables each (the "PerformanceLog" section).
/// Names and defaults mirror vmangos mangosd.conf.dist.in:898-906 (PerformanceLog.SlowWorldUpdate
/// 100, SlowMapUpdate 100, SlowPackets 20). SlowWorldUpdate follows vmangos by default, comparing the interval
/// between two world frames (WorldRunnable.cpp:73-74, so it reports one frame late);
/// <see cref="SlowWorldUpdateMeasure"/> selects the tick duration instead.
/// </summary>
public sealed class PerformanceLogOptions
{
    public const string SectionName = "PerformanceLog";

    /// <summary>Event name carried by every slow-update entry; the name and event id 9001 let a log sink filter on them (no built-in sink writes a separate Perf.log).</summary>
    public const string EventName = "Perf";

    public static readonly EventId PerfEventId = new(9001, EventName);

    /// <summary>What SlowWorldUpdate is compared with. Defaults to the retail frame interval.</summary>
    public SlowWorldUpdateMeasure SlowWorldUpdateMeasure { get; set; } = SlowWorldUpdateMeasure.FrameInterval;

    /// <summary>Log a world frame (or tick, see SlowWorldUpdateMeasure) longer than this many ms.</summary>
    public int SlowWorldUpdate { get; set; } = 100;

    /// <summary>Log one map's update longer than this many ms.</summary>
    public int SlowMapUpdate { get; set; } = 100;

    /// <summary>Log one in-world packet handler longer than this many ms.</summary>
    public int SlowPackets { get; set; } = 20;

    /// <summary>
    /// Seconds between the slow-update summary lines. Slow world frames and slow map updates are counted over the interval and written as
    /// one summary (counts, p50/p95/max, top contributors per map, host verdicts) instead of one line each; only the updates over
    /// SlowWorldUpdateDetail / SlowMapUpdateDetail still get their own line, a few per map per interval. 0 writes every slow update as
    /// its own line (the vmangos behaviour) and ignores the three keys below.
    /// </summary>
    public int SummaryIntervalSeconds { get; set; } = 60;

    /// <summary>With a summary interval: a world frame (or tick) longer than this many ms also gets its own detail line (0: never).</summary>
    public int SlowWorldUpdateDetail { get; set; } = 250;

    /// <summary>With a summary interval: one map's update longer than this many ms also gets its own detail line (0: never).</summary>
    public int SlowMapUpdateDetail { get; set; } = 200;

    /// <summary>With a summary interval: the most detail lines written per map instance (and for the world) in one interval; the rest are counted in the summary (0: none).</summary>
    public int DetailLinesPerMapPerInterval { get; set; } = 1;
}

/// <summary>The quantity the slow world update threshold is compared with.</summary>
public enum SlowWorldUpdateMeasure
{
    /// <summary>Retail: the time between two frames, sleep included (vmangos WorldRunnable.cpp:60-74).</summary>
    FrameInterval,

    /// <summary>Deviation: only the time the tick itself took.</summary>
    TickDuration,
}
