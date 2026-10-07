using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Casters.Bonus;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Stats;

namespace ArcaneCore.Game.Combat;

/// <summary>
/// The done and taken bonuses of weapon-based damage: a white swing, or a weapon damage spell (vmangos <c>SpellCaster::MeleeDamageBonusDone</c>,
/// SpellCaster.cpp:1295-1455, and <c>Unit::MeleeDamageBonusTaken</c>, Unit.cpp:5676-5749, for <c>isWeaponDamageBasedSpell</c>: no SCHOOL_DAMAGE
/// effect). The weapon damage fields already hold MOD_DAMAGE_DONE and MOD_DAMAGE_PERCENT_DONE (<see cref="PlayerStatSystem"/>), so they are not
/// counted again here.
/// <list type="bullet">
/// <item>Done, flat: the attacker's MOD_DAMAGE_DONE_CREATURE (59) for the victim's creature type, plus attack power converted at
/// <c>AP / 14 * GetAPMultiplier</c> from the victim's MELEE_ATTACK_POWER_ATTACKER_BONUS (165) / RANGED_ATTACK_POWER_ATTACKER_BONUS (127, Hunter's
/// Mark) and the attacker's MOD_MELEE_ATTACK_POWER_VERSUS (102) / MOD_RANGED_ATTACK_POWER_VERSUS (131, the slaying auras); that flat part is
/// scaled by the hand's TOTAL_PCT.</item>
/// <item>Done, percent: the attacker's MOD_DAMAGE_DONE_VERSUS (168) for the victim's creature type.</item>
/// <item>Taken: the victim's MOD_MELEE_DAMAGE_TAKEN (125) / MOD_RANGED_DAMAGE_TAKEN (113) and MOD_DAMAGE_TAKEN (14) for the damage school as a flat
/// amount, then MOD_DAMAGE_PERCENT_TAKEN (87) for the school and MOD_MELEE_DAMAGE_TAKEN_PCT (126) / MOD_RANGED_DAMAGE_TAKEN_PCT (114).</item>
/// </list>
/// Not modelled: the hunter pet happiness factor (pets keep their own damage code). World thread only; reads the live aura lists.
/// </summary>
public static class MeleeDamageBonus
{
    private const uint SchoolMaskNormal = 1;
    private const uint SealOfCommandDamage = 20424;
    private const uint PaladinFamily = 10;

    /// <summary>
    /// MeleeDamageBonusDone for weapon-based damage. <paramref name="normalized"/>: the attack power part uses the normalized weapon speed
    /// (NORMALIZED_WEAPON_DMG). <paramref name="spell"/>: the weapon spell, null for a white swing (IGNORE_CASTER_MODIFIERS skips the done part).
    /// </summary>
    public static float Done(SpellSystem spells, Unit attacker, Unit victim, float damage, WeaponAttackType attackType, bool normalized = false, SpellInfo? spell = null)
    {
        ArgumentNullException.ThrowIfNull(spells);
        ArgumentNullException.ThrowIfNull(attacker);
        ArgumentNullException.ThrowIfNull(victim);
        if (damage == 0 || (spell is not null && SpellBonusFormulas.IgnoresCasterModifiers(spell)))
        {
            return damage;
        }

        uint typeMask = victim.CreatureTypeMask();
        bool ranged = attackType == WeaponAttackType.RangedAttack;
        float doneFlat = spells.GetTotalAuraModifier(attacker, AuraType.ModDamageDoneCreature, a => ((uint)a.MiscValue & typeMask) != 0);
        float attackPowerBonus = ranged
            ? spells.GetTotalAuraModifier(victim, AuraType.RangedAttackPowerAttackerBonus)
                + spells.GetTotalAuraModifier(attacker, AuraType.ModRangedAttackPowerVersus, a => ((uint)a.MiscValue & typeMask) != 0)
            : spells.GetTotalAuraModifier(victim, AuraType.MeleeAttackPowerAttackerBonus)
                + spells.GetTotalAuraModifier(attacker, AuraType.ModMeleeAttackPowerVersus, a => ((uint)a.MiscValue & typeMask) != 0);
        float donePercent = Multiplier(spells, attacker, AuraType.ModDamageDoneVersus, typeMask);

        float doneTotal = 0f;
        if (attackPowerBonus != 0 || doneFlat != 0)
        {
            float multiplier = normalized ? spells.NormalizedWeaponSpeed(attacker, attackType) : attacker.Combat.GetAttackTime(attackType) / 1000.0f;
            doneTotal = (attackPowerBonus / 14.0f * multiplier) + doneFlat;

            // "for weapon damage based spells we still have to apply damage done percent mods (that are already included into pdamage) to
            // not-yet included DoneFlat": the hand's TOTAL_PCT.
            doneTotal *= HandTotalPct(attacker, attackType);
        }

        float total = (damage + doneTotal) * donePercent;
        return total > 0 ? total : 0;
    }

