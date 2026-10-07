using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Features;

namespace ArcaneCore.World.Spells;

/// <summary>Standing transitions interrupt food/drink auras carrying the vanilla standing-cancel flag.</summary>
public sealed class FoodDrinkFeature(SpellFeature spells) : IWorldFeature
{
    public void Attach(WorldRuntime world)
        => world.PlayerLoggedIn += player => player.StandStateChanged += OnStandStateChanged;

    private void OnStandStateChanged(Player player, StandState previous, StandState current)
    {
        if (current is StandState.Stand or StandState.Dead)
        {
            spells.System.RemoveAurasWithInterruptFlags(player, AuraInterruptMask.Standing);
        }
    }
}
