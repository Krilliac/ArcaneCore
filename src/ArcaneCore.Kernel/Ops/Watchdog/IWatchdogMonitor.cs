using Microsoft.Extensions.Logging;

namespace ArcaneCore.Kernel.Ops.Watchdog;

/// <summary>
/// One health check the watchdog thread runs every <c>Ops:Watchdog:CheckIntervalMs</c>. All
/// three methods are called on the watchdog thread only (never on the world thread or the
/// thread pool); a monitor that is fed from another thread (the tick monitor) publishes with
/// volatile or interlocked stores and reads them here. An exception from <see cref="Check"/> is
/// logged and does not stop the other monitors or the thread.
/// </summary>
public interface IWatchdogMonitor
{
    /// <summary>Short name for log lines ("tick", "memory", ...).</summary>
    string Name { get; }

    /// <summary>Called once before the first check, after the host started the monitoring thread.</summary>
    void Start()
    {
    }

    /// <summary>One check at <paramref name="nowMicros"/> (the watchdog clock).</summary>
    void Check(long nowMicros);

    /// <summary>Called once when the host stops, after the last check.</summary>
    void Stop()
    {
    }
}

/// <summary>
/// Something whose health decides whether the heartbeat is sent. All sources must report alive
/// for a beat to go out (fail closed: an unknown or hung state withholds the beat so the
/// supervisor restarts the process).
/// </summary>
public interface ILivenessSource
{
    string Name { get; }

    /// <summary>True when healthy. <paramref name="reason"/> is set only when not alive (that path is cold and may allocate).</summary>
    bool IsAlive(long nowMicros, out string? reason);
}

/// <summary>Event ids of every watchdog log line (7100-7199), so a sink can route them to their own file.</summary>
public static class WatchdogEvents
{
    public static readonly EventId Started = new(7100, "WatchdogStarted");
    public static readonly EventId TickOverrun = new(7101, "TickOverrun");
    public static readonly EventId TickHang = new(7102, "TickHang");
    public static readonly EventId TickSummary = new(7103, "TickSummary");
    public static readonly EventId MemoryGrowth = new(7110, "MemoryGrowth");
    public static readonly EventId MemoryPressure = new(7111, "MemoryPressure");
    public static readonly EventId MemoryAction = new(7112, "MemoryAction");
    public static readonly EventId FullGc = new(7113, "FullGc");
    public static readonly EventId ThreadPoolStarvation = new(7120, "ThreadPoolStarvation");
    public static readonly EventId HeartbeatWithheld = new(7130, "HeartbeatWithheld");
    public static readonly EventId HeartbeatSink = new(7131, "HeartbeatSink");
    public static readonly EventId CounterDump = new(7140, "CounterDump");
    public static readonly EventId MonitorFault = new(7190, "WatchdogMonitorFault");
}
