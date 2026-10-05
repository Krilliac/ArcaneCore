using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Updates;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Items;

/// <summary>A planned placement: <see cref="Count"/> items go to (<see cref="Bag"/>, <see cref="Slot"/>) (vmangos ItemPosCount).</summary>
public readonly record struct ItemPosCount(byte Bag, byte Slot, uint Count);

/// <summary>
/// A character's items: equipment, equipped bags and their contents, backpack, bank, bank bags,
/// buyback and keyring (vmangos Player m_items and the storage half of Player.cpp). The rules
/// and result codes are a port of vmangos Player.cpp (FindEquipSlot, _CanStoreItem and its
/// _InSpecificSlot / _InBag / _InInventorySlots helpers, CanEquipItem, CanUnequipItem,
/// CanBankItem, _StoreItem, EquipItem, VisualizeItem, RemoveItem, DestroyItem, DestroyItemCount,
/// SplitItem, SwapItem, AddStartingItems) and the WorldSession item handlers (ItemHandler.cpp).
/// <para>
/// Thread affinity: the world thread once the player is in the world. An inventory without a
/// <see cref="Player"/> (character creation) is owned by whoever created it.
/// </para>
/// </summary>
public sealed partial class PlayerInventory
{
    /// <summary>MAX_VISIBLE_ITEM_OFFSET (PLAYER_VISIBLE_ITEM_2_CREATOR − PLAYER_VISIBLE_ITEM_1_CREATOR).</summary>
    private const int VisibleItemStride = 12;

    private readonly Item?[] _items = new Item?[InventorySlots.KeyringEnd];
    private readonly List<InventoryItemData> _unloadable = [];
    private readonly HashSet<Item> _modsApplied = [];
    private readonly ObjectGuid _ownerGuid;
    private readonly Race _race;
    private readonly Class _class;
    private readonly byte _level;
    private bool _loaded;

    /// <summary>The inventory of an in-world (or loading) player.</summary>
    public PlayerInventory(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        Player = player;
        _ownerGuid = player.Guid;
        _enchantLifetime = new(OnEnchantExpired, OnEnchantCleared);
    }

    /// <summary>An inventory with no player object (character creation: vmangos Player::Create → AddStartingItems).</summary>
    public PlayerInventory(ObjectGuid owner, Race race, Class cls, byte level)
    {
        _ownerGuid = owner;
        _race = race;
        _class = cls;
        _level = level;
        _enchantLifetime = new(OnEnchantExpired, OnEnchantCleared);
    }

    /// <summary>Raised with (entry, delta) when items enter or leave the inventory (quest item counters, vmangos ItemAddedQuestCheck / ItemRemovedQuestCheck).</summary>
    public event Action<uint, int>? ItemCountChanged;

    /// <summary>The player, or null for an offline inventory.</summary>
    public Player? Player { get; }

    public ObjectGuid OwnerGuid => _ownerGuid;

    public Race Race => Player?.Race ?? _race;

    public Class Class => Player?.Class ?? _class;

    public byte Level => Player?.Level ?? _level;

    /// <summary>Item content used to create items by entry.</summary>
    public IItemTemplateStore Templates { get; set; } = ItemTemplateStore.Empty;

    /// <summary>Item-mechanics configuration (retail defaults until the items feature binds the <c>Items</c> section).</summary>
    public ItemMechanicsOptions Options { get; set; } = new();

    /// <summary>Source of new item GUIDs; required to create items.</summary>
    public ItemGuidAllocator? GuidAllocator { get; set; }

    /// <summary>Skill/spell/dual-wield answers (replaced by the skills/spells areas).</summary>
    public IItemRequirements Requirements { get; set; } = DefaultItemRequirements.Instance;

    /// <summary>The stat application hook (replaceable by the combat/spells areas).</summary>
    public IItemStatsApplier StatsApplier { get; set; } = EquipmentStatsApplier.Instance;

    public IItemEquipSpellSink? EquipSpellSink { get; set; }

    public IItemEnchantmentSink? EnchantmentSink { get; set; }

    public IItemEnchantmentSpellSink? EnchantmentSpellSink { get; set; }

