namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The fair rotation of the bots' thinks over ticks. A tick thinks for as many running bots as its budget lets it (the shared
/// action budget, World:Playerbots:MaxActionsPerTick, and <see cref="TickBudgetMs"/>, <see cref="TickWithin"/>), starting at the
/// cursor; the next tick starts right after the last bot this one visited.
/// <para>
/// Before (scale campaign 2026-10-08), the cursor moved one bot per tick whatever the tick visited. A tick that visits K of N bots
/// then visits bot j in K consecutive ticks and skips it for the next N - K: at 300 bots and about 30 thinks per tick, every bot
/// went about 13.5 s without a think. A ghost on its way to a spirit healer measures "not closing" in world time
/// (<see cref="PlayerbotRecovery.StuckMs"/>, 10 s), so after such a gap it faulted with
/// <see cref="PlayerbotRecovery.SpiritHealerFailed"/> and was quarantined, then disabled: 117 faults in 4 minutes at 300 bots
/// live, none at 200. Advancing by the visited count bounds the gap at ceil(N / K) ticks.
/// </para>
/// </summary>
internal sealed class PlayerbotThinkRotation
{
    private int _cursor;

    /// <summary>The first bot the next tick visits (tests).</summary>
    internal int Cursor => _cursor;

    /// <summary>
    /// One tick over <paramref name="count"/> bots: <paramref name="visit"/> is called with each index from the cursor on, wrapping,
    /// until it returns false (the budget or the time cap ran out before that bot: it is not counted as visited) or every bot was
    /// visited once. Returns the number visited.
    /// </summary>
    internal int Tick(int count, Func<int, bool> visit)
    {
        if (count <= 0)
        {
            _cursor = 0;
            return 0;
        }

        if (_cursor >= count) _cursor %= count;
        int visited = 0;
        while (visited < count && visit((_cursor + visited) % count)) visited++;
        // A tick that could visit nobody still moves on by one, so no bot can hold the head of the rotation for good.
        _cursor = (_cursor + Math.Max(1, visited)) % count;
        return visited;
    }

    /// <summary>Whether the last <see cref="TickWithin(int, double, Func{int, bool}, Func{double})"/> left bots unvisited because its time budget went by.</summary>
    internal bool LastTickTimeLimited { get; private set; }

    /// <summary>The wall time one tick spends on the bots' thinks before it leaves the rest to the next tick.</summary>
    internal const double TickBudgetMs = 8;

    /// <summary>
    /// <see cref="Tick"/>, visiting no further bot once <paramref name="budgetMs"/> went by since the tick began: a tick spends at
    /// most that much plus one think, and the bots it did not reach come first in the next tick. The first bot is always visited
    /// (when <paramref name="visit"/> admits it), so the rotation moves on even when one think alone takes longer than the budget.
    /// </summary>
    internal int TickWithin(int count, double budgetMs, Func<int, bool> visit)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        return TickWithin(count, budgetMs, visit, () => System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <summary><see cref="TickWithin(int, double, Func{int, bool})"/> on a given clock: <paramref name="elapsedMs"/> is the time since the tick began (tests).</summary>
    internal int TickWithin(int count, double budgetMs, Func<int, bool> visit, Func<double> elapsedMs)
    {
        bool first = true;
        LastTickTimeLimited = false;
        return Tick(count, index =>
        {
            if (!first && elapsedMs() >= budgetMs) { LastTickTimeLimited = true; return false; }
            first = false;
            return visit(index);
        });
    }
}
