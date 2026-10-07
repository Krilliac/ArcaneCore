using System.Runtime;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops.Watchdog.Counters;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Kernel.Ops.Watchdog;

/// <summary>One reading of the managed heap. Byte fields are 0 when the runtime does not report them.</summary>
public readonly record struct MemorySample(
    long HeapBytes,
    long Gen2Bytes,
    long LohBytes,
    long PohBytes,
    long FragmentedBytes,
    long MemoryLoadBytes,
    long TotalAvailableBytes,
    long HighMemoryLoadThresholdBytes,
    int Gen2Collections,
    double PauseTimePercent,
    long WorkingSetBytes)
{
    /// <summary>Memory load as a percentage of the available memory (0 when unknown).</summary>
    public int LoadPercent => TotalAvailableBytes <= 0 ? 0 : (int)(MemoryLoadBytes * 100 / TotalAvailableBytes);
}

/// <summary>Where the memory monitor reads from; a seam so tests feed synthetic samples.</summary>
public interface IMemoryProbe
{
    MemorySample Read();
}

/// <summary>The real heap (<see cref="GC.GetGCMemoryInfo()"/>).</summary>
public sealed class GcMemoryProbe : IMemoryProbe
{
    public MemorySample Read()
    {
        GCMemoryInfo info = GC.GetGCMemoryInfo();
        ReadOnlySpan<GCGenerationInfo> generations = info.GenerationInfo;
        // Before the first collection the info describes no GC (Index 0) and every size is 0; the live total is the honest heap figure then.
        return new MemorySample(
            info.Index == 0 ? GC.GetTotalMemory(false) : info.HeapSizeBytes,
            generations.Length > 2 ? generations[2].SizeAfterBytes : 0,
            generations.Length > 3 ? generations[3].SizeAfterBytes : 0,
            generations.Length > 4 ? generations[4].SizeAfterBytes : 0,
            info.FragmentedBytes,
            info.MemoryLoadBytes,
            info.TotalAvailableMemoryBytes,
            info.HighMemoryLoadThresholdBytes,
            GC.CollectionCount(2),
            info.PauseTimePercentage,
            Environment.WorkingSet);
    }
}

/// <summary>What the memory monitor may do to the process; a seam so tests see the decision without a real collection or stop.</summary>
public interface IMemoryPressureActuator
{
    /// <summary>A blocking, compacting gen2 collection with the large object heap compacted once.</summary>
    void Collect();

    /// <summary>Stop the host with <see cref="ExitCodes.Failure"/>.</summary>
    void Stop();
}

/// <summary>The real runtime.</summary>
public sealed class RuntimeMemoryPressureActuator(IHostApplicationLifetime? lifetime = null) : IMemoryPressureActuator
{
    public void Collect()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    public void Stop()
    {
        ExitCodes.Current = ExitCodes.Failure;
        lifetime?.StopApplication();
    }
}

/// <summary>
/// Samples the managed heap every <c>Ops:Watchdog:Memory:SampleIntervalSeconds</c> on the
/// watchdog thread: high-water marks of gen2, the large object heap and the whole heap, a growth
/// line when a mark rises by <c>GrowthLogBytes</c>, rate-limited warnings at the heap and load
/// thresholds, and the opt-in action. With <c>FullGcNotifications</c> it also registers for the
/// runtime's full-GC approach notification where the runtime permits (concurrent GC refuses, and
/// the refusal is logged once) and reports approach and completion from a dedicated thread.
/// </summary>
public sealed class MemoryMonitor : IWatchdogMonitor
{
    private readonly MemoryMonitorOptions _options;
    private readonly WatchdogClock _clock;
    private readonly IMemoryProbe _probe;
    private readonly IMemoryPressureActuator _actuator;
    private readonly ILogger _logger;
    private readonly Counter _heapGauge;
    private readonly Counter _gen2Gauge;
    private readonly Counter _lohGauge;
    private readonly Counter _gen2Collections;
    private readonly Counter _actions;
    private readonly Counter _samples;
    private LogRateLimiter _heapLimiter;
    private LogRateLimiter _loadLimiter;
    private long _nextSampleMicros = long.MinValue;
    private long _lastActionMicros = long.MinValue;
    private long _loggedGen2;
    private long _loggedLoh;
    private bool _stopIssued;
    private Thread? _notificationThread;
    private volatile bool _stopping;

    public MemoryMonitor(WatchdogOptions options, WatchdogClock clock, CounterRegistry counters, IMemoryProbe probe, IMemoryPressureActuator actuator, ILogger<MemoryMonitor> logger)
        : this(options.Memory, clock, counters, probe, actuator, logger)
    {
    }

