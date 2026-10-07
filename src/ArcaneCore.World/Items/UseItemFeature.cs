using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Items.ItemUse;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Items;

/// <summary>
/// Item use in the world daemon (discovered <see cref="IWorldFeature"/>, docs/areas/crafting.md): installs the cast-item check on the
/// shared spell system and owns the <see cref="ItemUseService"/> for programmatic item use. The CMSG_USE_ITEM opcode itself is handled by
/// <see cref="ItemHandlers"/> through <see cref="SpellSystem.HandleItemUse"/>. An item in the player's open trade window is found through the
/// economy feature (<see cref="EconomyFeature.TradeOf"/>).
/// </summary>
public sealed class UseItemFeature(IServiceProvider services) : IWorldFeature
{
    /// <summary>The service; valid once the world is attached.</summary>
    public ItemUseService Service { get; private set; } = null!;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        Service = ItemUseService.Install(services.GetRequiredService<SpellFeature>().System, IsInTrade);
    }

    /// <summary>vmangos Item::IsInTrade: the item is offered in one of the six trade slots of the player's open trade.</summary>
    private bool IsInTrade(Player player, Item item)
        => services.GetService<EconomyFeature>()?.TradeOf(player) is { } trade && trade.SideOf(player).Items.Contains(item.Guid);
}
