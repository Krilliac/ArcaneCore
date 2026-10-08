using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

// Aura-stack entry point for instance scripts (raid lane), kept apart from the class-scripts lane's SpellSystem.ScriptSeams.cs.
public sealed partial class SpellSystem
{
    /// <summary>
    /// mangos-classic Unit::RemoveAuraStack as called by boss_hakkar.cpp HakkarPowerDown::OnEffectExecute: one script-requested stack,
    /// with the normal amount recalculation of SpellAuraHolder::ModStackAmount (not a dispel); the holder goes with its last stack.
    /// </summary>
    internal void RemoveScriptAuraStack(Unit target, uint spellId)
    {
        if (GetAuras(target).FirstOrDefault(h => h.Spell.Id == spellId && !h.IsRemoved) is not { } holder)
        {
            return;
        }

        if (ModStackAmount(holder, -1) && GetState(target.Guid) is { } state)
        {
            RemoveHolder(state, holder, AuraRemoveMode.Default);
        }
    }
}
