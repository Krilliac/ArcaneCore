namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Splits the bots' think pass by map for World:Parallel:BotThinkPerMap (docs/research/map-local-stages-design-20261024.md, section 5.7).
/// <para>
/// <see cref="Assign"/> runs on the world thread outside the maps phase: it groups the bots (given in id order, as the feature keeps
/// them) by the map each stands on, keeping that order inside every group, and gives each map its own <see cref="PlayerbotThinkRotation"/>.
/// <see cref="ThinkMap"/> then runs on the thread that owns the map during the maps phase and touches only that map's group and rotation:
/// a bot is read and written by one thread at a time, and the scheduler's own tables are read-only during the phase (no lock, no
/// concurrent collection).
/// </para>
/// <para>
/// Same rotation as the serial pass: each map walks its bots with <see cref="PlayerbotThinkRotation.TickLanes{TDriver}"/> (the combat, dead
/// and ghost lane first, then the main cursor) and keeps its cursors across ticks with <see cref="PlayerbotThinkRotation.Track{T}"/>. A map's
/// bot array is rebuilt only when its membership or order changes (a bot joined, left or changed map), so the cursors re-anchor by bot id
/// then and a bot that moves between maps neither shifts the survivors' turn nor thinks twice.
/// </para>
/// <para>
/// The time and the actions of the tick are split by head count: a map with k of n bots gets <c>total * k / n</c> ms (the share of one
/// map, measured on its own thread, so with parallel maps the shares overlap and the wall time of the phase is the longest share, not the
/// sum), and <see cref="ActionsShare"/> of the two action budgets, at least one main-lane action. With one map, or unbounded budgets,
/// the walk is the serial one.
/// </para>
/// Thread affinity: <see cref="Assign"/> and <see cref="CollectUnthought"/> world thread only, never during the phase;
/// <see cref="ThinkMap"/> the owner of that map only (different maps may run at once). Allocation: <see cref="Assign"/> reuses its group
/// lists and allocates an array only for a group whose membership changed; <see cref="ThinkMap"/> allocates nothing in steady state.
/// </summary>
/// <typeparam name="TBot">The bot type (the feature's active bot).</typeparam>
internal sealed class PlayerbotThinkScheduler<TBot> where TBot : class
{
    /// <summary>The key of bots that stand on no map: they are thought for by <see cref="CollectUnthought"/>'s caller on the world thread.</summary>
    internal const ulong NoMapKey = ulong.MaxValue;

    private sealed class Group
    {
        public TBot[] Bots = [];
        public readonly List<TBot> Next = [];
        public readonly PlayerbotThinkRotation Rotation = new();
        public bool Seen;
        public bool Thought;
    }

    private readonly Func<TBot, Guid> _idOf;
    private readonly Dictionary<ulong, Group> _groups = [];
    private readonly List<ulong> _stale = [];
    private int _total;

    /// <param name="idOf">The bot's id: the key the per-map rotations re-anchor by (the order of the lists given to <see cref="Assign"/>).</param>
    internal PlayerbotThinkScheduler(Func<TBot, Guid> idOf)
    {
        ArgumentNullException.ThrowIfNull(idOf);
        _idOf = idOf;
    }

    /// <summary>Bots assigned by the last <see cref="Assign"/>.</summary>
    internal int TotalBots => _total;

    /// <summary>Maps that have at least one bot after the last <see cref="Assign"/>.</summary>
    internal int MapCount => _groups.Count;

    /// <summary>The key of a map instance: map id in the high half, instance id in the low half.</summary>
    internal static ulong MapKey(uint mapId, uint instanceId) => ((ulong)mapId << 32) | instanceId;

    /// <summary>The bots of one map, in assignment order (empty for a map without bots). The array is replaced only when the group changes.</summary>
    internal TBot[] BotsOf(ulong mapKey) => _groups.TryGetValue(mapKey, out Group? group) ? group.Bots : [];

    /// <summary>The rotation of one map (tests); null for a map without bots.</summary>
    internal PlayerbotThinkRotation? RotationOf(ulong mapKey) => _groups.TryGetValue(mapKey, out Group? group) ? group.Rotation : null;

