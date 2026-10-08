using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Rules;

namespace ArcaneCore.Game.Spells.Procs.Talents;

/// <summary>
/// Eye for an Eye (vmangos <c>Unit::HandleDummyAuraProc</c>, UnitAuraProcHandler.cpp:577-593, the branch after 1.8.4): the paladin talent's
/// DUMMY aura returns its percent of a critical magic spell's damage before absorbs and resists to the caster, at most half of the owner's maximum
/// health, as 25997. The "critical harmful spell only" check is the hard-coded one of <see cref="SpellSystem"/>'s trigger check; a weapon special
/// attack never reflects ("prevent damage back from weapon special attacks").
/// </summary>
public sealed class EyeForAnEyeProc : IProcScript
{
    public static readonly uint[] Ranks = [9799, 25988];
    public const uint DamageSpell = 25997;

    public AuraProcResult? OnProc(in AuraProcContext context)
    {
        if (context.Aura.Type != AuraType.Dummy)
        {
            return null;
        }

        if (context.ProcSpell is not { DamageClass: SpellDamageClass.Magic } || context.Target is not { } attacker
            || context.System.Store.Get(DamageSpell) is not { } damage)
        {
            return AuraProcResult.Failed;
        }

        int basePoints = TalentProcSupport.Dither(context.Aura.Amount * (int)context.OriginalAmount / 100f, context.System.Random);
        basePoints = Math.Min(basePoints, (int)context.Owner.MaxHealth / 2);
        return context.System.TriggerProccedSpell(context.Owner, attacker, damage, context.Holder, context.CooldownMs, TalentProcSupport.BasePoints(basePoints));
    }
}

/// <summary>
/// Sweeping Strikes (12292, and the creature version 18765; vmangos UnitAuraProcHandler.cpp:594-656): a melee hit that dealt damage (an amount of
/// 1 is a hit that dealt none: "rend does not trigger sweeping strikes") repeats its damage before the victim's armor on a random other unfriendly
/// unit within 5 yards of the warrior (8 for Whirlwind), as 12723. 1.7.0: dead units and, for a warrior not PvP-flagged, PvP-flagged players are
/// skipped. 1.10.0 Execute: below 20% on both, the full Execute damage; only the main target below 20%, an ordinary swing (26654). The strike
/// itself never chains (12723 and 26654 cannot proc it again). A charge goes with each strike.
/// </summary>
public sealed class SweepingStrikesProc : IProcScript
{
    public static readonly uint[] Ranks = [12292, 18765];
    public const uint DamageSpell = 12723;
    public const uint SwingSpell = 26654;
    public const uint Whirlwind = 1680;
    public const uint Execute = 20647;
    public const float WhirlwindRadius = 8f;

    public AuraProcResult? OnProc(in AuraProcContext context)
    {
        if (context.Aura.Type != AuraType.Dummy)
        {
            return null;
        }

        if (context.Target is not { } victim || context.Amount <= 1 || context.ProcSpell?.Id is DamageSpell or SwingSpell)
        {
            return AuraProcResult.Failed;
        }

        SpellSystem system = context.System;
        float radius = context.ProcSpell?.Id == Whirlwind ? WhirlwindRadius : CombatConstants.AttackDistance;
        if (TalentProcSupport.SelectRandomUnfriendlyTarget(system, context.Owner, victim, radius, validAttackTarget: true, notPvpEnabling: true) is not { } target)
        {
            return AuraProcResult.Failed;
        }

        uint triggerId = DamageSpell;
        int? basePoints = TalentProcSupport.BasePoints(TalentProcSupport.DamageBeforeArmor(system, context.Owner, victim, context.Amount));
        if (context.ProcSpell?.Id == Execute && TalentProcSupport.HealthPercent(victim) <= 20f && TalentProcSupport.HealthPercent(target) > 20f)
        {
            triggerId = SwingSpell;
            basePoints = null;
        }

        return system.Store.Get(triggerId) is { } trigger
            ? system.TriggerProccedSpell(context.Owner, target, trigger, context.Holder, context.CooldownMs, basePoints)
            : AuraProcResult.Failed;
    }
}

