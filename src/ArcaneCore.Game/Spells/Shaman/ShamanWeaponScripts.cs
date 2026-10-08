using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Combat.Threat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Spells.Shaman;

/// <summary>
/// Flametongue Weapon / Flametongue Totem proc (8026, 8028, 8029, 8248, 8253, 10445, 10523, 16343, 16344, 16389; vmangos scripts/spells/spell_shaman.cpp:19-44):
/// the weapon enchantment's combat spell is a DUMMY whose damage scales with the weapon that procced it and the caster's fire spell damage,
/// <c>(value + 3.85 * spell damage) * 0.01 * weapon speed</c> ("found spelldamage coefficients of 0.381% per 0.1 speed and 15.244 per 4.0 speed but
/// own calculation say 0.385"), dithered and dealt by Flametongue Attack (10444) at the target, triggered. Without the cast item there is nothing
/// to scale by and the proc does nothing (vmangos logs an error).
/// <para>
/// The caster's spell damage is SpellBaseDamageBonusDone for the spell's school: the SPELL_AURA_MOD_DAMAGE_DONE auras of the school (the spirit
/// share of SpellBaseDamageBonusDone belongs to priest talents and is not a shaman's).
/// </para>
/// </summary>
[SpellScript(8026, 8028, 8029, 8248, 8253, 10445, 10523, 16343, 16344, 16389)]
public sealed class FlametongueProcScript : ISpellScript
{
    public const uint FlametongueAttack = 10444;
    public const float SpellDamageCoefficient = 3.85f;

    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex != 0 || context.Effect.Effect != SpellEffectName.Dummy || context.Cast.CastItem is not { } item)
        {
            return;
        }

        SpellSystem system = context.System;
        int mask = 1 << (int)context.Spell.School;
        float spellDamage = system.GetTotalAuraModifier(context.Caster, AuraType.ModDamageDone, a => (a.MiscValue & mask) != 0);
        float weaponSpeed = item.Template.Delay / 1000.0f;
        float total = Damage(context.Value, spellDamage, weaponSpeed);
        int dithered = (int)Math.Floor(Math.Max(total, 0f) + system.Random.NextSingle()); // rand_dither
        system.CastCustomSpell(context.Caster, FlametongueAttack, SpellCastTargets.ForUnit(context.Target.Guid), dithered);
    }

    /// <summary>The proc damage before dithering (spell_shaman.cpp:37-39).</summary>
    public static float Damage(int value, float spellDamage, float weaponSpeedSeconds)
        => (value + (SpellDamageCoefficient * spellDamage)) * 0.01f * weaponSpeedSeconds;
}

/// <summary>
/// Rockbiter Weapon proc (20865, 20866, 20867, 20868, 20870, 20871; SCRIPT_EFFECT; vmangos <c>Spell::EffectScriptEffect</c>, SpellEffects.cpp:4544-4561):
/// on a target that can have a threat list and already holds the caster on it, the caster gains <c>value * main-hand attack time / 1000</c> threat.
/// </summary>
[SpellScript(20865, 20866, 20867, 20868, 20870, 20871)]
public sealed class RockbiterProcScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.Effect.Effect != SpellEffectName.ScriptEffect || !ThreatRules.CanHaveThreatList(context.Target)
            || context.Target.Combat.Threat.GetThreat(context.Caster) == 0)
        {
            return;
        }

        float threat = context.Value * context.Caster.Combat.GetAttackTime(WeaponAttackType.BaseAttack) / 1000.0f;
        context.System.AddSpellThreat(context.Caster, context.Target, context.Spell, threat);
    }
}