    /// <summary>Regroup <paramref name="bots"/> by <paramref name="mapOf"/> (world thread, outside the maps phase).</summary>
    internal void Assign(IReadOnlyList<TBot> bots, Func<TBot, ulong> mapOf)
    {
        ArgumentNullException.ThrowIfNull(bots);
        ArgumentNullException.ThrowIfNull(mapOf);
        foreach (Group existing in _groups.Values)
        {
            existing.Next.Clear();
            existing.Seen = false;
            existing.Thought = false;
        }

        for (int i = 0; i < bots.Count; i++)
        {
            ulong key = mapOf(bots[i]);
            if (!_groups.TryGetValue(key, out Group? group))
            {
                group = new Group();
                _groups.Add(key, group);
            }

            group.Next.Add(bots[i]);
            group.Seen = true;
        }

        _total = bots.Count;
        _stale.Clear();
        foreach (KeyValuePair<ulong, Group> pair in _groups)
        {
            Group group = pair.Value;
            if (!group.Seen)
            {
                _stale.Add(pair.Key);
            }
            else if (!SameBots(group.Bots, group.Next))
            {
                group.Bots = [.. group.Next];
            }
        }

        foreach (ulong key in _stale)
        {
            _groups.Remove(key);
        }
    }

    private static bool SameBots(TBot[] current, List<TBot> next)
    {
        if (current.Length != next.Count)
        {
            return false;
        }

        for (int i = 0; i < current.Length; i++)
        {
            if (!ReferenceEquals(current[i], next[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The think time a map's bots get of <paramref name="totalBudgetMs"/>: its head-count share (0 for a map without bots).</summary>
    internal double ShareMs(ulong mapKey, double totalBudgetMs)
    {
        if (_total <= 0 || !_groups.TryGetValue(mapKey, out Group? group) || group.Bots.Length == 0)
        {
            return 0;
        }

        return totalBudgetMs * group.Bots.Length / _total;
    }

    /// <summary>
    /// A map's part of the tick's two action budgets (<see cref="PlayerbotThinkRotation.PriorityActions"/> and
    /// <see cref="PlayerbotThinkRotation.ActionReserve"/> for the whole population): the head-count share of each, rounded down, except that
    /// the main lane keeps at least one action (the rotation's guarantee that every map thinks for a bot each tick). With every bot on one
    /// map the shares are the totals.
    /// </summary>
    internal static (int Combat, int Reserve) ActionsShare(int combatTotal, int reserveTotal, int mapBots, int totalBots)
    {
        if (totalBots <= 0 || mapBots >= totalBots)
        {
            return (Math.Max(0, combatTotal), Math.Max(1, reserveTotal));
        }

        long bots = Math.Max(0, mapBots);
        return ((int)(Math.Max(0, combatTotal) * bots / totalBots), Math.Max(1, (int)(Math.Max(0, reserveTotal) * bots / totalBots)));
    }

    /// <summary>
    /// One tick of one map: <paramref name="driver"/> (built over <see cref="BotsOf"/> of this map) is walked by the map's own rotation in
    /// two lanes, within the map's <see cref="ShareMs"/> of <paramref name="totalBudgetMs"/>. Marks the map as thought for this pass; a second call for the same key before the next <see cref="Assign"/> does nothing and returns 0.
    /// Returns the thinks made (0 for a map without bots); <paramref name="timeLimited"/> is whether the time ran out before the walk did.
    /// </summary>
    internal int ThinkMap<TDriver>(ulong mapKey, double totalBudgetMs, double priorityShare, double priorityScale, ref TDriver driver, out bool timeLimited)
        where TDriver : struct, PlayerbotThinkRotation.IThinkLaneDriver
    {
        timeLimited = false;
        if (!_groups.TryGetValue(mapKey, out Group? group) || group.Bots.Length == 0)
        {
            return 0;
        }

        if (group.Thought)
        {
            return 0; // already served this pass: a second walk would classify bots that thought in the first one against the same frame time
        }

        group.Thought = true;
        group.Rotation.Track(group.Bots, _idOf);
        int thinks = group.Rotation.TickLanes(group.Bots.Length, ShareMs(mapKey, totalBudgetMs), priorityShare, ref driver, priorityScale);
        timeLimited = group.Rotation.LastTickTimeLimited;
        return thinks;
    }

    /// <summary>
    /// The maps with bots that no <see cref="ThinkMap"/> has served since the last <see cref="Assign"/> (a map whose stage was not attached,
    /// was skipped by the fault breaker or was created during the phase; <see cref="NoMapKey"/> always): the caller thinks for them on the
    /// world thread after the phase, so no bot is left out. World thread, after the maps phase.
    /// </summary>
    internal void CollectUnthought(List<ulong> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        foreach (KeyValuePair<ulong, Group> pair in _groups)
        {
            if (!pair.Value.Thought)
            {
                destination.Add(pair.Key);
            }
        }
    }
}
