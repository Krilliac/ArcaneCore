namespace ArcaneCore.Game.Creatures;

/// <summary>
/// The work creature movement may spend on path searches in one map update. A creature whose chase or follow re-path does not fit
/// keeps its current movement and asks again on the next update; any other request (waypoint, flee, return home, formation,
/// confused) that does not fit walks the straight line, as vmangos does on PATHFIND_NOPATH. Without it every chaser in a crowd
/// ran a full A* (up to <see cref="Maps.Collision.PathOptions.DefaultMaxSearchNodes"/> nodes) on the world thread in one tick.
/// </summary>
/// <param name="MaxSearches">Most searches per update.</param>
/// <param name="MaxNodes">A* nodes after which no further search starts; the search that crosses it still finishes, so the true bound
/// is MaxNodes + <see cref="Maps.Collision.PathOptions.DefaultMaxSearchNodes"/> - 1.</param>
public readonly record struct CreaturePathBudget(int MaxSearches, long MaxNodes)
{
    /// <summary>64 searches / 16 384 nodes per update (a worst-case search is 2 048 nodes).</summary>
    public static CreaturePathBudget Default { get; } = new(64, 16384);

    /// <summary>No limit (the behaviour before the budget).</summary>
    public static CreaturePathBudget Unlimited { get; } = new(int.MaxValue, long.MaxValue);
}
