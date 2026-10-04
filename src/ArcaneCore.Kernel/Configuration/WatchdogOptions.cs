namespace ArcaneCore.Kernel.Configuration;

/// <summary>
/// The <c>Ops:Watchdog</c> section: the runtime health watchdogs both daemons run on one dedicated
/// monitoring thread (docs/ops/watchdog.md). Every monitor is passive by default (it logs); the
/// only active behaviour, the memory-pressure action, is opt-in. The section is read once when
/// the daemon starts (restart-only; it is not part of <c>.reload config</c>). No vmangos
/// equivalent: vmangos has the per-frame "Slow world update" log only (WorldRunnable.cpp:60-74).
/// </summary>
public sealed class WatchdogOptions
{
    public const string SectionName = "Ops:Watchdog";

    /// <summary>The master switch. <c>false</c>: the monitoring thread is not started, nothing in this section runs, no heartbeat is sent. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the monitoring thread wakes to run every check (hang detection, heartbeat, probes). 100-60000. Default 1000.</summary>
    public int CheckIntervalMs { get; set; } = 1000;

    /// <summary>The world-tick monitor (world daemon only; the realm daemon has no tick).</summary>
    public TickMonitorOptions TickMonitor { get; } = new();

    /// <summary>The managed-heap pressure monitor.</summary>
    public MemoryMonitorOptions Memory { get; } = new();

    /// <summary>The thread-pool starvation probe.</summary>
    public ThreadPoolProbeOptions ThreadPool { get; } = new();

    /// <summary>The process-supervisor heartbeat (systemd, a liveness file or stdout).</summary>
    public HeartbeatOptions Heartbeat { get; } = new();

    /// <summary>The counters registry dump.</summary>
    public CounterDumpOptions Counters { get; } = new();
}

/// <summary>World-tick frame monitor (<c>Ops:Watchdog:TickMonitor</c>). It records the interval between consecutive tick starts, vmangos' "Slow world update" measure, in a lock-free ring.</summary>
public sealed class TickMonitorOptions
{
    /// <summary><c>false</c>: no tick is recorded, no overrun or hang is reported and the heartbeat does not depend on the tick. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Ring capacity in ticks (rounded up to a power of two, 16-1048576); the percentiles are over the last capacity-1 ticks. Default 4096 (3.4 minutes at 50 ms).</summary>
    public int RingCapacity { get; set; } = 4096;

    /// <summary>A frame longer than this is an overrun. 0 = twice the configured tick interval (100 ms at the retail 50 ms tick), tolerating sleep jitter. Default 0.</summary>
    public int BudgetMs { get; set; }

    /// <summary>A frame longer than this is a hang: one critical line when it is still running and one when it completes. 0 disables hang detection. Default 2000.</summary>
    public int HangMs { get; set; } = 2000;

    /// <summary>Least seconds between two overrun warnings; overruns in between are counted and reported with the next warning. Default 30.</summary>
    public int WarnIntervalSeconds { get; set; } = 30;

    /// <summary>Seconds between periodic frame-time summaries (p50/p99/max, overruns) at Information level. 0 disables the summary. Default 300.</summary>
    public int SummaryIntervalSeconds { get; set; } = 300;

    /// <summary><c>true</c>: the heartbeat is withheld while a tick has run longer than <see cref="HangMs"/>, so a supervisor watchdog restarts a hung world. Default true.</summary>
    public bool GatesHeartbeat { get; set; } = true;
}

/// <summary>Managed-heap pressure monitor (<c>Ops:Watchdog:Memory</c>), sampled with <c>GC.GetGCMemoryInfo</c> on the monitoring thread.</summary>
public sealed class MemoryMonitorOptions
{
    /// <summary><c>false</c>: no sample is taken and no action can fire. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Seconds between heap samples. 1-3600. Default 10.</summary>
    public int SampleIntervalSeconds { get; set; } = 10;

    /// <summary>An Information line when the gen2 or large-object-heap high-water mark grows by at least this many bytes since the last line. 0 disables growth lines. Default 16777216 (16 MiB).</summary>
    public long GrowthLogBytes { get; set; } = 16L * 1024 * 1024;

    /// <summary>A rate-limited warning when the managed heap is at least this many bytes. 0 disables the check. Default 0.</summary>
    public long WarnHeapBytes { get; set; }

    /// <summary>A rate-limited warning when the runtime's memory load (process and machine) reaches this percentage of the available memory; the GC's own high-load threshold is 90. 0 disables the check. Default 85.</summary>
    public int WarnLoadPercent { get; set; } = 85;

    /// <summary>What happens when the managed heap reaches <see cref="ActionHeapBytes"/>. Default None (log only; <c>Collect</c> pauses the world thread for a blocking compacting gen2 collection, <c>Stop</c> ends the process with exit code 1).</summary>
    public MemoryPressureAction Action { get; set; } = MemoryPressureAction.None;

