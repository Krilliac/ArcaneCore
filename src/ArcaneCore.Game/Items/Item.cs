using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Game.Items;

/// <summary>
/// An item instance (vmangos Item). Items are not placed in a map: they exist for their owner's
/// client only. Field changes are queued on the owner's map and sent to the owner alone
/// (vmangos Item::AddToClientUpdateList → owner's map; Item::BuildUpdateData → owner only).
/// <para>Thread affinity: world thread once its owner is in the world.</para>
/// </summary>
public class Item : WorldObject
{
    /// <summary>Spell-charge fields (ITEM_FIELD_SPELL_CHARGES, 5 values).</summary>
    public const int SpellChargeSlots = 5;

    /// <summary>Enchantment fields (ITEM_FIELD_ENCHANTMENT: 7 slots × id/duration/charges).</summary>
    public const int EnchantmentValues = 21;

    /// <summary>A new item of <paramref name="template"/> (vmangos Item::Create): count 1, full durability, the template's charges and duration.</summary>
    public Item(uint guidLow, ItemTemplate template, ObjectGuid owner)
        : this(guidLow, template, owner, Game.TypeId.Item, TypeMask.Object | TypeMask.Item, UpdateFields.ItemEnd)
    {
    }

    protected Item(uint guidLow, ItemTemplate template, ObjectGuid owner, byte typeId, uint typeMask, int valuesCount)
        : base(ObjectGuid.Item(guidLow), typeId, typeMask, valuesCount)
    {
        ArgumentNullException.ThrowIfNull(template);
        Template = template;
        SetUInt32(UpdateFields.ObjectFieldEntry, template.Entry);
        SetUInt64(UpdateFields.ItemFieldOwner, owner.Value);
        SetUInt32(UpdateFields.ItemFieldStackCount, 1);
        SetUInt32(UpdateFields.ItemFieldMaxdurability, template.MaxDurability);
        SetUInt32(UpdateFields.ItemFieldDurability, template.MaxDurability);
        for (int i = 0; i < SpellChargeSlots && i < template.Spells.Count; i++)
        {
            SetInt32(UpdateFields.ItemFieldSpellCharges + i, template.Spells[i].Charges);
        }

        SetUInt32(UpdateFields.ItemFieldDuration, template.Duration);
    }

    public ItemTemplate Template { get; }

    public uint Entry => Template.Entry;

    // Items and containers share one generated table (vmangos UpdateFields: the container
    // fields follow ITEM_END); an item's values array is just shorter.
    public override ReadOnlySpan<ushort> FieldFlags => UpdateFieldTables.ContainerVisibility;

    public override ReadOnlySpan<bool> GuidFieldStarts => UpdateFieldTables.ContainerGuidStarts;

    /// <summary>vmangos Item(): m_updateFlag = UPDATEFLAG_ALL for builds &gt; 1.8.4.</summary>
    public override ObjectUpdateFlags CreateUpdateFlags => ObjectUpdateFlags.All;

    /// <summary>ITEM_FIELD_OWNER.</summary>
    public ObjectGuid OwnerGuid
    {
        get => new(GetUInt64(UpdateFields.ItemFieldOwner));
        internal set => SetUInt64(UpdateFields.ItemFieldOwner, value.Value);
    }

    /// <summary>ITEM_FIELD_CONTAINED: the owning player or the bag holding the item.</summary>
    public ObjectGuid ContainedIn
    {
        get => new(GetUInt64(UpdateFields.ItemFieldContained));
        internal set => SetUInt64(UpdateFields.ItemFieldContained, value.Value);
    }

    /// <summary>ITEM_FIELD_STACK_COUNT.</summary>
    public uint Count
    {
        get => GetUInt32(UpdateFields.ItemFieldStackCount);
        set => SetUInt32(UpdateFields.ItemFieldStackCount, value);
    }

    public uint Durability
    {
        get => GetUInt32(UpdateFields.ItemFieldDurability);
        set => SetUInt32(UpdateFields.ItemFieldDurability, value);
    }

