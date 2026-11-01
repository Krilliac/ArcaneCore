using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Combat;

public sealed partial class MapCombat
{
    /// <summary>
    /// The resurrection half of vmangos Spell::EffectSpiritHeal (SpellEffects.cpp:5821-5846), the battleground spirit guide's wave:
    /// a dead player in the world comes back at full health and its corpse goes away (ResurrectPlayer(1.0f), SpawnCorpseBones). When its
    /// match is not in progress it is first sent to its graveyard ("no resurrection on a GY other than homie if BG is not in progress";
    /// a game master is not). The caller checks the Waiting to Resurrect aura (2584) and re-summons the pet; <paramref name="beforeResurrect"/>
    /// runs after the graveyard step and right before the resurrection, where vmangos removes the aura (RemoveAurasDueToSpell(2584) between
    /// RepopAtGraveyard and ResurrectPlayer), so a refused resurrection never costs the player the aura.
    /// Returns false when the player is alive or not in this map, or stays dead.
    /// </summary>
    public bool ResurrectBySpiritGuide(Player player, Action<Player>? beforeResurrect = null)
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

        beforeResurrect?.Invoke(player);
        ResurrectPlayer(player, 1.0f, applySickness: false);
        if (!IsAliveState(player))
        {
            return false;
        }

        RemoveResurrectedCorpse(player);
        return true;
    }

    /// <summary>The radius of vmangos <c>npc_spirit_guideAI::CorpseRemoved</c> (battleground.cpp:87).</summary>
    public const float SpiritGuideRemovalRadius = 20.0f;

    /// <summary>
    /// A battleground spirit guide at (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>) was removed (an Arathi Basin node
    /// was assaulted or captured, an Alterac Valley node changed hands): every dead player of this map within 20 yards that carries Waiting
    /// to Resurrect (2584) is repopped at a graveyard again, "now this node won't be counted and another node is searched"
    /// (vmangos npc_spirit_guideAI::CorpseRemoved, battleground.cpp:74-93; the retail rule, re-expressed). Returns the number repopped.
    /// Runs on the world thread; needs the spell system (<see cref="SpellMitigation"/>) to see the aura, else repops nobody.
    /// </summary>
    public int RepopWaitingGhostsNear(float x, float y, float z)
    {
        if (SpellMitigation is not { } spells)
        {
            return 0;
        }

        int repopped = 0;
        foreach (Player player in _world.OnlinePlayers.ToArray())
        {
            if (!player.IsInWorld || !ReferenceEquals(player.Map, _map) || IsAliveState(player))
            {
                continue;
            }

            float dx = player.X - x;
            float dy = player.Y - y;
            float dz = player.Z - z;
            float maxDist = SpiritGuideRemovalRadius + player.BoundingRadius + Player.DefaultBoundingRadius;
            if (((dx * dx) + (dy * dy) + (dz * dz)) >= maxDist * maxDist
                || !spells.HasAura(player, Battlegrounds.BattlegroundConstants.SpellWaitingToResurrect))
            {
                continue;
            }

            if (RepopViaSeam(player))
            {
                repopped++;
            }
        }

        return repopped;
    }
}