    /// <summary>The heap size in bytes at which <see cref="Action"/> fires. 0 disables the action whatever <see cref="Action"/> says. Default 0.</summary>
    public long ActionHeapBytes { get; set; }

    /// <summary>Least seconds between two actions (a Stop fires once). Default 300.</summary>
    public int ActionCooldownSeconds { get; set; } = 300;

    /// <summary><c>true</c>: register for full-GC approach/completion notifications where the runtime allows it (concurrent GC refuses; then this logs once and only polling runs). Default false.</summary>
    public bool FullGcNotifications { get; set; }

    /// <summary>Least seconds between two memory warnings of the same kind. Default 60.</summary>
    public int WarnIntervalSeconds { get; set; } = 60;
}

/// <summary>What the memory monitor does when the heap reaches <c>Ops:Watchdog:Memory:ActionHeapBytes</c>.</summary>
public enum MemoryPressureAction
{
    /// <summary>Nothing beyond the warning line.</summary>
    None = 0,

    /// <summary>A Critical log line with the full sample.</summary>
    Log = 1,

    /// <summary>A blocking, compacting gen2 collection with the large object heap compacted once (pauses every managed thread, the world thread included).</summary>
    Collect = 2,

    /// <summary>Stop the host with exit code 1 (a supervisor that restarts on 1 brings the realm back with a fresh heap).</summary>
    Stop = 3,
}

/// <summary>Thread-pool starvation probe (<c>Ops:Watchdog:ThreadPool</c>): a work item is queued and the time until it runs is measured.</summary>
public sealed class ThreadPoolProbeOptions
{
    /// <summary><c>false</c>: nothing is queued. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Seconds between probes. 1-3600. Default 5.</summary>
    public int ProbeIntervalSeconds { get; set; } = 5;

    /// <summary>A probe that waits longer than this (milliseconds) for a pool thread is a rate-limited warning with the pool counts. Default 200.</summary>
    public int WarnDelayMs { get; set; } = 200;

    /// <summary>A probe that waits longer than this (milliseconds), or has not run at all for this long, is a critical line. Default 2000.</summary>
    public int CriticalDelayMs { get; set; } = 2000;

    /// <summary>Least seconds between two starvation warnings. Default 30.</summary>
    public int WarnIntervalSeconds { get; set; } = 30;
}

/// <summary>Where the heartbeat goes (<c>Ops:Watchdog:Heartbeat:Mode</c>).</summary>
public enum HeartbeatMode
{
    /// <summary>systemd when <c>NOTIFY_SOCKET</c> is set in the environment, otherwise no heartbeat.</summary>
    Auto = 0,

    /// <summary>No heartbeat, whatever the environment says.</summary>
    None = 1,

    /// <summary><c>sd_notify</c> over <c>NOTIFY_SOCKET</c> (READY=1 at start, WATCHDOG=1 periodically, STOPPING=1 at stop); logs once and sends nothing when the socket is missing or the platform has no unix sockets.</summary>
    Systemd = 2,

    /// <summary>Rewrite <c>Ops:Watchdog:Heartbeat:FilePath</c> with a UTC timestamp and the process id on every beat.</summary>
    File = 3,

    /// <summary>One <c>heartbeat</c> line on standard output per beat.</summary>
    Stdout = 4,
}

/// <summary>Process-supervisor heartbeat (<c>Ops:Watchdog:Heartbeat</c>). A beat is withheld (fail closed) while any liveness source reports the process unhealthy.</summary>
public sealed class HeartbeatOptions
{
    /// <summary>Where the heartbeat goes. Default Auto.</summary>
    public HeartbeatMode Mode { get; set; } = HeartbeatMode.Auto;

    /// <summary>Seconds between beats. 0 = half of systemd's <c>WATCHDOG_USEC</c> when it is set, otherwise 10. Default 0.</summary>
    public int IntervalSeconds { get; set; }

    /// <summary>The liveness file for <c>File</c> mode; relative paths are under the content root. Required in that mode (the daemon refuses to start without it).</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Least seconds between two "heartbeat withheld" warnings. Default 30.</summary>
    public int WarnIntervalSeconds { get; set; } = 30;
}

/// <summary>Counter dump (<c>Ops:Watchdog:Counters</c>): every registered counter in one Information line.</summary>
public sealed class CounterDumpOptions
{
    /// <summary>Seconds between dumps. 0 disables the dump (the registry still counts). Default 300.</summary>
    public int DumpIntervalSeconds { get; set; } = 300;

    /// <summary><c>true</c>: counters that have not changed since the last dump are left out of it. Default false.</summary>
    public bool ChangedOnly { get; set; }
}