    /// <summary>
    /// MeleeDamageBonusTaken for weapon-based damage of <paramref name="schoolMask"/>. <paramref name="spell"/>: the weapon spell (null for a white
    /// swing); IGNORE_DAMAGE_TAKEN_MODIFIERS, Seal of Command's damage and the paladin melee class spells skip the taken part.
    /// </summary>
    public static float Taken(SpellSystem spells, Unit attacker, Unit victim, float damage, WeaponAttackType attackType, uint schoolMask, SpellInfo? spell = null)
    {
        ArgumentNullException.ThrowIfNull(spells);
        ArgumentNullException.ThrowIfNull(attacker);
        ArgumentNullException.ThrowIfNull(victim);
        if (damage == 0)
        {
            return damage;
        }

        if (spell is not null && (SpellBonusFormulas.IgnoresDamageTakenModifiers(spell) || spell.Id == SealOfCommandDamage
            || (spell.SpellFamilyName == PaladinFamily && spell.DamageClass == SpellDamageClass.Melee)))
        {
            return damage;
        }

        bool ranged = attackType == WeaponAttackType.RangedAttack;
        float takenFlat = spells.GetTotalAuraModifier(victim, ranged ? AuraType.ModRangedDamageTaken : AuraType.ModMeleeDamageTaken);
        int schoolTaken = spells.GetTotalAuraModifier(victim, AuraType.ModDamageTaken, a => ((uint)a.MiscValue & schoolMask) != 0);
        if (spell is null && (schoolMask & SchoolMaskNormal) == 0 && schoolTaken < 0 && -schoolTaken > damage / 2)
        {
            schoolTaken = -(int)(damage / 2); // "dampen magic does not reduce magic melee damage below half"
        }

        takenFlat += schoolTaken;
        float takenPercent = Multiplier(spells, victim, AuraType.ModDamagePercentTaken, schoolMask)
            * Multiplier(spells, victim, ranged ? AuraType.ModRangedDamageTakenPct : AuraType.ModMeleeDamageTakenPct, null);
        float total = (damage + takenFlat) * takenPercent;
        return total > 0 ? total : 0;
    }

    /// <summary>
    /// GetModifierValue(UNIT_MOD_DAMAGE_hand, TOTAL_PCT): a player's ledger factor times the weapon-restricted MOD_DAMAGE_PERCENT_DONE of the hand's
    /// usable weapon; another unit's ledger factor (the group default when it has none).
    /// </summary>
    public static float HandTotalPct(Unit unit, WeaponAttackType attackType)
    {
        ArgumentNullException.ThrowIfNull(unit);
        UnitMods group = attackType switch
        {
            WeaponAttackType.OffAttack => UnitMods.DamageOffHand,
            WeaponAttackType.RangedAttack => UnitMods.DamageRanged,
            _ => UnitMods.DamageMainHand,
        };

        if (unit is Player player)
        {
            bool wandUser = player.Class is Class.Priest or Class.Mage or Class.Warlock;
            return player.StatState.Mods.TotalPct(group)
                * player.StatState.Auras.WeaponDamage(PlayerStatSystem.GetWeaponForAttack(player, attackType, nonBroken: true, useable: true), percent: true, wandUser);
        }

        return UnitModLedger.Find(unit)?.TotalPct(group) ?? UnitModConstants.Default(UnitModifierType.TotalPct, group);
    }

    /// <summary>vmangos GetTotalAuraMultiplier / GetTotalAuraMultiplierByMiscMask: the product of <c>(100 + amount) / 100</c>.</summary>
    private static float Multiplier(SpellSystem spells, Unit unit, AuraType type, uint? mask)
    {
        float multiplier = 1.0f;
        foreach (SpellAuraHolder holder in spells.GetAuras(unit))
        {
            if (holder.IsRemoved)
            {
                continue;
            }

            foreach (SpellAura? aura in holder.Auras)
            {
                if (aura is not null && aura.Type == type && (mask is not { } m || ((uint)aura.MiscValue & m) != 0))
                {
                    multiplier *= (100.0f + aura.Amount) / 100.0f;
                }
            }
        }

        return multiplier;
    }
}
