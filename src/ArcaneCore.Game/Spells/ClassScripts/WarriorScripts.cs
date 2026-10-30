using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Rogue;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Spells.ClassScripts;

/// <summary>Last Stand (12975; vmangos SpellEffects.cpp:776-783): the caster gains 30% of its maximum health through 12976 (MOD_INCREASE_HEALTH).</summary>
[SpellScript(12975)]
public sealed class LastStandScript : ISpellScript
{
    public const uint LastStandHealth = 12976;

    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex == 0 && context.Effect.Effect == SpellEffectName.Dummy)
        {
            context.System.CastCustomSpell(context.Caster, LastStandHealth, SpellCastTargets.ForSelf(), (int)(context.Caster.MaxHealth * 0.3));
        }
    }
}

/// <summary>
/// Execute, the dummy (5308, 20658, 20660, 20661, 20662; vmangos scripts/spells/spell_warrior.cpp:94-111): the rank's base points plus the caster's
/// rage (in the tenths vmangos stores) times effect 0's damage multiplier, dithered, are dealt by 20647 at the target.
/// </summary>
[SpellScript(5308, 20658, 20660, 20661, 20662)]
public sealed class ExecuteDummyScript : ISpellScript
{
    public const uint ExecuteDamage = 20647;

    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex != 0 || context.Effect.Effect != SpellEffectName.Dummy)
        {
            return;
        }

        SpellSystem system = context.System;
        float rage = SpellSystem.GetPower(context.Caster, PowerType.Rage) * context.Spell.Effects[0].DamageMultiplier;
        int basePoints = (int)context.Spell.SimpleValue(0) + SpellRounding.DitherFloor(rage, system.Random); // rand_dither
        system.CastCustomSpell(context.Caster, ExecuteDamage, SpellCastTargets.ForUnit(context.Target.Guid, context.Cast), basePoints);
    }
}

/// <summary>Execute, the damage (20647; spell_warrior.cpp:76-92): it takes all the caster's rage.</summary>
[SpellScript(ExecuteDummyScript.ExecuteDamage, ExecuteEffects = [SpellEffectName.SchoolDamage])]
public sealed class ExecuteDamageScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex == 0)
        {
            SpellSystem.SetPower(context.Caster, PowerType.Rage, 0);
        }
    }
}

/// <summary>Bloodrage (2687; spell_warrior.cpp:131-147): the warrior enters combat when its rage arrives (the ENERGIZE effect).</summary>
[SpellScript(2687, ExecuteEffects = [SpellEffectName.Energize])]
public sealed class BloodrageScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex == 0 && context.Target.Map is { } map)
        {
            map.Combat.SetInCombatState(context.Target, 0);
        }
    }
}

/// <summary>
/// Deep Wounds (12162, 12850, 12868; vmangos SpellEffects.cpp:742-775): the average weapon damage of the hand that swung last (the off hand when the
/// caster has one and its timer is lower) times 20, 40 or 60 percent, a quarter of it per tick, through Deep Wound (12721) on the target.
/// </summary>
[SpellScript(12162, 12850, 12868)]
public sealed class DeepWoundsScript : ISpellScript
{
    public const uint DeepWound = 12721;

    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex != 0 || context.Effect.Effect != SpellEffectName.Dummy)
        {
            return;
        }

        Unit caster = context.Caster;
        float share = context.Spell.Id switch
        {
            12162 => 0.2f,
            12850 => 0.4f,
            _ => 0.6f,
        };
        bool offhand = caster.GetFloat(UpdateFields.UnitFieldMaxoffhanddamage) > 0
            && caster.Combat.GetAttackTimer(WeaponAttackType.BaseAttack) > caster.Combat.GetAttackTimer(WeaponAttackType.OffAttack);
        float damage = offhand
            ? (caster.GetFloat(UpdateFields.UnitFieldMinoffhanddamage) + caster.GetFloat(UpdateFields.UnitFieldMaxoffhanddamage)) / 2
            : (caster.GetFloat(UpdateFields.UnitFieldMindamage) + caster.GetFloat(UpdateFields.UnitFieldMaxdamage)) / 2;
        context.System.CastCustomSpell(caster, DeepWound, SpellCastTargets.ForUnit(context.Target.Guid, context.Cast), (int)(damage * share / 4));
    }
}

