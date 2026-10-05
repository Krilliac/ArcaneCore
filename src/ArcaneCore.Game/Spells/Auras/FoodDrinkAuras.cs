using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

/// <summary>Registers the non-periodic food/drink modifier aura types consumed by combat regeneration.</summary>
public sealed class FoodDrinkAuras : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        AuraHandler handler = new((spells, holder, aura, apply) =>
        {
            if (!apply) return;
            spells.ApplyFoodDrinkVisual(holder);
            spells.ObserveFoodDrinkHeartbeat(holder);
        }, null);
        system.RegisterAura(AuraType.ModRegen, handler);
    }

}
