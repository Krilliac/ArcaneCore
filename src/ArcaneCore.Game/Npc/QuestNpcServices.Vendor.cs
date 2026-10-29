using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Npc;

/// <summary>Vendors (vmangos SendListInventory, Player::BuyItemFromVendor, HandleSellItemOpcode).</summary>
public sealed partial class QuestNpcServices
{
    /// <summary>MAX_VENDOR_ITEMS (vmangos CreatureDefines.h): the client shows at most 128 lines.</summary>
    public const int MaxVendorItems = 128;

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
        => BuyItemAt(player, vendorGuid, itemId, count, Items.InventorySlots.NullBag, Items.InventorySlots.NullSlot);

    /// <summary>
    /// CMSG_BUY_ITEM_IN_SLOT (vmangos HandleBuyItemInSlotOpcode, ItemHandler.cpp:659-686): the bag GUID
    /// is resolved to the backpack side or a worn bag first (an unknown bag is ignored as a cheat),
    /// then Player::BuyItemFromVendor runs with that (bag, slot).
    /// </summary>
    public void BuyItemInSlot(Player player, ObjectGuid vendorGuid, uint itemId, ObjectGuid bagGuid, byte slot, byte count)
    {
        if (Ready(player) is null || Deps.Items is not { } items || items.FindBagSlot(player, bagGuid) is not { } bag)
        {
            return;
        }

        BuyItemAt(player, vendorGuid, itemId, count, bag, slot);
    }

    private void BuyItemAt(Player player, ObjectGuid vendorGuid, uint itemId, byte count, byte bag, byte slot)
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

        VendorItem? vendorItem = null;
        uint clientSlot = 0;
        foreach (var (row, _, visibleSlot) in VisibleVendorItems(player, npc))
        {
            if (row.Item == itemId)
            {
                vendorItem = row;
                clientSlot = visibleSlot;
                break;
            }
        }

        if (vendorItem is null)
        {
            Send(player, WorldOpcode.SmsgBuyFailed, NpcPackets.BuyFailed(npc.Guid, itemId, BuyResult.CantFindItem));
            return;
        }

        uint totalCount = proto.BuyCount * count;
        if (vendorItem.MaxCount != 0 && CurrentStock(npc.Guid, vendorItem, proto) < totalCount)
        {
            Send(player, WorldOpcode.SmsgBuyFailed, NpcPackets.BuyFailed(npc.Guid, itemId, BuyResult.ItemAlreadySold));
            return;
        }

        if (!MeetsVendorReputation(player, npc, proto))
        {
            Send(player, WorldOpcode.SmsgBuyFailed, NpcPackets.BuyFailed(npc.Guid, itemId, BuyResult.ReputationRequire));
            return;
        }

        // Player::BuyItemFromVendor (Player.cpp:18431-18439): the CURRENT honor rank (not the highest, unlike equipping) and the
        // item's required level must both be met. Without an honor owner the rank items still fail closed.
        if (proto.RequiredHonorRank != 0
            && (Deps.Honor is not { } honor || honor.CurrentRank(player) < proto.RequiredHonorRank || player.Level < proto.RequiredLevel))
        {
            Send(player, WorldOpcode.SmsgBuyFailed, NpcPackets.BuyFailed(npc.Guid, itemId, BuyResult.RankRequire));
            return;
        }

        uint price = Discounted((ulong)proto.BuyPrice * count, PriceDiscount(player, npc));
        if (player.Money < price)
        {
            Send(player, WorldOpcode.SmsgBuyFailed, NpcPackets.BuyFailed(npc.Guid, itemId, BuyResult.NotEnoughMoney));
            return;
        }

        Items.InventoryResult store = items.CanStoreNewItemAt(player, itemId, totalCount, bag, slot);
        if (store != Items.InventoryResult.Ok)
        {
            items.SendEquipError(player, store, itemId);
            return;
        }

        if (!items.StoreNewItemAt(player, itemId, totalCount, bag, slot))
        {
            return;
        }

        ModifyMoney(s, -(long)price);

        uint newCount = UseStock(npc.Guid, vendorItem, proto, totalCount);

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

