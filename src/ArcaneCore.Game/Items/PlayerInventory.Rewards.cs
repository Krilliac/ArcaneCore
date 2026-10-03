using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Items;

internal readonly record struct InventoryRewardGrant(uint Entry, uint Count);

/// <summary>A detached reward inventory. Preparation consumes GUIDs, but never changes the owner's fields or sends packets.</summary>
internal sealed class InventoryRewardStage(
    PlayerInventory inventory,
    InventorySnapshot before,
    InventorySnapshot after,
    IReadOnlyList<(Item Existing, uint Count, ItemDynFlags Flags)> stacks,
    IReadOnlyList<(byte Bag, byte Slot, Item Item)> additions,
    IReadOnlyList<(InventoryRewardGrant Grant, Item Result)> grants,
    IReadOnlyList<Item>? removed = null,
    IReadOnlyList<InventoryRewardGrant>? removals = null)
{
    internal PlayerInventory Inventory { get; } = inventory;
    internal InventorySnapshot Before { get; } = before;
    internal InventorySnapshot After { get; } = after;
    internal IReadOnlyList<(Item Existing, uint Count, ItemDynFlags Flags)> Stacks { get; } = stacks;
    internal IReadOnlyList<(byte Bag, byte Slot, Item Item)> Additions { get; } = additions;
    internal IReadOnlyList<(InventoryRewardGrant Grant, Item Result)> Grants { get; } = grants;

    /// <summary>Live items the reward destroys completely (required delivery items).</summary>
    internal IReadOnlyList<Item> Removed { get; } = removed ?? [];

    /// <summary>The requested destruction batch (entry, count), notified before the grants.</summary>
    internal IReadOnlyList<InventoryRewardGrant> Removals { get; } = removals ?? [];

    internal bool Applied { get; set; }

    internal bool MatchesBefore() => !Applied && PlayerInventory.SameRewardSnapshot(Before, Inventory.CreateSnapshot())
        && Stacks.All(s => ReferenceEquals(Inventory.GetItemByGuid(s.Existing.Guid), s.Existing))
        && Removed.All(r => ReferenceEquals(Inventory.GetItemByGuid(r.Guid), r));
}

public sealed partial class PlayerInventory
{
    /// <summary>Plan all reward grants together, using the ordinary storage/stack/bag/unique-item rules in a detached inventory.</summary>
    internal InventoryResult TryStageQuestRewards(IReadOnlyList<InventoryRewardGrant> grants,
        out InventoryRewardStage? stage, out uint failedEntry)
        => TryStageQuestRewards(grants, [], out stage, out failedEntry);

    /// <summary>
    /// Plan the destruction of required delivery items (vmangos RewardQuest DestroyItemCount,
    /// outside the bank) followed by every reward grant, all in one detached inventory.
    /// </summary>
    internal InventoryResult TryStageQuestRewards(IReadOnlyList<InventoryRewardGrant> grants,
        IReadOnlyList<InventoryRewardGrant> removals, out InventoryRewardStage? stage, out uint failedEntry)
    {
        stage = null;
        failedEntry = 0;
        if (!_loaded || GuidAllocator is null)
        {
            return InventoryResult.CantDoRightNow;
        }

        InventorySnapshot before = FreezeRewardSnapshot(CreateSnapshot());
        var shadow = new PlayerInventory(OwnerGuid, Race, Class, Level)
        {
            Templates = Templates,
            GuidAllocator = GuidAllocator,
        };
        shadow.Load(before.Items);
        foreach (InventoryRewardGrant removal in removals)
        {
            failedEntry = removal.Entry;
            if (removal.Entry == 0 || removal.Count is 0 or > int.MaxValue
                || shadow.GetItemCount(removal.Entry) < removal.Count
                || shadow.AllItems.Any(i => i.Entry == removal.Entry && i is Container)
                || shadow.DestroyItemCount(removal.Entry, removal.Count) != removal.Count)
            {
                return InventoryResult.ItemNotFound;
            }
        }

        var stored = new List<(InventoryRewardGrant Grant, uint Guid)>();
        foreach (InventoryRewardGrant grant in grants)
        {
            failedEntry = grant.Entry;
            if (grant.Entry == 0 || grant.Count is 0 or > int.MaxValue || Templates.Find(grant.Entry) is not { } template)
            {
                return InventoryResult.ItemNotFound;
            }

            // Include preserved unloadable rows as owned items, and avoid the ordinary uint
            // storage checks wrapping if corrupt content supplies an excessive quantity.
            ulong owned = shadow.CreateSnapshot().Items.Where(r => r.Item.Entry == grant.Entry)
                .Aggregate(0UL, (total, row) => total + row.Item.Count);
            if (owned + grant.Count > uint.MaxValue || (template.MaxCount > 0 && owned + grant.Count > template.MaxCount))
            {
                return InventoryResult.CantCarryMoreOfThis;
            }

            var destinations = new List<ItemPosCount>();
            InventoryResult result = shadow.CanStoreNewItem(grant.Entry, grant.Count, destinations, out _);
            if (result != InventoryResult.Ok)
            {
                return result;
            }

            Item item = shadow.StoreNewItem(destinations, template, grant.Count);
            stored.Add((grant, item.Guid.Low));
        }

        InventorySnapshot after = FreezeRewardSnapshot(shadow.CreateSnapshot());
        if (after.Items.GroupBy(row => row.Item.Guid).Any(group => group.Count() > 1))
        {
            return InventoryResult.CantDoRightNow;
        }

        var stacks = new List<(Item Existing, uint Count, ItemDynFlags Flags)>();
        var additions = new List<(byte Bag, byte Slot, Item Item)>();
        var results = new Dictionary<uint, Item>();
        foreach (Item item in shadow.AllItems)
        {
            if (GetItemByGuid(item.Guid) is { } existing)
            {
                results[item.Guid.Low] = existing;
                if (item.Count != existing.Count || item.DynamicFlags != existing.DynamicFlags)
                {
                    stacks.Add((existing, item.Count, item.DynamicFlags));
                }
            }
            else
            {
                additions.Add((item.BagSlot, item.Slot, item));
                results[item.Guid.Low] = item;
            }
        }

        var shadowGuids = shadow.AllItems.Select(i => i.Guid).ToHashSet();
        Item[] removed = AllItems.Where(i => !shadowGuids.Contains(i.Guid)).ToArray();
        if (removed.Any(i => i is Container))
        {
            return InventoryResult.CantDoRightNow;
        }

        stage = new InventoryRewardStage(this, before, after, stacks, additions,
            stored.Select(s => (s.Grant, results[s.Guid])).ToArray(), removed, removals.ToArray());
        failedEntry = 0;
        return InventoryResult.Ok;
    }

