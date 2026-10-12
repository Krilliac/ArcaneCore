using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using ArcaneCore.Kernel.Configuration;

namespace ArcaneCore.Kernel.Ops.Watchdog;

/// <summary>
/// One cumulative reading of the host: CPU time of the process and (where the platform offers it) of the whole machine, the garbage
/// collector's counts and total pause, and the thread pool. Times are in 100 ns ticks. Differences between two readings describe
/// the window between them.
/// </summary>
public readonly record struct HostLoadReading(
    long ProcessCpuTicks,
    bool MachineKnown,
    long MachineBusyTicks,
    long MachineTotalTicks,
    int Gen0,
    int Gen1,
    int Gen2,
    long GcPauseTicks,
    int PoolThreads,
    long PoolPending,
    int Cpus);

/// <summary>Where <see cref="HostLoadSampler"/> reads the host (tests substitute their own).</summary>
public interface IHostLoadProbe
{
    /// <summary>One cumulative reading. Called on the watchdog thread each check and on whichever thread writes a slow-tick line; thread-safe, no unbounded work.</summary>
    HostLoadReading Read();
}

/// <summary>The runtime probe: <see cref="Environment.CpuUsage"/>, <c>GetSystemTimes</c> on Windows, <c>/proc/stat</c> on Linux, <see cref="GC"/> and <see cref="ThreadPool"/>.</summary>
public sealed class SystemHostLoadProbe : IHostLoadProbe
{
    // Linux USER_HZ is 100 on every platform .NET runs on; one jiffy is 10 ms = 100,000 ticks of 100 ns.
    private const long TicksPerJiffy = 100_000;

    public HostLoadReading Read()
    {
        bool machineKnown = false;
        long busy = 0;
        long total = 0;
        if (OperatingSystem.IsWindows())
        {
            machineKnown = TryReadWindows(out busy, out total);
        }
        else if (OperatingSystem.IsLinux())
        {
            machineKnown = TryReadLinux(out busy, out total);
        }

        return new HostLoadReading(
            Environment.CpuUsage.TotalTime.Ticks,
            machineKnown,
            busy,
            total,
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            GC.GetTotalPauseDuration().Ticks,
            ThreadPool.ThreadCount,
            ThreadPool.PendingWorkItemCount,
            Environment.ProcessorCount);
    }

    private static bool TryReadWindows(out long busy, out long total)
    {
        busy = total = 0;
        try
        {
            if (!GetSystemTimes(out ulong idle, out ulong kernel, out ulong user))
            {
                return false;
            }

            // Kernel time includes the idle time; FILETIME units are 100 ns, the unit of this reading.
            total = (long)(kernel + user);
            busy = total - (long)idle;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false; // a Windows without kernel32 exports (not a real host): the report says "machine cpu unknown"
        }
    }

    private static bool TryReadLinux(out long busy, out long total)
    {
        busy = total = 0;
        try
        {
            using var reader = new StreamReader("/proc/stat");
            string? line = reader.ReadLine();
            if (line is null || !line.StartsWith("cpu ", StringComparison.Ordinal))
            {
                return false;
            }

            // cpu user nice system idle iowait irq softirq steal ...
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5)
            {
                return false;
            }

            long sum = 0;
            long idle = 0;
            for (int i = 1; i < parts.Length && i <= 8; i++)
            {
                if (!long.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out long value))
                {
                    return false;
                }

                sum += value;
                if (i is 4 or 5)
                {
                    idle += value;
                }
            }

            total = sum * TicksPerJiffy;
            busy = (sum - idle) * TicksPerJiffy;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // no /proc (a container without it): the report says "machine cpu unknown"
        }
    }