    /// <summary>
    /// CMSG_BUYBACK_ITEM (vmangos HandleBuybackItem): an interactable vendor, an item in the buyback
    /// slot, the slot price affordable and the item storable; then the price is taken and the item
    /// returns to the bags.
    /// </summary>
    public void BuybackItem(Player player, ObjectGuid vendorGuid, uint slot)
    {
        if (Ready(player) is not { } s || !player.IsInWorld)
        {
            return;
        }

        if (InteractableNpc(player, vendorGuid, NpcFlags.Vendor) is not { } npc)
        {
            LogMissing("BuybackItem", vendorGuid);
            Send(player, WorldOpcode.SmsgSellItem, NpcPackets.SellFailed(ObjectGuid.Empty, ObjectGuid.Empty, SellResult.CantFindVendor));
            return;
        }

        if (slot > byte.MaxValue || Deps.Items is not { } items || items.GetBuyback(player, (byte)slot) is not { } buyback)
        {
            Send(player, WorldOpcode.SmsgBuyFailed, NpcPackets.BuyFailed(npc.Guid, 0, BuyResult.CantFindItem));
            return;
        }

        if (player.Money < buyback.Price)
        {
            Send(player, WorldOpcode.SmsgBuyFailed, NpcPackets.BuyFailed(npc.Guid, buyback.Entry, BuyResult.NotEnoughMoney));
            return;
        }

        Items.InventoryResult result = items.RestoreBuyback(player, (byte)slot);
        if (result != Items.InventoryResult.Ok)
        {
            items.SendEquipError(player, result, buyback.Entry);
            return;
        }

        ModifyMoney(s, -(long)buyback.Price);
        Flush(s);
    }

    /// <summary>
    /// CMSG_REPAIR_ITEM (vmangos HandleRepairItemOpcode → DurabilityRepair / DurabilityRepairAll):
    /// an interactable NPC with UNIT_NPC_FLAG_REPAIR; one item (GUID) or everything carried (empty
    /// GUID), each item charged its discounted cost and skipped when that is not affordable.
    /// </summary>
    public void RepairItem(Player player, ObjectGuid npcGuid, ObjectGuid itemGuid)
    {
        if (Ready(player) is not { } s || !player.IsInWorld)
        {
            return;
        }

        if (InteractableNpc(player, npcGuid, NpcFlags.Repair) is not { } npc)
        {
            LogMissing("RepairItem", npcGuid);
            return;
        }

        if (Deps.Items is not { } items)
        {
            return;
        }

        uint paid = items.Repair(player, itemGuid, PriceDiscount(player, npc), cost =>
        {
            if (player.Money < cost)
            {
                return false;
            }

            // The item owner restores durability after this callback; save the completed batch below.
            ModifyMoney(s, -(long)cost, saveCharacter: false);
            return true;
        });
        if (paid > 0)
        {
            _sink.CharacterChanged(player);
            Flush(s);
        }
    }

    /// <summary>
    /// vmangos WorldSession::SendListInventory (the creature's own npc_vendor rows, then its shared npc_vendor_template rows; NpcStore merges
    /// them). The vendor pauses its movement first (ItemHandler.cpp:716-718, <see cref="PauseInteractionMovement"/>): one that walked on
    /// would leave INTERACTION_DISTANCE while the window is open, and the next sale would get SELL_ERR_CANT_FIND_VENDOR.
    /// </summary>
    internal void SendListInventory(PlayerNpcState s, ObjectGuid guid)
    {
        Player player = s.Quests.Player;
        if (InteractableNpc(player, guid, NpcFlags.Vendor) is not { } npc)
        {
            LogMissing("SendListInventory", guid);
            Send(player, WorldOpcode.SmsgSellItem, NpcPackets.SellFailed(ObjectGuid.Empty, ObjectGuid.Empty, SellResult.CantFindVendor));
            return;
        }

        InteractingCancels?.Invoke(player);
        PauseInteractionMovement(player, npc);
        var entries = new List<VendorListEntry>();
        float discount = PriceDiscount(player, npc);
        foreach (var (vendorItem, proto, slot) in VisibleVendorItems(player, npc))
        {
            uint left = vendorItem.MaxCount == 0 ? 0xFFFFFFFF : CurrentStock(npc.Guid, vendorItem, proto);
            entries.Add(new VendorListEntry(slot, vendorItem.Item, proto.DisplayId, left,
                Discounted(proto.BuyPrice, discount), proto.MaxDurability, proto.BuyCount));
        }

        Send(player, WorldOpcode.SmsgListInventory, NpcPackets.ListInventory(npc.Guid, entries));
    }

