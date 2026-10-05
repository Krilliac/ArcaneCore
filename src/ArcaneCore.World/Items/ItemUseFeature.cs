using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;

namespace ArcaneCore.World.Items;

/// <summary>Connects item-use lifecycle/trade state to the game spell seam.</summary>
public sealed class ItemUseFeature(SpellFeature spells, EconomyFeature economy) : IWorldFeature
{
    public void Attach(WorldRuntime world)
    {
        spells.System.ItemUseTradeGuard = (player, item) => economy.TradeOf(player) is { } trade
            && trade.SideOf(player).SlotOf(item.Guid) >= 0;
    }
}
