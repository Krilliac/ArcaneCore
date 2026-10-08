using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Combat;

public sealed partial class MapCombat
{
    /// <summary>
    /// The resurrection half of vmangos Spell::EffectSpiritHeal (SpellEffects.cpp:5821-5846), the battleground spirit guide's wave:
    /// a dead player in the world comes back at full health and its corpse goes away (ResurrectPlayer(1.0f), SpawnCorpseBones). When its
    /// match is not in progress it is first sent to its graveyard ("no resurrection on a GY other than homie if BG is not in progress";
    /// a game master is not). The caller checks and removes the Waiting to Resurrect aura (2584) and re-summons the pet.
    /// Returns false when the player is alive or not in this map.
    /// </summary>
    public bool ResurrectBySpiritGuide(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (IsQuestSettlementPending(player) || IsAliveState(player) || !player.IsInWorld || !ReferenceEquals(player.Map, _map))
        {
            return false;
        }

        Death.DeathSeams? seams = Death.DeathSeams.Find(_world);
        if (!player.IsGameMaster && seams?.Battlegrounds?.MatchStatusOf(player.Guid) is { } status
            && status != Battlegrounds.BattlegroundStatus.InProgress)
        {
            seams.Graveyards?.RepopAtGraveyard(player);
        }

        ResurrectPlayer(player, 1.0f, applySickness: false);
        SpawnCorpseBones(player);
        return true;
    }
}