/// <summary>
/// Retaliation (20230; vmangos UnitAuraProcHandler.cpp:660-675): a melee swing that lands from in front of the warrior is answered with 22858 at
/// the attacker; 1.7.0: "retaliatory strikes will not be possible while stunned" (UNIT_STATE_CAN_NOT_REACT: stunned, confused, fleeing or feigning
/// death). Each strike costs one of the 30 charges.
/// </summary>
public sealed class RetaliationProc : IProcScript
{
    public const uint Spell = 20230;
    public const uint StrikeSpell = 22858;

    private const UnitFlags CannotReact = UnitFlags.Stunned | UnitFlags.Confused | UnitFlags.Fleeing;

    public AuraProcResult? OnProc(in AuraProcContext context)
    {
        if (context.Aura.Type != AuraType.Dummy)
        {
            return null;
        }

        Unit owner = context.Owner;
        if (context.Target is not { } attacker || !MapCombat.HasInArc(owner, attacker, CombatConstants.DefaultArc)
            || (owner.UnitFlags & CannotReact) != 0 || context.System.IsFeigningDeath(owner)
            || context.System.Store.Get(StrikeSpell) is not { } strike)
        {
            return AuraProcResult.Failed;
        }

        return context.System.TriggerProccedSpell(owner, attacker, strike, context.Holder, context.CooldownMs);
    }
}

/// <summary>
/// Magic Absorption (mage, SpellIconID 459; vmangos UnitAuraProcHandler.cpp:832-845, "only this spell have SpellIconID == 459 and dummy aura"): a
/// mana user gains its percent of maximum mana (29442). The resist that triggers it is the spell_proc_event row's condition (29441, procEx
/// PROC_EX_RESIST, cooldown 1 s; mangos-classic mangos.sql:13839). Spell.dbc alone (procFlags TAKE_HARMFUL_SPELL, chance 100) would let every
/// harmful spell that lands proc the DUMMY effect, so without a row the script requires the resist itself; since the engine's no-row check only
/// passes hits, the talent then never procs (docs/areas/procs.md).
/// </summary>
public sealed class MagicAbsorptionProc : IProcScript
{
    public const uint Icon = 459;
    public const uint ManaSpell = 29442;

    public AuraProcResult? OnProc(in AuraProcContext context)
    {
        if (context.Aura.Type != AuraType.Dummy)
        {
            return null;
        }

        Unit owner = context.Owner;
        if (owner.PowerType != PowerType.Mana || context.System.Store.Get(ManaSpell) is not { } mana)
        {
            return AuraProcResult.Failed;
        }

        if (context.System.ProcEvents.Find(context.Holder.Spell.Id) is null && (context.ProcExtra & ProcFlagsEx.Resist) == 0)
        {
            return AuraProcResult.Failed;
        }

        uint maxMana = owner.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana);
        int basePoints = TalentProcSupport.Dither(context.Aura.Amount * maxMana / 100f, context.System.Random);
        return context.System.TriggerProccedSpell(owner, owner, mana, context.Holder, context.CooldownMs, TalentProcSupport.BasePoints(basePoints));
    }
}

/// <summary>
/// Master of Elements (mage, SpellIconID 1920; vmangos UnitAuraProcHandler.cpp:849-862): the talent's percent of the spell's base mana cost
/// (<c>manaCost + ManaCostPercentage x create mana / 100</c>, before modifiers) comes back as 29077; nothing for a free spell. vmangos leaves the
/// "fire or frost critical strike" condition to spell_proc_event (SchoolMask fire|frost, PROC_EX_CRITICAL_HIT); without a row for the talent the
/// script applies that condition itself, so the talent does not refund every hit of every school (docs/areas/procs.md).
/// </summary>
public sealed class MasterOfElementsProc : IProcScript
{
    public const uint Icon = 1920;
    public const uint ManaSpell = 29077;
    public const uint FireFrostMask = (1u << (int)SpellSchool.Fire) | (1u << (int)SpellSchool.Frost);

