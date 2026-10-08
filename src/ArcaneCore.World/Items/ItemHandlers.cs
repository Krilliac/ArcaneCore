using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Economy;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Items;

/// <summary>
/// Inventory opcodes (vmangos ItemHandler.cpp). The moves run on the world thread against the
/// player's inventory; the item query is answered from the in-memory content on the session task.
/// Payload layouts: vmangos handlers and gtker/wow_messages 1.12 (cmsg_swap_item, cmsg_swap_inv_item,
/// cmsg_autoequip_item, cmsg_autostore_bag_item, cmsg_split_item, cmsg_destroyitem,
/// cmsg_item_query_single).
/// </summary>
public sealed class ItemHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.CmsgUseItem, HandleUseItem);
        table.OnWorld(WorldOpcode.CmsgSwapItem, HandleSwapItem);
        table.OnWorld(WorldOpcode.CmsgSwapInvItem, HandleSwapInvItem);
        table.OnWorld(WorldOpcode.CmsgAutoequipItem, HandleAutoEquipItem);
        table.OnWorld(WorldOpcode.CmsgAutostoreBagItem, HandleAutoStoreBagItem);
        table.OnWorld(WorldOpcode.CmsgSplitItem, HandleSplitItem);
        table.OnWorld(WorldOpcode.CmsgDestroyitem, HandleDestroyItem);
        table.OnSession(WorldOpcode.CmsgItemQuerySingle, SessionStates.LoggedIn, HandleItemQuerySingleAsync);
    }

    /// <summary>
    /// CMSG_USE_ITEM build 5875: u8 bag, u8 slot, u8 item-spell index, SpellCastTargets.
    /// Verified against gtker/wow_messages world/item/cmsg_use_item.wowm (1.12) and vmangos
    /// SpellHandler.cpp:36-139; the later cast-count/item-GUID trailer is TBC-only.
    /// </summary>
    private static void HandleUseItem(WorldSession session, Player player, byte[] payload)
    {
        try
        {
            // The 5875 request always has the three-byte item prefix and the
            // two-byte target mask, even for SELF (wow_messages 1.12).
            if (payload.Length < 5)
            {
                return;
            }

            var reader = new PacketReader(payload);
            byte bag = reader.ReadByte();
            byte slot = reader.ReadByte();
            byte spellIndex = reader.ReadByte();
            SpellCastTargets targets = SpellCastTargets.Read(ref reader);
            if (reader.Remaining != 0)
            {
                return;
            }

            Item? item = player.Inventory.GetItem(bag, slot);
            if (item is null)
            {
                player.Inventory.SendEquipError(InventoryResult.ItemNotFound, null, null);
                return;
            }

            // vmangos rejects a cast item that is currently offered in a trade. The economy
            // feature owns the trade table; keep this guard at the world boundary rather than
            // coupling the game spell system to a session-level trade service.
            EconomyFeature? economy = session.Services.GetService<EconomyFeature>();
            if (economy?.TradeOf(player) is { } trade && trade.SideOf(player).TradedItems.Contains(item.Guid))
            {
                // vmangos "cheat way only": scored by the anticheat (docs/areas/anticheat.md).
                session.Services.GetService<AntiCheat.AntiCheatFeature>()?.OnRejected(session, player, Game.AntiCheat.AntiCheatViolation.Item, 30f, "use of an item offered in the trade window");
                player.Inventory.SendEquipError(InventoryResult.ItemNotFound, item, null);
                return;
            }

            SpellFeature spells = session.Services.GetRequiredService<SpellFeature>();
            SpellCastResult result = spells.System.HandleItemUse(player, bag, slot, spellIndex, targets);
            if (result == SpellCastResult.ItemNotReady && spellIndex >= item.Template.Spells.Count)
            {
                player.Inventory.SendEquipError(InventoryResult.ItemNotFound, item, null);
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // Reject malformed or wrong-version payloads without touching inventory.
        }
        catch (IndexOutOfRangeException)
        {
            // PacketReader.ReadByte uses direct span indexing for short payloads.
        }
    }

    /// <summary>CMSG_SWAP_ITEM: u8 dst bag, u8 dst slot, u8 src bag, u8 src slot (WorldSession::HandleSwapItem).</summary>
    private static void HandleSwapItem(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        byte dstBag = reader.ReadByte();
        byte dstSlot = reader.ReadByte();
        byte srcBag = reader.ReadByte();
        byte srcSlot = reader.ReadByte();
        PlayerInventory inventory = player.Inventory;

        if (!inventory.IsValidPosition(srcBag, srcSlot, explicitPos: true))
        {
            inventory.SendEquipError(InventoryResult.ItemNotFound, null, null);
            return;
        }

        // vmangos ItemHandler.cpp:115-126 distinguishes an invalid destination from a missing source.
        if (!inventory.IsValidPosition(dstBag, dstSlot, explicitPos: true))
        {
            inventory.SendEquipError(InventoryResult.ItemDoesntGoToSlot, null, null);
            return;
        }

        if (srcBag == dstBag && srcSlot == dstSlot)
        {
            return; // vmangos: "prevent attempt swap same item to current position generated by client at special checting sequence"
        }

        if ((InventorySlots.IsBankPos(srcBag, srcSlot) || InventorySlots.IsBankPos(dstBag, dstSlot)) && !inventory.BankUsable)
        {
            inventory.SendEquipError(InventoryResult.TooFarAwayFromBank, inventory.GetItem(srcBag, srcSlot), null);
            return;
        }

        inventory.SwapItem(srcBag, srcSlot, dstBag, dstSlot);
    }

    /// <summary>CMSG_SWAP_INV_ITEM: u8 src slot, u8 dst slot, both in the player's own slots (WorldSession::HandleSwapInvItemOpcode).</summary>
    private static void HandleSwapInvItem(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        byte srcSlot = reader.ReadByte();
        byte dstSlot = reader.ReadByte();
        PlayerInventory inventory = player.Inventory;

        if (srcSlot == dstSlot)
        {
            return;
        }

        if (!inventory.IsValidPosition(InventorySlots.Bag0, srcSlot, explicitPos: true))
        {
            inventory.SendEquipError(InventoryResult.ItemNotFound, null, null);
            return;
        }

        // vmangos ItemHandler.cpp:52-64 reports the destination separately.
        if (!inventory.IsValidPosition(InventorySlots.Bag0, dstSlot, explicitPos: true))
        {
            inventory.SendEquipError(InventoryResult.ItemDoesntGoToSlot, null, null);
            return;
        }

        if ((InventorySlots.IsBankPos(InventorySlots.Bag0, srcSlot) || InventorySlots.IsBankPos(InventorySlots.Bag0, dstSlot)) && !inventory.BankUsable)
        {
            inventory.SendEquipError(InventoryResult.TooFarAwayFromBank, inventory.GetItem(InventorySlots.Bag0, srcSlot), null);
            return;
        }

        inventory.SwapItem(InventorySlots.Bag0, srcSlot, InventorySlots.Bag0, dstSlot);
    }

    /// <summary>CMSG_AUTOEQUIP_ITEM: u8 src bag, u8 src slot (WorldSession::HandleAutoEquipItemOpcode).</summary>
    private static void HandleAutoEquipItem(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        byte srcBag = reader.ReadByte();
        byte srcSlot = reader.ReadByte();
        player.Inventory.AutoEquipItem(srcBag, srcSlot);
    }

    /// <summary>CMSG_AUTOSTORE_BAG_ITEM: u8 src bag, u8 src slot, u8 dst bag (WorldSession::HandleAutoStoreBagItemOpcode).</summary>
    private static void HandleAutoStoreBagItem(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        byte srcBag = reader.ReadByte();
        byte srcSlot = reader.ReadByte();
        byte dstBag = reader.ReadByte();
        PlayerInventory inventory = player.Inventory;
        bool bank = InventorySlots.IsBankPos(srcBag, srcSlot)
            || (dstBag >= InventorySlots.BankBagStart && dstBag < InventorySlots.BankBagEnd);
        if (bank && !inventory.BankUsable)
        {
            inventory.SendEquipError(InventoryResult.TooFarAwayFromBank, inventory.GetItem(srcBag, srcSlot), null);
            return;
        }

        inventory.AutoStoreBagItem(srcBag, srcSlot, dstBag);
    }

    /// <summary>CMSG_SPLIT_ITEM: u8 src bag, u8 src slot, u8 dst bag, u8 dst slot, u8 count (WorldSession::HandleSplitItemOpcode).</summary>
    private static void HandleSplitItem(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        byte srcBag = reader.ReadByte();
        byte srcSlot = reader.ReadByte();
        byte dstBag = reader.ReadByte();
        byte dstSlot = reader.ReadByte();
        byte count = reader.ReadByte();
        PlayerInventory inventory = player.Inventory;

        if (srcBag == dstBag && srcSlot == dstSlot)
        {
            return;
        }

        if (count == 0)
        {
            return; // vmangos: "check count - if zero it's fake packet"
        }

        if (!inventory.IsValidPosition(srcBag, srcSlot, explicitPos: true))
        {
            inventory.SendEquipError(InventoryResult.ItemNotFound, null, null);
            return;
        }

        // vmangos ItemHandler.cpp:36-48 accepts an automatic destination and uses a distinct error.
        if (!inventory.IsValidPosition(dstBag, dstSlot, explicitPos: false))
        {
            inventory.SendEquipError(InventoryResult.ItemDoesntGoToSlot, null, null);
            return;
        }

        if ((InventorySlots.IsBankPos(srcBag, srcSlot) || InventorySlots.IsBankPos(dstBag, dstSlot)) && !inventory.BankUsable)
        {
            inventory.SendEquipError(InventoryResult.TooFarAwayFromBank, inventory.GetItem(srcBag, srcSlot), null);
            return;
        }

        inventory.SplitItem(srcBag, srcSlot, dstBag, dstSlot, count);
    }

    /// <summary>CMSG_DESTROYITEM: u8 bag, u8 slot, u8 count, u8 data1-3 (unused) (WorldSession::HandleDestroyItemOpcode).</summary>
    private static void HandleDestroyItem(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        byte bag = reader.ReadByte();
        byte slot = reader.ReadByte();
        byte count = reader.ReadByte();
        player.Inventory.DestroyItemRequest(bag, slot, count);
    }

    /// <summary>
    /// CMSG_ITEM_QUERY_SINGLE: u32 entry, u64 guid (unused) → SMSG_ITEM_QUERY_SINGLE_RESPONSE,
    /// or entry | 0x80000000 for an unknown entry (WorldSession::HandleItemQuerySingleOpcode).
    /// </summary>
    private static async Task HandleItemQuerySingleAsync(WorldSession session, byte[] payload)
    {
        var reader = new PacketReader(payload);
        uint entry = reader.ReadUInt32();
        ItemsFeature items = session.Services.GetRequiredService<ItemsFeature>();
        var templates = await items.EnsureLoadedAsync().ConfigureAwait(false);
        session.Send(WorldOpcode.SmsgItemQuerySingleResponse, templates.Find(entry) is { } template
            ? ItemPackets.ItemQueryResponse(template)
            : ItemPackets.ItemQueryUnknown(entry));
    }
}
