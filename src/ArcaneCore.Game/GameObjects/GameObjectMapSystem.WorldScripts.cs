using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.GameObjects;

/// <summary>The game-object helpers open-world (area trigger) scripts share.</summary>
public sealed partial class GameObjectMapSystem
{
    /// <summary>
    /// ScriptDev GetClosestGameObjectWithEntry(source, entry, range): the nearest spawned object of <paramref name="entry"/> on this map within
    /// <paramref name="range"/> yards (3D) of <paramref name="center"/>.
    /// </summary>
    public GameObject? ClosestOfEntry(WorldObject center, uint entry, float range)
    {
        ArgumentNullException.ThrowIfNull(center);
        GameObject? best = null;
        float bestSq = range * range;
        foreach (GameObject go in _objects.Values)
        {
            if (go.Entry != entry || !go.IsSpawned)
            {
                continue;
            }

            float dx = go.X - center.X, dy = go.Y - center.Y, dz = go.Z - center.Z;
            float sq = (dx * dx) + (dy * dy) + (dz * dz);
            if (sq <= bestSq)
            {
                best = go;
                bestSq = sq;
            }
        }

        return best;
    }

    /// <summary>
    /// cmangos GameObject::Use(Unit*) of a trap for a player an area trigger names (at_wondervolt): the trap casts its spell at the player
    /// (cooldown and charges counted) whether or not the player stands in its radius, as <see cref="UseByUnit"/> does. Refused for an
    /// object of another type, one not spawned on this map, or a player on another map.
    /// </summary>
    public bool UseTrapFor(GameObject trap, Player player)
    {
        ArgumentNullException.ThrowIfNull(trap);
        ArgumentNullException.ThrowIfNull(player);
        if (trap.Type != GameObjectType.Trap || !ReferenceEquals(player.Map, Map))
        {
            return false;
        }

        return UseByUnit(player, trap) == GameObjectUseResult.Ok;
    }
}
