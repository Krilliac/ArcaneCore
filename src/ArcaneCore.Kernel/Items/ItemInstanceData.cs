namespace ArcaneCore.Kernel.Items;

/// <summary>
/// The persistent state of one item instance (vmangos <c>item_instance</c>: guid, owner_guid,
/// item_id, creator_guid, gift_creator_guid, count, duration, charges, flags, enchantments,
/// random_property_id, durability, text).
/// </summary>
public sealed record ItemInstanceData
{
    /// <summary>Item GUID low part (HIGHGUID_ITEM counter), unique across the realm.</summary>
    public uint Guid { get; init; }

    public uint Entry { get; init; }
    public uint Count { get; init; } = 1;
    public ulong Creator { get; init; }
    public ulong GiftCreator { get; init; }
    public uint Duration { get; init; }

    /// <summary>Spell charges (5 values, vmangos MAX_ITEM_PROTO_SPELLS).</summary>
    public IReadOnlyList<int> Charges { get; init; } = [];

    /// <summary>ITEM_FIELD_FLAGS (dynamic flags: bound, wrapped, …).</summary>
    public uint Flags { get; init; }

    /// <summary>Enchantment triples (id, duration, charges) for the 7 slots, flattened (21 values).</summary>
    public IReadOnlyList<uint> Enchantments { get; init; } = [];

    public int RandomPropertyId { get; init; }
    public uint Durability { get; init; }
    public uint TextId { get; init; }

    /// <summary>The generated, not yet taken loot of a container item, or null when none was generated (vmangos generated_loot + item_loot).</summary>
    public ItemLootData? Loot { get; init; }

    /// <summary>
    /// A gift-wrapped item's own entry (vmangos <c>character_gifts.item_id</c>); 0 when the item is not wrapped. While wrapped, <see cref="Entry"/>
    /// is the wrapping paper's gift entry and <see cref="Flags"/> holds ITEM_DYNFLAG_WRAPPED.
    /// </summary>
    public uint GiftEntry { get; init; }

    /// <summary>The wrapped item's own ITEM_FIELD_FLAGS (vmangos <c>character_gifts.flags</c>), restored when it is opened.</summary>
    public uint GiftFlags { get; init; }
}

/// <summary>
/// Where an item sits (vmangos <c>character_inventory</c>): <see cref="ContainerGuid"/> is 0 for
/// the character's own slots (equipment, bag slots, backpack, bank, buyback, keyring) or the item
/// GUID of the bag holding it; <see cref="Slot"/> is the slot within that.
/// </summary>
public sealed record InventoryItemData(uint ContainerGuid, byte Slot, ItemInstanceData Item);

/// <summary>
/// A character's complete inventory, saved as a whole (replaces every stored row of the character).
/// <see cref="AmmoId"/> is the selected ammo (PLAYER_AMMO_ID); null means the snapshot does not carry it and the
/// stored selection is left alone.
/// </summary>
public sealed record InventorySnapshot(IReadOnlyList<InventoryItemData> Items, uint? AmmoId = null);

/// <summary>Character-database access for items (characters DB, M-items tables).</summary>
public interface IItemStore
{
    /// <summary>Every item a character owns with its position, containers before their contents.</summary>
    Task<IReadOnlyList<InventoryItemData>> GetInventoryAsync(int characterId, CancellationToken cancellationToken = default);

    /// <summary>Replace a character's stored items with <paramref name="snapshot"/>.</summary>
    Task SaveInventoryAsync(int characterId, InventorySnapshot snapshot, CancellationToken cancellationToken = default);

    /// <summary>Item entries in the character's own slots 0..19 (equipment + first bag slot), for the character list.</summary>
    Task<IReadOnlyDictionary<int, IReadOnlyDictionary<byte, uint>>> GetEquippedEntriesAsync(
        IReadOnlyCollection<int> characterIds, CancellationToken cancellationToken = default);

    /// <summary>The highest item GUID in use (0 when none), to seed the GUID allocator.</summary>
    Task<uint> GetMaxItemGuidAsync(CancellationToken cancellationToken = default);
}

/// <summary>World-database access for item content (loaded once into memory at startup).</summary>
public interface IItemTemplateSource
{
    Task<IReadOnlyList<ItemTemplate>> LoadTemplatesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StartingItem>> LoadStartingItemsAsync(CancellationToken cancellationToken = default);
}
