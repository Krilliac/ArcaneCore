using System.Collections.Concurrent;
using System.Diagnostics;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Maps;

/// <summary>
/// The parallel maps phase (docs/research/multithreading-plan-20261017.md, W2/W3). With <c>World:Parallel:MapUpdateThreads</c> 1 (the
/// default) none of this runs and the maps phase is the serial loop it always was.
/// </summary>
public sealed partial class WorldRuntime
{
    private readonly object _serialGate = new();
    private volatile bool _parallelPhase;
    private MapUpdateScheduler? _mapScheduler;
    private MapRun[] _mapRuns = [];
    private int[] _mapOrder = [];
    private readonly ConcurrentDictionary<string, byte> _ownerViolationSites = new(StringComparer.Ordinal);
    private long _ownerViolations;
    private long _gateWaitMicros;
    private long _gateEntries;
    private long _gateContended;
    private long _parallelTicks;

    /// <summary>True while maps are being updated concurrently (between the start of the parallel maps phase and its barrier).</summary>
    public bool IsParallelPhase => _parallelPhase;

    /// <summary>The map threads in use (1: serial maps phase).</summary>
    public int MapUpdateThreads => _mapScheduler?.Threads ?? 1;

    /// <summary>Counters of the parallel maps phase (any thread; diagnostics and <c>.server info</c>).</summary>
    public ParallelMapCounters ParallelCounters => new(
        MapUpdateThreads, Interlocked.Read(ref _parallelTicks), Interlocked.Read(ref _gateEntries), Interlocked.Read(ref _gateContended),
        Interlocked.Read(ref _gateWaitMicros), Interlocked.Read(ref _ownerViolations), _mapScheduler?.BarrierWaitMicros ?? 0);

    /// <summary>
    /// Enter the world's serial gate: while maps update in parallel, at most one thread at a time runs gated work (map work that is not
    /// classified map-local, and any code that touches world-wide state from inside a map update). Outside the parallel phase, and with
    /// one map thread, this does nothing. Reentrant. Dispose the scope to leave.
    /// </summary>
    public SerialScope EnterSerial()
    {
        if (!_parallelPhase)
        {
            return default;
        }

        Interlocked.Increment(ref _gateEntries);
        if (!Monitor.TryEnter(_serialGate))
        {
            long start = Stopwatch.GetTimestamp();
            Monitor.Enter(_serialGate);
            Interlocked.Increment(ref _gateContended);
            Interlocked.Add(ref _gateWaitMicros, (Stopwatch.GetTimestamp() - start) * 1_000_000 / Stopwatch.Frequency);
        }

        return new SerialScope(_serialGate);
    }

    /// <summary>Whether the current thread holds the serial gate (or no parallel phase is running).</summary>
    public bool HoldsSerialGate => !_parallelPhase || Monitor.IsEntered(_serialGate);

    /// <summary>A held <see cref="EnterSerial"/> gate; disposing it leaves the gate.</summary>
    public readonly struct SerialScope(object? gate) : IDisposable
    {
        public void Dispose()
        {
            if (gate is not null)
            {
                Monitor.Exit(gate);
            }
        }
    }

    /// <summary>
    /// Whether the current thread may change <paramref name="map"/>'s state now: outside the parallel phase, the world thread; inside
    /// it, the thread updating that map, or a thread holding the serial gate when the map is not being updated this tick (a map created
    /// during the phase).
    /// </summary>
    public bool IsOwner(Map map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (!_parallelPhase)
        {
            return IsWorldThread;
        }

        if (ReferenceEquals(TickOwner.CurrentMap, map))
        {
            return true;
        }

        return map.ParallelOwnerThreadId == 0 && Monitor.IsEntered(_serialGate);
    }

    /// <summary>
    /// The ownership guard (W2.1): called by map mutators while maps update in parallel. A change from a thread that does not own
    /// <paramref name="map"/> is reported once per call site to the anomaly ledger (subsystem "parallel", code "map-owner"), with the
    /// stack, and throws when <c>World:Parallel:OwnerGuard</c> is Throw.
    /// </summary>
    internal void CheckOwner(Map map, string what)
    {
        if (!_parallelPhase || Options.Parallel.OwnerGuard == OwnerGuardMode.Off || IsOwner(map))
        {
            return;
        }

        Interlocked.Increment(ref _ownerViolations);
        Map? current = TickOwner.CurrentMap;
        string stack = new StackTrace(2, false).ToString();
        string site = stack.Length > 600 ? stack[..600] : stack;
        if (_ownerViolationSites.Count < 256 && _ownerViolationSites.TryAdd(what + "|" + site, 0))
        {
            string detail = $"{what} on map {map.MapId} instance {map.InstanceId} from thread '{Thread.CurrentThread.Name}' ({Environment.CurrentManagedThreadId}) "
                + (current is null ? "outside any map update" : $"updating map {current.MapId} instance {current.InstanceId}")
                + $", gate {(Monitor.IsEntered(_serialGate) ? "held" : "not held")}; at {site.Replace(Environment.NewLine, " <- ", StringComparison.Ordinal)}";
            AnomalyLedger.Shared.Report("parallel", "map-owner", what, detail, AnomalySeverity.Error, Identity);
            _logger.LogError("Parallel map ownership violation: {Detail}", detail);
        }

        if (Options.Parallel.OwnerGuard == OwnerGuardMode.Throw)
        {
            throw new InvalidOperationException($"{what}: map {map.MapId} instance {map.InstanceId} is not owned by this thread during the parallel maps phase");
        }
    }

    private MapStageGates? _stageGates;

    /// <summary>Which map-update stages run outside the serial gate (built from <c>World:Parallel</c> on first use).</summary>
    internal MapStageGates StageGates => _stageGates ??= new MapStageGates(Options.Parallel);

