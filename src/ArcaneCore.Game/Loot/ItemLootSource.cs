using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;

namespace ArcaneCore.Game.Loot;

/// <summary>
/// Container items (lockboxes, junkboxes, clams, bags of goods) opened with CMSG_OPEN_ITEM, after vmangos HandleOpenItemOpcode (SpellHandler.cpp:142-226),
/// Player::SendLoot for an item (Player.cpp:7716-7756) and DoLootRelease (LootHandler.cpp:549-566):
/// <list type="bullet">
/// <item>a locked item needs <c>ITEM_DYNFLAG_UNLOCKED</c> (set by a successful Pick Lock) unless its lock is a key lock: only a lock with a skill
/// requirement, or a lock id with no Lock.dbc row, answers ITEM_LOCKED;</item>
/// <item>a wrapped gift is not opened here (the character_gifts store belongs to the economy area): CANT_DO_RIGHT_NOW;</item>
/// <item>the first open rolls <c>item_loot_template</c> of the item's entry plus the template's money range once and keeps the result on the item
/// (<see cref="Item.Loot"/>, saved with the inventory); every later open shows what is left;</item>
/// <item>the window is the owner's alone, its money is not shared; closing it with nothing left destroys the item (the whole stack, see
/// <see cref="ItemLootOptions.ConsumeWholeStack"/>); leftovers stay in the item.</item>
/// </list>
/// Not modelled: the taxi refusal (no taxi state on the base), the interlocks that stop splitting, moving, trading or selling an item that holds
/// generated loot (item mechanics lane), and an immediate save at generation (a crash before the next character save can reroll, as in vmangos).
/// </summary>
public sealed class ItemLootSource(LootService loot, ItemLootOptions? options = null, Random? random = null) : IItemLootSource, ILootReleaseHandler
{
    private readonly ItemLootOptions _options = options ?? new ItemLootOptions();
    private readonly Random _random = random ?? new Random();

    /// <summary>Lock.dbc lookup (the game object content); null reads every lock as unknown.</summary>
    public Func<uint, LockEntry?>? Locks { get; set; }

    /// <summary>vmangos <c>InterruptNonMeleeSpells(false)</c> before the loot opens (the spell area cancels the cast in progress).</summary>
    public Action<Player>? InterruptSpells { get; set; }

    public LootResult Open(Player player, Item item)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(item);
        if (!player.IsAlive)
        {
            return LootResult.Dead;
        }

        if (IsLocked(item))
        {
            player.Inventory.SendEquipError(InventoryResult.ItemLocked, item, null);
            return LootResult.Locked;
        }

        InterruptSpells?.Invoke(player);
        if ((item.DynamicFlags & ItemDynFlags.Wrapped) != 0)
        {
            player.Inventory.SendEquipError(InventoryResult.LootCantLootThatNow, item, null);
            return LootResult.NotAllowed;
        }

        LootBag bag = item.Loot is { } stored ? Rebuild(item, player, stored) : Generate(item, player);
        bag.Owner = player.Guid;
        bag.ShareMoney = false;
        bag.ReleaseHandler = this;
        bag.SourceCheck = viewer => viewer.IsAlive && viewer.Inventory.GetItemByGuid(item.Guid) is not null;
        bag.Changed = changed => item.Loot = Snapshot(changed);
        item.Loot = Snapshot(bag);
        return loot.ShowSpecial(player, item, bag);
    }

    /// <summary>The lock rule of HandleOpenItemOpcode: an unlocked or lock-less item opens; a lock without a row, or one that asks for a skill, refuses.</summary>
    private bool IsLocked(Item item)
    {
        uint lockId = item.Template.LockId;
        if (lockId == 0 || (item.DynamicFlags & ItemDynFlags.Unlocked) != 0)
        {
            return false;
        }

        return Locks?.Invoke(lockId) is not { } entry || entry.Skills[0] != 0 || entry.Skills[1] != 0;
    }

    /// <summary>Player::SendLoot: FillLoot(item entry, item_loot_template, personal) and GenerateMoneyLoot(MinMoneyLoot, MaxMoneyLoot).</summary>
    private LootBag Generate(Item item, Player player)
    {
        LootBag bag = loot.Generate(item.Guid, LootSourceKind.Item, LootType.Corpse, LootTableKind.Item, item.Entry, [player], zeroEntryIsATable: false);
        bag.Gold = Math.Min(LootMoneyRules.Generate(item.Template.MinMoneyLoot, item.Template.MaxMoneyLoot, loot.Options.MoneyRate, _random), LootService.MaxMoneyAmount);
        return bag;
    }

    /// <summary>A bag rebuilt from the loot kept on the item (stable slots, what was not taken yet).</summary>
    private LootBag Rebuild(Item item, Player player, ItemLootData stored)
    {
        var bag = new LootBag(item.Guid, LootSourceKind.Item, LootType.Corpse) { Gold = stored.Gold };
        bag.Recipients.Add(player.Guid);
        foreach (ItemLootEntry entry in stored.Items)
        {
            var rebuilt = new LootItem(entry.Slot, entry.ItemId, entry.Count, entry.IsQuest, isPerPlayer: false, loot.Items?.Find(entry.ItemId)?.DisplayId ?? 0);
            if (entry.IsQuest)
            {
                rebuilt.AllowedLooters.Add(player.Guid);
            }

            bag.Add(rebuilt);
        }

        return bag;
    }

    /// <summary>What is left in <paramref name="bag"/> as the persistent state of the item (vmangos item_loot rows).</summary>
    public static ItemLootData Snapshot(LootBag bag)
    {
        ArgumentNullException.ThrowIfNull(bag);
        return new ItemLootData(bag.Gold, [.. bag.Items.Where(i => !i.IsLooted).Select(i => new ItemLootEntry(i.Slot, i.ItemId, i.Count, i.IsQuestItem))]);
    }

    /// <summary>The window closed: nothing left destroys the item, leftovers stay for the next open.</summary>
    public void OnReleased(Player player, LootBag bag)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(bag);
        loot.RemoveSpecial(bag.Source);
        if (player.Inventory.GetItemByGuid(bag.Source) is not { } item)
        {
            return;
        }

        if (!bag.IsEmpty)
        {
            item.Loot = Snapshot(bag);
            return;
        }

        if (_options.ConsumeWholeStack || item.Count <= 1)
        {
            player.Inventory.DestroyItemCount(item, item.Count);
        }
        else
        {
            item.Loot = null;
            player.Inventory.DestroyItemCount(item, 1);
        }
    }
}