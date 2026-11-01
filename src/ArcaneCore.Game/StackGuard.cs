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
    /// <summary>
    /// True when the calling thread has enough stack left for the nested action. When it has not, records an
    /// <see cref="InsufficientExecutionStackException"/> under <paramref name="site"/> (in <paramref name="ledger"/>, <see cref="FaultLedger.Shared"/>
    /// by default) and returns false: the caller drops the nested action.
    /// </summary>
    public static bool TryEnter(string site, FaultLedger? ledger = null)
    {
        if (RuntimeHelpers.TryEnsureSufficientExecutionStack())
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
