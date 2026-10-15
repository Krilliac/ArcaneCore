namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The fair rotation of the bots' thinks over ticks. A tick thinks for as many running bots as its budget lets it (the shared
/// action budget, World:Playerbots:MaxActionsPerTick, and the time budget of <see cref="PlayerbotThinkBudget"/>, <see cref="TickWithin"/>),
/// starting at the cursor; the next tick starts right after the last bot this one visited.
/// <para>
/// Before (scale campaign 2026-10-08), the cursor moved one bot per tick whatever the tick visited. A tick that visits K of N bots
/// then visits bot j in K consecutive ticks and skips it for the next N - K: at 300 bots and about 30 thinks per tick, every bot
/// went about 13.5 s without a think. A ghost on its way to a spirit healer measures "not closing" in world time
/// (<see cref="PlayerbotRecovery.StuckMs"/>, 10 s), so after such a gap it faulted with
/// <see cref="PlayerbotRecovery.SpiritHealerFailed"/> and was quarantined, then disabled: 117 faults in 4 minutes at 300 bots
/// live, none at 200. Advancing by the visited count bounds the gap at ceil(N / K) ticks.
/// </para>
/// <para>
/// Two lanes (<see cref="TickLanes"/>, 2026-10-15): a bot that is fighting, dead or a ghost decides things that cannot wait seconds (targets,
/// spells, retreat), so the bots among them that are <i>due</i> (their own, shorter interval went by) are thought for first, from a
/// cursor of their own, for at most a share of the tick's time and of its shared actions. Then every bot, due or not, is thought for from the main cursor exactly
/// as before, skipping the ones the first lane already did this tick, for the rest of the time. The main cursor advances by what it
/// walked, skipped bots included, so the bound ceil(N / K) of the paragraph above holds for every bot with K the main lane's
/// walk per tick, which is never below the thinks the reserved (1 - share) of the budget buys, whatever the first lane does; and a
/// due bot of the first lane waits at most ceil(P / Kp) ticks for its turn (P due bots, Kp thinks per tick of that lane).
/// With room for everyone, nothing changes: every bot is thought for every tick.
/// </para>
/// </summary>
internal sealed class PlayerbotThinkRotation
{
    private int _cursor;
    private int _priorityCursor;
    private int[] _stamp = [];
    private int _tickNumber;

    /// <summary>The first bot the next tick visits (tests).</summary>
    internal int Cursor => _cursor;

    /// <summary>
    /// One tick over <paramref name="count"/> bots: <paramref name="visit"/> is called with each index from the cursor on, wrapping,
    /// until it returns false (the budget or the time cap ran out before that bot: it is not counted as visited) or every bot was
    /// visited once. Returns the number visited.
    /// </summary>
    internal int Tick(int count, Func<int, bool> visit) => Walk(ref _cursor, count, visit);

    private static int Walk(ref int cursor, int count, Func<int, bool> visit)
    {
        if (count <= 0)
        {
            cursor = 0;
            return 0;
        }

        if (cursor >= count) cursor %= count;
        int visited = 0;
        while (visited < count && visit((cursor + visited) % count)) visited++;
        // A tick that could visit nobody still moves on by one, so no bot can hold the head of the rotation for good.
        cursor = (cursor + Math.Max(1, visited)) % count;
        return visited;
    }

    /// <summary>Whether the last <see cref="TickWithin(int, double, Func{int, bool}, Func{double})"/> left bots unvisited because its time budget went by.</summary>
    internal bool LastTickTimeLimited { get; private set; }

    /// <summary>The wall time one tick spent on thinks in the first release of this rotation, and the floor of <see cref="PlayerbotThinkBudget"/> by default.</summary>
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

    /// <summary>How <see cref="TickLanes"/> sees one bot this tick.</summary>
    internal enum ThinkLane
    {
        /// <summary>An ordinary bot: thought for from the main cursor.</summary>
        Normal,

        /// <summary>A bot of the first lane that is due: thought for before the ordinary ones, for at most the lane's share of the time.</summary>
        Priority,
    }

