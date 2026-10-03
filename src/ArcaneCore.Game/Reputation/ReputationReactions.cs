using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Reputation;

namespace ArcaneCore.Game.Reputation;

/// <summary>
/// Player-versus-NPC reaction, re-implemented from vmangos/core 4b3d241 Object.cpp
/// WorldObject::GetReactionTo / GetFactionReactionTo (no GPL source copied). Every method
/// fails closed (returns false) when the data it needs is missing: an unknown template, a
/// nonzero faction absent from Faction.dbc, or a reputation faction whose player state is not
/// loaded. Forced reactions (SPELL_AURA_FORCE_REACTION) are not modelled yet.
/// </summary>
public static class ReputationReactions
{
    /// <summary>
    /// The NPC's reaction toward the player (CvP; what Unit::IsHostileTo(player) and
    /// GetNPCIfCanInteractWith use): GM is neutral, contested guards attack contested players,
    /// reputation factions use the player's rank (capped at Neutral while at war), and other
    /// factions use template relations.
    /// </summary>
    public static bool TryNpcReactionTo(FactionTemplateRecord npc, FactionTemplateRecord playerTemplate, Player player,
        FactionCatalog factions, PlayerReputation? reputation, out ReputationRank reaction)
    {
        ArgumentNullException.ThrowIfNull(npc);
        ArgumentNullException.ThrowIfNull(playerTemplate);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(factions);
        reaction = ReputationRank.Hostile;
        if (player.IsGameMaster)
        {
            reaction = ReputationRank.Neutral;
            return true;
        }

        if (npc.IsContestedGuard && (player.Flags & PlayerFlags.ContestedPvp) != 0)
        {
            return true;
        }

        if (npc.Faction != 0)
        {
            if (factions.Find(npc.Faction) is not { } faction)
            {
                return false;
            }

            if (faction.CanHaveReputation)
            {
                if (reputation?.State(faction) is not { } state)
                {
                    return false;
                }

                ReputationRank rank = reputation.Rank(faction);
                reaction = state.IsAtWar && rank > ReputationRank.Neutral ? ReputationRank.Neutral : rank;
                return true;
            }
        }

        reaction = TemplateReaction(npc, playerTemplate);
        return true;
    }

    /// <summary>
    /// The player's reaction toward the NPC (PvC, the player-controlled branch of GetReactionTo):
    /// GM is neutral; a reputation faction is hostile only for contested guards against a
    /// contested player or while the player is at war with it, otherwise friendly; anything else
    /// uses the player's template relations.
    /// </summary>
    public static bool TryPlayerReactionTo(FactionTemplateRecord npc, FactionTemplateRecord playerTemplate, Player player,
        FactionCatalog factions, PlayerReputation? reputation, out ReputationRank reaction)
    {
        ArgumentNullException.ThrowIfNull(npc);
        ArgumentNullException.ThrowIfNull(playerTemplate);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(factions);
        reaction = ReputationRank.Hostile;
        if (player.IsGameMaster)
        {
            reaction = ReputationRank.Neutral;
            return true;
        }

        if (npc.Faction != 0)
        {
            if (factions.Find(npc.Faction) is not { } faction)
            {
                return false;
            }

            if (faction.CanHaveReputation)
            {
                if (reputation?.State(faction) is not { } state)
                {
                    return false;
                }

                reaction = (npc.IsContestedGuard && (player.Flags & PlayerFlags.ContestedPvp) != 0) || state.IsAtWar
                    ? ReputationRank.Hostile
                    : ReputationRank.Friendly;
                return true;
            }
        }

        reaction = TemplateReaction(playerTemplate, npc);
        return true;
    }

    /// <summary>GetFactionReactionTo's common template check: hostile, then friendly either way, else neutral.</summary>
    public static ReputationRank TemplateReaction(FactionTemplateRecord self, FactionTemplateRecord target)
    {
        ArgumentNullException.ThrowIfNull(self);
        ArgumentNullException.ThrowIfNull(target);
        if (self.IsHostileTo(target))
        {
            return ReputationRank.Hostile;
        }

        return self.IsFriendlyTo(target) || target.IsFriendlyTo(self) ? ReputationRank.Friendly : ReputationRank.Neutral;
    }
}
