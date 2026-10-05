using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>Keep the newly applied form and remove other exact holders through normal aura notifications.</summary>
    internal void RemoveOtherShapeshiftHolders(Unit target, SpellAuraHolder keep)
    {
        if (GetState(target.Guid) is not { } state || !ReferenceEquals(state.Unit, target)) return;
        foreach (SpellAuraHolder holder in state.Auras.Where(h => !ReferenceEquals(h, keep)
            && !h.IsRemoved && h.HasAura(AuraType.ModShapeshift)).ToArray())
            RemoveHolder(state, holder);
    }
}