/// <summary>
/// Bloodthirst (23881, 23892, 23893, 23894; spell_warrior.cpp:37-56): effect 0's damage is that many percent of the caster's total melee attack power.
/// With a target, the caster's SPELL_AURA_MOD_MELEE_ATTACK_POWER_VERSUS auras whose creature-type mask matches the target are added to the attack
/// power first (Mark of the Champion, Seal of the Dawn, slaying enchants and the like).
/// </summary>
public sealed class BloodthirstDamage(SpellSystem? system = null) : ISpellValueModifier
{
    public static readonly uint[] Ranks = [23881, 23892, 23893, 23894];

    public int Modify(SpellValueKind kind, in SpellValueContext context, int value)
    {
        if (kind != SpellValueKind.EffectValue || context.EffectIndex != 0 || Array.IndexOf(Ranks, context.Spell.Id) < 0)
        {
            return value;
        }

        float attackPower = RogueBleedScripts.MeleeAttackPower(context.Caster);
        if (system is not null && context.Target is { } target)
        {
            uint typeMask = target.CreatureTypeMask();
            attackPower += system.GetTotalAuraModifier(context.Caster, AuraType.ModMeleeAttackPowerVersus, a => ((uint)a.MiscValue & typeMask) != 0);
        }

        return (int)(value * attackPower / 100);
    }
}

/// <summary>
/// Berserking, the troll racial (20554, 26296, 26297; SPELLFAMILY_GENERIC icon 1661; vmangos SpellEffects.cpp:1440-1458): the haste is 10 percent at
/// full health, 30 at 40 percent health or less and <c>10 + (100 - health percent) / 3</c> between, cast as 26635 on the caster with that value in
/// all three effects, and the caster gets AURA_STATE_BERSERKING ("custom spell required this aura state by some unknown reason").
/// </summary>
[SpellScript(20554, 26296, 26297)]
public sealed class BerserkingScript : ISpellScript
{
    public const uint BerserkingHaste = 26635;

    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex != 0 || context.Effect.Effect != SpellEffectName.Dummy)
        {
            return;
        }

        Unit caster = context.Caster;
        int meleeMod = MeleeMod(caster.MaxHealth == 0 ? 100u : (uint)(caster.Health * 100.0 / caster.MaxHealth));
        context.System.ModifyAuraState(caster, AuraState.Berserking, true);
        context.System.CastCustomSpell(caster, BerserkingHaste, SpellCastTargets.ForSelf(), meleeMod, meleeMod, meleeMod);
    }

    /// <summary>The haste percent for a health percent (uint32 arithmetic, as vmangos).</summary>
    public static int MeleeMod(uint healthPct)
    {
        int meleeMod = 10;
        if (healthPct <= 40)
        {
            meleeMod = 30;
        }

        if (healthPct is < 100 and > 40)
        {
            meleeMod = 10 + (int)((100 - healthPct) / 3);
        }

        return meleeMod;
    }
}

/// <summary>
/// Shield Slam (23922, 23923, 23924, 23925; spell_warrior.cpp:58-74): effect 1's damage adds the caster's shield block value.
/// </summary>
public sealed class ShieldSlamDamage : ISpellValueModifier
{
    public static readonly uint[] Ranks = [23922, 23923, 23924, 23925];

    public int Modify(SpellValueKind kind, in SpellValueContext context, int value)
        => kind == SpellValueKind.EffectValue
            && context.EffectIndex == 1
            && (Array.IndexOf(Ranks, context.Spell.Id) >= 0 || Rules.Dispel.DispelRules.IsShieldSlam(context.Spell))
            ? value + (int)GetShieldBlockValue(context.Caster)
            : value;

    public static uint GetShieldBlockValue(Unit caster)
    {
        if (caster.Map?.FindUpdater<MapCombat>() is { Stats: not null } mapCombat)
        {
            return mapCombat.ShieldBlockValue(caster);
        }

        if (caster is Player player)
        {
            return Stats.StatFormulas.ShieldBlockValue(
                player.StatState.ShieldBlockFlat + player.StatState.ShieldBlockAuraFlat,
                player.GetUInt32(UpdateFields.UnitFieldStat0),
                player.StatState.ShieldBlockPct);
        }

        return caster.Map?.FindUpdater<MapCombat>() is { } combat
            ? combat.ShieldBlockValue(caster)
            : CombatHooks.Default.GetShieldBlockValue(caster);
    }
}

