namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The fair rotation of the bots' thinks over ticks. A tick thinks for as many running bots as its budget lets it (the action budgets,
/// World:Playerbots:MaxActionsPerTick, and the time budget of <see cref="PlayerbotThinkBudget"/>, <see cref="TickWithin"/>),
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
/// cursor of their own, for at most a share of the tick's time and of its actions. Then every bot, due or not, is thought for from the main cursor exactly
/// as before, skipping the ones the first lane already did this tick, for the rest of the time. The main cursor advances by what it
/// walked, skipped bots included.
/// </para>
/// <para>
/// Hard per-lane budgets: the actions are split in two budgets the lanes cannot spend from each other (see
/// <see cref="ActionReserve"/> and <see cref="PriorityActions"/>), and the main lane thinks for at least one bot every tick,
/// unconditionally (no action or time check before its first think). So any bot's gap is at most N ticks, and at most ceil(N / K) where K
/// is the main lane's walk per tick, K at least max(1, floor(ActionReserve / a)) for a think costing a actions; a due bot of the first lane
/// waits at most ceil(P / Kp) ticks for its turn (P due bots, Kp = max(1, ceil(PriorityActions / c)) for a think costing c actions) when
/// PriorityActions is at least 1. At MaxActionsPerTick 1 the first lane is idle and the fighters keep their turn in the main rotation.
/// With room for everyone, nothing changes: every bot is thought for every tick.
/// </para>
/// <para>
/// Review note (2026-10-15): the first version of the lanes kept a soft reserve: the first lane stopped once no more than the reserve was
/// left in the one shared budget, but a single think spends what it needs, so it could drain the budget to 0 and the main lane then
/// never thought (harness: max 2 or 3 with a combat cost of 2 or 3, and max 48 with a combat cost of 16 or more), the cursor restore
/// pinning the cursor in place. The two budgets are hard.
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

    /// <summary>The actions the main lane keeps of <c>MaxActionsPerTick</c>: at least one, whatever the share.</summary>
    internal static int ActionReserve(int maxActions, double priorityShare) => maxActions - PriorityActions(maxActions, priorityShare);

    /// <summary>
    /// The actions of <c>MaxActionsPerTick</c> that are the first lane's own: floor(share × max), never all of them (zero at 1). The
    /// epsilon keeps a product a double puts a hair under a whole number (10 × 0.7) on that number, as the option's text promises.
    /// </summary>
    internal static int PriorityActions(int maxActions, double priorityShare)
        => Math.Clamp((int)Math.Floor(maxActions * Math.Clamp(priorityShare, 0, 1) + 1e-9), 0, Math.Max(0, maxActions - 1));

    /// <summary>
    /// <see cref="TickWithin(int, double, Func{int, bool}, Func{double})"/> in two lanes. First the bots <paramref name="classify"/> calls
    /// <see cref="ThinkLane.Priority"/>, from a cursor of their own, until <paramref name="priorityShare"/> of <paramref name="budgetMs"/>
    /// went by or <paramref name="visit"/> refuses (the caller's first-lane action budget ran out); then every bot from the main cursor (a bot
    /// of the first lane that was not reached is thought for here in its turn), skipping the ones already thought for this tick, until
    /// <paramref name="budgetMs"/> went by or <paramref name="visit"/> refuses.
    /// <para>
    /// <paramref name="visit"/> gets the lane it is called for and must draw the think's actions from that lane's own budget. Guarantees: a
    /// lane starts no think after its time ran out; the main lane's first visit is exempt from the time check, and nothing in the first
    /// lane can refuse it, so the main lane thinks for at least one bot every tick (its cursor always moves on) and a tick spends at most the
    /// budget plus the overrun of the last first-lane think plus one main-lane think.
    /// </para>
    /// Returns the thinks made; <see cref="LastTickTimeLimited"/> is set when either lane left bots because the time went by.
    /// </summary>
    internal int TickLanes(int count, double budgetMs, double priorityShare, Func<int, ThinkLane> classify, Func<int, ThinkLane, bool> visit, Func<double> elapsedMs)
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
            if (elapsedMs() >= priorityMs)
            {
                // Only a lane that still had a due bot to think for was cut short by the time.
                LastTickTimeLimited = true;
                return false;
            }

            if (!visit(index, ThinkLane.Priority)) return false;
            _stamp[index] = tick;
            thinks++;
            LastPriorityVisits++;
            return true;
        });

        bool firstOfLane = true;
        Walk(ref _cursor, count, index =>
        {
            if (_stamp[index] == tick) return true; // thought for in the first lane this tick
            if (!firstOfLane && elapsedMs() >= budgetMs) { LastTickTimeLimited = true; return false; }
            firstOfLane = false;
            if (!visit(index, ThinkLane.Normal)) return false;
            thinks++;
            return true;
        });
        return thinks;
    }
}