    public AuraProcResult? OnProc(in AuraProcContext context)
    {
        if (context.Aura.Type != AuraType.Dummy)
        {
            return null;
        }

        SpellSystem system = context.System;
        if (context.ProcSpell is not { } spell || system.Store.Get(ManaSpell) is not { } mana)
        {
            return AuraProcResult.Failed;
        }

        if (system.ProcEvents.Find(context.Holder.Spell.Id) is null
            && ((context.ProcExtra & ProcFlagsEx.CriticalHit) == 0 || (spell.SchoolMask() & FireFrostMask) == 0))
        {
            return AuraProcResult.Failed;
        }

        int cost = (int)spell.ManaCost + (int)(spell.ManaCostPercentage * context.Owner.GetUInt32(UpdateFields.UnitFieldBaseMana) / 100);
        int basePoints = TalentProcSupport.Dither(cost * context.Aura.Amount / 100f, system.Random);
        return basePoints <= 0
            ? AuraProcResult.Failed
            : system.TriggerProccedSpell(context.Owner, context.Owner, mana, context.Holder, context.CooldownMs, basePoints);
    }
}

/// <summary>
/// Vampiric Embrace (15286; vmangos UnitAuraProcHandler.cpp:875-893): the debuff's owner is the victim; damage its own caster deals to it makes
/// that caster cast 15290 (the party heal) for the debuff's percent of the damage, at least 1, triggered by the debuff, with no hidden cooldown.
/// vmangos leaves "shadow damage" to spell_proc_event (SchoolMask shadow); without a row the script requires a shadow spell itself.
/// </summary>
public sealed class VampiricEmbraceProc : IProcScript
{
    public const uint Spell = 15286;
    public const uint HealSpell = 15290;
    public const uint ShadowMask = 1u << (int)SpellSchool.Shadow;

    public AuraProcResult? OnProc(in AuraProcContext context)
    {
        if (context.Aura.Type != AuraType.Dummy)
        {
            return null;
        }

        SpellSystem system = context.System;
        if (context.Target is not { IsAlive: true } priest || context.Holder.CasterGuid != priest.Guid || system.Store.Get(HealSpell) is not { } heal)
        {
            return AuraProcResult.Failed;
        }

        if (system.ProcEvents.Find(Spell) is null && (context.ProcSpell is not { } spell || (spell.SchoolMask() & ShadowMask) == 0))
        {
            return AuraProcResult.Failed;
        }

        int basePoints = Math.Max(TalentProcSupport.Dither(context.Aura.Amount * context.Amount / 100f, system.Random), 1); // "don't heal for 0"
        system.CastProcSpell(priest, heal, SpellCastTargets.ForUnit(priest.Guid), context.Holder.Spell, basePoints);
        return AuraProcResult.Ok;
    }
}

/// <summary>
/// Blade Flurry (13877; vmangos UnitAuraProcHandler.cpp:951-971): a melee hit repeats its damage before the victim's armor on a random other
/// unfriendly unit within 5 yards of the rogue, as 22482 (which never chains).
/// </summary>
public sealed class BladeFlurryProc : IProcScript
{
    public const uint Spell = 13877;
    public const uint DamageSpell = 22482;
    public const float Radius = 5f;

    public AuraProcResult? OnProc(in AuraProcContext context)
    {
        if (context.Aura.Type != AuraType.Dummy)
        {
            return null;
        }

        SpellSystem system = context.System;
        if (context.Target is not { } victim || context.ProcSpell?.Id == DamageSpell || system.Store.Get(DamageSpell) is not { } damage
            || TalentProcSupport.SelectRandomUnfriendlyTarget(system, context.Owner, victim, Radius, validAttackTarget: true, notPvpEnabling: false) is not { } target)
        {
            return AuraProcResult.Failed;
        }

        int basePoints = TalentProcSupport.DamageBeforeArmor(system, context.Owner, victim, context.Amount);
        return system.TriggerProccedSpell(context.Owner, target, damage, context.Holder, context.CooldownMs, TalentProcSupport.BasePoints(basePoints));
    }
}
