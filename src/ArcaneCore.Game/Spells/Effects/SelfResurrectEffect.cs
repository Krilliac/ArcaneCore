using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// SPELL_EFFECT_SELF_RESURRECT (94), after vmangos <c>Spell::EffectSelfResurrect</c> (SpellEffects.cpp:5334-5368): the Soulstone, Twisting
/// Nether, Reincarnation and the item spells bring a dead player back at once. A negative effect value is a flat amount (health = -value,
/// mana = the misc value: Soulstone rank 1 is -400 and 700); a positive one is a percent of the maximums (Reincarnation: 20%). The
/// results are dithered to whole numbers, rage is emptied, energy is full and the corpse is gone, as for any resurrection.
/// </summary>
public sealed class SelfResurrectEffect : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterEffect(SpellEffectName.SelfResurrect, EffectSelfResurrect);
    }

    private static void EffectSelfResurrect(SpellEffectContext context)
    {
        if (context.Target is not Player player || player.IsAlive || !player.IsInWorld || player.Map is not { } map)
        {
            return;
        }

        float health;
        float mana = 0;
        if (context.Value < 0)
        {
            health = -context.Value;
            mana = context.Effect.MiscValue;
        }
        else
        {
            health = context.Value / 100.0f * player.MaxHealth;
            uint maxMana = MapCombat.GetMaxPower(player, PowerType.Mana);
            if (maxMana > 0)
            {
                mana = context.Value / 100.0f * maxMana;
            }
        }

        map.Combat.CompleteResurrection(player, Dither(context.System, health), Dither(context.System, mana));
    }

    private static uint Dither(SpellSystem system, float value)
    {
        if (value <= 0f)
        {
            return 0;
        }

        uint whole = (uint)value;
        float fraction = value - whole;
        return fraction > 0f && system.Random.NextDouble() < fraction ? whole + 1 : whole;
    }
}
