using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Items;

/// <summary>
/// The smaller inventory opcodes (vmangos ItemHandler.cpp): item name query, read item, the two
/// bank shortcuts and equip-to-slot. Layouts: vmangos Server/Packets/Item.cpp:11-80 and
/// gtker/wow_messages (cmsg_item_name_query, cmsg_read_item, cmsg_autobank_item,
/// cmsg_autostore_bank_item, cmsg_autoequip_item_slot).
/// </summary>
public sealed class ItemMiscHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        table.OnSession(WorldOpcode.CmsgItemNameQuery, SessionStates.LoggedIn, HandleItemNameQueryAsync);
        table.OnWorld(WorldOpcode.CmsgReadItem, HandleReadItem);
        table.OnWorld(WorldOpcode.CmsgAutobankItem, HandleAutoBankItem);
        table.OnWorld(WorldOpcode.CmsgAutostoreBankItem, HandleAutoStoreBankItem);
        table.OnWorld(WorldOpcode.CmsgAutoequipItemSlot, HandleAutoEquipItemSlot);
    }

    /// <summary>CMSG_ITEM_NAME_QUERY: u32 entry, u64 guid (unused). An unknown entry gets no reply (vmangos).</summary>
    private static async Task HandleItemNameQueryAsync(WorldSession session, byte[] payload)
    {
        var reader = new PacketReader(payload);
        uint entry = reader.ReadUInt32();
        var templates = await session.Services.GetRequiredService<ItemsFeature>().EnsureLoadedAsync().ConfigureAwait(false);
        if (templates.Find(entry) is { } template)
        {
            session.Send(WorldOpcode.SmsgItemNameQueryResponse, ItemMiscPackets.ItemNameQueryResponse(template.Entry, template.Name));
        }
    }

    /// <summary>CMSG_READ_ITEM: u8 bag, u8 slot.</summary>
    private static void HandleReadItem(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        byte bag = reader.ReadByte();
        byte slot = reader.ReadByte();
        player.Inventory.TryReadItem(bag, slot);
    }

    /// <summary>CMSG_AUTOBANK_ITEM: u8 bag, u8 slot.</summary>
    private static void HandleAutoBankItem(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        byte bag = reader.ReadByte();
        byte slot = reader.ReadByte();
        player.Inventory.AutoBankItem(bag, slot);
    }

    /// <summary>CMSG_AUTOSTORE_BANK_ITEM: u8 bag, u8 slot.</summary>
    private static void HandleAutoStoreBankItem(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        byte bag = reader.ReadByte();
        byte slot = reader.ReadByte();
        player.Inventory.AutoStoreBankItem(bag, slot);
    }

    /// <summary>CMSG_AUTOEQUIP_ITEM_SLOT: u64 item guid, u8 destination slot.</summary>
    private static void HandleAutoEquipItemSlot(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        ObjectGuid guid = new(reader.ReadUInt64());
        byte slot = reader.ReadByte();
        player.Inventory.AutoEquipItemSlot(guid, slot);
    }
}