    /// <summary>
    /// Whether bank slots may be used now (vmangos Player::CanUseBank: a banker in range). The
    /// NPC-services area sets this when a bank window opens; without it, bank moves fail with
    /// TOO_FAR_AWAY_FROM_BANK.
    /// </summary>
    public Func<bool>? CanUseBank { get; set; }

    /// <summary>Whether the bank may be used now (<see cref="CanUseBank"/>).</summary>
    public bool BankUsable => CanUseBank?.Invoke() ?? false;

    /// <summary>Whether items were loaded (or a starting outfit given); only then is the inventory saved.</summary>
    public bool IsLoaded => _loaded;

    /// <summary>Bank bag slots bought (vmangos GetBankBagSlotCount: PLAYER_BYTES_2 byte 2).</summary>
    public byte BankBagSlotCount
    {
        get => Player?.GetByte(UpdateFields.PlayerBytes2, 2) ?? 0;
        set => Player?.SetByte(UpdateFields.PlayerBytes2, 2, value);
    }

    /// <summary>Every item in the player's own slots (buyback excluded, as vmangos GetItemByGuid/GetItemCount), then every item inside bags.</summary>
    public IEnumerable<Item> AllItems
    {
        get
        {
            for (int slot = 0; slot < _items.Length; slot++)
            {
                if (_items[slot] is { } item && !IsBuybackSlot((byte)slot))
                {
                    yield return item;
                }
            }

            foreach (Item? item in _items)
            {
                if (item is Container bag)
                {
                    foreach (Item inner in bag.Items)
                    {
                        yield return inner;
                    }
                }
            }
        }
    }

    /// <summary>Equipped items with their slots (0..18).</summary>
    public IEnumerable<(byte Slot, Item Item)> Equipped
    {
        get
        {
            for (byte slot = 0; slot < InventorySlots.EquipmentEnd; slot++)
            {
                if (_items[slot] is { } item)
                {
                    yield return (slot, item);
                }
            }
        }
    }

    /// <summary>vmangos Player::IsTwoHandUsed.</summary>
    public bool IsTwoHandUsed => _items[InventorySlots.MainHand]?.Template.GetInventoryType() == InventoryType.TwoHandWeapon;

    /// <summary>vmangos Player::GetItemByPos.</summary>
    public Item? GetItem(byte bag, byte slot)
    {
        if (bag == InventorySlots.Bag0)
        {
            return slot < InventorySlots.KeyringEnd ? _items[slot] : null;
        }

        if ((bag >= InventorySlots.BagStart && bag < InventorySlots.BagEnd) || (bag >= InventorySlots.BankBagStart && bag < InventorySlots.BankBagEnd))
        {
            return _items[bag] is Container container && slot < container.Size ? container[slot] : null;
        }

        return null;
    }

    public Item? GetItemByGuid(ObjectGuid guid) => AllItems.FirstOrDefault(i => i.Guid == guid);

    /// <summary>vmangos Player::GetItemCount: equipment, bags, backpack, keyring and bag contents; the bank only when asked.</summary>
    public uint GetItemCount(uint entry, bool inBankAlso = false, Item? skip = null)
    {
        uint count = 0;
        foreach (Item item in AllItems)
        {
            if (item != skip && item.Entry == entry && (inBankAlso || !IsInBank(item)))
            {
                count += item.Count;
            }
        }

        return count;
    }

