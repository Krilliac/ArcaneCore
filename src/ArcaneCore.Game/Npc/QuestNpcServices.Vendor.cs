using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Npc;

/// <summary>Vendors (vmangos SendListInventory, Player::BuyItemFromVendor, HandleSellItemOpcode).</summary>
public sealed partial class QuestNpcServices
{
    /// <summary>MAX_VENDOR_ITEMS (vmangos Creature.h): the client shows at most 150 lines.</summary>
    public const int MaxVendorItems = 150;

    /// <summary>Limited stock per creature and item (vmangos Creature::m_vendorItemCounts).</summary>
    private readonly Dictionary<(ObjectGuid Vendor, uint Item), VendorStock> _vendorStock = [];

    /// <summary>CMSG_LIST_INVENTORY (vmangos HandleListInventoryOpcode: alive only).</summary>
    public void ListInventory(Player player, ObjectGuid guid)
    {
        if (Ready(player) is not { } s || !player.IsAlive)
        {
            return;
        }

        SendListInventory(s, guid);
    }

    /// <summary>
    /// CMSG_BUY_ITEM (vmangos Player::BuyItemFromVendor with NULL_BAG/NULL_SLOT): the item must be
    /// listed and visible, in stock, reputation/honor rank met, affordable and storable; then money
    /// is taken, the item stored, stock reduced and SMSG_BUY_ITEM sent with the client's list slot.
    /// </summary>
    public void BuyItem(Player player, ObjectGuid vendorGuid, uint itemId, byte count)
    {
        if (Ready(player) is not { } s)
        {
            return;
        }

        count = Math.Max(count, (byte)1);
        if (!player.IsAlive)
        {
            return;
        }

        if (Deps.Items is not { } items || items.GetItem(itemId) is not { } proto)
        {
            Send(player, WorldOpcode.SmsgBuyFailed, NpcPackets.BuyFailed(ObjectGuid.Empty, itemId, BuyResult.CantFindItem));
            return;
        }

        if (InteractableNpc(player, vendorGuid, NpcFlags.Vendor) is not { } npc)
        {
            LogMissing("BuyItem", vendorGuid);
            Send(player, WorldOpcode.SmsgBuyFailed, NpcPackets.BuyFailed(ObjectGuid.Empty, itemId, BuyResult.DistanceTooFar));
            return;
        }

        IReadOnlyList<VendorItem> list = Npcs.VendorItems(npc.Entry);
        int vendorSlot = -1;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Item == itemId)
            {
                vendorSlot = i;
                break;
            }
        }

        if (vendorSlot < 0 || !IsVendorItemVisible(player, npc, list[vendorSlot], proto))
        {
            Send(player, WorldOpcode.SmsgBuyFailed, NpcPackets.BuyFailed(npc.Guid, itemId, BuyResult.CantFindItem));
            return;
        }

        VendorItem vendorItem = list[vendorSlot];
        uint totalCount = proto.BuyCount * count;
        if (vendorItem.MaxCount != 0 && CurrentStock(npc.Guid, vendorItem, proto) < totalCount)
        {
            Send(player, WorldOpcode.SmsgBuyFailed, NpcPackets.BuyFailed(npc.Guid, itemId, BuyResult.ItemAlreadySold));
            return;
        }

        uint reqFaction = proto.RequiredReputationFaction != 0 || proto.RequiredReputationRank == 0 ? proto.RequiredReputationFaction : npc.FactionId;
        if (proto.RequiredReputationRank > 0 && (Deps.Reputation is not { } rep || rep.GetReputationRank(player, reqFaction) < proto.RequiredReputationRank))
        {
            Send(player, WorldOpcode.SmsgBuyFailed, NpcPackets.BuyFailed(npc.Guid, itemId, BuyResult.ReputationRequire));
            return;
        }

        // PvP ranks belong to the honor owner, which this area has no seam to: rank items fail closed.
        if (proto.RequiredHonorRank != 0)
        {
            Send(player, WorldOpcode.SmsgBuyFailed, NpcPackets.BuyFailed(npc.Guid, itemId, BuyResult.RankRequire));
            return;
        }

        uint price = (uint)((proto.BuyPrice * count * PriceDiscount(player, npc)) + 0.5f);
        if (player.Money < price)
        {
            Send(player, WorldOpcode.SmsgBuyFailed, NpcPackets.BuyFailed(npc.Guid, itemId, BuyResult.NotEnoughMoney));
            return;
        }

        InventoryResult store = items.CanStoreNewItem(player, itemId, totalCount);
        if (store != InventoryResult.Ok)
        {
            items.SendEquipError(player, store, itemId);
            return;
        }

        if (!items.StoreNewItem(player, itemId, totalCount))
        {
            return;
        }

        ModifyMoney(s, -(long)price);

        uint newCount = UseStock(npc.Guid, vendorItem, proto, totalCount);

        // SMSG_LIST_INVENTORY numbers only visible rows: report the same compact slot.
        uint clientSlot = 0;
        for (int i = 0; i <= vendorSlot; i++)
        {
            if (items.GetItem(list[i].Item) is { } listed && IsVendorItemVisible(player, npc, list[i], listed))
            {
                clientSlot++;
            }
        }

        Send(player, WorldOpcode.SmsgBuyItem, NpcPackets.BuyItem(npc.Guid, clientSlot, vendorItem.MaxCount > 0 ? newCount : 0xFFFFFFFF, count));
        Flush(s);
    }

    /// <summary>
    /// CMSG_SELL_ITEM (vmangos HandleSellItemOpcode): the vendor must be interactable; the item
    /// checks and buyback belong to the items owner, the money is added here.
    /// </summary>
    public void SellItem(Player player, ObjectGuid vendorGuid, ObjectGuid itemGuid, byte count)
    {
        if (Ready(player) is not { } s || itemGuid.IsEmpty || !player.IsInWorld)
        {
            return;
        }

        if (InteractableNpc(player, vendorGuid, NpcFlags.Vendor) is not { } npc)
        {
            LogMissing("SellItem", vendorGuid);
            Send(player, WorldOpcode.SmsgSellItem, NpcPackets.SellFailed(ObjectGuid.Empty, itemGuid, SellResult.CantFindVendor));
            return;
        }

        if (Deps.Items is not { } items)
        {
            Send(player, WorldOpcode.SmsgSellItem, NpcPackets.SellFailed(npc.Guid, itemGuid, SellResult.CantFindItem));
            return;
        }

        ItemSale sale = items.SellToVendor(player, npc.Guid, itemGuid, count);
        if (sale.Error is { } error)
        {
            Send(player, WorldOpcode.SmsgSellItem, NpcPackets.SellFailed(npc.Guid, itemGuid, error));
        }
        else if (sale.Sold)
        {
            ModifyMoney(s, sale.Money);
        }

        Flush(s);
    }

    /// <summary>vmangos WorldSession::SendListInventory (npc_vendor rows; vendor templates are not modelled).</summary>
    internal void SendListInventory(PlayerNpcState s, ObjectGuid guid)
    {
        Player player = s.Quests.Player;
        if (InteractableNpc(player, guid, NpcFlags.Vendor) is not { } npc)
        {
            LogMissing("SendListInventory", guid);
            Send(player, WorldOpcode.SmsgSellItem, NpcPackets.SellFailed(ObjectGuid.Empty, ObjectGuid.Empty, SellResult.CantFindVendor));
            return;
        }

        var entries = new List<VendorListEntry>();
        float discount = PriceDiscount(player, npc);
        foreach (VendorItem vendorItem in Npcs.VendorItems(npc.Entry))
        {
            if (Deps.Items?.GetItem(vendorItem.Item) is not { } proto || !IsVendorItemVisible(player, npc, vendorItem, proto))
            {
                continue;
            }

            uint left = vendorItem.MaxCount == 0 ? 0xFFFFFFFF : CurrentStock(npc.Guid, vendorItem, proto);
            entries.Add(new VendorListEntry((uint)entries.Count + 1, vendorItem.Item, proto.DisplayId, left,
                (uint)((proto.BuyPrice * discount) + 0.5f), proto.MaxDurability, proto.BuyCount));
            if (entries.Count >= MaxVendorItems)
            {
                break;
            }
        }

        Send(player, WorldOpcode.SmsgListInventory, NpcPackets.ListInventory(npc.Guid, entries));
    }

    /// <summary>vmangos Player::IsVendorItemVisible (build &gt; 1.6.1: item reputation faction does not hide).</summary>
    private bool IsVendorItemVisible(Player player, NpcInfo npc, VendorItem vendorItem, ItemInfo proto)
    {
        if (player.IsGameMaster)
        {
            return true;
        }

        if ((proto.AllowableClass & Mask((byte)player.Class)) == 0 && proto.Bonding == ArcaneCore.Game.Quests.QuestConstants.BindWhenPickedUp)
        {
            return false;
        }

        if ((proto.AllowableRace & Mask((byte)player.Race)) == 0)
        {
            return false;
        }

        if (proto.RequiredReputationFaction == 0 && proto.RequiredReputationRank > 0
            && (Deps.Reputation is not { } rep || proto.RequiredReputationRank > rep.GetReputationRank(player, npc.FactionId)))
        {
            return false;
        }

        return vendorItem.ConditionId == 0 || (Deps.Conditions?.IsSatisfied(vendorItem.ConditionId, player, npc) ?? false);
    }

    private float PriceDiscount(Player player, NpcInfo npc) => Deps.Reputation?.GetPriceDiscount(player, npc) ?? 1.0f;

    /// <summary>vmangos Creature::GetVendorItemCurrentCount (restock by BuyCount every incrtime seconds).</summary>
    private uint CurrentStock(ObjectGuid vendor, VendorItem item, ItemInfo proto)
    {
        if (item.MaxCount == 0)
        {
            return 0;
        }

        if (!_vendorStock.TryGetValue((vendor, item.Item), out VendorStock? stock))
        {
            return item.MaxCount;
        }

        long now = UnixNow;
        if (stock.RestockDelay > 0 && stock.LastIncrement + stock.RestockDelay <= now)
        {
            long diff = (now - stock.LastIncrement) / stock.RestockDelay;
            if (stock.Count + (diff * proto.BuyCount) >= item.MaxCount)
            {
                _vendorStock.Remove((vendor, item.Item));
                return item.MaxCount;
            }

            stock.Count += (uint)(diff * proto.BuyCount);
            stock.LastIncrement = now;
        }

        return stock.Count;
    }

    /// <summary>vmangos Creature::UpdateVendorItemCurrentCount.</summary>
    private uint UseStock(ObjectGuid vendor, VendorItem item, ItemInfo proto, uint used)
    {
        if (item.MaxCount == 0)
        {
            return 0;
        }

        long now = UnixNow;
        if (!_vendorStock.TryGetValue((vendor, item.Item), out VendorStock? stock))
        {
            uint fresh = item.MaxCount > used ? item.MaxCount - used : 0;
            _vendorStock[(vendor, item.Item)] = new VendorStock { Count = fresh, LastIncrement = now, RestockDelay = item.IncrTime };
            return fresh;
        }

        if (stock.RestockDelay > 0 && stock.LastIncrement + stock.RestockDelay <= now)
        {
            long diff = (now - stock.LastIncrement) / stock.RestockDelay;
            stock.Count = (uint)Math.Min(stock.Count + (diff * proto.BuyCount), item.MaxCount);
        }

        stock.Count = stock.Count > used ? stock.Count - used : 0;
        stock.LastIncrement = now;
        stock.RestockDelay = item.IncrTime;
        return stock.Count;
    }

    private sealed class VendorStock
    {
        public uint Count { get; set; }

        public long LastIncrement { get; set; }

        public long RestockDelay { get; set; }
    }
}
