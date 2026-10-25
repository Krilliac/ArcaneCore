using System.Diagnostics;
using System.Globalization;
using ArcaneCore.Data.Resilience;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.DebugTools;

/// <summary>
/// Live performance and health readouts under <c>.debug</c> (docs/reference/gm-commands.md): <c>tick</c>, <c>slowticks</c>, <c>features</c>,
/// <c>maps</c>, <c>latency</c>, <c>sqlite</c>, <c>threads</c> and <c>mt</c>. None has a vmangos equivalent, none changes anything (except
/// <c>.debug maps reset</c>, which clears the per-map maxima), and none needs a character, so all of them work from the server console.
/// Everything they read is either lock-free or copied under a short lock, so they are safe to run on a loaded live server.
/// </summary>
public sealed class DebugToolsCommands : ICommandExtension
{
    /// <summary>Rows printed by the list commands when no count is given.</summary>
    public const int DefaultTop = 15;

    /// <summary>Most rows a list command prints.</summary>
    public const int MaxTop = 60;

    public string Path => "debug";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new("tick", AccountSecurity.GameMaster,
            "Syntax: .debug tick\nShow the world tick: target interval, body p50/p99/max over the last minute, each phase (commands, world-tick handlers, features, maps, autosave) over the last minute, late and skipped starts, and what the world thread is doing right now.",
            Tick, RetailLevel: 3),
        new("slowticks", AccountSecurity.GameMaster,
            "Syntax: .debug slowticks [#seconds]\nList the slowest recent world ticks kept for the hang log (within #seconds, default 60, at most 600). The ring holds only 8 ticks: when it is full a new slow tick replaces an entry older than 60 s first, else the fastest one, and the hang log itself uses only the last 60 s; an older entry stays listed until it is replaced, and faster ticks may be missing when the ring is full. Shows duration, phase split, GC and the slowest features of each.",
            SlowTicks, RetailLevel: 3),
        new("features", AccountSecurity.GameMaster,
            "Syntax: .debug features [#count]\nList the world features by their smoothed per-tick time (about the last 50 ticks) with their allocation per tick, slowest first (default 15 rows). The playerbot think pass is one of them.",
            Features, RetailLevel: 3),
        new("maps", AccountSecurity.GameMaster,
            "Syntax: .debug maps [#count|reset]\nList the running maps by their smoothed update time with the last and largest update, players, creatures and objects (default 15 rows). 'reset' clears the maxima.",
            Maps, RetailLevel: 3),
        new("latency", AccountSecurity.GameMaster,
            "Syntax: .debug latency [$prefix] [#count]\nList the recorded latency histograms (character saves, SQLite checkpoints, path queries, login, packet handlers...) by their last-15-minute p99, optionally only the names starting with $prefix (default 15 rows).",
            Latency, RetailLevel: 3),
        new("sqlite", AccountSecurity.GameMaster,
            "Syntax: .debug sqlite\nShow every in-process SQLite write gate: acquisitions, timeouts, forced and dead releases since start, writers waiting now and the current holder with how long it has held the gate.",
            Sqlite, RetailLevel: 3),
        new("threads", AccountSecurity.GameMaster,
            "Syntax: .debug threads\nShow the process threads, CPU use since start, the thread pool (busy workers and IO threads, queued work items, completed items, limits) and lock contention.",
            Threads, RetailLevel: 3),
        new("mt", AccountSecurity.GameMaster,
            "Syntax: .debug mt\nShow the multithreading workers: parallel map update threads and counters (parallel ticks, serial-gate entries, contention, owner violations, barrier wait), and the shared path-job pool (threads, queue, completed, faulted, average compute and queue time).",
            Multithreading, RetailLevel: 3),
    ];

    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    internal static string Ms(double micros) => (micros / 1000.0).ToString(micros >= 100_000 ? "F0" : "F1", C) + " ms";

    private static bool TryCount(string text, out int count, out string rest)
    {
        var args = new CommandArgs(text);
        string? first = args.ExtractArg();
        rest = string.Empty;
        count = DefaultTop;
        if (first is null)
        {
            return true;
        }

        if (int.TryParse(first, NumberStyles.None, C, out int parsed))
        {
            count = Math.Clamp(parsed, 1, MaxTop);
            return args.IsEmpty;
        }

        rest = first;
        string? second = args.ExtractArg();
        if (second is not null)
        {
            if (!int.TryParse(second, NumberStyles.None, C, out parsed) || !args.IsEmpty)
            {
                return false;
            }

            count = Math.Clamp(parsed, 1, MaxTop);
        }

        return true;
    }

    private static bool Tick(CommandContext context, string args)
    {
        if (args.Trim().Length != 0)
        {
            return false;
        }

        WorldRuntime world = context.World;
        TickStatsSnapshot snapshot = world.Stats.Snapshot();
        LatencySnapshot body = snapshot.BodyLatency.Last1Min;
        context.Reply(string.Create(C,
            $"World tick: interval {world.Scheduler.IntervalMs} ms, {snapshot.TotalTicks} ticks since start, {snapshot.Overruns} over budget (ring), {world.Scheduler.LateTicks} late starts, {world.Scheduler.SkippedTicks} skipped, uptime {world.Uptime:d\\.hh\\:mm\\:ss}"));
        context.Reply(body.IsEmpty
            ? "Last minute: no ticks recorded."
            : string.Create(C, $"Last minute body: {body.Count} ticks, p50 {Ms(body.P50)}, p90 {Ms(body.P90)}, p99 {Ms(body.P99)}, max {Ms(body.Max)}, mean {Ms(body.Mean)}"));
        foreach ((string name, LatencySnapshot phase) in snapshot.PhaseLatency)
        {
            if (!phase.IsEmpty)
            {
                context.Reply(string.Create(C, $"  {name}: p50 {Ms(phase.P50)}, p99 {Ms(phase.P99)}, max {Ms(phase.Max)} ({phase.Count} samples)"));
            }
        }

        context.Reply(string.Create(C,
            $"Allocated per tick: commands {snapshot.CommandsMeanBytes / 1024:F0} KB, maps {snapshot.MapsMeanBytes / 1024:F0} KB, features {snapshot.FeaturesMeanBytes / 1024:F0} KB; {world.PendingCommandCount} posted commands queued, {world.OnlinePlayerCount} players online"));
        TickPosition position = world.CurrentPosition;
        double sinceMs = position.Phase == TickPhase.Idle || position.SinceTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(position.SinceTimestamp).TotalMilliseconds;
        context.Reply(string.Create(C, $"World thread now: {position.Phase} {position.DescribeWork()}{(sinceMs > 0 ? $" for {sinceMs:F1} ms" : string.Empty)}"));
        return true;
    }

    private static bool SlowTicks(CommandContext context, string args)
    {
        var parsed = new CommandArgs(args);
        if (!parsed.ExtractOptUInt32(out uint seconds, 60) || !parsed.IsEmpty || seconds == 0 || seconds > 600)
        {
            return false;
        }

        // The ring keeps SlowTickCapacity ticks and never evicts by age: an entry older than SlowTickRetention is only the first
        // to be replaced when the ring is full, so a longer window can still show it. The reply says what the list can miss.
        TickStats stats = context.World.Stats;
        IReadOnlyList<SlowTickInfo> slow = stats.SlowTicks(TimeSpan.FromSeconds(seconds));
        double retention = TickStats.SlowTickRetention.TotalSeconds;
        string window = seconds > retention
            ? $" (the ring keeps {TickStats.SlowTickCapacity} ticks; entries older than {retention:0} s are replaced first and the hang log uses only the last {retention:0} s)"
            : string.Empty;
        if (slow.Count == 0)
        {
            context.Reply(string.Create(C, $"No slow ticks kept within the last {seconds} s{window}."));
            return true;
        }

        string full = slow.Count >= TickStats.SlowTickCapacity ? $"; the ring is full ({TickStats.SlowTickCapacity}), so faster slow ticks may have been dropped" : string.Empty;
        context.Reply(string.Create(C, $"{slow.Count} slow tick(s) within the last {seconds} s{window}{full}, slowest first:"));
        foreach (SlowTickInfo tick in slow.OrderByDescending(t => t.DurationMicros))
        {
            double ago = stats.AgeSeconds(tick.Timestamp);
            string top = string.Join(", ", tick.TopFeatures.Select(f => string.Create(C, $"{f.Name} {Ms(f.Micros)}")));
            context.Reply(string.Create(C, $"#{tick.Sequence} {Ms(tick.DurationMicros)}, {ago:F0} s ago: {tick.Phases.Format()}"));
            if (top.Length != 0)
            {
                context.Reply("  slowest features: " + top);
            }
        }

        return true;
    }

    private static bool Features(CommandContext context, string args)
    {
        if (!TryCount(args, out int count, out string rest) || rest.Length != 0)
        {
            return false;
        }

        TickStatsSnapshot snapshot = context.World.Stats.Snapshot();
        if (snapshot.FeatureMeans.Count == 0)
        {
            context.Reply("No feature timings recorded yet.");
            return true;
        }

        Dictionary<string, double> bytes = snapshot.FeatureAllocations.ToDictionary(f => f.Name, f => f.MeanBytes, StringComparer.Ordinal);
        double total = snapshot.FeatureMeans.Sum(f => f.MeanMicros);
        context.Reply(string.Create(C, $"{snapshot.FeatureMeans.Count} features, {Ms(total)} per tick together (smoothed); slowest {Math.Min(count, snapshot.FeatureMeans.Count)}:"));
        foreach ((string name, double micros) in snapshot.FeatureMeans.Take(count))
        {
            double share = total > 0 ? micros * 100 / total : 0;
            string alloc = bytes.TryGetValue(name, out double b) ? string.Create(C, $", {b / 1024:F1} KB/tick") : string.Empty;
            context.Reply(string.Create(C, $"  {name}: {Ms(micros)} ({share:F0}%){alloc}"));
        }

        return true;
    }

    private static bool Maps(CommandContext context, string args)
    {
        if (!TryCount(args, out int count, out string rest))
        {
            return false;
        }

        MapCostTracker? tracker = context.Session.Services.GetService<MapCostTracker>();
        if (rest.Length != 0)
        {
            if (!rest.Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            tracker?.ResetMaxima();
            context.Reply("Map update maxima cleared.");
            return true;
        }

        CreatureWorldFeature? creatures = context.Session.Services.GetService<CreatureWorldFeature>();
        IReadOnlyList<MapCostSample> costs = tracker?.Snapshot() ?? [];
        Dictionary<Map, MapCostSample> byMap = costs.ToDictionary(c => c.Map);
        Map[] maps = [.. context.World.Maps.OrderByDescending(m => byMap.TryGetValue(m, out MapCostSample? s) ? s.MeanMicros : -1).ThenBy(m => m.MapId).ThenBy(m => m.InstanceId)];
        context.Reply(string.Create(C, $"{maps.Length} map(s) running, {context.World.MapUpdateThreads} update thread(s); {Ms(costs.Sum(c => c.MeanMicros))} per tick together (smoothed):"));
        foreach (Map map in maps.Take(count))
        {
            string name = map.Template?.Name is { Length: > 0 } n ? n : "?";
            int creatureCount = creatures?.FindSystem(map) is { } system ? system.Creatures.Count : 0;
            string cost = byMap.TryGetValue(map, out MapCostSample? s)
                ? string.Create(C, $"mean {Ms(s.MeanMicros)}, last {Ms(s.LastMicros)}, max {Ms(s.MaxMicros)} over {s.Updates} updates")
                : "no updates recorded";
            context.Reply(string.Create(C, $"  map {map.MapId}/{map.InstanceId} [{name}]: {cost}; {map.PlayerCount} players, {creatureCount} creatures, {map.ObjectCount} objects"));
        }

        if (maps.Length > count)
        {
            context.Reply(string.Create(C, $"... {maps.Length - count} more not shown."));
        }

        return true;
    }

    private static bool Latency(CommandContext context, string args)
    {
        if (!TryCount(args, out int count, out string prefix))
        {
            return false;
        }

        LatencyRegistry registry = context.Session.Services.GetService<LatencyRegistry>() ?? LatencyRegistry.Shared;
        var window = TimeSpan.FromMinutes(15);
        var rows = registry.Histograms()
            .Where(kv => prefix.Length == 0 || kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(kv => (Name: kv.Key, Recent: kv.Value.Snapshot(window), Total: kv.Value.Count))
            .Where(r => r.Total > 0)
            .OrderByDescending(r => r.Recent.P99).ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToArray();
        if (rows.Length == 0)
        {
            context.Reply(prefix.Length == 0 ? "No latency recorded yet." : $"No latency recorded under '{prefix}'.");
            return true;
        }

        context.Reply(string.Create(C, $"{rows.Length} histogram(s){(prefix.Length == 0 ? string.Empty : $" under '{prefix}'")}, by last-15-minute p99:"));
        foreach ((string name, LatencySnapshot recent, long total) in rows.Take(count))
        {
            context.Reply(recent.IsEmpty
                ? string.Create(C, $"  {name}: idle for 15 min ({total} since start)")
                : string.Create(C, $"  {name}: {recent.Count} in 15 min, p50 {Ms(recent.P50)}, p99 {Ms(recent.P99)}, max {Ms(recent.Max)} ({total} since start)"));
        }

        if (rows.Length > count)
        {
            context.Reply(string.Create(C, $"... {rows.Length - count} more not shown."));
        }

        return true;
    }

    private static bool Sqlite(CommandContext context, string args)
    {
        if (args.Trim().Length != 0)
        {
            return false;
        }

        IReadOnlyCollection<SqliteWriteGate> gates = SqliteWriteGate.All;
        if (gates.Count == 0)
        {
            context.Reply("No SQLite write gate is active (no file-backed SQLite database has written yet).");
            return true;
        }

        foreach (SqliteWriteGate gate in gates.OrderBy(g => g.Path, StringComparer.OrdinalIgnoreCase))
        {
            SqliteWriteGate.Hold? hold = gate.Current;
            string holder = hold is null || hold.Released
                ? "free"
                : string.Create(C, $"held {hold.HeldMs:F0} ms by {hold.Owner} (waited {hold.WaitedMs:F0} ms)");
            context.Reply(string.Create(C,
                $"{System.IO.Path.GetFileName(gate.Path)}: {gate.AcquiredTotal} writes, {gate.TimeoutsTotal} timeouts, {gate.ForcedTotal} forced and {gate.DeadTotal} dead releases and {gate.HandlerFaultsTotal} release-handler faults{(gate.HandlerFaultsTotal == 0 ? "" : " (first: " + gate.FirstHandlerFault + ")")} since start; {gate.Waiting} waiting; {holder}"));
        }

        return true;
    }

    private static bool Threads(CommandContext context, string args)
    {
        if (args.Trim().Length != 0)
        {
            return false;
        }

        using Process process = Process.GetCurrentProcess();
        TimeSpan cpu = process.TotalProcessorTime;
        TimeSpan wall = DateTime.Now - process.StartTime;
        double cpuPercent = wall.TotalMilliseconds > 0 ? cpu.TotalMilliseconds * 100 / wall.TotalMilliseconds / Environment.ProcessorCount : 0;
        ThreadPool.GetMinThreads(out int minWorkers, out int minIo);
        ThreadPool.GetMaxThreads(out int maxWorkers, out int maxIo);
        ThreadPool.GetAvailableThreads(out int freeWorkers, out int freeIo);
        context.Reply(string.Create(C,
            $"Process: {process.Threads.Count} threads, {Environment.ProcessorCount} logical CPUs, CPU {cpu.TotalSeconds:F0} s since start ({cpuPercent:F1}% of the machine on average), working set {process.WorkingSet64 / (1024 * 1024)} MB"));
        context.Reply(string.Create(C,
            $"Thread pool: {ThreadPool.ThreadCount} threads, {maxWorkers - freeWorkers} busy workers, {maxIo - freeIo} busy IO, {ThreadPool.PendingWorkItemCount} queued items, {ThreadPool.CompletedWorkItemCount} completed; min {minWorkers}/{minIo}, max {maxWorkers}/{maxIo}"));
        context.Reply(string.Create(C, $"Lock contention since start: {System.Threading.Monitor.LockContentionCount}; timers: {Timer.ActiveCount}"));
        return true;
    }

    private static bool Multithreading(CommandContext context, string args)
    {
        if (args.Trim().Length != 0)
        {
            return false;
        }

        WorldRuntime world = context.World;
        ParallelMapCounters counters = world.ParallelCounters;
        context.Reply(string.Create(C,
            $"Map updates: {world.MapUpdateThreads} thread(s){(world.MapUpdateThreads > 1 ? " (parallel)" : " (serial)")}; {counters.ParallelTicks} parallel ticks, {counters.GateEntries} serial-gate entries ({counters.GateContended} contended, {Ms(counters.GateWaitMicros)} waited), {counters.OwnerViolations} owner violations, barrier wait {Ms(counters.BarrierWaitMicros)} in total"));
        if (NavJobSystem.SharedIfStarted is { } jobs)
        {
            NavJobTotals totals = jobs.Totals;
            long served = totals.Completed + totals.Canceled + totals.Skipped;
            double computeMs = served > 0 ? totals.ComputeTicks * 1000.0 / Stopwatch.Frequency / served : 0;
            double queueMs = served > 0 ? totals.QueueTicks * 1000.0 / Stopwatch.Frequency / served : 0;
            double maxMs = totals.MaxComputeTicks * 1000.0 / Stopwatch.Frequency;
            context.Reply(string.Create(C,
                $"Path jobs: {jobs.Threads} thread(s), {jobs.Queued} queued; {totals.Submitted} submitted, {totals.Rejected} rejected, {totals.Completed} completed, {totals.Canceled} canceled, {totals.Skipped} skipped, {totals.Faulted} faulted; compute {computeMs:F2} ms avg ({maxMs:F1} max), queue {queueMs:F2} ms avg"));
        }
        else
        {
            context.Reply("Path jobs: the shared pool has not started (World:Creatures:Movement path jobs are off or unused).");
        }

        ThreadPool.GetMaxThreads(out int maxWorkers, out _);
        ThreadPool.GetAvailableThreads(out int freeWorkers, out _);
        context.Reply(string.Create(C, $"Thread pool: {maxWorkers - freeWorkers} busy workers, {ThreadPool.PendingWorkItemCount} queued items; world thread is {(world.IsParallelPhase ? "in the parallel maps phase" : "serial")}"));
        return true;
    }
}