    /// <summary>
    /// Place stored items (vmangos Player::_LoadInventory). Rows that cannot be placed (unknown
    /// template, invalid or occupied slot, missing bag) are kept aside and written back unchanged
    /// by every save, so no item is lost; vmangos deletes or mails them instead.
    /// </summary>
    public void Load(IEnumerable<InventoryItemData> rows)
    {
        Player?.EnsureQuestSettlementMutationAllowed();
        ArgumentNullException.ThrowIfNull(rows);
        var pending = rows.ToList();
        var bags = new Dictionary<uint, Container>();

        // Own slots first so bags exist before their contents (vmangos orders by bag, slot).
        foreach (InventoryItemData row in pending.Where(r => r.ContainerGuid == 0).OrderBy(r => r.Slot))
        {
            ItemTemplate? template = Templates.Find(row.Item.Entry);
            if (template is null || !CanLoadIntoOwnSlot(template, row.Slot))
            {
                _unloadable.Add(row);
                continue;
            }

            Item item = Item.Create(row.Item.Guid, template, _ownerGuid);
            item.Load(row.Item);
            PlaceInOwnSlot(row.Slot, item);
            if (row.Slot < InventorySlots.EquipmentEnd)
            {
                ApplyMods(item, row.Slot, apply: true);
            }
            RegisterEnchantDurations(item);

            if (item is Container bag)
            {
                bags[item.Guid.Low] = bag;
            }
        }

        foreach (InventoryItemData row in pending.Where(r => r.ContainerGuid != 0).OrderBy(r => r.ContainerGuid).ThenBy(r => r.Slot))
        {
            ItemTemplate? template = Templates.Find(row.Item.Entry);
            if (template is null
                || !bags.TryGetValue(row.ContainerGuid, out Container? bag)
                || row.Slot >= bag.Size
                || bag[row.Slot] is not null
                || !template.CanGoIntoBag(bag.Template))
            {
                _unloadable.Add(row);
                continue;
            }

            Item item = Item.Create(row.Item.Guid, template, _ownerGuid);
            item.Load(row.Item);
            bag.StoreItem(row.Slot, item);
            item.Inventory = this;
            RegisterEnchantDurations(item);
        }

        _loaded = true;
    }

    /// <summary>
    /// The inventory to save, or null when nothing was loaded (a player object built without its
    /// items must never overwrite the stored ones). Unchanged rows cost no writes (the store diffs).
    /// </summary>
    public InventorySnapshot? TakeSnapshotIfChanged() => _loaded ? CreateSnapshot() : null;

    /// <summary>The complete inventory as stored rows (including rows that could not be loaded).</summary>
    public InventorySnapshot CreateSnapshot()
    {
        FlushEnchantDurations();
        var rows = new List<InventoryItemData>();
        for (int slot = 0; slot < _items.Length; slot++)
        {
            // Buyback items are never stored (vmangos _SaveInventory deletes them).
            if (_items[slot] is { } item && !IsBuybackSlot((byte)slot))
            {
                rows.Add(new InventoryItemData(0, (byte)slot, item.ToData()));
            }
        }

        foreach (Item? item in _items)
        {
            if (item is Container bag)
            {
                for (int slot = 0; slot < bag.Size; slot++)
                {
                    if (bag[slot] is { } inner)
                    {
                        rows.Add(new InventoryItemData(bag.Guid.Low, (byte)slot, inner.ToData()));
                    }
                }
            }
        }

        rows.AddRange(_unloadable);
        return new InventorySnapshot(rows, AmmoId);
    }

    /// <summary>
    /// Create blocks for every owned item, in vmangos Player::BuildCreateUpdateBlockForPlayer
    /// order (equipment; bags, backpack, bank and bank bags; buyback; keyring), each bag followed
    /// by its contents (Bag::BuildCreateUpdateBlockForPlayer). Called by the map before the
    /// player's own create block.
    /// </summary>
    internal void WriteCreateBlocks(UpdateData updates, uint nowMs)
    {
        if (Player is null)
        {
            return;
        }

        for (int slot = 0; slot < _items.Length; slot++)
        {
            if (_items[slot] is { } item)
            {
                WriteCreate(updates, item, nowMs);
            }
        }
    }

    /// <summary>
    /// vmangos Player::SendEquipError: SMSG_INVENTORY_CHANGE_FAILURE with the involved items; the
    /// required level comes from the first item (or <paramref name="entry"/>) for CANT_EQUIP_LEVEL_I.
    /// </summary>
    public void SendEquipError(InventoryResult result, Item? item1, Item? item2, byte bagSlot = 0, uint entry = 0)
    {
        if (Player is not { } player)
        {
            return;
        }

        uint level = result == InventoryResult.CantEquipLevelI
            ? (item1?.Template ?? Templates.Find(entry))?.RequiredLevel ?? 0
            : 0;
        player.Session.Send(WorldOpcode.SmsgInventoryChangeFailure, ItemPackets.InventoryChangeFailure(
            result, item1?.Guid ?? ObjectGuid.Empty, item2?.Guid ?? ObjectGuid.Empty, bagSlot, level));
    }
}
