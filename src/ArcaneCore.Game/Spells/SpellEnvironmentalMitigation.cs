using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Spells.Rules.Immunity;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The spell system's answers for environmental and fall damage (<see cref="IEnvironmentalDamageMitigation"/>,
/// <see cref="IFallDamageModifiers"/>). Player::EnvironmentalDamage (vmangos Player.cpp:713-758) asks
/// <c>IsImmuneToDamage(mask)</c> with no spell and then, for lava, fire and slime, <c>CalculateDamageAbsorbAndResist(this, mask,
/// DIRECT_DAMAGE, damage, .., nullptr)</c>: the player is its own caster, so its resistance is reduced by its own MOD_TARGET_RESISTANCE
/// auras and no level difference applies. Player::HandleFall multiplies the fall by the physical MOD_DAMAGE_PERCENT_TAKEN auras
/// (Player.cpp:20854-20855). Every answer reads the live auras and the installed <see cref="SpellSystem.CombatRules"/> when asked.
/// World thread only.
/// </summary>
public sealed class SpellEnvironmentalMitigation(SpellSystem system) : IEnvironmentalDamageMitigation, IFallDamageModifiers
{
    private readonly SpellSystem _system = system ?? throw new ArgumentNullException(nameof(system));

    /// <summary>vmangos Unit::IsImmuneToDamage with no spell (Unit.cpp:5404-5439): damage immunity, or a school immunity that applies to a harmful spell.</summary>
    public bool IsImmune(Player player, EnvironmentalSchool school)
    {
        ArgumentNullException.ThrowIfNull(player);
        return ImmunityRules.IsImmuneToDamage(_system, player, MaskOf(school), spell: null);
    }

    /// <summary>
    /// vmangos Unit::CalculateDamageAbsorbAndResist (Unit.cpp:1920-2202) with no spell: a unit immune to the school by any school
    /// immunity absorbs all of it (:1929-1933); otherwise the resist roll of the installed rules (<see cref="ISchoolResistRoll"/>; none
    /// when the rules do not offer it), then the absorbs of <see cref="SpellSystem.AbsorbDamage"/> on what the resist leaves.
    /// </summary>
    public EnvironmentalMitigation Calculate(Player player, EnvironmentalSchool school, uint damage)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (damage == 0 || !player.IsAlive)
        {
            return default;
        }

        uint mask = MaskOf(school);
        if (ImmunityRules.IsImmuneToSchoolMask(_system, player, mask))
        {
            return new EnvironmentalMitigation(damage, 0);
        }

        int resist = _system.CombatRules is ISchoolResistRoll roll ? roll.RollResist(_system, player, player, SchoolOf(school), damage) : 0;
        // A player has no negative create resistance, so the roll is never negative today; the branch stays for the shared signed-roll contract.
        uint remaining = resist < 0 ? damage + (uint)-resist : damage - Math.Min(damage, (uint)resist);
        uint absorb = _system.AbsorbDamage(player, player, mask, remaining, spell: null);
        return new EnvironmentalMitigation(absorb, resist);
    }

    /// <summary>vmangos GetTotalAuraMultiplierByMiscMask(SPELL_AURA_MOD_DAMAGE_PERCENT_TAKEN, SPELL_SCHOOL_MASK_NORMAL) (Unit.cpp:3060-3075).</summary>
    public float TakenDamageMultiplier(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        uint physical = SpellSchoolMasks.Of(SpellSchool.Normal);
        float multiplier = 1.0f;
        foreach (SpellAuraHolder holder in _system.GetAuras(player))
        {
            if (holder.IsRemoved)
            {
                continue;
            }

            foreach (SpellAura? aura in holder.AuraSpan)
            {
                if (aura is not null && aura.Type == AuraType.ModDamagePercentTaken && ((uint)aura.MiscValue & physical) != 0)
                {
                    multiplier *= (100.0f + aura.Amount) / 100.0f;
                }
            }
        }

        return multiplier;
    }

    private static SpellSchool SchoolOf(EnvironmentalSchool school) => school switch
    {
        EnvironmentalSchool.Fire => SpellSchool.Fire,
        EnvironmentalSchool.Nature => SpellSchool.Nature,
        _ => SpellSchool.Normal,
    };

    private static uint MaskOf(EnvironmentalSchool school) => SpellSchoolMasks.Of(SchoolOf(school));
}
