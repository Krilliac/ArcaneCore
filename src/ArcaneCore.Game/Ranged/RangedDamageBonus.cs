using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;

namespace ArcaneCore.Game.Ranged;

/// <summary>
/// The ranged half of the weapon-damage spell bonuses (ranged (autorepeat lane); vmangos Unit::MeleeDamageBonusDone and
/// MeleeDamageBonusTaken for <c>attType == RANGED_ATTACK</c> of a weapon-damage-based spell):
/// <list type="bullet">
/// <item>RANGED_ATTACK_POWER_ATTACKER_BONUS (aura 127; Hunter's Mark ranks 20, 45, 75, 110, Expose Weakness 450) on the victim
/// and MOD_RANGED_ATTACK_POWER_VERSUS (aura 131; the "X slaying" auras, matched against the victim's creature type mask) on the
/// attacker add attack power to the spell: <c>APbonus / 14 * GetAPMultiplier</c>, where the multiplier is the weapon's UNHASTED speed
/// in seconds, or 2.8 for normalized weapon damage (SpellCaster.cpp:1340-1346, 1411-1437, 1826-1853).</item>
/// <item>MOD_RANGED_DAMAGE_TAKEN (aura 113; Elune's Grace) on the victim is a flat amount added to the damage after the done bonuses
/// (Unit.cpp:5703, 5728).</item>
/// </list>
/// Limits: the melee analogues (aura 126, 130, 112), the weapon-class MOD_DAMAGE_DONE flat part and the UNIT_MOD_DAMAGE_RANGED TOTAL_PCT multiplier
/// of the done bonus belong to the aura-engine lane and the stat ledger and are not applied here.
/// </summary>
public static class RangedDamageBonus
{
    /// <summary>Total attack power bonus from the victim's and the attacker's ranged attack power auras.</summary>
    public static int AttackPowerBonus(SpellSystem system, Unit attacker, Unit victim)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(attacker);
        ArgumentNullException.ThrowIfNull(victim);
        uint typeMask = victim.CreatureTypeMask();
        return system.GetTotalAuraModifier(victim, AuraType.RangedAttackPowerAttackerBonus)
            + system.GetTotalAuraModifier(attacker, AuraType.ModRangedAttackPowerVersus, a => ((uint)a.MiscValue & typeMask) != 0);
    }

    /// <summary>
    /// The damage a ranged weapon spell gains from <see cref="AttackPowerBonus"/> (positive or negative) and the victim's flat ranged
    /// damage taken, to add to the weapon roll; 0 when nothing applies.
    /// </summary>
    public static float FlatBonus(SpellSystem system, Unit attacker, Unit victim, bool normalized)
    {
        ArgumentNullException.ThrowIfNull(system);
        float total = 0f;
        int apBonus = AttackPowerBonus(system, attacker, victim);
        if (apBonus != 0)
        {
            float multiplier = normalized
                ? system.NormalizedWeaponSpeed(attacker, WeaponAttackType.RangedAttack)
                : attacker.Combat.GetUnhastedTime(WeaponAttackType.RangedAttack) / 1000.0f;
            total += apBonus / 14.0f * multiplier;
        }

        return total + system.GetTotalAuraModifier(victim, AuraType.ModRangedDamageTaken);
    }
}
