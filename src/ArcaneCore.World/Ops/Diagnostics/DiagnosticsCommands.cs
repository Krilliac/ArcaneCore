using System.Diagnostics;
using System.Globalization;
using ArcaneCore.Data.Resilience;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Resilience;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Ops.Diagnostics;

/// <summary>
/// Memory, GC and runtime self-diagnosis commands for a live server, as sub-commands of the existing <c>.debug</c> root (docs/ops/diagnostics.md):
/// <c>.debug gc</c>, <c>.debug selfcheck</c> and <c>.debug invariants</c> (game master), <c>.debug gccollect</c>, <c>.debug alloc start|report|stop</c> and <c>.debug dump</c> (administrator).
/// They have no vmangos equivalent. All of them work from the server console too (it has Administrator authority); none needs a character.
/// </summary>
public sealed class DiagnosticsCommands : ICommandExtension
{
    public const int MaxInvariantSites = 20;

    public string Path => "debug";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new("gc", AccountSecurity.GameMaster,
            "Syntax: .debug gc\nPrint the garbage collector's state: the last ephemeral, full blocking and background collection (pauses, promoted bytes, heap and fragmentation per generation), memory load, collection counts, total pause time, and the GC mode.",
            Gc, RetailLevel: 3),
        // After gc: a word resolves to the first entry it is a prefix of, so a gccollect listed first would take every .debug gc.
        new("gccollect", AccountSecurity.Administrator,
            "Syntax: .debug gccollect\nForce a full blocking, compacting garbage collection (including the large object heap) and show the heap before and after and how long it paused the server. Use sparingly on a live server.",
            GcCollect, RetailLevel: 6),
        new("selfcheck", AccountSecurity.GameMaster,
            "Syntax: .debug selfcheck\nInspect process and world health: thread-pool starvation, GC memory load, fault ledger signatures with source locations, invariant failures, world tick position/scheduler and database circuit breakers.",
            SelfCheck, RetailLevel: 3),
        new("invariants", AccountSecurity.GameMaster,
            "Syntax: .debug invariants\nPrint the process-wide invariant failure count, policy, and up to 20 recorded invariant violation sites.",
            Invariants, RetailLevel: 3),
        new("alloc", AccountSecurity.Administrator,
            "Syntax: .debug alloc $subcommand\nSample what the server allocates, by type, for a while: start, report, stop.",
            Children:
            [
                new ChatCommand("start", AccountSecurity.Administrator,
                    "Syntax: .debug alloc start [seconds=30]\nStart sampling allocations (the runtime's GCAllocationTick event, about one per 100 KB) for 1-600 seconds; it stops by itself. One sample at a time.",
                    AllocStart, RetailLevel: 6),
                new ChatCommand("report", AccountSecurity.Administrator,
                    "Syntax: .debug alloc report [top=15]\nList the biggest allocating types of the running or last sample, separately for the small, large and pinned object heaps: sampled MiB, MiB per second and share. The numbers are sampled estimates.",
                    AllocReport, RetailLevel: 6),
                new ChatCommand("stop", AccountSecurity.Administrator,
                    "Syntax: .debug alloc stop\nStop the running allocation sample now; .debug alloc report still shows its result.",
                    AllocStop, RetailLevel: 6),
            ], RetailLevel: 6),
        new("dump", AccountSecurity.Administrator,
            "Syntax: .debug dump [full|heap|mini]\nWrite a dump of this server process to disk (default full). Off unless Ops:Diagnostics:Dumps:Enabled is set: a dump holds session keys and passwords. A full dump suspends the process for about a minute at 7 GB. Heap dumps fail on some hosts (E_INVALIDARG); use full.",
            Dump, RetailLevel: 6),
    ];

    private static bool Gc(CommandContext context, string args)
    {
        foreach (string line in GcReport.Format(GcReport.Capture()))
        {
            context.Reply(line);
        }

        return true;
    }

    private static bool GcCollect(CommandContext context, string args)
    {
        if (args.Trim().Length != 0) return false;
        long before = GC.GetTotalMemory(false);
        long workingBefore = Environment.WorkingSet;
        int[] counts = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        long t0 = Stopwatch.GetTimestamp();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        double pause = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        long after = GC.GetTotalMemory(false);
        GCMemoryInfo info = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        static string Mb(long bytes) => (bytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture) + " MiB";
        context.Reply(string.Create(CultureInfo.InvariantCulture,
            $"Full compacting collection took {pause:F1} ms: managed heap {Mb(before)} -> {Mb(after)} (freed {Mb(Math.Max(0, before - after))}), working set {Mb(workingBefore)} -> {Mb(Environment.WorkingSet)}"));
        context.Reply(string.Create(CultureInfo.InvariantCulture,
            $"  collections since start: gen0 {GC.CollectionCount(0)} (+{GC.CollectionCount(0) - counts[0]}), gen1 {GC.CollectionCount(1)} (+{GC.CollectionCount(1) - counts[1]}), gen2 {GC.CollectionCount(2)} (+{GC.CollectionCount(2) - counts[2]}); fragmented {Mb(info.FragmentedBytes)}, committed {Mb(info.TotalCommittedBytes)}"));
        return true;
    }

    private static bool Invariants(CommandContext context, string args)
    {
        if (args.Trim().Length != 0)
        {
            return false;
        }

        long total = Invariant.FailureCount;
        var policy = Invariant.Policy;
        IReadOnlyList<InvariantFailure> failures = Invariant.Failures();
        if (total == 0 && failures.Count == 0)
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture, $"Invariants: 0 failures recorded (policy={policy})."));
            return true;
        }

        context.Reply(string.Create(CultureInfo.InvariantCulture,
            $"Invariants: {total} failure(s) recorded ({failures.Count} site(s), policy={policy}):"));
        int shown = 0;
        foreach (InvariantFailure inv in failures)
        {
            if (shown++ == MaxInvariantSites)
            {
                break;
            }

            context.Reply(string.Create(CultureInfo.InvariantCulture,
                $"{inv.Member} ({inv.File}:{inv.Line}) count={inv.Count}: {inv.LastMessage}"));
        }

        if (failures.Count > MaxInvariantSites)
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture,
                $"... {failures.Count - MaxInvariantSites} more site(s) omitted."));
        }

        return true;
    }

    private static bool SelfCheck(CommandContext context, string args)
    {
        if (args.Trim().Length != 0)
        {
            return false;
        }

        FaultLedger ledger = context.Session.Services.GetService<FaultLedger>() ?? FaultLedger.Shared;
        foreach (string line in SelfDiagnosisReport.Format(SelfDiagnosisReport.Capture(ledger)))
        {
            context.Reply(line);
        }

        WorldRuntime world = context.World;
        TickPosition pos = world.CurrentPosition;
        long posElapsedMs = pos.SinceTimestamp > 0
            ? Math.Max(0L, (Stopwatch.GetTimestamp() - pos.SinceTimestamp) * 1000L / Stopwatch.Frequency)
            : 0L;
        context.Reply(string.Create(CultureInfo.InvariantCulture,
            $"World tick: phase={pos.Phase}, work={pos.DescribeWork()}, elapsed={posElapsedMs} ms, pendingCommands={world.PendingCommandCount}, lateTicks={world.Scheduler.LateTicks}, skippedTicks={world.Scheduler.SkippedTicks}"));

        var isolated = new List<string>();
        foreach (Map map in world.Maps)
        {
            foreach (IMapUpdater updater in map.IsolatedUpdaters)
            {
                isolated.Add(string.Create(CultureInfo.InvariantCulture, $"map {map.MapId}#{map.InstanceId}:{updater.GetType().Name}"));
            }
        }

        context.Reply(isolated.Count == 0
            ? "World updaters: ok (0 isolated)"
            : "World updaters: WARNING isolated=" + string.Join(", ", isolated));

        if (context.Session.Services.GetService<DatabaseCircuits>() is { } circuits)
        {
            var summary = new List<string>(circuits.All.Count);
            foreach (CircuitBreaker breaker in circuits.All)
            {
                summary.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{breaker.Name}={breaker.State}(failures={breaker.ConsecutiveFailures},rejected={breaker.Rejected})"));
            }

            context.Reply("Database circuits: " + string.Join(", ", summary));
        }

        return true;
    }

    private static bool AllocStart(CommandContext context, string args)
    {
        if (!DiagnosticsArgs.TryOption(args, "seconds", AllocationProfiler.DefaultSeconds, out int seconds))
        {
            return false;
        }

        if (Profiler(context) is not { } profiler)
        {
            context.Reply("The allocation profiler is not available on this host.");
            return true;
        }

        profiler.TryStart(seconds, out string message);
        context.Reply(message);
        return true;
    }

    private static bool AllocReport(CommandContext context, string args)
    {
        if (!DiagnosticsArgs.TryOption(args, "top", AllocationProfiler.DefaultTop, out int top))
        {
            return false;
        }

        if (top is < 1 or > AllocationProfiler.MaxTop)
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture, $"top must be 1-{AllocationProfiler.MaxTop}."));
            return true;
        }

        if (Profiler(context) is not { } profiler)
        {
            context.Reply("The allocation profiler is not available on this host.");
            return true;
        }

        if (profiler.Report() is not { } report)
        {
            context.Reply("No allocation sample has run. Start one with .debug alloc start [seconds].");
            return true;
        }

        foreach (string line in AllocationReportFormat.Format(report.Snapshot, report.Elapsed, report.Running, top))
        {
            context.Reply(line);
        }

        return true;
    }

    private static bool AllocStop(CommandContext context, string args)
    {
        if (args.Length != 0)
        {
            return false;
        }

        if (Profiler(context) is not { } profiler)
        {
            context.Reply("The allocation profiler is not available on this host.");
            return true;
        }

        context.Reply(profiler.Stop(detachOffThread: true) ? "Allocation sampling stopped. Use .debug alloc report." : "No allocation sample is running.");
        return true;
    }

    private static bool Dump(CommandContext context, string args)
    {
        if (!DiagnosticsArgs.TryDumpKind(args, out DumpKind kind))
        {
            return false;
        }

        if (context.Session.Services.GetService<ProcessDumpService>() is not { } service)
        {
            context.Reply("Process dumps are not available on this host.");
            return true;
        }

        DumpRequestResult result = service.Request(kind);
        context.Reply(result.Message);
        if (result.Completion is { } completion)
        {
            _ = ReplyWhenDoneAsync(context, completion);
        }

        return true;
    }

    // The dump runs on the thread pool; the outcome is posted back to the world thread, where a reply is safe (the invoker may have left).
    private static async Task ReplyWhenDoneAsync(CommandContext context, Task<DumpOutcome> completion)
    {
        try
        {
            DumpOutcome outcome = await completion.ConfigureAwait(false);
            string text = ProcessDumpService.DescribeOutcome(outcome);
            context.World.Post(() =>
            {
                if (context.CanReplyLater)
                {
                    context.Reply(text);
                }
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("diagnostics-dump-reply", ex);
        }
    }

    private static AllocationProfiler? Profiler(CommandContext context) => context.Session.Services.GetService<AllocationProfiler>();
}

/// <summary>Argument parsing of the diagnostics commands.</summary>
internal static class DiagnosticsArgs
{
    /// <summary>
    /// One optional integer, given as <c>30</c> or <c>seconds=30</c> (the name case-insensitive). Empty arguments give <paramref name="defaultValue"/>.
    /// False when the text is anything else.
    /// </summary>
    internal static bool TryOption(string args, string name, int defaultValue, out int value)
    {
        value = defaultValue;
        string text = args.Trim();
        if (text.Length == 0)
        {
            return true;
        }

        string prefix = name + "=";
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            text = text[prefix.Length..];
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>The dump type word (<c>full</c>, <c>heap</c>, <c>mini</c>, any case); empty means full.</summary>
    internal static bool TryDumpKind(string args, out DumpKind kind)
    {
        kind = DumpKind.Full;
        string text = args.Trim();
        switch (text.ToLowerInvariant())
        {
            case "":
            case "full":
                return true;
            case "heap":
                kind = DumpKind.Heap;
                return true;
            case "mini":
                kind = DumpKind.Mini;
                return true;
            default:
                return false;
        }
    }
}
