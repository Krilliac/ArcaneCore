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
        IReadOnlyList<Item> removed, IReadOnlyList<(byte Bag, byte Slot, ItemInstanceData Data)> added)
    {
        Inventory = inventory;
        Before = before;
        After = after;
        Removed = removed;
        Added = added;
    }

    public PlayerInventory Inventory { get; }

    /// <summary>The complete inventory the transfer was planned against.</summary>
    public InventorySnapshot Before { get; }

    /// <summary>The complete inventory after the transfer.</summary>
    public InventorySnapshot After { get; }

    internal IReadOnlyList<Item> Removed { get; }

    internal IReadOnlyList<(byte Bag, byte Slot, ItemInstanceData Data)> Added { get; }

    public bool Applied { get; internal set; }

    /// <summary>The persistent data of the leaving items, as they will be escrowed or handed over.</summary>
    public IReadOnlyList<ItemInstanceData> RemovedData => Removed.Select(i => i.ToData()).ToArray();
}

public sealed partial class PlayerInventory
{
    /// <summary>
    /// Whether an item may be offered in a trade (vmangos Item::CanBeTraded, Item.cpp:932-952):
    /// loaded, carried in the backpack or a carried bag (not equipped, not an equipped bag, not in
    /// the bank; the carried rule is stricter than vmangos' CanUnequipItem and keeps client-supplied
    /// positions honest), not soulbound, and an empty bag only. Conjured and timed items are
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

        if (item.IsSoulBound)
        {
            return InventoryResult.CantDropSoulbound;
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
    /// (each placed whole into a free backpack or general-bag slot, never merged, so its GUID and data
    /// survive unchanged). The ordinary unique-count limits apply.
    /// </summary>
    public InventoryResult TryStageEconomyTransfer(IReadOnlyList<ObjectGuid> remove, IReadOnlyList<ItemInstanceData> add,
        out EconomyInventoryStage? stage, bool trade = false)
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
        var placed = new List<(byte Bag, byte Slot, ItemInstanceData Data)>();
        foreach (ItemInstanceData data in add)
        {
            if (data.Count == 0 || Templates.Find(data.Entry) is not { } template)
            {
                return InventoryResult.ItemNotFound;
            }

            InventoryResult limit = shadow.CanTakeMoreSimilarItems(template, data.Count, null, out _);
            if (limit != InventoryResult.Ok)
            {
                return limit;
            }

            if (shadow.FindFreeCarriedSlot(template) is not { } position)
            {
                return InventoryResult.InventoryFull;
            }

            Item item = Item.Create(data.Guid, template, _ownerGuid);
            item.Load(data);
            shadow.PlaceDetached(position.Bag, position.Slot, item);
            placed.Add((position.Bag, position.Slot, item.ToData()));
        }

        stage = new EconomyInventoryStage(this, before, FreezeRewardSnapshot(shadow.CreateSnapshot()), removed, placed);
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

        foreach ((byte bag, byte slot, ItemInstanceData data) in stage.Added)
        {
            ItemTemplate template = Templates.Find(data.Entry)
                ?? throw new InvalidOperationException($"item template {data.Entry} disappeared during settlement");
            Item item = Item.Create(data.Guid, template, _ownerGuid);
            item.Load(data);
            PlaceDetached(bag, slot, item);
            SendCreateIfNeeded(item);
            ItemCountChanged?.Invoke(item.Entry, (int)item.Count);
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

    /// <summary>First free backpack slot, then the first free slot of a carried general bag that accepts the item.</summary>
    private (byte Bag, byte Slot)? FindFreeCarriedSlot(ItemTemplate template)
    {
        for (byte slot = InventorySlots.ItemStart; slot < InventorySlots.ItemEnd; slot++)
        {
            if (_items[slot] is null)
            {
                return (InventorySlots.Bag0, slot);
            }
        }

        for (byte bagSlot = InventorySlots.BagStart; bagSlot < InventorySlots.BagEnd; bagSlot++)
        {
            if (_items[bagSlot] is Container bag && template.CanGoIntoBag(bag.Template))
            {
                for (byte slot = 0; slot < bag.Size; slot++)
                {
                    if (bag[slot] is null)
                    {
                        return (bagSlot, slot);
                    }
                }
            }
        }

        return null;
    }

    private void PlaceDetached(byte bag, byte slot, Item item)
    {
        if (bag == InventorySlots.Bag0)
        {
            PlaceInOwnSlot(slot, item);
        }
        else
        {
            ((Container)_items[bag]!).StoreItem(slot, item);
            item.Inventory = this;
        }
    }
}
