using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Game.Items;

/// <summary>
/// A detached economy inventory transfer (mail, auction, trade): existing items leave and
/// existing item instances arrive with their GUIDs and data preserved. Preparation never
/// changes the owner's fields or sends packets; the settlement publishes it after commit.
/// </summary>
public sealed class EconomyInventoryStage
{
    internal EconomyInventoryStage(PlayerInventory inventory, InventorySnapshot before, InventorySnapshot after,
        IReadOnlyList<Item> removed, IReadOnlyList<(IReadOnlyList<ItemPosCount> Dest, ItemInstanceData Data)> added,
        IReadOnlyList<(Item Existing, ItemInstanceData Data)>? replacements = null,
        IReadOnlyList<InventoryRewardGrant>? consumes = null,
        IReadOnlyList<(Item Existing, uint Count)>? consumeItems = null)
    {
        Inventory = inventory;
        Before = before;
        After = after;
        Removed = removed;
        Added = added;
        Replacements = replacements ?? [];
        Consumes = [.. consumes ?? []];
        ConsumeItems = [.. consumeItems ?? []];
    }

    public PlayerInventory Inventory { get; }

    /// <summary>The complete inventory the transfer was planned against.</summary>
    public InventorySnapshot Before { get; }

    /// <summary>The complete inventory after the transfer.</summary>
    public InventorySnapshot After { get; }

    internal IReadOnlyList<Item> Removed { get; }

    /// <summary>Each arriving instance with its planned destination (merges into existing stacks first, then at most one free slot).</summary>
    internal IReadOnlyList<(IReadOnlyList<ItemPosCount> Dest, ItemInstanceData Data)> Added { get; }
    internal IReadOnlyList<(Item Existing, ItemInstanceData Data)> Replacements { get; }
    internal IReadOnlyList<InventoryRewardGrant> Consumes { get; }
    internal IReadOnlyList<(Item Existing, uint Count)> ConsumeItems { get; }

    public bool Applied { get; internal set; }

    /// <summary>Full reagent stacks destroyed by this stage, rather than transferred.</summary>
    public IReadOnlyList<uint> ConsumedItemGuids => Array.AsReadOnly(ConsumeItems.Select(c => c.Existing.Guid.Low)
        .Distinct().Where(guid => Before.Items.Any(row => row.Item.Guid == guid)
            && !After.Items.Any(row => row.Item.Guid == guid)).ToArray());

    /// <summary>Arriving instances merged whole into existing stacks: their GUIDs end with this transfer.</summary>
    public IReadOnlyList<uint> MergedItemGuids => Array.AsReadOnly(Added.Select(a => a.Data.Guid)
        .Where(guid => !After.Items.Any(row => row.Item.Guid == guid)).ToArray());

    /// <summary>The persistent data of the leaving items, as they will be escrowed or handed over.</summary>
    public IReadOnlyList<ItemInstanceData> RemovedData => Removed.Select(i => i.ToData()).ToArray();
}

public sealed partial class PlayerInventory
{
    /// <summary>
    /// Whether an item may be offered in a trade (vmangos Item::CanBeTraded, Item.cpp:932-952):
    /// loaded, carried in the backpack or a carried bag (not equipped, not an equipped bag, not in
    /// the bank; the carried rule is stricter than vmangos' CanUnequipItem and keeps client-supplied
    /// positions honest), not soulbound nor bound by an enchantment, and an empty bag only. Conjured and timed items are
    /// tradable (TradeHandler.cpp:313,322,702 check only CanBeTraded).
    /// </summary>
    public InventoryResult CanBeTraded(Item item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!ReferenceEquals(item.Inventory, this) || !ReferenceEquals(GetItemByGuid(item.Guid), item))
        {
            return InventoryResult.ItemNotFound;
        }

        byte position = item.Container?.Slot ?? item.Slot;
        bool carried = item.Container is not null
            ? position >= InventorySlots.BagStart && position < InventorySlots.BagEnd
            : position >= InventorySlots.ItemStart && position < InventorySlots.ItemEnd;
        if (!carried)
        {
            return InventoryResult.ItemNotFound;
        }

        // vmangos Item::CanBeTraded also refuses IsBoundByEnchant (Item.cpp:950): an enchantment that can soulbind the item
        // does not set the bound flag, so it is checked separately (no enchantment engine attached: no catalog to read).
        if (item.IsSoulBound || Player?.Enchantments?.IsBoundByEnchant(item) == true)
        {
            return InventoryResult.CantDropSoulbound;
        }

        // vmangos Item::CanBeTraded (Item.cpp:932-952) refuses an item whose generated
        // loot still holds money or items. Mail and auction use this same departure gate.
        if (item.Loot is { } loot && (loot.Gold != 0 || loot.Items.Count != 0))
        {
            return InventoryResult.AlreadyLooted;
        }

