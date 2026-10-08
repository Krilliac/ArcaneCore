using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Procs;
using ArcaneCore.Game.Spells.Rules;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// Spell reflection (vmangos SpellCaster::SpellHitResult, Objects/SpellCaster.cpp:197-212; SpellEntry::IsReflectableSpell, Spells/SpellEntry.cpp:1025-1030):
/// the victim's REFLECT_SPELLS amount plus every REFLECT_SPELLS_SCHOOL aura whose school mask covers the spell is a percent chance to turn a
/// reflectable spell back. A reflect first runs the victim's procs with TAKE_HARMFUL_SPELL and PROC_EX_REFLECT, which spends the charges of the
/// reflect auras (Shield Reflection, Frost Reflector: their REFLECT_SPELLS_SCHOOL proc handler accepts only a spell of their school).
/// </summary>
public sealed partial class SpellSystem
{
    /// <summary>SPELL_ATTR_IS_ABILITY (Attributes bit 4), SPELL_ATTR_NO_IMMUNITIES (bit 29), SPELL_ATTR_EX_NO_REFLECTION (AttributesEx bit 7).</summary>
    private const uint AttrIsAbility = 0x00000010;
    private const uint AttrNoImmunities = 0x20000000;
    private const uint AttrExNoReflection = 0x00000080;

    private int _noReflectDepth;

    /// <summary>
    /// The hit roll of a spell that cannot be reflected whatever its data says (vmangos SpellHitResult(..., CanReflect = false)): proc damage
    /// (UnitAuraProcHandler.cpp:1635) and damage shields (Unit.cpp:1795).
    /// </summary>
    public SpellMissInfo RollHitWithoutReflect(Unit caster, Unit victim, SpellInfo spell)
    {
        _noReflectDepth++;
        try
        {
            return CombatRules.RollHit(this, caster, victim, spell);
        }
        finally
        {
            _noReflectDepth--;
        }
    }

    /// <summary>vmangos SpellEntry::IsReflectableSpell(caster, victim): a harmful magic spell that is no ability, passive, unreflectable or immunity-piercing spell.</summary>
    public static bool IsReflectableSpell(SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        return spell.DamageClass == SpellDamageClass.Magic && ((uint)spell.Attributes & (AttrIsAbility | AttrNoImmunities)) == 0
            && ((uint)spell.AttributesEx & AttrExNoReflection) == 0 && !spell.IsPassive && !spell.IsPositive;
    }

    /// <summary>The reflect chance in percent of <paramref name="victim"/> against <paramref name="spell"/> (REFLECT_SPELLS + matching REFLECT_SPELLS_SCHOOL).</summary>
    public int ReflectChance(Unit victim, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(victim);
        ArgumentNullException.ThrowIfNull(spell);
        uint school = spell.SchoolMask();
        return GetTotalAuraModifier(victim, AuraType.ReflectSpells)
            + GetTotalAuraModifier(victim, AuraType.ReflectSpellsSchool, aura => ((uint)aura.MiscValue & school) != 0);
    }

    /// <summary>
    /// The reflect step of the hit roll (vmangos SpellHitResult with CanReflect, after the immunities): a reflectable spell from another unit rolls
    /// <see cref="ReflectChance"/>; a reflect runs the victim's reflect procs and returns true (the hit result is then <see cref="SpellMissInfo.Reflect"/>).
    /// </summary>
    public bool RollSpellReflect(Unit caster, Unit victim, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(victim);
        ArgumentNullException.ThrowIfNull(spell);
        if (_noReflectDepth > 0 || ReferenceEquals(caster, victim) || !IsReflectableSpell(spell))
        {
            return false;
        }

        int chance = ReflectChance(victim, spell);
        if (chance <= 0 || Random.Next(0, 100) >= chance)
        {
            return false;
        }

        // "Start triggers for remove charges if need (trigger only for victim, and mark as active spell)".
        ProcDamageAndSpell(caster, new ProcEvent
        {
            Victim = victim,
            VictimFlags = ProcFlags.TakeHarmfulSpell,
            Extra = ProcFlagsEx.Reflect,
            Amount = 1,
            OriginalAmount = 1,
            ProcSpell = spell,
        });
        return true;
    }
}