    /// <summary>The first lane's visits in the last <see cref="TickLanes"/> (tests, metrics).</summary>
    internal int LastPriorityVisits { get; private set; }

    /// <summary>The actions of the tick's shared budget the main lane keeps: the first lane stops once no more than this is left.</summary>
    internal static int ActionReserve(int maxActions, double priorityShare)
        => Math.Max(1, (int)Math.Ceiling(maxActions * (1 - Math.Clamp(priorityShare, 0, 1))));

    /// <summary>
    /// <see cref="TickWithin(int, double, Func{int, bool}, Func{double})"/> in two lanes. First the bots <paramref name="classify"/> calls
    /// <see cref="ThinkLane.Priority"/>, from a cursor of their own, until <paramref name="priorityShare"/> of <paramref name="budgetMs"/>
    /// went by or no more than <paramref name="actionReserve"/> of the shared actions (<paramref name="actionsLeft"/>) are left; then every
    /// bot from the main cursor (a bot of the first lane that was not reached is thought for here in its turn), skipping the ones already
    /// thought for this tick, until <paramref name="budgetMs"/> went by or <paramref name="visit"/> refuses.
    /// <para>
    /// Guarantees: a lane starts no think after its time ran out, and the first lane never takes the last
    /// <paramref name="actionReserve"/> actions, so the main lane has actions left. The main lane's first visit is exempt from the time check
    /// (its cursor moves on whatever one think costs), so a tick spends at most the budget plus the overrun of the last first-lane think plus
    /// one main-lane think; a think that starts in time may end later (it spends the actions it needs, so a single first-lane think can
    /// still overspend the reserve). If the main lane is refused on its first visit only because the first lane spent the actions, its
    /// cursor does not move: that bot is first next tick.
    /// </para>
    /// Returns the thinks made; <see cref="LastTickTimeLimited"/> is set when either lane left bots because the time went by.
    /// </summary>
    internal int TickLanes(int count, double budgetMs, double priorityShare, Func<int, ThinkLane> classify, Func<int, bool> visit, Func<double> elapsedMs,
        Func<int>? actionsLeft = null, int actionReserve = 0)
    {
        LastTickTimeLimited = false;
        LastPriorityVisits = 0;
        if (count <= 0)
        {
            _cursor = 0;
            _priorityCursor = 0;
            return 0;
        }

        if (_stamp.Length != count) _stamp = new int[count];
        if (++_tickNumber == int.MaxValue)
        {
            Array.Clear(_stamp);
            _tickNumber = 1;
        }

        int tick = _tickNumber;
        double priorityMs = budgetMs * Math.Clamp(priorityShare, 0, 1);
        int thinks = 0;
        Walk(ref _priorityCursor, count, index =>
        {
            if (classify(index) != ThinkLane.Priority) return true; // not this lane's business: passed over at no cost
            if (actionsLeft is not null && actionsLeft() <= actionReserve) return false; // the rest of the actions are the main lane's
            if (elapsedMs() >= priorityMs)
            {
                // Only a lane that still had a due bot to think for was cut short by the time.
                LastTickTimeLimited = true;
                return false;
            }

            if (!visit(index)) return false;
            _stamp[index] = tick;
            thinks++;
            LastPriorityVisits++;
            return true;
        });

        bool firstOfLane = true;
        bool refusedFirst = false;
        int mainThinks = 0;
        int cursorBefore = _cursor >= count ? _cursor % count : _cursor;
        Walk(ref _cursor, count, index =>
        {
            if (_stamp[index] == tick) return true; // thought for in the first lane this tick
            if (!firstOfLane && elapsedMs() >= budgetMs) { LastTickTimeLimited = true; return false; }
            bool first = firstOfLane;
            firstOfLane = false;
            if (!visit(index))
            {
                refusedFirst |= first;
                return false;
            }

            thinks++;
            mainThinks++;
            return true;
        });
        // The shared actions went to the first lane: this bot was not refused for its own sake, so it keeps its place at the head.
        if (refusedFirst && mainThinks == 0 && LastPriorityVisits > 0) _cursor = cursorBefore;
        return thinks;
    }
}