    private void EnsureMapScheduler()
    {
        int threads = Options.Parallel.EffectiveMapUpdateThreads;
        if (threads > 1 && _mapScheduler is null)
        {
            _stageGates ??= new MapStageGates(Options.Parallel);
            _mapScheduler = new MapUpdateScheduler(threads);
            _logger.LogInformation("Parallel maps phase: {Threads} map threads (World:Parallel:MapUpdateThreads), owner guard {Guard}; {Gates}",
                threads, Options.Parallel.OwnerGuard, _stageGates.Describe());
        }
    }

    private string ParallelText(Map map)
        => _mapScheduler is { } scheduler ? $"; map threads {scheduler.Threads}, last {map.LastUpdateMicros / 1000} ms" : string.Empty;

    private void DisposeMapScheduler()
    {
        _mapScheduler?.Dispose();
        _mapScheduler = null;
    }

    private struct MapRun
    {
        public Map Map;
        public long Start;
        public long Micros;
        public long Bytes;
        public bool OnCaller;
        public MapUpdateDiagnostics? Diagnostics;
        public GcSnapshot GcBefore;
    }

    /// <summary>
    /// The maps phase on the map threads: every map of <paramref name="maps"/> is updated exactly once, by one thread, longest (by its
    /// last update) first. The observers, slow-update lines and benchmark seam run afterwards on this thread in map order, as in the
    /// serial loop. Returns the bytes the map updates allocated on the other threads (this thread's own count covers the rest).
    /// </summary>
    private long RunMapsParallel(MapUpdateScheduler scheduler, Map[] maps, int mapCount, uint diffMs, Action<Map, MapUpdateDiagnostics>? observeMap)
    {
        if (_mapRuns.Length < mapCount)
        {
            _mapRuns = new MapRun[Math.Max(mapCount, _mapRuns.Length * 2)];
            _mapOrder = new int[_mapRuns.Length];
        }

        MapRun[] runs = _mapRuns;
        int[] order = _mapOrder;
        bool wantDiagnostics = observeMap is not null || Options.Perf.SlowMapUpdate > 0 && _logger.IsEnabled(LogLevel.Warning);
        for (int i = 0; i < mapCount; i++)
        {
            Map map = maps[i];
            runs[i] = new MapRun { Map = map, Diagnostics = wantDiagnostics ? map.ReusableDiagnostics() : null };
            order[i] = i;
            map.ParallelOwnerThreadId = -1; // scheduled this tick: only its worker may change it (IsOwner)
        }

        // Longest first (LPT); ties keep map order, so the order is deterministic for a given history.
        Array.Sort(order, 0, mapCount, Comparer<int>.Create((a, b) =>
        {
            int byCost = runs[b].Map.LastUpdateMicros.CompareTo(runs[a].Map.LastUpdateMicros);
            return byCost != 0 ? byCost : a.CompareTo(b);
        }));

        int callerThread = Environment.CurrentManagedThreadId;
        _parallelPhase = true;
        try
        {
            scheduler.Run(mapCount, index =>
            {
                ref MapRun run = ref runs[order[index]];
                Map map = run.Map;
                map.ParallelOwnerThreadId = Environment.CurrentManagedThreadId;
                using TickOwner.Scope owner = TickOwner.Enter(this, map);
                long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
                run.GcBefore = run.Diagnostics is not null ? GcSource() : default;
                run.Start = Stopwatch.GetTimestamp();
                try
                {
                    map.Update(diffMs, run.Diagnostics);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogFault("map-update", ex,
                        "map {MapId} instance {InstanceId} update failed on {Thread} (diff={DiffMs} ms, players={Players}, objects={Objects})",
                        map.MapId, map.InstanceId, Thread.CurrentThread.Name, diffMs, map.PlayerCount, map.ObjectCount);
                }
                finally
                {
                    run.Micros = Micros(Stopwatch.GetTimestamp() - run.Start);
                    run.Bytes = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;
                    run.OnCaller = Environment.CurrentManagedThreadId == callerThread;
                    map.LastUpdateMicros = run.Micros;
                }
            });
        }
        finally
        {
            _parallelPhase = false;
            for (int i = 0; i < mapCount; i++)
            {
                runs[i].Map.ParallelOwnerThreadId = 0;
            }
        }

        Interlocked.Increment(ref _parallelTicks);
        long otherThreadBytes = 0;
        for (int i = 0; i < mapCount; i++)
        {
            ref MapRun run = ref runs[i];
            Map map = run.Map;
            if (!run.OnCaller)
            {
                otherThreadBytes += run.Bytes;
            }

            if (MapUpdated is { } mapUpdated)
            {
                try
                {
                    mapUpdated(map, run.Micros);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogFault("map-observer", ex, "MapUpdated observer failed for map {MapId} instance {InstanceId}", map.MapId, map.InstanceId);
                }
            }

            LogIfSlowMicros(Options.Perf.SlowMapUpdate, run.Micros, "Slow map update", map, run.Diagnostics, run.GcBefore);
            if (run.Diagnostics is { } diagnostics && observeMap is not null)
            {
                try
                {
                    observeMap(map, diagnostics);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogFault("map-observer", ex, "map diagnostics observer failed for map {MapId} instance {InstanceId}", map.MapId, map.InstanceId);
                }
            }

            run = default;
        }

        return otherThreadBytes;
    }
}

/// <summary>Counters of the parallel maps phase (<see cref="WorldRuntime.ParallelCounters"/>).</summary>
public readonly record struct ParallelMapCounters(
    int Threads, long ParallelTicks, long GateEntries, long GateContended, long GateWaitMicros, long OwnerViolations, long BarrierWaitMicros);
