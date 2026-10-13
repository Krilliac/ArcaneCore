using System.Numerics;

namespace ArcaneCore.Game.Creatures;

/// <summary>How a creature path request ended (vmangos <c>PathFinder::getPathType</c> read by each movement generator).</summary>
internal enum PathOutcome : byte
{
    /// <summary>A whole path along the navmesh (<c>PATHFIND_NORMAL</c>).</summary>
    Mesh,

    /// <summary>
    /// The mesh has no data to search with: no navmesh for the map, an endpoint on a tile that is not loaded, or pathing off
    /// (<c>PATHFIND_NORMAL | PATHFIND_NOT_USING_PATH</c>, PathFinder.cpp:86-105). The corners are the straight line; vmangos walks it.
    /// </summary>
    Direct,

    /// <summary>The mesh path ends at the reachable point nearest the destination (<c>PATHFIND_INCOMPLETE</c>).</summary>
    Partial,

    /// <summary>
    /// <c>PATHFIND_NOPATH</c>: an endpoint is off the mesh or nothing connects them. There are no corners: whoever asked decides
    /// what no path means for its motion (chase stands, flee waits, home teleports, wander skips the leg).
    /// </summary>
    NoPath,

    /// <summary>The map's per-update search budget was spent. No search ran, there are no corners; the request is made again on a later update.</summary>
    Deferred,
}

/// <summary>A path request's outcome and its corners (every point after the start, destination last; empty for NoPath and Deferred).</summary>
internal readonly record struct PlannedPath(PathOutcome Outcome, IReadOnlyList<Vector3> Corners)
{
    private static readonly Vector3[] None = [];

    public static PlannedPath Of(PathOutcome outcome) => new(outcome, None);

    /// <summary>There is something to walk: a mesh path, the straight line of a map without mesh data, or a partial path.</summary>
    public bool CanMove => Outcome is PathOutcome.Mesh or PathOutcome.Direct or PathOutcome.Partial;
}

/// <summary>
/// Process-wide counts of the creature path answers that matter on a live server (world thread writes, the metrics feature reads):
/// how often a creature was still sent in a straight line, how often the mesh had no path, and how often a request waited for budget.
/// </summary>
public static class CreaturePathStats
{
    private static long s_directFallbacks;
    private static long s_noPath;
    private static long s_deferred;
    private static long s_homeTeleports;

    /// <summary>
    /// Authored routes (waypoint, point, formation, scripted move) sent along the straight line because the mesh had no path, as
    /// vmangos does. Meshless maps and unloaded tiles are not counted: there the straight line is the whole answer.
    /// </summary>
    public static long DirectFallbacks => Interlocked.Read(ref s_directFallbacks);

    /// <summary>Searches that ended <c>PATHFIND_NOPATH</c>, whoever asked.</summary>
    public static long NoPath => Interlocked.Read(ref s_noPath);

    /// <summary>Requests put off to a later update because the search budget was spent.</summary>
    public static long Deferred => Interlocked.Read(ref s_deferred);

    /// <summary>Evade/home returns done by teleport because the mesh had no whole path home (vmangos HomeMovementGenerator.cpp:70-71).</summary>
    public static long HomeTeleports => Interlocked.Read(ref s_homeTeleports);

    internal static void AddDirectFallback() => Interlocked.Increment(ref s_directFallbacks);

    internal static void AddNoPath() => Interlocked.Increment(ref s_noPath);

    internal static void AddDeferred() => Interlocked.Increment(ref s_deferred);

    internal static void AddHomeTeleport() => Interlocked.Increment(ref s_homeTeleports);
}
