using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Maps.Collision;

/// <summary>
/// Optional hook for creature movement (chase, follow, return home): ask the map's
/// <see cref="IPathfinder"/> for a path and launch the creature's straight spline to the next
/// corner. Called again on later updates, the creature walks the path corner by corner; with no
/// navigation data the path is the straight line, so behaviour without mmaps is unchanged.
/// The creature AI area owns when to call this (docs/integration/vmap-los.md).
/// </summary>
public static class CreaturePathing
{
    /// <summary>Corners closer than this are treated as reached and skipped.</summary>
    public const float ReachedDistance = 0.5f;

    /// <summary>
    /// Move <paramref name="creature"/> one path segment towards <paramref name="destination"/>.
    /// Returns the path used; no movement is launched when there is no usable path (the caller
    /// decides, as vmangos chase does on PATHFIND_NOPATH) or the destination is already reached.
    /// </summary>
    public static PathResult MoveTowards(CreatureMapSystem system, Creature creature, Vector3 destination, bool run, PathOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(creature);
        var start = new Vector3(creature.X, creature.Y, creature.Z);
        PathResult path = creature.Map is { } map
            ? map.Collision.FindPath(start, destination, options)
            : PathResult.StraightLine(start, destination, PathType.Normal | PathType.NotUsingPath);
        if (!path.HasPath)
        {
            return path;
        }

        for (int i = 1; i < path.Points.Count; i++)
        {
            Vector3 corner = path.Points[i];
            if (Vector3.Distance(start, corner) > ReachedDistance)
            {
                system.MoveTo(creature, corner.X, corner.Y, corner.Z, run, finalOrientation: null);
                break;
            }
        }

        return path;
    }
}