        return item is Container { IsEmpty: false } ? InventoryResult.CanOnlyDoWithEmptyBags : InventoryResult.Ok;
    }

    /// <summary>
    /// Whether an item may leave this inventory by mail or auction: <see cref="CanBeTraded"/> plus the
    /// handlers' extra rule that conjured or timed items are refused (MailHandler.cpp:301-307,
    /// AuctionHouseHandler.cpp:332-342).
    /// </summary>
    public InventoryResult CanTransferOut(Item item)
    {
        InventoryResult tradable = CanBeTraded(item);
        if (tradable != InventoryResult.Ok)
        {
            return tradable;
        }

        return (item.Template.Flags & (uint)ItemTemplateFlags.Conjured) != 0 || item.ToData().Duration != 0
            ? InventoryResult.ItemNotFound
            : InventoryResult.Ok;
    }

    /// <summary>
    /// Plan removing <paramref name="remove"/> and adding the existing instances in <paramref name="add"/>
    /// where vmangos CanStoreItem(NULL_BAG, NULL_SLOT, …, item) / StoreItem put them: room in existing
    /// stacks first, then one free slot. A part placed in a free slot keeps its GUID and data; an
    /// instance merged whole into stacks ends (<see cref="EconomyInventoryStage.MergedItemGuids"/>), as
    /// vmangos _StoreItem deletes it. The ordinary unique-count limits apply.
    /// </summary>
    public InventoryResult TryStageEconomyTransfer(IReadOnlyList<ObjectGuid> remove, IReadOnlyList<ItemInstanceData> add,
        out EconomyInventoryStage? stage, bool trade = false)
        => TryStageEconomyTransfer(remove, add, out stage, trade, null, null);

    public InventoryResult TryStageEconomyTransfer(IReadOnlyList<ObjectGuid> remove, IReadOnlyList<ItemInstanceData> add,
        out EconomyInventoryStage? stage, bool trade, IReadOnlyList<ItemInstanceData>? replacements,
        IReadOnlyList<InventoryRewardGrant>? consume, ItemUsePaymentPlan? itemUse = null)
    {
        ArgumentNullException.ThrowIfNull(remove);
        ArgumentNullException.ThrowIfNull(add);
        stage = null;
        if (!_loaded)
        {
            return InventoryResult.CantDoRightNow;
        }

        if (remove.Distinct().Count() != remove.Count || add.Select(a => a.Guid).Distinct().Count() != add.Count)
        {
            return InventoryResult.ItemNotFound;
        }

        var removed = new List<Item>();
        foreach (ObjectGuid guid in remove)
        {
            if (GetItemByGuid(guid) is not { } item)
            {
                return InventoryResult.ItemNotFound;
            }

            InventoryResult allowed = trade ? CanBeTraded(item) : CanTransferOut(item);
            if (allowed != InventoryResult.Ok)
            {
                return allowed;
            }

            removed.Add(item);
        }

        InventorySnapshot before = FreezeRewardSnapshot(CreateSnapshot());
        if (add.Any(a => a.Guid == 0 || before.Items.Any(row => row.Item.Guid == a.Guid)))
        {
            return InventoryResult.CantDoRightNow;
        }

        HashSet<uint> leaving = [.. removed.Select(i => i.Guid.Low)];
        var shadow = new PlayerInventory(OwnerGuid, Race, Class, Level) { Templates = Templates, GuidAllocator = GuidAllocator };
        shadow.Load(before.Items.Where(row => !leaving.Contains(row.Item.Guid)));
        var replacementPlan = new List<(Item Existing, ItemInstanceData Data)>();
        var consumeItems = new List<(Item Existing, uint Count)>();
        IReadOnlyList<ItemInstanceData> replacementInputs = replacements ?? [];
        if (replacementInputs.Select(data => data.Guid).Distinct().Count() != replacementInputs.Count)
            return InventoryResult.ItemNotFound;

        // Cast-item payment is validated against the complete live image and the pure item-use
        // recomputation. Its charge-only replacement is kept separate from enchant replacements.
        if (itemUse is { } payment)
        {
            ObjectGuid castGuid = ObjectGuid.Item(payment.Before.Guid);
            if (payment.Before.Guid == 0 || remove.Contains(castGuid) || add.Any(data => data.Guid == payment.Before.Guid)
                || GetItemByGuid(castGuid) is not { } liveCast || !ReferenceEquals(liveCast.Inventory, this)
                || liveCast.OwnerGuid != OwnerGuid
                || CanUnequipItem(liveCast.BagSlot, liveCast.Slot, swap: false) != InventoryResult.Ok
                || !SameRewardItem(payment.Before, liveCast.ToData()))
                return InventoryResult.ItemNotFound;

            ItemUsePaymentPlan recomputed = ItemUsePaymentPlan.Create(liveCast);
            bool sameAfter = payment.After is null
                ? recomputed.After is null
                : recomputed.After is not null && SameRewardItem(payment.After, recomputed.After);
            if (!SameRewardItem(payment.Before, recomputed.Before) || !sameAfter
                || payment.DestroyCount != recomputed.DestroyCount)
                return InventoryResult.ItemNotFound;

            Item shadowCast = shadow.GetItemByGuid(castGuid)
                ?? throw new InvalidOperationException("planned cast item disappeared");
            if (replacementInputs.Any(data => data.Guid == payment.Before.Guid))
                return InventoryResult.ItemNotFound;

            if (payment.DestroyCount == 1)
            {
                // ConsumeItems owns count handling and exact GUID destruction. For a surviving
                // stack, apply only the charge delta before consuming one unit in the shadow.
                if (payment.After is not null)
                {
                    ItemInstanceData chargeOnly = payment.After with { Count = payment.Before.Count };
                    if (!SameRewardItem(payment.Before, chargeOnly))
                    {
                        replacementPlan.Add((liveCast, chargeOnly));
                        shadow.ReplaceDetached(shadowCast.Guid, chargeOnly);
                    }
                    shadowCast = shadow.GetItemByGuid(castGuid)
                        ?? throw new InvalidOperationException("planned cast item disappeared after payment");
                }

                consumeItems.Add((liveCast, 1));
                if (shadow.DestroyItemCount(shadowCast, 1) != 1)
                    return InventoryResult.ItemNotFound;
            }
            else if (payment.After is { } paid)
            {
                if (!SameRewardItem(payment.Before, paid))
                {
                    replacementPlan.Add((liveCast, paid));
                    shadow.ReplaceDetached(shadowCast.Guid, paid);
                }
            }
        }

        foreach (ItemInstanceData data in replacementInputs)
        {
            if (data.Guid == 0 || remove.Contains(ObjectGuid.Item(data.Guid)) || shadow.GetItemByGuid(ObjectGuid.Item(data.Guid)) is not { } existing
                || existing.OwnerGuid != OwnerGuid || Templates.Find(data.Entry) is null
                || GetItemByGuid(ObjectGuid.Item(data.Guid)) is not { } liveExisting
                || existing.Count != data.Count || existing.Entry != data.Entry)
                return InventoryResult.ItemNotFound;
            ItemInstanceData original = before.Items.Single(row => row.Item.Guid == data.Guid).Item;
            if (!SameRewardItem(original, data with { Enchantments = original.Enchantments }))
                return InventoryResult.ItemNotFound;
            ItemInstanceData frozen = original with { Enchantments = Array.AsReadOnly(data.Enchantments.ToArray()) };
            replacementPlan.Add((liveExisting, frozen));
            shadow.ReplaceDetached(existing.Guid, frozen);
        }
        foreach (InventoryRewardGrant grant in consume ?? [])
        {
            if (grant.Entry == 0 || grant.Count is 0 or > int.MaxValue)
                return InventoryResult.ItemNotFound;
            uint remaining = grant.Count;
            foreach (Item shadowItem in shadow.RemovalOrder(false).Where(i => i.Entry == grant.Entry).ToList())
            {
                if (remaining == 0) break;
                if (shadowItem.Container is null && shadowItem.Slot < InventorySlots.BagEnd
                    && shadow.CanUnequipItem(InventorySlots.Bag0, shadowItem.Slot, swap: false) != InventoryResult.Ok)
                    continue;
                uint take = Math.Min(remaining, shadowItem.Count);
                Item liveItem = GetItemByGuid(shadowItem.Guid)
                    ?? throw new InvalidOperationException("planned reagent item disappeared");
                if (replacementPlan.Any(r => r.Existing.Guid == liveItem.Guid)
                    || consumeItems.Any(c => c.Existing.Guid == liveItem.Guid)
                    || (itemUse is not null && liveItem.Guid == ObjectGuid.Item(itemUse.Before.Guid))
                    || CanUnequipItem(liveItem.BagSlot, liveItem.Slot, swap: false) != InventoryResult.Ok)
                    return InventoryResult.ItemNotFound;
                consumeItems.Add((liveItem, take));
                shadow.DestroyItemCount(shadowItem, take);
                remaining -= take;
            }
            if (remaining != 0) return InventoryResult.ItemNotFound;
        }
        var placed = new List<(IReadOnlyList<ItemPosCount> Dest, ItemInstanceData Data)>();
        foreach (ItemInstanceData data in add)
        {
            if (data.Count == 0 || Templates.Find(data.Entry) is not { } template || data.Count > Math.Max(template.Stackable, 1u))
            {
                return InventoryResult.ItemNotFound;
            }

            InventoryResult limit = shadow.CanTakeMoreSimilarItems(template, data.Count, null, out _);
            if (limit != InventoryResult.Ok)
            {
                return limit;
            }

            Item item = Item.Create(data.Guid, template, _ownerGuid);
            item.Load(data);
            var dest = new List<ItemPosCount>();
            InventoryResult room = shadow.CanStoreItem(InventorySlots.NullBag, InventorySlots.NullSlot, dest, item, swap: false, out _);
            if (room != InventoryResult.Ok)
            {
                return room;
            }

            // Only the last position may be a free slot: StoreItem would otherwise clone the instance under a new GUID.
            if (dest.Count == 0 || dest.Take(dest.Count - 1).Any(pos => shadow.GetItem(pos.Bag, pos.Slot) is null))
            {
                return InventoryResult.ItemNotFound;
            }

            shadow.StoreItem(dest, item);
            placed.Add((dest.AsReadOnly(), data));
        }

        stage = new EconomyInventoryStage(this, before, FreezeRewardSnapshot(shadow.CreateSnapshot()), removed, placed, replacementPlan, consume, consumeItems);
        return InventoryResult.Ok;
    }

    /// <summary>
    /// Publish a committed transfer: the leaving items are destroyed client-side, the arriving
    /// instances are created in their planned slots. Throws (the caller requires a fresh login)
    /// when the live inventory no longer matches the planned Before or the result differs from After.
    /// </summary>
    public void ApplyEconomyTransfer(EconomyInventoryStage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        Player?.EnsureQuestSettlementMutationAllowed();
        if (!ReferenceEquals(stage.Inventory, this) || stage.Applied || !SameEconomySnapshot(stage.Before, CreateSnapshot())
            || stage.Removed.Any(i => !ReferenceEquals(GetItemByGuid(i.Guid), i)))
        {
            throw new InvalidOperationException("the inventory changed while its economy transfer was settling");
        }

        foreach (Item item in stage.Removed)
        {
            RemoveItem(item.BagSlot, item.Slot);
            ItemCountChanged?.Invoke(item.Entry, -(int)item.Count);
            Discard(item);
        }

        foreach ((Item existing, ItemInstanceData data) in stage.Replacements)
        {
            if (existing.Inventory != this || GetItemByGuid(existing.Guid) is not { } live || !ReferenceEquals(existing, live))
                throw new InvalidOperationException("the replacement item changed while its economy transfer was settling");
            // The live item keeps its identity: its enchantments come off before the committed after-image is loaded and go back on from
            // it (a trade enchant lands this way), so the worn bonuses always match the item fields (vmangos ApplyEnchantment pairs).
            Player?.Enchantments?.Apply(existing, apply: false);
            existing.Load(data);
            Player?.Enchantments?.Apply(existing, apply: true);
        }

        foreach ((Item item, uint count) in stage.ConsumeItems)
        {
            if (!ReferenceEquals(GetItemByGuid(item.Guid), item) || DestroyItemCount(item, count) != count)
                throw new InvalidOperationException("the reagent inventory changed while its economy transfer was settling");
        }

        foreach ((IReadOnlyList<ItemPosCount> dest, ItemInstanceData data) in stage.Added)
        {
            ItemTemplate template = Templates.Find(data.Entry)
                ?? throw new InvalidOperationException($"item template {data.Entry} disappeared during settlement");
            Item item = Item.Create(data.Guid, template, _ownerGuid);
            item.Load(data);
            uint count = item.Count;
            StoreItem(dest, item); // the planned merges and free slot; a fresh instance in a free slot is created client-side
            ItemCountChanged?.Invoke(item.Entry, (int)count);
        }

        stage.Applied = true;
        if (!SameEconomySnapshot(stage.After, CreateSnapshot()))
        {
            throw new InvalidOperationException("the published economy inventory differs from the committed one");
        }
    }

    /// <summary>Order-insensitive comparison of complete inventories (positions and every persistent field).</summary>
    public static bool SameEconomySnapshot(InventorySnapshot a, InventorySnapshot b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        InventoryItemData[] left = [.. a.Items.OrderBy(i => i.Item.Guid)];
        InventoryItemData[] right = [.. b.Items.OrderBy(i => i.Item.Guid)];
        return left.Length == right.Length && left.Zip(right).All(p => p.First.ContainerGuid == p.Second.ContainerGuid
            && p.First.Slot == p.Second.Slot && SameRewardItem(p.First.Item, p.Second.Item));
    }

    private void ReplaceDetached(ObjectGuid guid, ItemInstanceData data)
    {
        if (GetItemByGuid(guid) is not { } item || Templates.Find(data.Entry) is not { } template)
            throw new InvalidOperationException("replacement item disappeared during detached planning");
        item.Load(data);
    }
}
