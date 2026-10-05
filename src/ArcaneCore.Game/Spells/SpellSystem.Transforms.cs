using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

public readonly record struct TransformDisplay(uint DisplayId, float Scale);

public sealed partial class SpellSystem
{
    /// <summary>World-owned resolver for transform creature-entry displays; null leaves the vmangos box fallback.</summary>
    public Func<Unit, uint, TransformDisplay?>? TransformDisplayResolver { get; set; }

    public bool IsPolymorphed(Unit unit)
        => unit.TransformSpellId != 0
            && unit.TransformHolder is { IsRemoved: false } holder && GetAuras(unit).Contains(holder)
            && holder.Spell.SpellFamilyName == 3
            && holder.Auras[0] is { Type: AuraType.ModConfuse }
            && holder.Spell.PreventionType == SpellConstants.PreventionTypeSilence;

    internal TransformDisplay ResolveTransformDisplay(Unit target, SpellAuraHolder holder, SpellAura aura)
        => aura.MiscValue != 0
            ? TransformDisplayResolver?.Invoke(target, unchecked((uint)aura.MiscValue)) ?? new TransformDisplay(4, 1.0f)
            : new TransformDisplay(4, 1.0f);
}
