using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures;

public sealed partial class CreatureMapSystem
{
    /// <summary>
    /// Slack (yd) added to a <see cref="CollectCreaturesNear"/> query for creatures that moved since their last grid relocation; the exact
    /// distance test runs on the current position, so the slack only widens the cells read.
    /// </summary>
    private const float NearQuerySlackYards = 10f;

    private readonly List<WorldObject> _nearObjects = [];
    private readonly List<WorldObject> _nearExtras = [];

    /// <summary>
    /// The creatures of this map in the world (in its grid: alive or a corpse on the ground) within <paramref name="radius"/> yards of
    /// (<paramref name="x"/>, <paramref name="y"/>) in 2D, appended to <paramref name="into"/> in map-join order. It reads the grid cells
    /// around the point, not every creature of the map: per-bot scans of all ~9,000-10,000 creatures of a continent were a few percent of
    /// the live world thread each (Oct 17). An oversized or non-finite query falls back to the full scan (same answer, unordered).
    /// World thread (the map's owner) only; not reentrant with itself.
    /// </summary>
    public void CollectCreaturesNear(float x, float y, float radius, List<Creature> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        float radiusSq = radius * radius;
        List<WorldObject> near = _nearObjects;
        List<WorldObject> extras = _nearExtras;
        near.Clear();
        extras.Clear();
        try
        {
            if (float.IsFinite(radius) && radius >= 0
                && Map.Grids.TryCollectObjectsInJoinOrder(x, y, radius + NearQuerySlackYards + Map.Grids.MaxBoundingRadius, near, extras, out _))
            {
                near.AddRange(extras);
                foreach (WorldObject obj in near)
                {
                    if (obj is Creature creature && Within(creature) && _creatures.TryGetValue(creature.Guid, out Creature? own)
                        && ReferenceEquals(own, creature))
                    {
                        into.Add(creature);
                    }
                }

                return;
            }
        }
        finally
        {
            near.Clear();
            extras.Clear();
        }

        foreach (Creature creature in _creatures.Values)
        {
            if (creature.IsInWorld && Within(creature))
            {
                into.Add(creature);
            }
        }

        bool Within(Creature c) => ((c.X - x) * (c.X - x)) + ((c.Y - y) * (c.Y - y)) <= radiusSq;
    }
}
