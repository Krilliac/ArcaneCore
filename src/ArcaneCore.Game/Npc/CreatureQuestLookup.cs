using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Npc;

namespace ArcaneCore.Game.Npc;

/// <summary>
/// World-thread questgiver snapshot from the player's actual map and visible GUID set.
/// vmangos/core 4b3d241cffe245a1f68da11380bce96c23db48c0 Player.cpp
/// GetNPCIfCanInteractWith/CanInteractWithNPC and Object.cpp GetReactionTo.
/// Only quest handlers consume this lookup; gossip/trainer metadata is outside this adapter.
/// </summary>
public sealed class CreatureQuestLookup(FactionTemplateCatalog factions) : ICreatureLookup
{
    public NpcInfo? Find(Player player, ObjectGuid guid)
    {
        if (!player.IsInWorld || player.Map is not { } map || !player.VisibleObjects.Contains(guid)
            || map.FindObject(guid) is not Creature creature || !creature.IsInWorld
            || !ReferenceEquals(creature.Map, map)
            || creature.GetUInt64(UpdateFields.UnitFieldCharmedby) != 0
            || !factions.TryNpcHostility(creature.FactionTemplate, player.FactionTemplate, out bool hostile))
        {
            return null;
        }

        return new NpcInfo(creature.Guid, creature.Entry, creature.Spawn?.Guid ?? creature.Guid.Low,
            (NpcFlags)creature.NpcFlags, creature.MapId, creature.X, creature.Y, creature.Z,
            creature.BoundingRadius, creature.IsAlive, hostile, creature.Combat.IsInCombat,
            (creature.UnitFlags & UnitFlags.NotSelectable) != 0, 0);
    }
}
