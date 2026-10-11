using System.Diagnostics;

namespace ArcaneCore.Game.Maps.Collision;

/// <summary>
/// What the navigation-mesh searches of the calling thread have cost so far: searches run, A* nodes created, nodes expanded,
/// navigation tiles read on demand (<see cref="PathOptions.LoadTiles"/>) and the time those reads took. Thread-static and only ever
/// added to, so a caller takes <see cref="Current"/> before and after a piece of work and subtracts (a bot's think, one query).
/// Each search adds a few integer increments; nothing allocates.
/// </summary>
public static class PathSearchCounters
{
    [ThreadStatic] private static long t_searches;
    [ThreadStatic] private static long t_nodes;
    [ThreadStatic] private static long t_expansions;
    [ThreadStatic] private static long t_tileLoads;
    [ThreadStatic] private static long t_tileLoadTicks;
    [ThreadStatic] private static long t_capped;

    /// <summary>This thread's totals so far.</summary>
    public static PathSearchTotals Current => new(t_searches, t_nodes, t_expansions, t_tileLoads, t_tileLoadTicks, t_capped);

    /// <summary>One A* search ended: <paramref name="nodes"/> created, <paramref name="expansions"/> expanded, whether the node cap was hit.</summary>
    internal static void RecordSearch(int nodes, int expansions, bool capped)
    {
        t_searches++;
        t_nodes += nodes;
        t_expansions += expansions;
        if (capped) t_capped++;
    }

    /// <summary>A navigation tile was read for a query, taking <paramref name="ticks"/> (<see cref="Stopwatch"/> ticks).</summary>
    internal static void RecordTileLoad(long ticks)
    {
        t_tileLoads++;
        t_tileLoadTicks += ticks;
    }
}

/// <summary>A snapshot of <see cref="PathSearchCounters"/>; the difference of two is the work between them.</summary>
public readonly record struct PathSearchTotals(long Searches, long Nodes, long Expansions, long TileLoads, long TileLoadTicks, long Capped)
{
    public static PathSearchTotals operator -(PathSearchTotals a, PathSearchTotals b)
        => new(a.Searches - b.Searches, a.Nodes - b.Nodes, a.Expansions - b.Expansions, a.TileLoads - b.TileLoads,
            a.TileLoadTicks - b.TileLoadTicks, a.Capped - b.Capped);

    /// <summary>The tile reads' time in milliseconds.</summary>
    public double TileLoadMs => TileLoadTicks * 1000.0 / Stopwatch.Frequency;
}
