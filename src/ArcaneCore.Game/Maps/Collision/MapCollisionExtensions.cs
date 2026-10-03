using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Maps.Collision;

/// <summary>
/// <c>map.Collision</c> / <c>unit.IsWithinLineOfSight(other)</c>: the seam other areas (spells,
/// creature AI, combat) call. A map without a <see cref="MapCollision"/> updater (a map built by
/// hand in a test) answers with the open defaults.
/// </summary>
public static class MapCollisionExtensions
{
    extension(Map map)
    {
        /// <summary>This map's collision view (docs/integration/vmap-los.md).</summary>
        public MapCollision Collision => map.FindUpdater<MapCollision>() ?? new MapCollision(map, WorldCollision.Unattached, followGrids: false);
    }

    extension(WorldObject source)
    {
        /// <summary>
        /// vmangos <c>WorldObject::IsWithinLOSInMap</c>: true when no static model blocks the view
        /// (or there is no collision data). Objects outside a map are only in sight of themselves.
        /// </summary>
        public bool IsWithinLineOfSight(WorldObject target)
        {
            ArgumentNullException.ThrowIfNull(target);
            if (ReferenceEquals(source, target))
            {
                return true;
            }

            return source.Map is { } map && map.Collision.IsWithinLineOfSight(source, target);
        }
    }
}
