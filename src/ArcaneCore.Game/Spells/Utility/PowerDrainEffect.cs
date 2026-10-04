using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Utility;

/// <summary>
/// SPELL_EFFECT_POWER_DRAIN (8, vmangos Spell::EffectPowerDrain, SpellEffects.cpp:1696-1760): Dark Pact (drains the pet's mana), Viper Sting and
/// the other drains. The effect value gets the caster's spell damage bonus and the target's damage-taken bonus like a direct damage (the
/// <see cref="SpellSystem.AmountModifier"/> direct damage stage, truncated to int32), is capped at the target's current power, and for mana the
/// caster gains the drained amount times the effect's multiplier (EffectMultipleValue, 0 counts as 1), dithered; a drain of oneself gives nothing back.
/// <para>
/// Only a target that uses the drained power type is drained (a happiness drain only hits pets). Limits: the SPELLMOD_MULTIPLE_VALUE talent modifier of
/// the gain belongs to the spell-modifier engine lane, and the SMSG_SPELLLOGEXECUTE power drain entry (combat text) is not sent.
/// </para>
/// </summary>
public sealed class PowerDrainEffect : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterEffect(SpellEffectName.PowerDrain, Drain);
    }

    private static void Drain(SpellEffectContext context)
    {
        int misc = context.Effect.MiscValue;
        if (misc is < 0 or > (int)PowerType.Happiness)
        {
            return;
        }

        Unit target = context.Target;
        Unit caster = context.Caster;
        if (!target.IsAlive || context.Value < 0)
        {
            return;
        }

        var power = (PowerType)misc;
        if (power == PowerType.Happiness)
        {
            if (target is not Creature { IsPet: true })
            {
                return;
            }
        }
        else if (target.PowerType != power)
        {
            return;
        }

        uint current = SpellSystem.GetPower(target, power);
        int damage = context.Value;
        if (context.System.AmountModifier is { } modifier)
        {
            damage = (int)modifier.Modify(SpellAmountStage.DirectDamage, caster, target, context.Spell, context.EffectIndex, damage, 1);
        }

        uint drained = (uint)Math.Min(current, Math.Max(damage, 0));
        SpellSystem.SetPower(target, power, current - drained);

        // Don't restore from a self drain.
        if (power == PowerType.Mana && !ReferenceEquals(caster, target))
        {
            float multiplier = context.Effect.MultipleValue == 0 ? 1f : context.Effect.MultipleValue;
            float gain = drained * multiplier;
            uint dithered = (uint)Math.Floor(Math.Max(gain, 0f) + context.System.Random.NextSingle());
            SpellSystem.SetPower(caster, PowerType.Mana, SpellSystem.GetPower(caster, PowerType.Mana) + dithered);
        }
    }
}
