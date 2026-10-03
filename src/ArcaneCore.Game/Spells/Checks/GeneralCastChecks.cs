using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The generic <c>Spell::CheckCast</c> rules that the warrior and rogue abilities hit (vmangos Spell.cpp:5302-5350,
/// 5640-5649), registered through <see cref="ISpellCastCheck"/>: standing (every ability), combat-forbidden spells (Charge),
/// stealth-only spells, and the behind/in-front rules. Rules that need terrain (indoors, outdoors, water), battlegrounds,
/// mounts or taxis, target level limits and the other target flags are not covered.
/// </summary>
public static class GeneralCastChecks
{
    /// <summary>Install the four checks on <paramref name="spells"/>.</summary>
    public static void Install(SpellSystem spells)
    {
        ArgumentNullException.ThrowIfNull(spells);
        spells.RegisterCastCheck(new StandingCastCheck());
        spells.RegisterCastCheck(new CombatRestrictionCastCheck());
        spells.RegisterCastCheck(new StealthCastCheck(spells));
        spells.RegisterCastCheck(new FacingCastCheck());
    }
}

/// <summary>vmangos Spell.cpp:5308-5310: a non-triggered cast needs a standing caster unless the spell has ALLOW_WHILE_SITTING (NOT_STANDING).</summary>
public sealed class StandingCastCheck : ISpellCastCheck
{
    public SpellCheckPhase Phase => SpellCheckPhase.Start;

    public int Order => SpellCastCheckOrder.Standing;

    public SpellCastResult Check(in SpellCastCheckContext context)
        => !context.Triggered && !MapCombat.IsStandingUp(context.Caster) && !context.Spell.HasAttribute(SpellAttributes.AllowWhileSitting)
            ? SpellCastResult.NotStanding
            : SpellCastResult.CastOk;
}

/// <summary>
/// vmangos Spell.cpp:5343-5344: a spell with NOT_IN_COMBAT_ONLY_PEACEFUL (Charge, the stealth openers) cannot be started in
/// combat (AFFECTING_COMBAT); strict, non-triggered casts only.
/// </summary>
public sealed class CombatRestrictionCastCheck : ISpellCastCheck
{
    public SpellCheckPhase Phase => SpellCheckPhase.Caster;

    public int Order => SpellCastCheckOrder.AffectingCombat;

    public SpellCastResult Check(in SpellCastCheckContext context)
        => !context.Triggered && context.Strict && context.Caster.Combat.IsInCombat
            && context.Spell.HasAttribute(SpellAttributesCombat.NotInCombatOnlyPeaceful)
            ? SpellCastResult.AffectingCombat
            : SpellCastResult.CastOk;
}

/// <summary>vmangos Spell.cpp:5353-5354: a spell with ONLY_STEALTHED needs a stealth aura on the caster (ONLY_STEALTHED); strict, non-triggered casts only.</summary>
public sealed class StealthCastCheck(SpellSystem spells) : ISpellCastCheck
{
    /// <summary>SPELL_ATTR_ONLY_STEALTHED (vmangos SpellDefines.h, Attributes bit 17).</summary>
    private const uint OnlyStealthed = 0x00020000;

    public SpellCheckPhase Phase => SpellCheckPhase.Caster;

    public int Order => SpellCastCheckOrder.Stealth;

    public SpellCastResult Check(in SpellCastCheckContext context)
    {
        if (context.Triggered || !context.Strict || ((uint)context.Spell.Attributes & OnlyStealthed) == 0)
        {
            return SpellCastResult.CastOk;
        }

        bool stealthed = spells.GetAuras(context.Caster).Any(h => !h.IsRemoved && h.HasAura(AuraType.ModStealth));
        return stealthed ? SpellCastResult.CastOk : SpellCastResult.OnlyStealthed;
    }
}

/// <summary>
/// vmangos Spell.cpp:5640-5649: <see cref="IsFromBehindOnly"/> spells (Backstab, Ambush) need the caster behind the target
/// (NOT_BEHIND; a creature that fights the caster and is not incapacitated always faces it on the strict check), and a spell
/// whose Attributes are exactly 0x150010 needs the target to face the caster (NOT_INFRONT).
/// </summary>
public sealed class FacingCastCheck : ISpellCastCheck
{
    private const uint FromBehindEx2 = 0x00100000;
    private const uint FromBehindEx = 0x00000200;
    private const uint FaceCasterAttributes = 0x00150010;

    public SpellCheckPhase Phase => SpellCheckPhase.Target;

    public int Order => SpellCastCheckOrder.Facing;

    /// <summary>vmangos SpellEntry::IsFromBehindOnlySpell without the database "behind target" custom flag (SpellEntry.h:909-912).</summary>
    public static bool IsFromBehindOnly(SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        return (uint)spell.AttributesEx2 == FromBehindEx2 && ((uint)spell.AttributesEx & FromBehindEx) == FromBehindEx;
    }

    public SpellCastResult Check(in SpellCastCheckContext context)
    {
        if (context.Target is not { } target || ReferenceEquals(target, context.Caster))
        {
            return SpellCastResult.CastOk;
        }

        if (IsFromBehindOnly(context.Spell) && !IsBehindTarget(context.Caster, target, context.Strict))
        {
            return SpellCastResult.NotBehind;
        }

        if ((uint)context.Spell.Attributes == FaceCasterAttributes && !MapCombat.HasInArc(target, context.Caster, CombatConstants.DefaultArc))
        {
            return SpellCastResult.NotInfront;
        }

        return SpellCastResult.CastOk;
    }

    /// <summary>vmangos Unit::IsBehindTarget (Unit.cpp:2806-2821).</summary>
    public static bool IsBehindTarget(Unit caster, Unit target, bool strict)
    {
        if (strict && target is ICombatCreature && ReferenceEquals(target.Combat.Victim, caster)
            && (target.UnitFlags & (UnitFlags.Stunned | UnitFlags.Confused | UnitFlags.Fleeing | UnitFlags.Possessed)) == 0)
        {
            return false;
        }

        return !MapCombat.HasInArc(target, caster, CombatConstants.DefaultArc);
    }
}
