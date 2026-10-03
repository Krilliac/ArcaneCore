using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Maps;

/// <summary>
/// Slow-update thresholds in milliseconds, 0 disables each (the "PerformanceLog" section).
/// Names and defaults mirror vmangos mangosd.conf.dist.in:898-906 (PerformanceLog.SlowWorldUpdate
/// 100, SlowMapUpdate 100, SlowPackets 20). Deliberate difference: vmangos compares the interval
/// between two world frames (WorldRunnable.cpp:73-74, so it reports one frame late); ArcaneCore
/// measures the duration of the tick itself.
/// </summary>
public sealed class PerformanceLogOptions
{
    public const string SectionName = "PerformanceLog";

    /// <summary>Event name carried by every slow-update entry; a file sink routes it to Perf.log.</summary>
    public const string EventName = "Perf";

    public static readonly EventId PerfEventId = new(9001, EventName);

    /// <summary>Log a world tick longer than this many ms.</summary>
    public int SlowWorldUpdate { get; set; } = 100;

    /// <summary>Log one map's update longer than this many ms.</summary>
    public int SlowMapUpdate { get; set; } = 100;

    /// <summary>Log one in-world packet handler longer than this many ms.</summary>
    public int SlowPackets { get; set; } = 20;
}
