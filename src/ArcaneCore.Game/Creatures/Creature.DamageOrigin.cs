using ArcaneCore.Game.Combat;

namespace ArcaneCore.Game.Creatures;

public sealed partial class Creature
{
    /// <summary>vmangos CREATURE_STATIC_FLAG_CORPSE_RAID (CreatureDefines.h:110): no damage-origin check for loot and experience.</summary>
    private const uint StaticFlagCorpseRaid = 0x00001000;

    /// <summary>vmangos m_playerDamageTaken (Creature.h:676): damage from players, their pets, totems, guardians and charms, or itself.</summary>
    internal ulong PlayerDamageTaken { get; private set; }

    /// <summary>vmangos m_nonPlayerDamageTaken (Creature.h:677): damage from units no player controls.</summary>
    internal ulong NonPlayerDamageTaken { get; private set; }

    /// <summary>
    /// The experience multiplier of this death (vmangos Creature::GetXPModifierDueToDamageOrigin, Creature.h:556-566, applied in
    /// MaNGOS::XP::Gain, Formulas.h:152): the players' share of the damage, 0 when they dealt 35% or less, 1 for a raid corpse.
    /// Taken at the kill (<see cref="SettleKillCredit"/>), before the death resets the counters; 1 while the creature lives.
    /// </summary>
    public float KillXpModifier { get; private set; } = 1f;

    /// <summary>Whether this death passed Creature::IsLootAllowedDueToDamageOrigin (taken at the kill; true while the creature lives).</summary>
    public bool KillCreditAllowed { get; private set; } = true;

    /// <summary>vmangos Creature::IsLootAllowedDueToDamageOrigin (Creature.h:548-554).</summary>
    public bool IsLootAllowedDueToDamageOrigin
        => (Template.StaticFlags1 & StaticFlagCorpseRaid) != 0 || 65 * PlayerDamageTaken > 35 * NonPlayerDamageTaken;

    /// <inheritdoc/>
    public void CountDamageTaken(uint damage, bool fromPlayerOrSelf)
    {
        if (fromPlayerOrSelf)
        {
            PlayerDamageTaken += damage;
        }
        else
        {
            NonPlayerDamageTaken += damage;
        }
    }

    /// <inheritdoc/>
    public void ResetDamageTakenOrigin()
    {
        PlayerDamageTaken = 0;
        NonPlayerDamageTaken = 0;
    }

    /// <inheritdoc/>
    public void SettleKillCredit()
    {
        KillCreditAllowed = IsLootAllowedDueToDamageOrigin;
        KillXpModifier = (Template.StaticFlags1 & StaticFlagCorpseRaid) != 0 ? 1f
            : KillCreditAllowed ? (float)PlayerDamageTaken / (PlayerDamageTaken + NonPlayerDamageTaken)
            : 0f;
        if (!KillCreditAllowed)
        {
            ClearLootRecipient(); // Unit.cpp:1007-1008
        }
    }

    /// <summary>A new life (respawn): no damage counted, and the kill snapshot back to its neutral values.</summary>
    internal void ResetKillCredit()
    {
        ResetDamageTakenOrigin();
        KillXpModifier = 1f;
        KillCreditAllowed = true;
    }
}
