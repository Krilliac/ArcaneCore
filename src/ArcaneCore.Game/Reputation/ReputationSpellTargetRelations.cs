using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Reputation;

namespace ArcaneCore.Game.Reputation;

/// <summary>
/// Reputation-aware explicit helpful targeting. It decorates the existing combat relation seam
/// only when a player/NPC pair has resolvable faction templates and loaded reputation state.
/// </summary>
public sealed class ReputationSpellTargetRelations(
    Func<FactionTemplateCatalog?> templates,
    Func<IReputationService?> reputation,
    ISpellTargetRelations fallback) : ISpellTargetRelations
{
    public bool IsHostile(Unit caster, Unit target) => fallback.IsHostile(caster, target);

    public bool IsFriendly(Unit caster, Unit target) => fallback.IsFriendly(caster, target);

    public bool CanAssist(Unit caster, Unit target)
    {
        if (ReferenceEquals(caster, target) || caster is not Player && target is not Player)
        {
            return fallback.CanAssist(caster, target);
        }

        Player player;
        Unit npcUnit;
        if (caster is Player casterPlayer)
        {
            player = casterPlayer;
            npcUnit = target;
        }
        else if (target is Player targetPlayer)
        {
            player = targetPlayer;
            npcUnit = caster;
        }
        else
        {
            return fallback.CanAssist(caster, target);
        }

        if (npcUnit is not Creature || !npcUnit.CharmerOrOwnerGuid.IsEmpty || templates() is not { } factionTemplates
            || reputation() is not { } reputationService
            || reputationService.For(player) is not { } playerReputation
            || factionTemplates.Find(player.FactionTemplate) is not { } playerTemplate
            || factionTemplates.Find(npcUnit.FactionTemplate) is not { } npcTemplate)
        {
            return fallback.CanAssist(caster, target);
        }

        if (player.IsGameMaster || npcTemplate.IsContestedGuard && (player.Flags & PlayerFlags.ContestedPvp) != 0
            || npcTemplate.Faction == 0
            || reputationService.Factions.Find(npcTemplate.Faction) is not { CanHaveReputation: true } faction
            || playerReputation.State(faction) is null)
        {
            return fallback.CanAssist(caster, target);
        }

        if (!reputationService.TryGetPlayerReaction(player, npcTemplate, playerTemplate, out ReputationRank playerReaction)
            || !reputationService.TryGetNpcReaction(player, npcTemplate, playerTemplate, out ReputationRank npcReaction))
        {
            return fallback.CanAssist(caster, target);
        }

        // vmangos IsValidHelpfulTarget rejects only reactions below REP_UNFRIENDLY;
        // Neutral and Unfriendly remain assistable.
        return playerReaction >= ReputationRank.Unfriendly && npcReaction >= ReputationRank.Unfriendly;
    }
}
