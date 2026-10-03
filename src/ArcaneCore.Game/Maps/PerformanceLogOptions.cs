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

    /// <summary>Event name carried by every slow-update entry; a file sink routes it to Perf.log.</summary>
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
}

/// <summary>The quantity the slow world update threshold is compared with.</summary>
public enum SlowWorldUpdateMeasure
{
    /// <summary>Retail: the time between two frames, sleep included (vmangos WorldRunnable.cpp:60-74).</summary>
    FrameInterval,

    /// <summary>Deviation: only the time the tick itself took.</summary>
    TickDuration,
}
