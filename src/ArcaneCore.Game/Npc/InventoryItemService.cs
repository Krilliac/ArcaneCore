using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ItemsResult = ArcaneCore.Game.Items.InventoryResult;
using NpcResult = ArcaneCore.Game.Npc.InventoryResult;

namespace ArcaneCore.Game.Npc;

/// <summary>
/// <see cref="IItemService"/> over each player's <see cref="PlayerInventory"/> (the items area's
/// vmangos port). Quest item counters are not reported from here: the inventory raises
/// <see cref="PlayerInventory.ItemCountChanged"/> for every add/remove and the quest owner listens
/// there, so vendor trades are counted once. World thread.
/// </summary>
/// <param name="templates">Item content (resolved lazily: features attach in name order).</param>
/// <param name="repairCosts">DurabilityCosts/DurabilityQuality (empty: repairs fail closed).</param>
/// <param name="bankSlotPrices">BankBagSlotPrices (empty: no slot is sold).</param>
/// <param name="unixNow">Seconds clock for buyback timestamps.</param>
/// <param name="persistBankBagSlots">
/// Optional purchase veto used by alternate item services. The normal world path saves the
/// count and money together in the next character snapshot (CharacterBankSlotsDataModule).
/// </param>
public sealed class InventoryItemService(
    Func<IItemTemplateStore> templates,
    RepairCostTable repairCosts,
    BankBagSlotPriceTable bankSlotPrices,
    Func<long> unixNow,
    Func<Player, byte, bool>? persistBankBagSlots = null) : IItemService
{
    private readonly Dictionary<ObjectGuid, long> _sessionStart = [];

    public ItemInfo? GetItem(uint itemId) => templates().Find(itemId) is { } t
        ? new ItemInfo(t.Entry, t.DisplayId, t.BuyPrice, t.BuyCount == 0 ? 1 : t.BuyCount, t.MaxDurability, t.AllowableClass, t.AllowableRace,
            t.Bonding, t.RequiredReputationFaction, t.RequiredReputationRank, t.RequiredHonorRank, t.RequiredLevel)
        : null;

    public uint GetItemCount(Player player, uint itemId, bool inBankAlso) => player.Inventory.GetItemCount(itemId, inBankAlso);

    public NpcResult CanStoreNewItem(Player player, uint itemId, uint count)
        => (NpcResult)(byte)player.Inventory.CanStoreNewItem(itemId, count, [], out _);

    public bool StoreNewItem(Player player, uint itemId, uint count)
        => player.Inventory.AddItem(itemId, count, out _, received: true) == ItemsResult.Ok;

    public byte? FindBagSlot(Player player, ObjectGuid bagGuid) => player.Inventory.FindBagSlot(bagGuid);

    public NpcResult CanStoreNewItemAt(Player player, uint itemId, uint count, byte bag, byte slot)
        => (NpcResult)(byte)player.Inventory.CheckAddItemAt(bag, slot, itemId, count);

    public bool StoreNewItemAt(Player player, uint itemId, uint count, byte bag, byte slot)
        => player.Inventory.AddItemAt(bag, slot, itemId, count, out _, received: true) == ItemsResult.Ok;

    public void DestroyItemCount(Player player, uint itemId, uint count) => player.Inventory.DestroyItemCount(itemId, count);

    public ItemSale SellToVendor(Player player, ObjectGuid vendor, ObjectGuid item, byte count)
    {
        VendorSellError error = player.Inventory.SellItem(item, count, BuybackTimestamp(player), out uint money);
        return error switch
        {
            VendorSellError.None => ItemSale.Succeeded(money),
            VendorSellError.CantFindItem => ItemSale.Failed(SellResult.CantFindItem),
            _ => ItemSale.Failed(SellResult.CantSellItem),
        };
    }

    public void SendEquipError(Player player, NpcResult result, uint itemId)
        => player.Inventory.SendEquipError((ItemsResult)(byte)result, null, null, 0, itemId);

    public BuybackInfo? GetBuyback(Player player, byte slot)
        => player.Inventory.GetBuybackItem(slot) is { } item ? new BuybackInfo(item.Entry, player.Inventory.GetBuybackPrice(slot)) : null;

    public NpcResult RestoreBuyback(Player player, byte slot)
    {
        var dest = new List<ItemPosCount>();
        ItemsResult result = player.Inventory.CanRestoreBuyback(slot, dest);
        if (result != ItemsResult.Ok)
        {
            return (NpcResult)(byte)result;
        }

        player.Inventory.RestoreBuyback(slot, dest);
        return NpcResult.Ok;
    }

    public uint Repair(Player player, ObjectGuid item, float discount, Func<uint, bool> pay)
    {
        uint total = 0;
        foreach (Item candidate in player.Inventory.RepairCandidates(item))
        {
            uint max = candidate.MaxDurability;
            if (max == 0 || candidate.Durability >= max)
            {
                continue;
            }

            ItemTemplate t = candidate.Template;
            if (!repairCosts.TryGetCost(t.Class, t.SubClass, t.ItemLevel, t.Quality, max - candidate.Durability, out uint baseCost))
            {
                continue; // vmangos: unknown item level / quality row — nothing repaired
            }

            // vmangos Player.cpp:4953-4958 truncates the base DBC cost first, then rounds the reputation-discounted amount with +0.5f.
            uint cost = ReputationPricing.Round(baseCost, discount); // Player.cpp:4955 uint32(costs * discountMod + 0.5f)
            cost = Math.Max(cost, 1u); // vmangos "fix for ITEM_QUALITY_ARTIFACT"
            if (!pay(cost))
            {
                continue;
            }

            player.Inventory.RepairDurability(candidate);
            total += cost;
        }

        return total;
    }

    public void DurabilityLossAll(Player player, double percent) => player.Inventory.DurabilityLossAll(percent, inventory: true);

    public void OpenBank(Player player, Func<bool> canUseBank) => player.Inventory.CanUseBank = canUseBank;

    public uint? BankBagSlotPrice(uint slot) => bankSlotPrices.Price(slot);

    public bool SetBankBagSlotCount(Player player, byte count)
    {
        if (persistBankBagSlots is not null && !persistBankBagSlots(player, count))
        {
            return false;
        }

        player.Inventory.BankBagSlotCount = count;
        return true;
    }

    /// <summary>A player's session began (buyback timestamps are relative to it, as vmangos m_logintime).</summary>
    public void SessionStarted(Player player) => _sessionStart[player.Guid] = unixNow();

    public void SessionEnded(Player player) => _sessionStart.Remove(player.Guid);

    /// <summary>vmangos AddItemToBuyBackSlot: time(nullptr) − m_logintime + 30 h.</summary>
    private uint BuybackTimestamp(Player player)
    {
        long now = unixNow();
        long start = _sessionStart.TryGetValue(player.Guid, out long s) ? s : now;
        long value = now - start + PlayerInventory.BuybackLifetimeSeconds;
        return (uint)Math.Clamp(value, 0, uint.MaxValue);
    }
}
