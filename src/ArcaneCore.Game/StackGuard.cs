using System.Runtime.CompilerServices;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.Game;

/// <summary>
/// A guard for the recursive relays of the game layer (a proc that casts a spell that procs, an aura apply that applies another aura, a DB, EventAI
/// or SmartAI script that starts another script). A stack overflow cannot be caught and ends the process, so each relay asks
/// <see cref="TryEnter"/> first: when less than the runtime's safety margin of stack is left, the nested action is dropped and one fault is recorded
/// instead. Stateless; callable from any thread (it checks the calling thread's own stack). Cost on the happy path: one runtime call that compares
/// the stack pointer with a cached limit, no allocation.
/// </summary>
internal static class StackGuard
{
    [ThreadStatic] private static bool t_suspended;

    /// <summary>
    /// Test seam for the RED proof: while the returned scope is open, <see cref="TryEnter"/> on the calling thread always answers true (the guard
    /// is off), so a test can show what the guarded path does when nothing stops it. Affects only the calling thread; dispose restores the
    /// previous state. Production code never calls it.
    /// </summary>
    internal static SuspendScope Suspend()
    {
        bool previous = t_suspended;
        t_suspended = true;
        return new SuspendScope(previous);
    }

    /// <summary>Restores the calling thread's guard state when disposed (<see cref="Suspend"/>); dispose it on the thread that opened it.</summary>
    internal readonly struct SuspendScope(bool previous) : IDisposable
    {
        public void Dispose() => t_suspended = previous;
    }

    /// <summary>
    /// True when the calling thread has enough stack left for the nested action. When it has not, records an
    /// <see cref="InsufficientExecutionStackException"/> under <paramref name="site"/> (in <paramref name="ledger"/>, <see cref="FaultLedger.Shared"/>
    /// by default) and returns false: the caller drops the nested action.
    /// </summary>
    public static bool TryEnter(string site, FaultLedger? ledger = null)
    {
        if (t_suspended || RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            return true;
        }

        // The exception is thrown only so the ledger has a signature to count; it is never propagated.
        try
        {
            throw new InsufficientExecutionStackException("nested action dropped: not enough stack left (recursive proc, aura or script chain)");
        }
        catch (InsufficientExecutionStackException ex)
        {
            (ledger ?? FaultLedger.Shared).Record(site, ex);
        }

        return false;
    }
}
