using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Stats;

namespace ArcaneCore.Game.Spells.Rogue;

/// <summary>
/// The attack power terms of the rogue bleeds at the 1.12 build (vmangos <c>Aura::CalculateDotDamage</c>, SpellAuras.cpp:4341-4396, the
/// branch after client 1.11.2; patch 1.12.0 notes "Rupture now increases in potency with greater attack power" and "Garrote now increases
/// in potency with greater attack power"):
/// <list type="bullet">
/// <item>Rupture (family Rogue, CF_ROGUE_RUPTURE bit 20): the tick amount gets <c>attack power * min(combo points, 3) / 100</c>, so only the
/// first three points add attack power, like Rip's first four.</item>
/// <item>Garrote (family Rogue, CF_ROGUE_GARROTE bit 8): the tick amount gets <c>attack power * 0.03</c>, no combo points.</item>
/// </list>
/// vmangos computes the term once, when the aura is created (HandlePeriodicDamage calls CalculateDotDamage on apply and every later
/// tick reads <c>m_modifier.m_amount</c>, :5861-5866), from the caster's total melee attack power (<c>GetTotalAttackPowerValue(BASE_ATTACK)</c>)
/// and the combo points the finisher still holds. The term is added after the EffectPointsPerComboPoint scaling, as in the reference.
/// Only player casters get the Rupture term (<c>caster->IsPlayer()</c>); Garrote's applies to any caster with attack power fields.
/// Register it after <see cref="ComboPointService.Install"/> so the combo value scaling comes first.
/// </summary>
public sealed class RogueBleedScripts : ISpellValueModifier
{
    /// <summary>vmangos SPELLFAMILY_ROGUE.</summary>
    public const uint RogueFamily = 8;

    /// <summary>vmangos CF_ROGUE_RUPTURE (SpellClassMask.h:227).</summary>
    public const int RuptureFlagBit = 20;

    /// <summary>vmangos CF_ROGUE_GARROTE (SpellClassMask.h:215).</summary>
    public const int GarroteFlagBit = 8;

    /// <summary>Only the first three combo points add attack power to Rupture (SpellAuras.cpp:4372-4374).</summary>
    public const int MaxRuptureComboPoints = 3;

    /// <summary>Garrote's share of the attack power per tick (SpellAuras.cpp:4381).</summary>
    public const float GarroteAttackPowerFactor = 0.03f;

    private readonly ComboPointService _combos;

    private RogueBleedScripts(ComboPointService combos) => _combos = combos;

    /// <summary>Install the scripts on <paramref name="spells"/>, which must already have the combo point service installed.</summary>
    public static RogueBleedScripts Install(SpellSystem spells, ComboPointService combos)
    {
        ArgumentNullException.ThrowIfNull(spells);
        ArgumentNullException.ThrowIfNull(combos);
        var scripts = new RogueBleedScripts(combos);
        spells.RegisterValueModifier(scripts);
        return scripts;
    }

    public static bool IsRupture(SpellInfo spell) => spell.IsFitToFamily(RogueFamily, RuptureFlagBit);

    public static bool IsGarrote(SpellInfo spell) => spell.IsFitToFamily(RogueFamily, GarroteFlagBit);

    /// <summary>Unit::GetTotalAttackPowerValue(BASE_ATTACK) (Unit.cpp:8037-8061) from the unit's fields.</summary>
    internal static float MeleeAttackPower(Unit unit) => StatFormulas.TotalAttackPower(
        unit.GetInt32(UpdateFields.UnitFieldAttackPower),
        (short)unit.GetUInt16(UpdateFields.UnitFieldAttackPowerMods, 0),
        (short)unit.GetUInt16(UpdateFields.UnitFieldAttackPowerMods, 1),
        unit.GetFloat(UpdateFields.UnitFieldAttackPowerMultiplier));

    public int Modify(SpellValueKind kind, in SpellValueContext context, int value)
    {
        SpellInfo spell = context.Spell;
        if (kind != SpellValueKind.EffectValue || context.EffectIndex < 0 || context.EffectIndex >= spell.Effects.Count
            || spell.Effects[context.EffectIndex].AuraType != AuraType.PeriodicDamage)
        {
            return value;
        }

        if (IsRupture(spell))
        {
            if (context.Caster is not Player player)
            {
                return value;
            }

            int combo = Math.Min(_combos.GetComboPoints(player), MaxRuptureComboPoints);
            return (int)(value + (MeleeAttackPower(player) * combo / 100));
        }

        return IsGarrote(spell) ? (int)(value + (MeleeAttackPower(context.Caster) * GarroteAttackPowerFactor)) : value;
    }
}
