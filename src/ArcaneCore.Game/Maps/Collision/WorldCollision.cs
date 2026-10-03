using System.Runtime.CompilerServices;

namespace ArcaneCore.Game.Maps.Collision;

/// <summary>
/// The collision services of one world: its <see cref="ILineOfSight"/> and <see cref="IPathfinder"/>.
/// Attached to the <see cref="WorldRuntime"/> without changing it (a weak side table, as
/// <see cref="Templates.WorldMaps"/>), created with the no-data defaults on first use and
/// replaced at startup by the world daemon's collision feature when vmap/mmap data is configured
/// (<c>World:Collision</c>, docs/integration/vmap-los.md).
/// <para>Thread affinity: install before the world thread starts (or on it); read on the world thread.</para>
/// </summary>
public sealed class WorldCollision
{
    private static readonly ConditionalWeakTable<WorldRuntime, WorldCollision> Attached = new();

    private WorldCollision()
    {
    }

    /// <summary>The defaults, for a map that is not attached to any world's collision services.</summary>
    internal static WorldCollision Unattached { get; } = new();

    /// <summary>Line of sight and model heights (default: <see cref="OpenLineOfSight"/>).</summary>
    public ILineOfSight LineOfSight { get; private set; } = OpenLineOfSight.Instance;

    /// <summary>Paths (default: <see cref="StraightLinePathfinder"/>).</summary>
    public IPathfinder Pathfinder { get; private set; } = StraightLinePathfinder.Instance;

    /// <summary>The collision services of <paramref name="world"/> (created with the defaults on first use).</summary>
    public static WorldCollision Of(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return Attached.GetValue(world, _ => new WorldCollision());
    }

    /// <summary>
    /// Replace the services. A null argument keeps the current one. Maps already created pick the
    /// new services up on their next query (they read through this object).
    /// </summary>
    public void Install(ILineOfSight? lineOfSight = null, IPathfinder? pathfinder = null)
    {
        if (lineOfSight is not null)
        {
            LineOfSight = lineOfSight;
        }

        if (pathfinder is not null)
        {
            Pathfinder = pathfinder;
        }
    }
}
