namespace ArcaneCore.Kernel.Diagnostics;

/// <summary>Which crash hook saw an exception that is handed to an <see cref="IFaultObserver"/>.</summary>
public enum FaultObservation
{
    /// <summary><see cref="AppDomain.FirstChanceException"/>: every throw in the process, caught or not, on the throwing thread, before any handler runs.</summary>
    FirstChance,

    /// <summary><see cref="TaskScheduler.UnobservedTaskException"/>: a faulted task nobody awaited (finalizer thread).</summary>
    UnobservedTask,

    /// <summary><see cref="AppDomain.UnhandledException"/>: the process is ending.</summary>
    Unhandled,
}

/// <summary>
/// Hears the exceptions the crash hooks (<see cref="CrashHandler"/>) see, as objects and not as log text (bot QA's fault pipeline).
/// Implementations run on whatever thread threw (the world thread included, for <see cref="FaultObservation.FirstChance"/>): they must be
/// cheap, must not block and need not guard against throwing (the dispatcher swallows it), but must be thread-safe.
/// </summary>
public interface IFaultObserver
{
    /// <summary>
    /// One exception seen by a crash hook. <paramref name="ledgerRecorded"/> is true when the hook already counted it in the
    /// <see cref="FaultLedger"/> under its own signature (unhandled and unobserved faults; never for a first-chance throw), so an observer
    /// that files the ledger's growth elsewhere does not file it a second time.
    /// </summary>
    void Observe(FaultObservation kind, Exception exception, bool ledgerRecorded);
}

/// <summary>
/// The process-wide list of <see cref="IFaultObserver"/>s. Registration is rare (feature attach and detach) and copy-on-write; dispatch is a volatile
/// read of an array and, per observer, a call under a per-thread re-entrancy guard (an observer that throws, or code it calls that throws, cannot recurse
/// into the observers from the first-chance hook). Registering the first observer attaches the first-chance hook even in a build that does not log
/// first-chance exceptions, and removing the last detaches it again (<see cref="CrashHandler"/>).
/// </summary>
public static class FaultObservers
{
    private static readonly object Gate = new();
    private static volatile IFaultObserver[] _observers = [];

    [ThreadStatic]
    private static bool _dispatching;

    /// <summary>Whether any observer is registered.</summary>
    public static bool Any => _observers.Length > 0;

    /// <summary>Register <paramref name="observer"/>; dispose the result to remove it.</summary>
    public static IDisposable Add(IFaultObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (Gate)
        {
            _observers = [.. _observers, observer];
        }

        CrashHandler.Current?.RefreshFirstChanceHook();
        return new Registration(observer);
    }

    /// <summary>Hand one exception to every observer (never throws; a nested call on the same thread is ignored). Called by the crash hooks; public so a test can stand in for them.</summary>
    public static void Notify(FaultObservation kind, Exception exception, bool ledgerRecorded)
    {
        IFaultObserver[] observers = _observers;
        if (observers.Length == 0 || _dispatching)
        {
            return;
        }

        _dispatching = true;
        try
        {
            foreach (IFaultObserver observer in observers)
            {
                try
                {
                    observer.Observe(kind, exception, ledgerRecorded);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // An observer must never turn a fault into a second one, nor stop the observers after it.
                }
            }
        }
        finally
        {
            _dispatching = false;
        }
    }

    private sealed class Registration(IFaultObserver observer) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (Gate)
            {
                int index = Array.IndexOf(_observers, observer);
                if (index >= 0)
                {
                    IFaultObserver[] next = new IFaultObserver[_observers.Length - 1];
                    Array.Copy(_observers, 0, next, 0, index);
                    Array.Copy(_observers, index + 1, next, index, next.Length - index);
                    _observers = next;
                }
            }

            CrashHandler.Current?.RefreshFirstChanceHook();
        }
    }
}
