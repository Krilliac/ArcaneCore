using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Reputation;

public sealed partial class ReputationService
{
    /// <summary>Option <c>Reputation:SendForcedReactions</c> (default false until a real client confirms the wire width): send SMSG_SET_FORCED_REACTIONS.</summary>
    public bool SendForcedReactions { get; init; }

    /// <summary>
    /// SPELL_AURA_FORCE_REACTION (Aura::HandleForceReaction, SpellAuras.cpp:2785-2805): force or release a rank for a faction,
    /// announce the set to the client when enabled, and stop the player's fight with that faction when the forced rank (or, on
    /// removal, the real rank) is Friendly or better. False when the player has no loaded standings.
    /// </summary>
    public bool ApplyForcedReaction(Player player, uint factionId, ReputationRank rank, bool apply)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (For(player) is not { } rep)
        {
            return false;
        }

        if (apply)
        {
            rep.SetForcedReaction(factionId, rank);
        }
        else
        {
            rep.ClearForcedReaction(factionId);
        }

        if (SendForcedReactions)
        {
            player.Session.Send(WorldOpcode.SmsgSetForcedReactions, ReputationPackets.SetForcedReactions(rep.ForcedReactions));
        }

        if ((apply && rank >= ReputationRank.Friendly) || (!apply && GetRank(player, factionId) >= ReputationRank.Friendly))
        {
            StopAttackFaction?.Invoke(player, factionId);
        }

        return true;
    }

    /// <summary>
    /// Unit::StopAttackFaction (Unit.cpp:10006-10029) for the player's own victim: supplied by the world feature, which knows the
    /// map's combat. Attackers' and pets' targets and the threat references of the faction (the threat area) are a documented limit.
    /// </summary>
    public Action<Player, uint>? StopAttackFaction { get; set; }
}
