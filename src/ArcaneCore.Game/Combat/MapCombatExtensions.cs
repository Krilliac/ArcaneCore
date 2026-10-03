using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Combat;

/// <summary><c>map.Combat</c>: the map's <see cref="MapCombat"/> updater (attached to every map by <see cref="DefaultMapUpdaters"/>).</summary>
public static class MapCombatExtensions
{
    extension(Map map)
    {
        /// <summary>Combat for this map (docs/integration/combat.md).</summary>
        public MapCombat Combat => map.FindUpdater<MapCombat>()
            ?? throw new InvalidOperationException($"map {map.MapId} has no combat updater");
    }
}
