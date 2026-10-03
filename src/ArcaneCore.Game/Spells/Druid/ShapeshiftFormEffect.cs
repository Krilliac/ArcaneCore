using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Rules;

namespace ArcaneCore.Game.Spells.Druid;

/// <summary>
/// Spell 9033, "Shapeshift Form Effect", cast on a druid at every animal shapeshift: its script effect removes the movement
/// impairing auras from the target (vmangos Spell::EffectScriptEffect, SpellEffects.cpp:4442-4480; the mechanic filter is
/// <see cref="ShapeshiftFormEffectRules"/>). Roots with any mechanic go; slows go unless they carry a crowd-control or
/// daze mechanic (or are a daze-like icon-15 spell that is not a snare). Registered in the
/// <see cref="ScriptEffectRegistry"/>, so this module does not own the effect itself.
/// </summary>
public sealed class ShapeshiftFormEffectModule : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        ScriptEffectRegistry.For(system).Add(ShapeshiftFormEffectRules.SpellId, Run);
    }

    private static void Run(SpellEffectContext context)
    {
        SpellSystem system = context.System;
        Unit target = context.Target;

        // target->RemoveSpellsCausingAuraWithMechanic(SPELL_AURA_MOD_ROOT): Unit.cpp:543-556.
        foreach (SpellAuraHolder holder in system.GetAuras(target).Where(h => !h.IsRemoved && h.HasAura(AuraType.ModRoot)).ToArray())
        {
            if (!holder.IsRemoved && ShapeshiftFormEffectRules.RemovesRoot(holder.Spell.AllMechanicMask()))
            {
                system.RemoveAuras(target, holder.Spell.Id);
            }
        }

        // The slowing auras (SpellEffects.cpp:4452-4476).
        foreach (SpellAuraHolder holder in system.GetAuras(target).Where(h => !h.IsRemoved && h.HasAura(AuraType.ModDecreaseSpeed)).ToArray())
        {
            SpellInfo spell = holder.Spell;
            if (!holder.IsRemoved && ShapeshiftFormEffectRules.RemovesSnare(spell.AllMechanicMask(), spell.SpellIconId, spell.Dispel))
            {
                system.RemoveAuras(target, spell.Id);
            }
        }
    }
}
