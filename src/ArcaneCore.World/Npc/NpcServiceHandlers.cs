using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Npc;

/// <summary>
/// Vendor, trainer, innkeeper, banker, spirit healer, flight master and gossip-option requests,
/// dispatched on the world thread to <see cref="QuestNpcServices"/>. Client layouts (build 5875):
/// gtker/wow_messages 70abb9deff0bb63440d8aeb4386b820653e8a176 (MIT) — cmsg_list_inventory,
/// cmsg_sell_item (u64 vendor, u64 item, u8 count), cmsg_buy_item (u64, u32 item, u8 count,
/// u8 unknown), cmsg_buy_item_in_slot (u64, u32, u64 bag, u8 slot, u8 count), cmsg_buyback_item
/// (u64, u32 slot), cmsg_repair_item (u64 npc, u64 item), cmsg_trainer_list,
/// cmsg_trainer_buy_spell (u64, u32), cmsg_binder_activate, cmsg_banker_activate,
/// cmsg_buy_bank_slot, cmsg_spirit_healer_activate, cmsg_taxinode_status_query,
/// cmsg_taxiquery_available_nodes (u64 each), cmsg_activatetaxi (u64, u32 src, u32 dst),
/// cmsg_activatetaxiexpress (u64, u32 total cost, u32 count, u32[count]), cmsg_gossip_select_option
/// (u64, u32 list id, CString code when coded). Lengths are exact; anything else disconnects.
/// </summary>
public sealed class NpcServiceHandlers : IOpcodeHandlerGroup
{
    /// <summary>Longest gossip code accepted (the client's edit box is far shorter).</summary>
    private const int MaxGossipCode = 255;

    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.CmsgGossipSelectOption, GossipSelectOption);
        table.OnWorld(WorldOpcode.CmsgListInventory, (s, p, b) => Services(s).ListInventory(p, Guid(b)));
        table.OnWorld(WorldOpcode.CmsgBuyItem, BuyItem);
        table.OnWorld(WorldOpcode.CmsgBuyItemInSlot, BuyItemInSlot);
        table.OnWorld(WorldOpcode.CmsgSellItem, SellItem);
        table.OnWorld(WorldOpcode.CmsgBuybackItem, BuybackItem);
        table.OnWorld(WorldOpcode.CmsgRepairItem, RepairItem);
        table.OnWorld(WorldOpcode.CmsgTrainerList, (s, p, b) => Services(s).TrainerList(p, Guid(b)));
        table.OnWorld(WorldOpcode.CmsgTrainerBuySpell, TrainerBuySpell);
        table.OnWorld(WorldOpcode.CmsgBinderActivate, (s, p, b) => Services(s).BinderActivate(p, Guid(b)));
        table.OnWorld(WorldOpcode.CmsgBankerActivate, (s, p, b) => Services(s).BankerActivate(p, Guid(b)));
        table.OnWorld(WorldOpcode.CmsgBuyBankSlot, (s, p, b) => Services(s).BuyBankSlot(p, Guid(b)));
        table.OnWorld(WorldOpcode.CmsgSpiritHealerActivate, (s, p, b) => Services(s).SpiritHealerActivate(p, Guid(b)));
        table.OnWorld(WorldOpcode.CmsgTaxinodeStatusQuery, (s, p, b) => Services(s).TaxiNodeStatusQuery(p, Guid(b)));
        table.OnWorld(WorldOpcode.CmsgTaxiqueryavailablenodes, (s, p, b) => Services(s).TaxiQueryAvailableNodes(p, Guid(b)));
        table.OnWorld(WorldOpcode.CmsgActivatetaxi, ActivateTaxi);
        table.OnWorld(WorldOpcode.CmsgActivatetaxiexpress, ActivateTaxiExpress);

        // The client reports the end of a server spline (taxi landing); the server's own timer decides.
        table.OnWorld(WorldOpcode.CmsgMoveSplineDone, static (_, _, _) => { });
    }

    private static QuestNpcServices Services(WorldSession session) => session.Services.GetRequiredService<QuestNpcFeature>().Services;

    private static void GossipSelectOption(WorldSession session, Player player, byte[] payload)
    {
        if (payload.Length < 12)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "gossip select requires at least 12 bytes");
        }

        var reader = new PacketReader(payload);
        var guid = new ObjectGuid(reader.ReadUInt64());
        uint listId = reader.ReadUInt32();
        string? code = null;
        if (reader.Remaining > 0)
        {
            if (payload[^1] != 0 || reader.Remaining > MaxGossipCode + 1 || Array.IndexOf(payload, (byte)0, 12) != payload.Length - 1)
            {
                throw new ArgumentOutOfRangeException(nameof(payload), "gossip code must be one terminated string");
            }

            code = reader.ReadCString();
        }

        Services(session).GossipSelectOption(player, guid, listId, code);
    }

    private static void BuyItem(WorldSession session, Player player, byte[] payload)
    {
        RequireLength(payload, 14);
        var reader = new PacketReader(payload);
        var vendor = new ObjectGuid(reader.ReadUInt64());
        uint item = reader.ReadUInt32();
        byte count = reader.ReadByte();
        Services(session).BuyItem(player, vendor, item, count);
    }

    /// <summary>
    /// CMSG_BUY_ITEM_IN_SLOT (u64 vendor, u32 item, u64 bag, u8 slot, u8 count; wow_messages
    /// cmsg_buy_item_in_slot, 22 bytes): bought into the named bag and slot (vmangos
    /// HandleBuyItemInSlotOpcode, ItemHandler.cpp:659-686).
    /// </summary>
    private static void BuyItemInSlot(WorldSession session, Player player, byte[] payload)
    {
        BuyItemInSlotRequest request = ReadBuyItemInSlot(payload);
        Services(session).BuyItemInSlot(player, request.Vendor, request.Item, request.Bag, request.Slot, request.Count);
    }

    /// <summary>The fields of CMSG_BUY_ITEM_IN_SLOT as the 1.12 client writes them (wow_messages cmsg_buy_item_in_slot, versions "1 2").</summary>
    public readonly record struct BuyItemInSlotRequest(ObjectGuid Vendor, uint Item, ObjectGuid Bag, byte Slot, byte Count);

    /// <summary>Parse the exact 22-byte payload (u64 vendor, u32 item, u64 bag, u8 bag slot, u8 amount); anything else throws.</summary>
    public static BuyItemInSlotRequest ReadBuyItemInSlot(byte[] payload)
    {
        RequireLength(payload, 22);
        var reader = new PacketReader(payload);
        var vendor = new ObjectGuid(reader.ReadUInt64());
        uint item = reader.ReadUInt32();
        var bag = new ObjectGuid(reader.ReadUInt64());
        byte slot = reader.ReadByte();
        byte count = reader.ReadByte();
        return new BuyItemInSlotRequest(vendor, item, bag, slot, count);
    }

    private static void SellItem(WorldSession session, Player player, byte[] payload)
    {
        RequireLength(payload, 17);
        var reader = new PacketReader(payload);
        var vendor = new ObjectGuid(reader.ReadUInt64());
        var item = new ObjectGuid(reader.ReadUInt64());
        byte count = reader.ReadByte();
        Services(session).SellItem(player, vendor, item, count);
    }

    private static void BuybackItem(WorldSession session, Player player, byte[] payload)
    {
        RequireLength(payload, 12);
        var reader = new PacketReader(payload);
        var vendor = new ObjectGuid(reader.ReadUInt64());
        Services(session).BuybackItem(player, vendor, reader.ReadUInt32());
    }

    private static void RepairItem(WorldSession session, Player player, byte[] payload)
    {
        RequireLength(payload, 16);
        var reader = new PacketReader(payload);
        var npc = new ObjectGuid(reader.ReadUInt64());
        var item = new ObjectGuid(reader.ReadUInt64());
        Services(session).RepairItem(player, npc, item);
    }

    private static void TrainerBuySpell(WorldSession session, Player player, byte[] payload)
    {
        RequireLength(payload, 12);
        var reader = new PacketReader(payload);
        var trainer = new ObjectGuid(reader.ReadUInt64());
        Services(session).BuyTrainerSpell(player, trainer, reader.ReadUInt32());
    }

    private static void ActivateTaxi(WorldSession session, Player player, byte[] payload)
    {
        RequireLength(payload, 16);
        var reader = new PacketReader(payload);
        var npc = new ObjectGuid(reader.ReadUInt64());
        uint source = reader.ReadUInt32();
        uint destination = reader.ReadUInt32();
        Services(session).ActivateTaxi(player, npc, source, destination);
    }

    private static void ActivateTaxiExpress(WorldSession session, Player player, byte[] payload)
    {
        if (payload.Length < 16)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "taxi express requires at least 16 bytes");
        }

        var reader = new PacketReader(payload);
        var npc = new ObjectGuid(reader.ReadUInt64());
        _ = reader.ReadUInt32(); // the client's total cost: recomputed by the server
        uint count = reader.ReadUInt32();
        if (count > QuestNpcServices.MaxExpressNodes || payload.Length != 16 + ((int)count * 4))
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "taxi express node count does not match the payload");
        }

        uint[] nodes = new uint[count];
        for (int i = 0; i < nodes.Length; i++)
        {
            nodes[i] = reader.ReadUInt32();
        }

        Services(session).ActivateTaxiExpress(player, npc, nodes);
    }

    private static ObjectGuid Guid(byte[] payload)
    {
        RequireLength(payload, 8);
        var reader = new PacketReader(payload);
        return new ObjectGuid(reader.ReadUInt64());
    }

    private static void RequireLength(byte[] payload, int expected)
    {
        if (payload.Length != expected)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), $"NPC service request requires exactly {expected} bytes");
        }
    }
}