    public MemoryMonitor(MemoryMonitorOptions options, WatchdogClock clock, CounterRegistry counters, IMemoryProbe probe, IMemoryPressureActuator actuator, ILogger logger)
    {
        _options = options;
        _clock = clock;
        _probe = probe;
        _actuator = actuator;
        _logger = logger;
        _heapGauge = counters.GetOrAdd("watchdog.memory.heap_bytes", CounterKind.Gauge);
        _gen2Gauge = counters.GetOrAdd("watchdog.memory.gen2_bytes", CounterKind.Gauge);
        _lohGauge = counters.GetOrAdd("watchdog.memory.loh_bytes", CounterKind.Gauge);
        _gen2Collections = counters.GetOrAdd("watchdog.memory.gen2_collections", CounterKind.Gauge);
        _actions = counters.GetOrAdd("watchdog.memory.actions");
        _samples = counters.GetOrAdd("watchdog.memory.samples");
        _heapLimiter = new LogRateLimiter(Micros.FromSeconds(options.WarnIntervalSeconds));
        _loadLimiter = new LogRateLimiter(Micros.FromSeconds(options.WarnIntervalSeconds));
    }

    public string Name => "memory";

    /// <summary>The highest heap size seen.</summary>
    public long HeapHighWaterBytes { get; private set; }

    public long Gen2HighWaterBytes { get; private set; }

    public long LohHighWaterBytes { get; private set; }

    /// <summary>The most recent sample (default before the first).</summary>
    public MemorySample LastSample { get; private set; }

    /// <summary>Actions fired so far.</summary>
    public long ActionsFired => _actions.Value;

    /// <summary>True when the runtime accepted the full-GC notification registration.</summary>
    public bool FullGcNotificationsActive { get; private set; }

    public void Start()
    {
        if (!_options.Enabled || !_options.FullGcNotifications)
        {
            return;
        }

        try
        {
            GC.RegisterForFullGCNotification(10, 10);
            FullGcNotificationsActive = true;
            _notificationThread = new Thread(WatchFullGc) { IsBackground = true, Name = "watchdog-gc" };
            _notificationThread.Start();
            _logger.LogInformation(WatchdogEvents.FullGc, "full GC notifications registered");
        }
        catch (InvalidOperationException ex)
        {
            // Concurrent (background) GC does not support the notification; the default runtime configuration has it on.
            _logger.LogInformation(WatchdogEvents.FullGc, "full GC notifications are not available ({Reason}); the memory monitor polls only", ex.Message);
        }
    }

    public void Stop()
    {
        _stopping = true;
        if (FullGcNotificationsActive)
        {
            try
            {
                GC.CancelFullGCNotification();
            }
            catch (InvalidOperationException)
            {
                // already cancelled by the runtime
            }

            _notificationThread?.Join(TimeSpan.FromSeconds(2));
        }
    }

    public void Check(long nowMicros)
    {
        if (!_options.Enabled)
        {
            return;
        }

        long interval = Micros.FromSeconds(Math.Max(1, _options.SampleIntervalSeconds));
        if (_nextSampleMicros != long.MinValue && nowMicros < _nextSampleMicros)
        {
            return;
        }

        _nextSampleMicros = nowMicros + interval;
        Sample(nowMicros);
    }

