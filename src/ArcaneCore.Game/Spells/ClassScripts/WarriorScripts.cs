using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Rogue;
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
        int basePoints = (int)context.Spell.SimpleValue(0) + (int)Math.Floor(rage + system.Random.NextSingle()); // rand_dither
        system.CastCustomSpell(context.Caster, ExecuteDamage, SpellCastTargets.ForUnit(context.Target.Guid), basePoints);
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
        context.System.CastCustomSpell(caster, DeepWound, SpellCastTargets.ForUnit(context.Target.Guid), (int)(damage * share / 4));
    }
}

/// <summary>
/// Bloodthirst (23881, 23892, 23893, 23894; spell_warrior.cpp:37-56): effect 0's damage is that many percent of the caster's total melee attack power.
/// LIMITS: SPELL_AURA_MOD_MELEE_ATTACK_POWER_VERSUS against the target's creature type is not added.
/// </summary>
public sealed class BloodthirstDamage : ISpellValueModifier
{
    public static readonly uint[] Ranks = [23881, 23892, 23893, 23894];

    public int Modify(SpellValueKind kind, in SpellValueContext context, int value)
        => kind == SpellValueKind.EffectValue && context.EffectIndex == 0 && Array.IndexOf(Ranks, context.Spell.Id) >= 0
            ? (int)(value * RogueBleedScripts.MeleeAttackPower(context.Caster) / 100)
            : value;
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

/// <summary>Registers the warrior value modifiers (discovered <see cref="ISpellHandlerModule"/>): Bloodthirst's attack power damage.</summary>
public sealed class WarriorScriptsModule : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterValueModifier(new BloodthirstDamage());
    }
}
