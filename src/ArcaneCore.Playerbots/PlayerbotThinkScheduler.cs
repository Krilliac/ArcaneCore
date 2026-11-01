namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Splits the bots' think pass by map for World:Parallel:BotThinkPerMap (docs/research/map-local-stages-design-20261024.md, section 5.7).
/// <para>
/// <see cref="Assign"/> runs on the world thread outside the maps phase: it groups the bots (given in a stable order, id order in the
/// feature) by the map each stands on, keeping that order inside every group, and gives each map its own fair rotation
/// (<see cref="PlayerbotThinkRotation"/>, so a tick that cannot reach every bot of a map starts where it stopped). <see cref="ThinkMap"/>
/// then runs on the thread that owns the map during the maps phase and touches only that map's group and rotation: a bot is read and
/// written by one thread at a time, and the scheduler's own tables are read-only during the phase (no lock, no concurrent collection).
/// </para>
/// <para>
/// The think budget is split by head count: a map with k of n bots gets <c>total * k / n</c> ms, and at least one think. With an
/// unbounded budget every bot of every map is thought for exactly once per pass, whichever thread runs it, so the outcome equals one
/// serial pass over all bots as long as a think reads only its own bot and its own map.
/// </para>
/// Thread affinity: <see cref="Assign"/> world thread only (never during the phase); <see cref="ThinkMap"/> the owner of that map only
/// (different maps may run at once). Allocation: <see cref="Assign"/> reuses the group lists and rotations of maps it has seen and
/// allocates only for a new map; <see cref="ThinkMap"/> allocates one closure per call.
/// </summary>
/// <typeparam name="TBot">The bot type (the feature's active bot).</typeparam>
internal sealed class PlayerbotThinkScheduler<TBot>
{
    private sealed class Group
    {
        public readonly List<TBot> Bots = [];
        public readonly PlayerbotThinkRotation Rotation = new();
        public bool Seen;
    }

    private readonly Dictionary<ulong, Group> _groups = [];
    private readonly List<ulong> _stale = [];
    private int _total;

    /// <summary>Bots assigned by the last <see cref="Assign"/>.</summary>
    internal int TotalBots => _total;

    /// <summary>Maps that have at least one bot after the last <see cref="Assign"/>.</summary>
    internal int MapCount => _groups.Count;

    /// <summary>The key of a map instance: map id in the high half, instance id in the low half.</summary>
    internal static ulong MapKey(uint mapId, uint instanceId) => ((ulong)mapId << 32) | instanceId;

    /// <summary>The bots of one map, in assignment order (tests).</summary>
    internal IReadOnlyList<TBot> BotsOf(ulong mapKey) => _groups.TryGetValue(mapKey, out Group? group) ? group.Bots : Array.Empty<TBot>();

    /// <summary>Regroup <paramref name="bots"/> by <paramref name="mapOf"/> (world thread, outside the maps phase).</summary>
    internal void Assign(IReadOnlyList<TBot> bots, Func<TBot, ulong> mapOf)
    {
        ArgumentNullException.ThrowIfNull(bots);
        ArgumentNullException.ThrowIfNull(mapOf);
        foreach (Group existing in _groups.Values)
        {
            existing.Bots.Clear();
            existing.Seen = false;
        }

        for (int i = 0; i < bots.Count; i++)
        {
            ulong key = mapOf(bots[i]);
            if (!_groups.TryGetValue(key, out Group? group))
            {
                group = new Group();
                _groups.Add(key, group);
            }

            group.Bots.Add(bots[i]);
            group.Seen = true;
        }

        _total = bots.Count;
        _stale.Clear();
        foreach (KeyValuePair<ulong, Group> pair in _groups)
        {
            if (!pair.Value.Seen)
            {
                _stale.Add(pair.Key);
            }
        }

        foreach (ulong key in _stale)
        {
            _groups.Remove(key);
        }
    }

    /// <summary>The think time a map's bots get of <paramref name="totalBudgetMs"/>: its head-count share (0 for a map without bots).</summary>
    internal double ShareMs(ulong mapKey, double totalBudgetMs)
    {
        if (_total <= 0 || !_groups.TryGetValue(mapKey, out Group? group) || group.Bots.Count == 0)
        {
            return 0;
        }

        return totalBudgetMs * group.Bots.Count / _total;
    }

    /// <summary>
    /// One tick of one map: <paramref name="think"/> is called for the map's bots from its rotation cursor on, until it returns false
    /// (an action budget ran out: that bot is not counted) or the map's share of the time budget went by (<paramref name="elapsedMs"/> is
    /// the time since the tick's think began). At least one bot is thought for. Returns the number of bots thought for.
    /// </summary>
    internal int ThinkMap(ulong mapKey, double totalBudgetMs, Func<TBot, bool> think, Func<double> elapsedMs)
    {
        ArgumentNullException.ThrowIfNull(think);
        ArgumentNullException.ThrowIfNull(elapsedMs);
        if (!_groups.TryGetValue(mapKey, out Group? group) || group.Bots.Count == 0)
        {
            return 0;
        }

        List<TBot> bots = group.Bots;
        double share = ShareMs(mapKey, totalBudgetMs);
        return group.Rotation.TickWithin(bots.Count, share, index => think(bots[index]), elapsedMs);
    }
}
