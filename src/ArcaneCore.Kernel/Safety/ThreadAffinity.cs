using System.Diagnostics;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.Kernel.Safety;

/// <summary>
/// Runtime backstop for <see cref="WorldThreadOnlyAttribute"/> (docs/ops/memory-safety.md): code that finds itself mutating a
/// world-owned object on another thread calls <see cref="Violated"/>. The violation is counted and the first <see cref="RecordLimit"/>
/// are recorded in <see cref="FaultLedger.Shared"/> (so they show in the fault report and the bot-QA issues) as an
/// <see cref="InvariantViolationException"/> that is never thrown: the ledger is handed the stack of the detection point, so the signature
/// names the code that changed the object (this class and its callers marked <c>[StackTraceHidden]</c> are left out). Nothing is thrown, because the mutation already happened or is about to and tearing a
/// session down for it would be worse than the race it reports.
/// <para>
/// Threads: callable from any thread; state is an interlocked counter and a volatile flag. Cost on the pass path is the caller's
/// own thread-id compare; this class is touched on failure only.
/// </para>
/// </summary>
public static class ThreadAffinity
{
    /// <summary>Violations recorded in the ledger (the rest are only counted): recording walks the stack, tens of microseconds each.</summary>
    public const int RecordLimit = 64;

    private static long _violations;
    private static volatile bool _enabled = true;

    /// <summary>Whether affinity checks report. On by default; <c>false</c> turns every <see cref="Violated"/> into a no-op.</summary>
    public static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>Violations seen since start or the last <see cref="Reset"/>.</summary>
    public static long Violations => Volatile.Read(ref _violations);

    /// <summary>Forget the count (tests). The ledger is not touched.</summary>
    public static void Reset() => Interlocked.Exchange(ref _violations, 0);

    /// <summary>Report that <paramref name="site"/> (a stable name such as <c>WorldObject.MarkChanged</c>) ran off the world thread.</summary>
    [StackTraceHidden]
    public static void Violated(string site)
    {
        if (!_enabled)
        {
            return;
        }

        long count = Interlocked.Increment(ref _violations);
        if (count <= RecordLimit)
        {
            var violation = new InvariantViolationException(
                $"{site} changed a world-owned object on managed thread {Environment.CurrentManagedThreadId}, not the world thread",
                site, string.Empty, 0);
            FaultLedger.Shared.Record("thread-affinity:" + site, violation, new StackTrace(fNeedFileInfo: false));
        }
    }
}