    /// <summary>Publish the exact inventory already committed by the reward transaction, preserving existing item and container identities.</summary>
    internal void ApplyQuestRewardInventory(InventoryRewardStage stage)
    {
        Player?.EnsureQuestSettlementMutationAllowed();
        if (!ReferenceEquals(stage.Inventory, this) || !stage.MatchesBefore())
        {
            throw new InvalidOperationException("the inventory changed while its quest reward was settling");
        }

        foreach (Item removed in stage.Removed)
        {
            RemoveItem(removed.BagSlot, removed.Slot);
            Discard(removed);
        }

        foreach ((Item existing, uint count, ItemDynFlags flags) in stage.Stacks)
        {
            existing.Count = count;
            existing.DynamicFlags = flags;
        }

        foreach ((byte bag, byte slot, Item item) in stage.Additions)
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

            SendCreateIfNeeded(item);
        }

        stage.Applied = true;
    }

    /// <summary>Objective callbacks and item notifications follow the rewarded journal mutation and durable commit.</summary>
    internal void NotifyQuestRewardInventory(InventoryRewardStage stage)
    {
        foreach (InventoryRewardGrant removal in stage.Removals)
        {
            ItemCountChanged?.Invoke(removal.Entry, -(int)removal.Count);
        }

        foreach ((InventoryRewardGrant grant, Item result) in stage.Grants)
        {
            ItemCountChanged?.Invoke(grant.Entry, (int)grant.Count);
            if (Player is { IsInWorld: true } player)
            {
                player.Session.Send(WorldOpcode.SmsgItemPushResult,
                    ItemPackets.ItemPushResult(player.Guid, result, grant.Count, received: true, created: false, showInChat: true));
            }
        }
    }

    /// <summary>
    /// Notifications of a committed durable loot take: what <see cref="AddItem"/> raises and sends
    /// for the live path (item-count change for quest objectives, then SMSG_ITEM_PUSH_RESULT as a
    /// loot pickup: not received from a trade or mail, not created).
    /// </summary>
    internal void NotifyLootInventory(InventoryRewardStage stage)
    {
        foreach ((InventoryRewardGrant grant, Item result) in stage.Grants)
        {
            ItemCountChanged?.Invoke(grant.Entry, (int)grant.Count);
            if (Player is { IsInWorld: true } player)
            {
                player.Session.Send(WorldOpcode.SmsgItemPushResult,
                    ItemPackets.ItemPushResult(player.Guid, result, grant.Count, received: false, created: false, showInChat: true));
            }
        }
    }

    private static InventorySnapshot FreezeRewardSnapshot(InventorySnapshot snapshot) => new(Array.AsReadOnly(snapshot.Items
        .Select(row => row with { Item = row.Item with
        {
            Charges = Array.AsReadOnly(row.Item.Charges.ToArray()),
            Enchantments = Array.AsReadOnly(row.Item.Enchantments.ToArray()),
        } }).ToArray()));

    internal static bool SameRewardSnapshot(InventorySnapshot a, InventorySnapshot b)
    {
        if (a.Items.Count != b.Items.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Items.Count; i++)
        {
            InventoryItemData x = a.Items[i];
            InventoryItemData y = b.Items[i];
            if (x.ContainerGuid != y.ContainerGuid || x.Slot != y.Slot || !SameRewardItem(x.Item, y.Item))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameRewardItem(ItemInstanceData a, ItemInstanceData b) => a.Guid == b.Guid && a.Entry == b.Entry
        && a.Count == b.Count && a.Creator == b.Creator && a.GiftCreator == b.GiftCreator && a.Duration == b.Duration
        && a.Flags == b.Flags && a.RandomPropertyId == b.RandomPropertyId && a.Durability == b.Durability && a.TextId == b.TextId
        && a.Charges.SequenceEqual(b.Charges) && a.Enchantments.SequenceEqual(b.Enchantments);
}