    /// <summary>ITEM_FIELD_DURATION: remaining lifetime in seconds of a timed item (0 = untimed).</summary>
    public uint Duration
    {
        get => GetUInt32(UpdateFields.ItemFieldDuration);
        set => SetUInt32(UpdateFields.ItemFieldDuration, value);
    }

    public uint MaxDurability => GetUInt32(UpdateFields.ItemFieldMaxdurability);

    /// <summary>ITEM_FIELD_FLAGS.</summary>
    public ItemDynFlags DynamicFlags
    {
        get => (ItemDynFlags)GetUInt32(UpdateFields.ItemFieldFlags);
        set => SetUInt32(UpdateFields.ItemFieldFlags, (uint)value);
    }

    /// <summary>vmangos Item::IsSoulBound (ITEM_DYNFLAG_BOUND).</summary>
    public bool IsSoulBound => (DynamicFlags & ItemDynFlags.Bound) != 0;

    public ObjectGuid Creator => new(GetUInt64(UpdateFields.ItemFieldCreator));

    public int RandomPropertyId => GetInt32(UpdateFields.ItemFieldRandomPropertiesId);

    /// <summary>ITEM_FIELD_PROPERTY_SEED (the suffix factor of random-suffix items).</summary>
    public uint SuffixFactor => GetUInt32(UpdateFields.ItemFieldPropertySeed);

    /// <summary>The permanent enchantment id of enchantment slot <paramref name="slot"/> (0..6).</summary>
    public uint EnchantmentId(int slot) => GetUInt32(UpdateFields.ItemFieldEnchantment + (slot * 3));

    /// <summary>
    /// The remaining time in ms of each enchantment slot while its owner is online (crafting lane). vmangos keeps it in <c>Player::m_enchantDuration</c> and
    /// writes the item field only when the timer stops; the field is also the value the client displays, so a running timer must not rewrite it. Null
    /// until a timer starts. <see cref="ToData"/> saves this value in place of the field, so a relog resumes with the time that was left.
    /// </summary>
    internal uint?[]? LiveEnchantDuration { get; set; }

    public bool IsBag => Template.IsBag();

    /// <summary>
    /// The generated, not yet taken loot of a container item (vmangos <c>item-&gt;loot</c> with <c>generated_loot</c>): rolled once by the first open and
    /// kept (and saved with the inventory) until it is taken completely. Null while nothing was generated. See <c>ItemLootSource</c>.
    /// </summary>
    public ItemLootData? Loot { get; set; }

    /// <summary>The bag holding this item, or null when it sits in the player's own slots.</summary>
    public Container? Container { get; internal set; }

    /// <summary>The slot within its bag or within the player's own slots (<see cref="InventorySlots.NullSlot"/> when not stored).</summary>
    public byte Slot { get; internal set; } = InventorySlots.NullSlot;

    /// <summary>vmangos Item::GetBagSlot: the slot of the holding bag, or INVENTORY_SLOT_BAG_0.</summary>
    public byte BagSlot => Container?.Slot ?? InventorySlots.Bag0;

    /// <summary>The inventory holding this item, or null when detached.</summary>
    public PlayerInventory? Inventory { get; internal set; }

    /// <summary>Whether the owner's client has this item's create block (so a delete needs SMSG_DESTROY_OBJECT).</summary>
    internal bool SentToClient { get; set; }

    /// <summary>vmangos Item::SetBinding.</summary>
    public void SetBinding(bool bound)
        => DynamicFlags = bound ? DynamicFlags | ItemDynFlags.Bound : DynamicFlags & ~ItemDynFlags.Bound;

    /// <summary>vmangos Item::CanBeMergedPartlyWith: same entry and room left in this stack.</summary>
    public InventoryResult CanBeMergedPartlyWith(ItemTemplate template)
        => Entry != template.Entry || Count >= template.MaxStackSize() ? InventoryResult.ItemCantStack : InventoryResult.Ok;