/// <summary>
/// Orc racial Blood Fury (20572; buff 23234, debuff 23230), the 1.12 design (vmangos SpellEffects.cpp:971-985, build above 1.8.4;
/// the spell data of build 5086 gives 20572 a dummy effect of 25 and 23230 a -50% healing aura of 25 seconds): casting 20572 applies
/// the healing reduction 23230 to the caster at once, and the attack power buff 23234 (a flat attack power aura whose own value is 0)
/// is cast with <c>dither(25 * attack power / 100)</c> as its value. Nothing happens when the buff fades.
/// </summary>
[SpellScript(BloodFury)]
public sealed class BloodFuryScript : ISpellScript
{
    public const uint BloodFury = 20572;
    public const uint BloodFuryBuff = 23234;
    public const uint BloodFuryDebuff = 23230;

    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex == 0 && context.Effect.Effect == SpellEffectName.Dummy)
        {
            Trigger(context.System, context.Caster, context.Value);
        }
    }

    internal static void Trigger(SpellSystem system, Unit caster, int percent)
    {
        // Both the dummy effect and the cast observer call this for one cast; the debuff marks the cast as handled.
        if (caster is not Player || system.HasAura(caster, BloodFuryDebuff))
        {
            return;
        }

        if (system.Store.Get(BloodFuryDebuff) is not null)
        {
            system.CastSpell(caster, BloodFuryDebuff, SpellCastTargets.ForUnit(caster.Guid), triggered: true);
        }

        if (system.Store.Get(BloodFuryBuff) is null)
        {
            return;
        }

        int attackPower = caster.GetInt32(UpdateFields.UnitFieldAttackPower);
        int value = SpellRounding.DitherFloor(percent * attackPower / 100f, system.Random);
        if (value > 0)
        {
            system.CastCustomSpell(caster, BloodFuryBuff, SpellCastTargets.ForUnit(caster.Guid), value);
        }
    }
}

/// <summary>
/// Undead racial Cannibalize (20577; periodic heal 20578; vmangos SpellEffects.cpp:1429-1434):
/// the dummy effect triggers 20578 on the caster.
/// </summary>
[SpellScript(Cannibalize)]
public sealed class CannibalizeScript : ISpellScript
{
    public const uint Cannibalize = 20577;
    public const uint CannibalizeHeal = 20578;

    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex == 0 && context.Effect.Effect == SpellEffectName.Dummy)
        {
            context.System.CastSpell(context.Caster, CannibalizeHeal, SpellCastTargets.ForUnit(context.Caster.Guid), triggered: true);
        }
    }
}

/// <summary>
/// Registers the warrior value modifiers and target filters (discovered <see cref="ISpellHandlerModule"/>):
/// Bloodthirst's attack power damage, Shield Slam's block value damage, Intimidating Shout's main-target exclusion on effects 1 and 2,
/// and the Orc Blood Fury / Undead Cannibalize racial hooks.
/// </summary>
public sealed class WarriorScriptsModule : ISpellHandlerModule
{
    public const uint IntimidatingShout = 5246;

    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterValueModifier(new BloodthirstDamage(system));
        system.RegisterValueModifier(new ShieldSlamDamage());
        system.RegisterSpellTargetFilter(IntimidatingShout, FilterIntimidatingShout);
        system.RegisterObserver(new RacialCastObserver(system));
    }

    /// <summary>
    /// Intimidating Shout (5246; spell_warrior.cpp:19-35): the primary target is cowered in place by effect 0 and must not be added to
    /// the AoE fear / threat target lists of effects 1 and 2.
    /// </summary>
    public static bool FilterIntimidatingShout(SpellCast cast, SpellEffectInfo effect, Unit target)
        => ReferenceEquals(effect, cast.Spell.Effects[0]) || cast.Targets.Unit.IsEmpty || target.Guid != cast.Targets.Unit;

    private sealed class RacialCastObserver(SpellSystem system) : ISpellCastObserver
    {
        public void OnCast(SpellCast cast)
        {
            if (cast.Spell.Id == BloodFuryScript.BloodFury)
            {
                BloodFuryScript.Trigger(system, cast.Caster, cast.Spell.SimpleValue(0));
            }
            else if (cast.Spell.Id == CannibalizeScript.Cannibalize
                && cast.Spell.HasEffect(SpellEffectName.Dummy)
                && !system.HasAura(cast.Caster, CannibalizeScript.CannibalizeHeal))
            {
                system.CastSpell(cast.Caster, CannibalizeScript.CannibalizeHeal, SpellCastTargets.ForUnit(cast.Caster.Guid), triggered: true);
            }
        }
    }
}

