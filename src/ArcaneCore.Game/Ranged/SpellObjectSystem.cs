using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Ranged;

/// <summary>Packets of spell-created objects.</summary>
public static class SpellObjectPackets
{
    /// <summary>SMSG_GAMEOBJECT_SPAWN_ANIM: u64 guid (vmangos GameObject::SendObjectSpawnAnim; gtker smsg_gameobject_spawn_anim).</summary>
    public static byte[] SpawnAnim(ObjectGuid guid)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt64(guid.Value);
        return writer.ToArray();
    }
}

/// <summary>
/// Upkeep of one map's spell-created objects (hunter traps): the spawn animation, and the removal
/// bookkeeping the game object system does not know about. Attached to the map as an
/// <see cref="IMapUpdater"/> by the world feature (<c>TrapFeature</c>).
/// <list type="bullet">
/// <item>An object that left the world (its duration ran out, a script removed it) leaves its
/// owner's slot and the creating spell's event cooldown starts (vmangos Unit::RemoveGameObject).</item>
/// <item>An object whose owner left the map is removed without a cooldown (Unit::RemoveAllGameObjects
/// from Unit::RemoveFromWorld, Unit.cpp:8287).</item>
/// <item>The spawn animation is sent once the clients have the object (vmangos sends it right after
/// Map::Add, which creates the object for observers at once; here the create block follows in the
/// tick's visibility phase, so the animation waits for the next tick).</item>
/// </list>
/// </summary>
public sealed class SpellObjectSystem(SpellSystem spells) : IMapUpdater
{
    /// <summary>Map updates after creation before the spawn animation goes out.</summary>
    public const int SpawnAnimAge = 2;

    public void Update(Map map, uint diffMs)
    {
        foreach (SpellCreatedObject entry in spells.SpellObjects.All.ToArray())
        {
            GameObjectMapSystem? system = entry.Object.System;
            if (system is null)
            {
                // The object left the world: free the slot, start the creating spell's cooldown.
                spells.RemoveSpellObject(entry, null, startEventCooldown: true);
                continue;
            }

            if (!ReferenceEquals(system.Map, map))
            {
                continue; // another map's system looks after it
            }

            if (!entry.Owner.IsInWorld || !ReferenceEquals(entry.Owner.Map, map))
            {
                spells.RemoveSpellObject(entry, system, startEventCooldown: false);
                continue;
            }

            entry.Age++;
            if (!entry.SpawnAnimSent && entry.Age >= SpawnAnimAge && entry.Object.IsSpawned)
            {
                entry.SpawnAnimSent = true;
                map.BroadcastToObservers(entry.Object, WorldOpcode.SmsgGameobjectSpawnAnim, SpellObjectPackets.SpawnAnim(entry.Object.Guid));
            }
        }
    }

    public void OnPlayerRemoved(Map map, Player player) => spells.RemoveOwnedObjects(player);
}