    /// <summary>The persistent state of this item.</summary>
    public ItemInstanceData ToData()
    {
        var charges = new int[SpellChargeSlots];
        for (int i = 0; i < SpellChargeSlots; i++)
        {
            charges[i] = GetInt32(UpdateFields.ItemFieldSpellCharges + i);
        }

        var enchantments = new uint[EnchantmentValues];
        for (int i = 0; i < EnchantmentValues; i++)
        {
            enchantments[i] = GetUInt32(UpdateFields.ItemFieldEnchantment + i);
        }

        if (LiveEnchantDuration is { } live)
        {
            for (int slot = 0; slot < live.Length && (slot * 3) + 1 < EnchantmentValues; slot++)
            {
                if (live[slot] is { } left && enchantments[slot * 3] != 0)
                {
                    enchantments[(slot * 3) + 1] = left;
                }
            }
        }

        return new ItemInstanceData
        {
            Guid = Guid.Low,
            Entry = Entry,
            Count = Count,
            Creator = GetUInt64(UpdateFields.ItemFieldCreator),
            GiftCreator = GetUInt64(UpdateFields.ItemFieldGiftcreator),
            Duration = GetUInt32(UpdateFields.ItemFieldDuration),
            Charges = charges,
            Flags = GetUInt32(UpdateFields.ItemFieldFlags),
            Enchantments = enchantments,
            RandomPropertyId = RandomPropertyId,
            Durability = Durability,
            TextId = GetUInt32(UpdateFields.ItemFieldItemTextId),
            Loot = Loot,
        };
    }

    /// <summary>Restore stored state (vmangos Item::LoadFromDB).</summary>
    internal void Load(ItemInstanceData data)
    {
        SetUInt32(UpdateFields.ItemFieldStackCount, Math.Max(data.Count, 1u));
        SetUInt64(UpdateFields.ItemFieldCreator, data.Creator);
        SetUInt64(UpdateFields.ItemFieldGiftcreator, data.GiftCreator);
        // vmangos Item.cpp:403-410: a stored duration that disagrees with the template about
        // "timed or not" is replaced by the template's (timed -> template, untimed -> 0).
        SetUInt32(UpdateFields.ItemFieldDuration, (Template.Duration == 0) != (data.Duration == 0) ? Template.Duration : data.Duration);
        for (int i = 0; i < SpellChargeSlots && i < data.Charges.Count; i++)
        {
            SetInt32(UpdateFields.ItemFieldSpellCharges + i, data.Charges[i]);
        }

        // vmangos Item.cpp:430-435: no bound flag on a NO_BIND template.
        // Item.cpp:462-476: a wrapped flag needs a wrapper template that is not stackable.
        uint flags = data.Flags;
        if ((flags & (uint)ItemDynFlags.Bound) != 0 && Template.Bonding == 0)
        {
            flags &= ~(uint)ItemDynFlags.Bound;
        }

        if ((flags & (uint)ItemDynFlags.Wrapped) != 0
            && ((Template.Flags & (uint)ItemTemplateFlags.Wrapper) == 0 || Template.MaxStackSize() > 1))
        {
            flags &= ~(uint)ItemDynFlags.Wrapped;
        }

        SetUInt32(UpdateFields.ItemFieldFlags, flags);
        for (int i = 0; i < EnchantmentValues && i < data.Enchantments.Count; i++)
        {
            SetUInt32(UpdateFields.ItemFieldEnchantment + i, data.Enchantments[i]);
        }

        SetInt32(UpdateFields.ItemFieldRandomPropertiesId, data.RandomPropertyId);
        SetUInt32(UpdateFields.ItemFieldDurability, Math.Min(data.Durability, MaxDurability));
        SetUInt32(UpdateFields.ItemFieldItemTextId, data.TextId);
        Loot = data.Loot;
    }

