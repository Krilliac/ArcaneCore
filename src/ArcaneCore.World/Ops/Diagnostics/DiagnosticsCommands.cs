using System.Globalization;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Ops.Diagnostics;

/// <summary>
/// Memory and GC diagnostics for a live server, as sub-commands of the existing <c>.debug</c> root (docs/ops/diagnostics.md):
/// <c>.debug gc</c> (game master), <c>.debug alloc start|report|stop</c> and <c>.debug dump</c> (administrator). They have no vmangos
/// equivalent. All of them work from the server console too (it has Administrator authority); none needs a character.
/// </summary>
public sealed class DiagnosticsCommands : ICommandExtension
{
    public string Path => "debug";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new("gc", AccountSecurity.GameMaster,
            "Syntax: .debug gc\nPrint the garbage collector's state: the last ephemeral, full blocking and background collection (pauses, promoted bytes, heap and fragmentation per generation), memory load, collection counts, total pause time, and the GC mode.",
            Gc, RetailLevel: 3),
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

        context.Reply(profiler.Stop() ? "Allocation sampling stopped. Use .debug alloc report." : "No allocation sample is running.");
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
