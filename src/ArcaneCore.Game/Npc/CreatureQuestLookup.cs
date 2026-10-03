using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Npc;

namespace ArcaneCore.Game.Npc;

/// <summary>
/// World-thread questgiver snapshot from the player's actual map and visible GUID set.
/// vmangos/core 4b3d241cffe245a1f68da11380bce96c23db48c0 Player.cpp
/// GetNPCIfCanInteractWith/CanInteractWithNPC and Object.cpp GetReactionTo.
/// Gossip and trainer metadata comes from the creature's imported template.
/// Without a <paramref name="reactions"/> source only reputation-free templates resolve; with
/// the reputation owner's source, known reputation factions and contested guards resolve too.
/// </summary>
public sealed class CreatureQuestLookup(FactionTemplateCatalog factions, INpcReactionSource? reactions = null) : ICreatureLookup
{
    public NpcInfo? Find(Player player, ObjectGuid guid)
    {
        if (guid.High == HighGuid.GameObject)
        {
            return FindGameObject(player, guid);
        }

        if (!player.IsInWorld || player.Map is not { } map || !player.VisibleObjects.Contains(guid)
            || map.FindObject(guid) is not Creature creature || !creature.IsInWorld
            || !ReferenceEquals(creature.Map, map)
            || creature.GetUInt64(UpdateFields.UnitFieldCharmedby) != 0
            || !TryHostility(creature, player, out bool hostile))
        {
            return null;
        }

        return new NpcInfo(creature.Guid, creature.Entry, creature.Spawn?.Guid ?? creature.Guid.Low,
            (NpcFlags)creature.NpcFlags, creature.MapId, creature.X, creature.Y, creature.Z,
            creature.BoundingRadius, creature.IsAlive, hostile, creature.Combat.IsInCombat,
            (creature.UnitFlags & UnitFlags.NotSelectable) != 0, creature.Template.GossipMenuId,
            TrainerType: (TrainerType)creature.Template.TrainerType,
            TrainerClass: creature.Template.TrainerClass,
            TrainerRace: creature.Template.TrainerRace,
            TrainerSpell: creature.Template.TrainerSpell,
            FactionId: factions.Find(creature.FactionTemplate)?.Faction ?? 0);
    }

    /// <summary>
    /// A quest-giving game object the player can see (vmangos GetObjectByTypeMask(TYPEMASK_CREATURE_OR_GAMEOBJECT) +
    /// CanInteractWithGameObject, Player.cpp:2540-2565): spawned in the player's map and interactable. A game object
    /// is never hostile, alive-checked or reputation-gated; the distance rule is applied by the caller (the object's
    /// own per-type interaction distance, <see cref="GameObjectMapSystem.InteractionDistanceFor"/>; Player.cpp:2540-2565 also rejects a dead or taxi-flying player, handled by InteractableNpc).
    /// </summary>
    private static NpcInfo? FindGameObject(Player player, ObjectGuid guid)
    {
        if (!player.IsInWorld || player.Map is not { } map || !player.VisibleObjects.Contains(guid)
            || map.FindObject(guid) is not GameObject go || !go.IsSpawned || !ReferenceEquals(go.Map, map)
            || (go.Flags & GameObjectFlags.NoInteract) != 0)
        {
            return null;
        }

        bool giver = go.Type == GameObjectType.QuestGiver;
        return new NpcInfo(go.Guid, go.Entry, go.Spawn?.Guid ?? go.Guid.Low, giver ? NpcFlags.QuestGiver : NpcFlags.None,
            go.MapId, go.X, go.Y, go.Z, go.BoundingRadius, true, false, false, false,
            giver ? go.Template.GetData(QuestGiverGossipIdIndex) : 0, IsGameObject: true,
            GameObjectInteractionDistance: GameObjectMapSystem.InteractionDistanceFor(go.Type));
    }

    /// <summary>questgiver.gossipID, data3 of GAMEOBJECT_TYPE_QUESTGIVER (vmangos GameObjectDefines.h:245-258).</summary>
    public const int QuestGiverGossipIdIndex = 3;

    private bool TryHostility(Creature creature, Player player, out bool hostile)
    {
        hostile = true;
        if (reactions is null)
        {
            return factions.TryNpcHostility(creature.FactionTemplate, player.FactionTemplate, out hostile);
        }

        if (factions.Find(creature.FactionTemplate) is not { } npc || factions.Find(player.FactionTemplate) is not { } self
            || !reactions.TryGetNpcReaction(player, npc, self, out ReputationRank reaction))
        {
            return false;
        }

        // vmangos Unit::IsHostileTo: reaction at or below Hostile.
        hostile = reaction <= ReputationRank.Hostile;
        return true;
    }
}