    /// <summary>A copy holding <paramref name="count"/> of this item (vmangos Item::CloneItem).</summary>
    internal Item Clone(uint guidLow, uint count, ObjectGuid owner)
    {
        Item clone = Create(guidLow, Template, owner);
        clone.Count = count;
        clone.SetUInt64(UpdateFields.ItemFieldCreator, GetUInt64(UpdateFields.ItemFieldCreator));
        clone.SetUInt64(UpdateFields.ItemFieldGiftcreator, GetUInt64(UpdateFields.ItemFieldGiftcreator));
        clone.SetUInt32(UpdateFields.ItemFieldFlags, GetUInt32(UpdateFields.ItemFieldFlags));
        clone.SetUInt32(UpdateFields.ItemFieldDuration, GetUInt32(UpdateFields.ItemFieldDuration));
        clone.SetInt32(UpdateFields.ItemFieldRandomPropertiesId, RandomPropertyId);
        return clone;
    }

    /// <summary>A new item or container of <paramref name="template"/> (vmangos Item::CreateItem picks Bag for INVTYPE_BAG).</summary>
    public static Item Create(uint guidLow, ItemTemplate template, ObjectGuid owner)
        => template.IsBag() ? new Container(guidLow, template, owner) : new Item(guidLow, template, owner);

    /// <summary>The map whose values queue carries this item's changes: its owner's (vmangos Item::AddToClientUpdateList).</summary>
    internal override Map? ValuesUpdateMap => Inventory?.Player?.Map;
}

/// <summary>
/// A bag (vmangos Bag): an item with CONTAINER_FIELD_NUM_SLOTS and one GUID field per slot.
/// </summary>
public sealed class Container : Item
{
    private readonly Item?[] _slots = new Item?[InventorySlots.MaxBagSize];

    /// <summary>vmangos Bag::Create: CONTAINED = owner, NUM_SLOTS from the template (capped at MAX_BAG_SIZE).</summary>
    public Container(uint guidLow, ItemTemplate template, ObjectGuid owner)
        : base(guidLow, template, owner, Game.TypeId.Container, TypeMask.Object | TypeMask.Item | TypeMask.Container, UpdateFields.ContainerEnd)
    {
        ContainedIn = owner;
        SetUInt32(UpdateFields.ContainerFieldNumSlots, Math.Min(template.ContainerSlots, (uint)InventorySlots.MaxBagSize));
    }

    /// <summary>CONTAINER_FIELD_NUM_SLOTS.</summary>
    public int Size => (int)GetUInt32(UpdateFields.ContainerFieldNumSlots);

    public Item? this[int slot] => slot >= 0 && slot < Size ? _slots[slot] : null;

    /// <summary>vmangos Bag::IsEmpty.</summary>
    public bool IsEmpty
    {
        get
        {
            for (int i = 0; i < Size; i++)
            {
                if (_slots[i] is not null)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>vmangos Bag::GetFreeSlots.</summary>
    public int FreeSlots
    {
        get
        {
            int free = 0;
            for (int i = 0; i < Size; i++)
            {
                if (_slots[i] is null)
                {
                    free++;
                }
            }

            return free;
        }
    }

    public IEnumerable<Item> Items
    {
        get
        {
            for (int i = 0; i < Size; i++)
            {
                if (_slots[i] is { } item)
                {
                    yield return item;
                }
            }
        }
    }

    /// <summary>vmangos Bag::StoreItem: slot GUID, CONTAINED = bag, OWNER = bag owner.</summary>
    internal void StoreItem(byte slot, Item item)
    {
        _slots[slot] = item;
        SetUInt64(UpdateFields.ContainerFieldSlot1 + (slot * 2), item.Guid.Value);
        item.ContainedIn = Guid;
        item.OwnerGuid = OwnerGuid;
        item.Container = this;
        item.Slot = slot;
    }

    /// <summary>vmangos Bag::RemoveItem.</summary>
    internal void RemoveItem(byte slot)
    {
        if (_slots[slot] is { } item)
        {
            item.Container = null;
        }

        _slots[slot] = null;
        SetUInt64(UpdateFields.ContainerFieldSlot1 + (slot * 2), 0);
    }
}