#pragma warning disable SYSLIB1054 // LibraryImport needs AllowUnsafeBlocks; this call runs only on Windows behind OperatingSystem.IsWindows
    [DllImport("kernel32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
#pragma warning restore SYSLIB1054
}

/// <summary>What the host did over a window, ready to print.</summary>
public readonly record struct HostLoadReport(
    long WindowMicros,
    double ProcessCpuPercent,
    bool MachineKnown,
    double MachineCpuPercent,
    int Gen0,
    int Gen1,
    int Gen2,
    long GcPauseMicros,
    int PoolThreads,
    long PoolPending,
    int Cpus,
    HostLoadVerdict Verdict)
{
    /// <summary>CPU the other processes used, as a percentage of the whole machine; 0 when the machine reading is unknown.</summary>
    public double OtherCpuPercent => MachineKnown ? Math.Max(0, MachineCpuPercent - ProcessCpuPercent) : 0;

    /// <summary>One line: <c>host: process cpu 8% / machine cpu 100% (other processes 92%) over 3.1 s on 24 cpus; gc +3/+1/+0 pause 12 ms; threadpool 24 threads, 0 pending; verdict host-starved</c>.</summary>
    public string Format()
    {
        var sb = new StringBuilder(192);
        sb.Append("host: process cpu ").Append(Percent(ProcessCpuPercent));
        if (MachineKnown)
        {
            sb.Append(" / machine cpu ").Append(Percent(MachineCpuPercent)).Append(" (other processes ").Append(Percent(OtherCpuPercent)).Append(')');
        }
        else
        {
            sb.Append(" / machine cpu unknown");
        }

        sb.Append(" over ").Append((WindowMicros / 1_000_000.0).ToString("F1", CultureInfo.InvariantCulture)).Append(" s on ").Append(Cpus).Append(" cpus");
        sb.Append("; gc +").Append(Gen0).Append("/+").Append(Gen1).Append("/+").Append(Gen2).Append(" (gen0/1/2) pause ").Append(GcPauseMicros / 1000).Append(" ms");
        sb.Append("; threadpool ").Append(PoolThreads).Append(" threads, ").Append(PoolPending).Append(" pending");
        sb.Append("; verdict ").Append(Verdict switch
        {
            HostLoadVerdict.HostStarved => "host-starved (other processes are using the machine)",
            HostLoadVerdict.GcPauses => "gc-pauses",
            HostLoadVerdict.ProcessBusy => "process-busy (this process is using the cpu)",
            HostLoadVerdict.Quiet => "host-quiet (the time went in the code, not the machine)",
            _ => "unknown",
        });
        return sb.ToString();

        static string Percent(double value) => value.ToString("F0", CultureInfo.InvariantCulture) + "%";
    }
}

/// <summary>The sampler's reading of why a window was slow.</summary>
public enum HostLoadVerdict
{
    /// <summary>The window is too short or the readings are missing.</summary>
    Unknown = 0,

    /// <summary>The machine was busy with other processes while this one used little of it: the slowness is the host's.</summary>
    HostStarved = 1,

    /// <summary>Garbage-collection pauses covered a large part of the window.</summary>
    GcPauses = 2,

    /// <summary>This process used a large share of the machine.</summary>
    ProcessBusy = 3,

    /// <summary>Neither the machine nor the collector was under pressure: the code was slow by itself.</summary>
    Quiet = 4,
}

/// <summary>
/// Keeps a ring of <see cref="HostLoadReading"/> (one per watchdog check) and answers <see cref="Describe"/>: what the host did over
/// the last N microseconds, by differencing a fresh reading against the newest stored one that is old enough. It is what lets a
/// slow-tick line say "host starved" (a machine at 100% with this process using little of it), "gc" (pauses covered the window) or
/// "own code" (neither). Writes come from the watchdog thread; <see cref="Describe"/> may be called from any thread (the world
/// thread writes the slow-map line), takes a short lock to copy one stored reading and reads the host outside it.
/// </summary>
public sealed class HostLoadSampler : IWatchdogMonitor
{
    /// <summary>A pause share of the window at or above this is a GC verdict.</summary>
    public const double GcShareThreshold = 0.30;

    /// <summary>Machine CPU at or above this, with other processes at or above <see cref="OtherShareThreshold"/>, is a starved verdict.</summary>
    public const double MachineBusyThreshold = 85;

    /// <summary>Other processes' share of the machine for the starved verdict.</summary>
    public const double OtherShareThreshold = 40;

    /// <summary>This process's share of the machine for the busy verdict.</summary>
    public const double ProcessShareThreshold = 50;

    private readonly HostLoadOptions _options;
    private readonly WatchdogClock _clock;
    private readonly IHostLoadProbe _probe;
    private readonly object _gate = new();
    private readonly (long Micros, HostLoadReading Reading)[] _ring;
    private int _next;
    private int _count;

    public HostLoadSampler(WatchdogOptions options, WatchdogClock clock, IHostLoadProbe? probe = null)
        : this(options.HostLoad, options.CheckIntervalMs, clock, probe)
    {
    }

    public HostLoadSampler(HostLoadOptions options, int checkIntervalMs, WatchdogClock clock, IHostLoadProbe? probe = null)
    {
        _options = options;
        _clock = clock;
        _probe = probe ?? new SystemHostLoadProbe();
        long perSecond = Math.Max(1, 1000 / Math.Max(100, checkIntervalMs));
        _ring = new (long, HostLoadReading)[(int)Math.Clamp(Math.Clamp(options.HistorySeconds, 10, 3600) * perSecond + 2, 4, 40_000)];
    }

    public string Name => "hostload";

    public bool Enabled => _options.Enabled;

    /// <summary>Readings currently stored.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>Watchdog thread, every check: store one reading.</summary>
    public void Check(long nowMicros)
    {
        if (!_options.Enabled)
        {
            return;
        }

        HostLoadReading reading = _probe.Read();
        lock (_gate)
        {
            _ring[_next] = (nowMicros, reading);
            _next = (_next + 1) % _ring.Length;
            if (_count < _ring.Length)
            {
                _count++;
            }
        }
    }

    /// <summary>
    /// What the host did over the last <paramref name="windowMicros"/> (measured from the newest stored reading at least that old, or the
    /// oldest one stored), or null when disabled or nothing is stored yet.
    /// </summary>
    public HostLoadReport? Report(long windowMicros)
    {
        if (!_options.Enabled)
        {
            return null;
        }

        long now = _clock.NowMicros;
        long wanted = now - Math.Max(0, windowMicros);
        long baseMicros = 0;
        HostLoadReading baseline = default;
        bool found = false;
        lock (_gate)
        {
            for (int i = 0; i < _count; i++)
            {
                // Newest first: _next - 1 is the latest write.
                int index = ((_next - 1 - i) % _ring.Length + _ring.Length) % _ring.Length;
                (long micros, HostLoadReading reading) = _ring[index];
                baseMicros = micros;
                baseline = reading;
                found = true;
                if (micros <= wanted)
                {
                    break;
                }
            }
        }

        if (!found)
        {
            return null;
        }

        return Compute(baseMicros, baseline, now, _probe.Read());
    }

    /// <summary>The report as text (empty when disabled), for appending to a log line.</summary>
    public string Describe(long windowMicros)
    {
        HostLoadReport? report = Report(windowMicros);
        return report is { } r ? r.Format() : _options.Enabled ? "host: no baseline sample yet" : string.Empty;
    }

    /// <summary>The differencing and the verdict, separate from the clock so it is testable on fixed readings.</summary>
    public static HostLoadReport Compute(long startMicros, in HostLoadReading start, long endMicros, in HostLoadReading end)
    {
        long window = Math.Max(1, endMicros - startMicros);
        int cpus = Math.Max(1, end.Cpus);
        double windowTicks = window * 10.0; // microseconds to 100 ns ticks
        double process = Math.Clamp((end.ProcessCpuTicks - start.ProcessCpuTicks) / (windowTicks * cpus) * 100.0, 0, 100);
        bool machineKnown = start.MachineKnown && end.MachineKnown && end.MachineTotalTicks > start.MachineTotalTicks;
        double machine = 0;
        if (machineKnown)
        {
            machine = Math.Clamp((double)(end.MachineBusyTicks - start.MachineBusyTicks) / (end.MachineTotalTicks - start.MachineTotalTicks) * 100.0, 0, 100);
        }

        long pauseMicros = Math.Max(0, (end.GcPauseTicks - start.GcPauseTicks) / 10);
        HostLoadVerdict verdict;
        if (window < 100_000)
        {
            verdict = HostLoadVerdict.Unknown;
        }
        else if (pauseMicros >= window * GcShareThreshold)
        {
            verdict = HostLoadVerdict.GcPauses;
        }
        else if (machineKnown && machine >= MachineBusyThreshold && machine - process >= OtherShareThreshold)
        {
            verdict = HostLoadVerdict.HostStarved;
        }
        else if (process >= ProcessShareThreshold)
        {
            verdict = HostLoadVerdict.ProcessBusy;
        }
        else
        {
            verdict = machineKnown ? HostLoadVerdict.Quiet : HostLoadVerdict.Unknown;
        }

        return new HostLoadReport(
            window, process, machineKnown, machine,
            end.Gen0 - start.Gen0, end.Gen1 - start.Gen1, end.Gen2 - start.Gen2,
            pauseMicros, end.PoolThreads, end.PoolPending, cpus, verdict);
    }
}
