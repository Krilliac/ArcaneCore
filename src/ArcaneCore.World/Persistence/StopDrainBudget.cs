using System.Diagnostics;

namespace ArcaneCore.World.Persistence;

/// <summary>
/// The shutdown bound of one write queue. A write attempt can wait up to the SQLite write gate's hard cap (20 s on the characters database) while the gate keeps
/// handing off, so the inline retry loop and an unlimited drain could together outlast <c>HostOptions:ShutdownTimeout</c>. Once <see cref="Begin"/> has been called
/// (the queue's <c>StopAsync</c>): the queue makes no further inline retry of a failed attempt (the failure is retained and <c>StopAsync</c> names it, exactly as for an
/// exhausted retry), and after <see cref="Budget"/> of draining it starts no further attempt at all (<see cref="ThrowIfExhausted"/>), so a stuck drain costs at most the budget plus
/// the attempt that was already running. Thread-safe; one instance per queue.
/// </summary>
internal sealed class StopDrainBudget
{
    /// <summary>The default drain budget of a queue: how long after <c>StopAsync</c> a new write attempt may still start.</summary>
    internal static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(5);

    private long _startTimestamp; // Stopwatch timestamp of Begin; 0: not stopping

    /// <summary>How long after <see cref="Begin"/> a new attempt may still start.</summary>
    internal TimeSpan Budget { get; set; } = DefaultBudget;

    /// <summary>True once <see cref="Begin"/> was called: no inline retry any more.</summary>
    internal bool Started => Volatile.Read(ref _startTimestamp) != 0;

    /// <summary>Starts the budget (the first call only).</summary>
    internal void Begin() => Interlocked.CompareExchange(ref _startTimestamp, Math.Max(1, Stopwatch.GetTimestamp()), 0);

    /// <summary>True when stopping and the budget has elapsed.</summary>
    internal bool Exhausted
    {
        get
        {
            long start = Volatile.Read(ref _startTimestamp);
            return start != 0 && Stopwatch.GetElapsedTime(start) >= Budget;
        }
    }

    /// <summary>Throws <see cref="TimeoutException"/> when <see cref="Exhausted"/>, so the caller's ordinary failure path retains the write.</summary>
    internal void ThrowIfExhausted()
    {
        if (ExhaustedFailure() is { } failure)
        {
            throw failure;
        }
    }

    /// <summary>The failure to retain a write with when <see cref="Exhausted"/> (null while attempts may still start).</summary>
    internal TimeoutException? ExhaustedFailure()
        => Exhausted ? new TimeoutException($"the shutdown drain budget ({Budget.TotalSeconds:0.#} s) is spent; the write stays retained") : null;
}
