using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Combat.Threat;

/// <summary>
/// The aura and talent side of the threat formula (vmangos ThreatCalcHelper::CalcThreat, Threat/ThreatManager.cpp:35-52, and
/// Unit::ApplyTotalThreatModifier, Objects/Unit.cpp:7409-7420). The world feature binds the spell system's implementation
/// (<see cref="SpellThreatModifiers"/>); with none bound threat is the plain amount.
/// </summary>
public interface IThreatModifierSource
{
    /// <summary>SPELLMOD_THREAT of the hated unit's talents for <paramref name="spell"/> (vmangos ApplySpellMod(SPELLMOD_THREAT)).</summary>
    float ApplySpellMod(Unit hated, SpellInfo spell, float threat);

    /// <summary>
    /// The product of (100 + amount) / 100 over the unit's SPELL_AURA_MOD_CRITICAL_THREAT auras whose school mask meets
    /// <paramref name="schoolMask"/> (vmangos GetTotalAuraMultiplierByMiscMask).
    /// </summary>
    float CriticalThreatMultiplier(Unit hated, uint schoolMask);

    /// <summary>
    /// The unit's threat multiplier for one school (vmangos Unit::m_threatModifier[school]): the product of its
    /// SPELL_AURA_MOD_THREAT auras for that school; only players have one (Aura::HandleModThreat, SpellAuras.cpp:3914), others 1.
    /// </summary>
    float TotalThreatMultiplier(Unit hated, int school);
}

/// <summary>vmangos SpellThreatEntry (Spells/SpellMgr.h:94-101): the spell_threat row of one spell.</summary>
/// <param name="Threat">Flat threat added once per hit target (the column is a uint16 in vmangos).</param>
/// <param name="Multiplier">Multiplies the threat of damage and healing the spell does (default 1).</param>
/// <param name="InverseEffectMask">Effects that do not count as causing the flat threat (bit i = effect i).</param>
public sealed record SpellThreatEntry(uint SpellId, int Threat, float Multiplier = 1f, byte InverseEffectMask = 0)
{
    /// <summary>vmangos CanCauseThreatOnMask: some effect of <paramref name="effectMask"/> is not inverted.</summary>
    public bool CanCauseThreatOnMask(int effectMask) => ((~InverseEffectMask) & effectMask & 0xFF) != 0;
}

/// <summary>The spell_threat table (vmangos SpellMgr::GetSpellThreatEntry / GetSpellThreatMultiplier). Data is bound by the content layer.</summary>
public interface ISpellThreatCatalog
{
    /// <summary>The row of <paramref name="spellId"/>, or null.</summary>
    SpellThreatEntry? Find(uint spellId);
}

/// <summary>The threat formula (vmangos ThreatCalcHelper::CalcThreat).</summary>
public static class ThreatCalc
{
    /// <summary>vmangos SPELL_SCHOOL_MASK_NORMAL: physical.</summary>
    public const uint PhysicalMask = 1;

    /// <summary>
    /// vmangos ThreatCalcHelper::CalcThreat (ThreatManager.cpp:35-52): no threat stays none; with a threat spell the caster's
    /// SPELLMOD_THREAT talents apply and a critical hit multiplies by the MOD_CRITICAL_THREAT auras of the spell's schools;
    /// then the first school of the mask picks the caster's MOD_THREAT multiplier (an empty mask applies none, :7414-7415).
    /// </summary>
    public static float Calc(IThreatModifierSource? modifiers, Unit hated, float threat, bool crit, uint schoolMask, SpellInfo? spell)
    {
        ArgumentNullException.ThrowIfNull(hated);
        if (threat == 0f || modifiers is null)
        {
            return threat;
        }

        if (spell is not null)
        {
            threat = modifiers.ApplySpellMod(hated, spell, threat);
            if (crit)
            {
                threat *= modifiers.CriticalThreatMultiplier(hated, schoolMask);
            }
        }

        return schoolMask == 0 ? threat : threat * modifiers.TotalThreatMultiplier(hated, BitOperations.TrailingZeroCount(schoolMask));
    }
}
