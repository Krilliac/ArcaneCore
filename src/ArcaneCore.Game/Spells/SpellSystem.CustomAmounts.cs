using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    internal void RemoveAuraHolder(SpellAuraHolder holder)
    {
        ArgumentNullException.ThrowIfNull(holder);
        if (GetState(holder.Target.Guid) is { } state && ReferenceEquals(state.Unit, holder.Target)
            && state.Auras.Contains(holder))
        {
            RemoveHolder(state, holder);
        }
    }

    /// <summary>Triggered server cast seam for one exact aura effect value (used by druid Heart of the Wild).</summary>
    internal SpellCastResult CastSpellWithCustomAuraAmount(Unit caster, uint spellId, SpellCastTargets targets,
        bool triggered, SpellInfo? triggeringSpell, int effectIndex, int amount)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(targets);
        SpellInfo? spell = Store.Get(spellId);
        if (spell is null)
        {
            return SpellCastResult.NotFound;
        }

        return Prepare(caster, spell, targets, triggered, triggeringSpell,
            customAuraAmounts: new Dictionary<int, int> { [effectIndex] = amount });
    }
}