    /// <summary>
    /// The client-facing order and cap from vmangos WorldSession::SendListInventory (ItemHandler.cpp),
    /// with its Player::IsVendorItemVisible filter (Player.cpp). Purchases use this same view;
    /// vmangos Player::BuyItemFromVendor checks visibility but omits the packet's 128-row cap (MAX_VENDOR_ITEMS).
    /// </summary>
    private IEnumerable<(VendorItem Row, ItemInfo Proto, uint Slot)> VisibleVendorItems(Player player, NpcInfo npc)
    {
        if (Deps.Items is not { } items)
        {
            yield break;
        }

        uint slot = 0;
        foreach (VendorItem row in Npcs.VendorItems(npc.Entry))
        {
            if (slot >= MaxVendorItems)
            {
                yield break;
            }

            if (items.GetItem(row.Item) is not { } proto || !IsVendorItemVisible(player, npc, row, proto))
            {
                continue;
            }

            yield return (row, proto, ++slot);
        }
    }

    /// <summary>
    /// The first row of <paramref name="itemId"/> in <see cref="VisibleVendorItems"/>, without walking the rows before it: a row among
    /// the first <see cref="MaxVendorItems"/> of the list has fewer visible rows before it than the cap, so only its own visibility
    /// decides. Past that the cap depends on the visible rows before, and the full view is walked. Price checks ask this for every row
    /// of a vendor (a bot's town scan weighs each row of each vendor in sight), so the walk made them quadratic in the row count.
    /// </summary>
    private VendorItem? VisibleVendorRow(Player player, NpcInfo npc, uint itemId)
    {
        if (Deps.Items is not { } items)
        {
            return null;
        }

        IReadOnlyList<VendorItem> rows = Npcs.VendorItems(npc.Entry);
        int direct = Math.Min(rows.Count, MaxVendorItems);
        for (int i = 0; i < direct; i++)
        {
            VendorItem row = rows[i];
            if (row.Item == itemId && items.GetItem(row.Item) is { } proto && IsVendorItemVisible(player, npc, row, proto))
            {
                return row;
            }
        }

        return rows.Count <= MaxVendorItems ? null : VisibleVendorItems(player, npc).FirstOrDefault(item => item.Row.Item == itemId).Row;
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

    /// <summary>
    /// Player::GetReputationPriceDiscount (Player.cpp:19470-19510): 1 minus the Honored discount minus, for the capital and
    /// faction vendors, the honor rank discount (additive, so 0.8 and not 0.81). <paramref name="taxi"/> selects the
    /// flight master variant.
    /// </summary>
    private float PriceDiscount(Player player, NpcInfo npc, bool taxi = false)
    {
        float discount = Deps.Reputation?.GetPriceDiscount(player, npc) ?? 1.0f;
        return Deps.Honor is { } honor ? Honor.HonorPriceDiscount.Apply(discount, honor.VisualRank(player), npc.FactionId, taxi) : discount;
    }

    /// <summary>vmangos uint32(price × GetReputationPriceDiscount() + 0.5f) (vendors and trainers): <see cref="ReputationPricing"/>.</summary>
    private static uint Discounted(ulong price, float discount) => ReputationPricing.Round(price, discount);

    /// <summary>
    /// The buy-time reputation gate shared by BuyItem, the purchase price and the advisory quote: the item's own faction, or
    /// the vendor's when the item names a rank but no faction. Items without a rank are never gated; no reputation owner
    /// fails closed. Not the list-visibility rule in <see cref="IsVendorItemVisible"/>, which is deliberately different.
    /// </summary>
    private bool MeetsVendorReputation(Player player, NpcInfo npc, ItemInfo proto)
    {
        if (proto.RequiredReputationRank == 0)
        {
            return true;
        }

        uint faction = proto.RequiredReputationFaction != 0 ? proto.RequiredReputationFaction : npc.FactionId;
        return Deps.Reputation is { } reputation && reputation.GetReputationRank(player, faction) >= proto.RequiredReputationRank;
    }

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
