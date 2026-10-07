using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells.Warlock;

/// <summary>
/// Life Tap, all six ranks (vmangos scripts/spells/spell_warlock.cpp:112-159, ids 1454, 1455, 1456, 11687, 11688, 11689): the DUMMY effect trades
/// health for mana one for one, quietly (no combat log), through the spell power of the caster.
/// <list type="bullet">
/// <item>Cast check: the cost is the first effect's simple value, base points plus base dice (not the rolled value; vmangos reads <c>m_currentBasePoints</c>,
/// which the Spell constructor sets to <c>CalculateSimpleValue</c>, Spell.cpp:77), taken through
/// the direct damage bonus; the cast fizzles when the caster's health is not above the rounded-up amount.</item>
/// <item>Effect: the rolled effect value through the same bonus, dithered to an integer; with more health than that the caster loses it and gains
/// as much mana, scaled by every Improved Life Tap aura (warlock family dummy aura, icon 208: <c>(amount + 100) * mana / 100</c>); otherwise the
/// cast fizzles after the cast result (for a rank with dice the rolled value can exceed the checked cost).</item>
/// <item>The mana arrives as an energize of spell 31818 (vmangos <c>CastCustomSpell(31818)</c>): the power is added and the energize log of that
/// spell id is sent; spell 31818 itself is not cast.</item>
/// </list>
/// LIMITS: the SPELLMOD_COST talent modifier the vmangos script applies to both amounts belongs to the spell-modifier engine lane.
/// </summary>
[SpellScript(1454, 1455, 1456, 11687, 11688, 11689)]
public sealed class LifeTapScript : ISpellScript
{
    /// <summary>The energize spell vmangos casts for the mana (SpellEffects of 31818).</summary>
    public const uint EnergizeSpell = 31818;

    /// <summary>SpellIconID of the Improved Life Tap dummy aura (spell_warlock.cpp:147).</summary>
    public const uint ImprovedLifeTapIcon = 208;

    public SpellCastResult OnCheckCast(in SpellCastCheckContext context)
    {
        Unit caster = context.Caster;
        float cost = context.Spell.SimpleValue(0);
        float damage = Bonus(context.System, caster, context.Spell, cost > 0 ? cost : 0);
        return caster.Health <= Math.Ceiling(damage) ? SpellCastResult.Fizzle : SpellCastResult.CastOk;
    }

    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex != 0)
        {
            return;
        }

        Unit caster = context.Caster;
        SpellSystem system = context.System;
        float damage = Bonus(system, caster, context.Spell, context.Value > 0 ? context.Value : 0);
        int integral = (int)Math.Floor(damage + system.Random.NextSingle()); // rand_dither
        if (caster.Health > integral)
        {
            caster.Health -= (uint)integral; // shouldn't appear in the combat log
            int mana = integral;
            foreach (SpellAuraHolder holder in system.GetAuras(caster))
            {
                if (holder.IsRemoved || holder.Spell.SpellFamilyName != SoulShardRules.WarlockFamily || holder.Spell.SpellIconId != ImprovedLifeTapIcon)
                {
                    continue;
                }

                foreach (SpellAura? aura in holder.Auras)
                {
                    if (aura is { Type: AuraType.Dummy })
                    {
                        mana = (aura.Amount + 100) * mana / 100;
                    }
                }
            }

            SpellSystem.SetPower(caster, PowerType.Mana, SpellSystem.GetPower(caster, PowerType.Mana) + (uint)mana);
            SpellSystem.SendToSet(caster, WorldOpcode.SmsgSpellenergizelog, SpellPackets.BuildSpellEnergizeLog(
                caster.Guid, caster.Guid, EnergizeSpell, (uint)PowerType.Mana, (uint)mana), includeSelf: true);
        }
        else if (caster is Player player)
        {
            player.Session.Send(WorldOpcode.SmsgCastResult, SpellPackets.BuildCastResult(context.Spell.Id, SpellCastResult.Fizzle));
        }
    }

    /// <summary>vmangos SpellDamageBonusDone then SpellDamageBonusTaken of the caster on itself (SPELL_DIRECT_DAMAGE).</summary>
    private static float Bonus(SpellSystem system, Unit caster, SpellInfo spell, float amount)
        => system.AmountModifier is { } modifier ? modifier.Modify(SpellAmountStage.DirectDamage, caster, caster, spell, 0, amount, 1) : amount;
}
