using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using ArcaneCore.Kernel.Ops;

namespace ArcaneCore.Kernel.Diagnostics;

/// <summary>Which hook produced a report.</summary>
public enum CrashKind
{
    /// <summary>AppDomain.UnhandledException: the process is ending.</summary>
    Unhandled,

    /// <summary>TaskScheduler.UnobservedTaskException: a faulted task nobody awaited.</summary>
    UnobservedTask,

    /// <summary>AppDomain.FirstChanceException (Debug builds, opt-in).</summary>
    FirstChance,
}

/// <summary>
/// Renders the text of a crash report: the exception with its managed stack and inner exceptions,
/// process and runtime facts, the crashing thread, GC and working-set numbers, the invariant
/// counters, and whatever the daemon's <see cref="ICrashContextProvider"/>s add. Pure: builds a string,
/// touches no global state, so tests check its content directly. The process-wide numbers come from
/// <see cref="Process.GetCurrentProcess"/> and <see cref="GC"/>; each is read inside its own guard so
/// one failing probe never hides the exception that matters.
/// </summary>
public static class CrashReport
{
    private static readonly DateTime StartedUtc = DateTime.UtcNow;

    /// <summary>The configuration this Kernel was compiled with (decides whether Invariant.Assert calls inside it exist).</summary>
    public const string BuildConfiguration =
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    public static string Render(CrashKind kind, Exception? exception, IReadOnlyList<ICrashContextProvider> providers, DateTime nowUtc)
    {
        var sb = new StringBuilder(4096);
        sb.Append("==== ArcaneCore crash report: ").Append(Title(kind)).Append(" at ")
            .Append(nowUtc.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(" UTC ====\n");

        AppendException(sb, exception);
        AppendProcess(sb, nowUtc);
        AppendThread(sb);
        AppendMemory(sb);
        AppendInvariants(sb);
        foreach (ICrashContextProvider provider in providers)
        {
            AppendProvider(sb, provider);
        }

        sb.Append("==== end of crash report ====");
        return sb.ToString();
    }

    private static string Title(CrashKind kind) => kind switch
    {
        CrashKind.Unhandled => "unhandled exception (AppDomain.UnhandledException)",
        CrashKind.UnobservedTask => "unobserved task exception (TaskScheduler.UnobservedTaskException)",
        CrashKind.FirstChance => "first-chance exception (AppDomain.FirstChanceException)",
        _ => kind.ToString(),
    };

    private static void AppendException(StringBuilder sb, Exception? exception)
    {
        sb.Append("[exception]\n");
        if (exception is null)
        {
            sb.Append("  (no managed exception object)\n");
            return;
        }

        sb.Append("  type: ").Append(exception.GetType().FullName).Append('\n');
        sb.Append("  message: ").Append(exception.Message).Append('\n');
        if (exception is AggregateException aggregate)
        {
            sb.Append("  inner exceptions: ").Append(aggregate.InnerExceptions.Count).Append('\n');
        }

        if (exception is InvariantViolationException invariant)
        {
            sb.Append("  invariant site: ").Append(invariant.Member).Append(" (").Append(invariant.File).Append(':').Append(invariant.Line).Append(")\n");
        }

        // ToString carries the managed stack of this exception and of every inner one.
        sb.Append("  managed stack:\n");
        foreach (string line in exception.ToString().Split('\n'))
        {
            sb.Append("    ").Append(line.TrimEnd('\r')).Append('\n');
        }
    }

    private static void AppendProcess(StringBuilder sb, DateTime nowUtc)
    {
        sb.Append("[process]\n");
        Guarded(sb, "pid", static () => Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Guarded(sb, "name", static () => Process.GetCurrentProcess().ProcessName);
        // Never the command line: it may carry --Database:ConnectionString=... with a password.
        Guarded(sb, "uptime", () => (nowUtc - StartedUtc).ToString("c", CultureInfo.InvariantCulture));
        Guarded(sb, "exit code set", static () => ExitCodes.Current.ToString(CultureInfo.InvariantCulture));
        Guarded(sb, "runtime", static () => RuntimeInformation.FrameworkDescription);
        Guarded(sb, "os", static () => RuntimeInformation.OSDescription + " (" + RuntimeInformation.OSArchitecture + ")");
        Guarded(sb, "process architecture", static () => RuntimeInformation.ProcessArchitecture.ToString());
        Guarded(sb, "kernel build", static () => BuildConfiguration);
        Guarded(sb, "gc mode", static () => (GCSettings.IsServerGC ? "server" : "workstation") + ", latency " + GCSettings.LatencyMode);
        Guarded(sb, "processors", static () => Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture));
    }

    private static void AppendThread(StringBuilder sb)
    {
        sb.Append("[thread]\n");
        Thread current = Thread.CurrentThread;
        Guarded(sb, "managed id", () => current.ManagedThreadId.ToString(CultureInfo.InvariantCulture));
        Guarded(sb, "name", () => current.Name ?? "(unnamed)");
        Guarded(sb, "thread pool", () => current.IsThreadPoolThread ? "yes" : "no");
        Guarded(sb, "pool threads", static () => ThreadPool.ThreadCount.ToString(CultureInfo.InvariantCulture));
        Guarded(sb, "pool pending work items", static () => ThreadPool.PendingWorkItemCount.ToString(CultureInfo.InvariantCulture));
    }

    private static void AppendMemory(StringBuilder sb)
    {
        sb.Append("[memory]\n");
        Guarded(sb, "working set", static () => Bytes(Environment.WorkingSet));
        Guarded(sb, "peak working set", static () => Bytes(Process.GetCurrentProcess().PeakWorkingSet64));
        Guarded(sb, "private bytes", static () => Bytes(Process.GetCurrentProcess().PrivateMemorySize64));
        Guarded(sb, "gc heap", static () => Bytes(GC.GetTotalMemory(forceFullCollection: false)));
        Guarded(sb, "gc committed", static () => Bytes(GC.GetGCMemoryInfo().TotalCommittedBytes));
        Guarded(sb, "gc collections gen0/gen1/gen2", static () =>
            GC.CollectionCount(0).ToString(CultureInfo.InvariantCulture) + "/" + GC.CollectionCount(1).ToString(CultureInfo.InvariantCulture) + "/" + GC.CollectionCount(2).ToString(CultureInfo.InvariantCulture));
        Guarded(sb, "total allocated", static () => Bytes(GC.GetTotalAllocatedBytes(precise: false)));
        Guarded(sb, "handles", static () => Process.GetCurrentProcess().HandleCount.ToString(CultureInfo.InvariantCulture));
    }

    private static void AppendInvariants(StringBuilder sb)
    {
        sb.Append("[invariants]\n");
        sb.Append("  failures: ").Append(Invariant.FailureCount).Append(" (policy ").Append(Invariant.Policy).Append(")\n");
        int shown = 0;
        foreach (InvariantFailure failure in Invariant.Failures())
        {
            if (shown++ == 10)
            {
                sb.Append("  ... more sites omitted\n");
                break;
            }

            sb.Append("  ").Append(failure.Count).Append("x ").Append(failure.Member).Append(" (").Append(failure.File).Append(':').Append(failure.Line).Append("): ")
                .Append(failure.LastMessage).Append('\n');
        }
    }

    private static void AppendProvider(StringBuilder sb, ICrashContextProvider provider)
    {
        string name = "(unnamed provider)";
        try
        {
            name = provider.Name;
            sb.Append('[').Append(name).Append("]\n");
            int before = sb.Length;
            provider.Describe(sb);
            if (sb.Length == before)
            {
                sb.Append("  (nothing reported)\n");
            }
            else if (sb[^1] != '\n')
            {
                sb.Append('\n');
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            sb.Append("  ").Append(name).Append(" failed to describe itself: ").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append('\n');
        }
    }

    private static void Guarded(StringBuilder sb, string label, Func<string> read)
    {
        sb.Append("  ").Append(label).Append(": ");
        try
        {
            sb.Append(read());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            sb.Append("(unavailable: ").Append(ex.GetType().Name).Append(')');
        }

        sb.Append('\n');
    }

    private static string Bytes(long value)
        => value.ToString("N0", CultureInfo.InvariantCulture) + " bytes (" + (value / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MiB)";
}
