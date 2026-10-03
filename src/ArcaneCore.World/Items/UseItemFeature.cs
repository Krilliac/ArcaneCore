using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Items.ItemUse;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Features;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Items;

/// <summary>
/// CMSG_USE_ITEM in the world daemon (discovered <see cref="IWorldFeature"/>, docs/areas/crafting.md): installs the cast-item check on the
/// shared spell system and owns the <see cref="ItemUseService"/> the opcode handler calls. An item in the player's open trade window is
/// found through the economy feature (<see cref="EconomyFeature.TradeOf"/>).
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

/// <summary>
/// CMSG_USE_ITEM (vmangos <c>HandleUseItemOpcode</c>; gtker wow_messages <c>cmsg_use_item.wowm</c> version 1.12): u8 bag, u8 slot, u8 spell index,
/// SpellCastTargets. A short or malformed payload is ignored.
/// </summary>
public sealed class UseItemHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table) => table.OnWorld(WorldOpcode.CmsgUseItem, UseItem);

    private static void UseItem(WorldSession session, Player player, byte[] payload)
    {
        if (payload.Length < 3 + 2)
        {
            return;
        }

        var reader = new PacketReader(payload);
        byte bag = reader.ReadByte();
        byte slot = reader.ReadByte();
        byte spellIndex = reader.ReadByte();
        SpellCastTargets targets = SpellCastTargets.Read(ref reader);
        session.Services.GetRequiredService<UseItemFeature>().Service.UseItem(player, bag, slot, spellIndex, targets);
    }
}
