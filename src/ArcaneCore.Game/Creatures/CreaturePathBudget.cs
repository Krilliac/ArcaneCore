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
/// <param name="MaxMilliseconds">
/// Wall time the update's searches may spend before no further search starts (the search that crosses it still finishes). Nodes do
/// not bound time: a search that reopens nodes, crosses tile borders or runs while the host is busy can cost tens of milliseconds
/// for the same node count (live 2026-10-16, map 0: one creature's searches took 65-425 ms of a map update). Infinite when omitted, so
/// a hand-built budget (tests) is count-only and deterministic; <see cref="Default"/> sets 12 ms, which makes what a map does in an
/// update depend on host load.
/// </param>
public readonly record struct CreaturePathBudget(int MaxSearches, long MaxNodes, double MaxMilliseconds = double.PositiveInfinity)
{
    /// <summary>64 searches / 16 384 nodes / 12 ms per update (a worst-case search is 2 048 nodes).</summary>
    public static CreaturePathBudget Default { get; } = new(64, 16384, 12);

    /// <summary>No limit (the behaviour before the budget).</summary>
    public static CreaturePathBudget Unlimited { get; } = new(int.MaxValue, long.MaxValue);

    /// <summary><see cref="MaxMilliseconds"/> in <see cref="System.Diagnostics.Stopwatch"/> ticks (<see cref="long.MaxValue"/> when unbounded).</summary>
    internal long MaxStopwatchTicks
        => double.IsFinite(MaxMilliseconds) && MaxMilliseconds * System.Diagnostics.Stopwatch.Frequency / 1000.0 < long.MaxValue
            ? (long)Math.Max(0, MaxMilliseconds * System.Diagnostics.Stopwatch.Frequency / 1000.0)
            : MaxMilliseconds < 0 ? 0 : long.MaxValue;
}