    /// <summary>Take one sample now (the check calls it on schedule; tests call it directly).</summary>
    public void Sample(long nowMicros)
    {
        MemorySample sample = _probe.Read();
        LastSample = sample;
        _samples.Increment();
        _heapGauge.Set(sample.HeapBytes);
        _gen2Gauge.Set(sample.Gen2Bytes);
        _lohGauge.Set(sample.LohBytes);
        _gen2Collections.Set(sample.Gen2Collections);

        HeapHighWaterBytes = Math.Max(HeapHighWaterBytes, sample.HeapBytes);
        bool gen2Rose = sample.Gen2Bytes > Gen2HighWaterBytes;
        bool lohRose = sample.LohBytes > LohHighWaterBytes;
        Gen2HighWaterBytes = Math.Max(Gen2HighWaterBytes, sample.Gen2Bytes);
        LohHighWaterBytes = Math.Max(LohHighWaterBytes, sample.LohBytes);

        if (_options.GrowthLogBytes > 0 && ((gen2Rose && Gen2HighWaterBytes - _loggedGen2 >= _options.GrowthLogBytes) || (lohRose && LohHighWaterBytes - _loggedLoh >= _options.GrowthLogBytes)))
        {
            _loggedGen2 = Gen2HighWaterBytes;
            _loggedLoh = LohHighWaterBytes;
            _logger.LogInformation(WatchdogEvents.MemoryGrowth, "heap high-water marks: gen2 {Gen2MiB} MiB, LOH {LohMiB} MiB, heap {HeapMiB} MiB (fragmented {FragMiB} MiB, working set {WsMiB} MiB, {Gen2Count} gen2 collections, pause {Pause:F1}%)",
                Mib(Gen2HighWaterBytes), Mib(LohHighWaterBytes), Mib(sample.HeapBytes), Mib(sample.FragmentedBytes), Mib(sample.WorkingSetBytes), sample.Gen2Collections, sample.PauseTimePercent);
        }

        if (_options.WarnHeapBytes > 0 && sample.HeapBytes >= _options.WarnHeapBytes && _heapLimiter.TryAcquire(nowMicros, out long suppressed))
        {
            _logger.LogWarning(WatchdogEvents.MemoryPressure, "managed heap is {HeapMiB} MiB (warning threshold {ThresholdMiB} MiB; gen2 {Gen2MiB} MiB, LOH {LohMiB} MiB, working set {WsMiB} MiB; {Suppressed} suppressed)",
                Mib(sample.HeapBytes), Mib(_options.WarnHeapBytes), Mib(sample.Gen2Bytes), Mib(sample.LohBytes), Mib(sample.WorkingSetBytes), suppressed);
        }

        if (_options.WarnLoadPercent > 0 && sample.TotalAvailableBytes > 0 && sample.LoadPercent >= _options.WarnLoadPercent && _loadLimiter.TryAcquire(nowMicros, out suppressed))
        {
            _logger.LogWarning(WatchdogEvents.MemoryPressure, "memory load is {LoadPercent}% of {AvailableMiB} MiB (warning at {WarnPercent}%; the GC's high-load threshold is {GcThresholdMiB} MiB; {Suppressed} suppressed)",
                sample.LoadPercent, Mib(sample.TotalAvailableBytes), _options.WarnLoadPercent, Mib(sample.HighMemoryLoadThresholdBytes), suppressed);
        }

        if (_options.Action != MemoryPressureAction.None && _options.ActionHeapBytes > 0 && sample.HeapBytes >= _options.ActionHeapBytes)
        {
            Act(nowMicros, sample);
        }
    }

    private void Act(long nowMicros, MemorySample sample)
    {
        if (_stopIssued)
        {
            return;
        }

        long cooldown = Micros.FromSeconds(_options.ActionCooldownSeconds);
        if (_lastActionMicros != long.MinValue && nowMicros - _lastActionMicros < cooldown)
        {
            return;
        }

        _lastActionMicros = nowMicros;
        _actions.Increment();
        _logger.LogCritical(WatchdogEvents.MemoryAction, "managed heap {HeapMiB} MiB reached the action threshold {ThresholdMiB} MiB: {Action} (gen2 {Gen2MiB} MiB, LOH {LohMiB} MiB, working set {WsMiB} MiB)",
            Mib(sample.HeapBytes), Mib(_options.ActionHeapBytes), _options.Action, Mib(sample.Gen2Bytes), Mib(sample.LohBytes), Mib(sample.WorkingSetBytes));
        switch (_options.Action)
        {
            case MemoryPressureAction.Collect:
                _actuator.Collect();
                MemorySample after = _probe.Read();
                _logger.LogInformation(WatchdogEvents.MemoryAction, "forced compacting gen2 collection: heap {BeforeMiB} MiB -> {AfterMiB} MiB", Mib(sample.HeapBytes), Mib(after.HeapBytes));
                break;
            case MemoryPressureAction.Stop:
                _stopIssued = true;
                _actuator.Stop();
                break;
            case MemoryPressureAction.Log:
            case MemoryPressureAction.None:
            default:
                break;
        }
    }

    private void WatchFullGc()
    {
        while (!_stopping)
        {
            GCNotificationStatus approach;
            try
            {
                approach = GC.WaitForFullGCApproach(1000);
            }
            catch (InvalidOperationException)
            {
                return; // notification cancelled
            }

            if (approach == GCNotificationStatus.Canceled)
            {
                return;
            }

            if (approach != GCNotificationStatus.Succeeded)
            {
                continue;
            }

            MemorySample before = _probe.Read();
            _logger.LogInformation(WatchdogEvents.FullGc, "full GC approaching: heap {HeapMiB} MiB, gen2 {Gen2MiB} MiB, LOH {LohMiB} MiB", Mib(before.HeapBytes), Mib(before.Gen2Bytes), Mib(before.LohBytes));
            GCNotificationStatus complete;
            try
            {
                complete = GC.WaitForFullGCComplete(60_000);
            }
            catch (InvalidOperationException)
            {
                return;
            }

            if (complete == GCNotificationStatus.Succeeded)
            {
                MemorySample after = _probe.Read();
                _logger.LogInformation(WatchdogEvents.FullGc, "full GC completed: heap {BeforeMiB} MiB -> {AfterMiB} MiB, {Gen2Count} gen2 collections", Mib(before.HeapBytes), Mib(after.HeapBytes), after.Gen2Collections);
            }
        }
    }

    private static long Mib(long bytes) => bytes / (1024 * 1024);
}
