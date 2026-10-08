using System.Globalization;
using ArcaneCore.Kernel.Items;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ArcaneCore.Data.Characters.Items;

/// <summary>
/// One item instance (vmangos <c>item_instance</c>, column names kept). <c>charges</c> and
/// <c>enchantments</c> are space-separated integer lists as in vmangos Item::SaveToDB.
/// vmangos' <c>generated_loot</c> belongs to item loot (not part of this area) and is not carried.
/// </summary>
public sealed class ItemInstanceRow
{
    public uint Guid { get; set; }
    public int OwnerGuid { get; set; }
    public uint ItemId { get; set; }
    public ulong CreatorGuid { get; set; }
    public ulong GiftCreatorGuid { get; set; }
    public uint Count { get; set; }
    public uint Duration { get; set; }
    public string Charges { get; set; } = string.Empty;
    public uint Flags { get; set; }
    public string Enchantments { get; set; } = string.Empty;
    public int RandomPropertyId { get; set; }
    public uint Durability { get; set; }
    public uint Text { get; set; }

    /// <summary>The wrapped item's own entry (vmangos character_gifts.item_id); 0 when not wrapped. Column added at characters <see cref="ItemGiftDataModule.Version"/>.</summary>
    public uint GiftEntry { get; set; }

    /// <summary>The wrapped item's own ITEM_FIELD_FLAGS (vmangos character_gifts.flags).</summary>
    public uint GiftFlags { get; set; }

    public ItemInstanceData ToData() => new()
    {
        Guid = Guid,
        Entry = ItemId,
        Count = Count,
        Creator = CreatorGuid,
        GiftCreator = GiftCreatorGuid,
        Duration = Duration,
        Charges = ParseList(Charges, int.Parse),
        Flags = Flags,
        Enchantments = ParseList(Enchantments, uint.Parse),
        RandomPropertyId = RandomPropertyId,
        Durability = Durability,
        TextId = Text,
        GiftEntry = GiftEntry,
        GiftFlags = GiftFlags,
    };

    public void CopyFrom(int ownerId, ItemInstanceData data)
    {
        OwnerGuid = ownerId;
        ItemId = data.Entry;
        CreatorGuid = data.Creator;
        GiftCreatorGuid = data.GiftCreator;
        Count = data.Count;
        Duration = data.Duration;
        Charges = string.Join(' ', data.Charges.Select(c => c.ToString(CultureInfo.InvariantCulture)));
        Flags = data.Flags;
        Enchantments = string.Join(' ', data.Enchantments.Select(e => e.ToString(CultureInfo.InvariantCulture)));
        RandomPropertyId = data.RandomPropertyId;
        Durability = data.Durability;
        Text = data.TextId;
        GiftEntry = data.GiftEntry;
        GiftFlags = data.GiftFlags;
    }

    internal static void Configure(EntityTypeBuilder<ItemInstanceRow> entity)
    {
        entity.ToTable("item_instance");
        entity.HasKey(r => r.Guid);
        entity.Property(r => r.Guid).HasColumnName("guid").ValueGeneratedNever();
        entity.Property(r => r.OwnerGuid).HasColumnName("owner_guid");
        entity.Property(r => r.ItemId).HasColumnName("item_id");
        entity.Property(r => r.CreatorGuid).HasColumnName("creator_guid");
        entity.Property(r => r.GiftCreatorGuid).HasColumnName("gift_creator_guid");
        entity.Property(r => r.Count).HasColumnName("count");
        entity.Property(r => r.Duration).HasColumnName("duration");
        entity.Property(r => r.Charges).HasColumnName("charges").HasMaxLength(128).IsRequired();
        entity.Property(r => r.Flags).HasColumnName("flags");
        entity.Property(r => r.Enchantments).HasColumnName("enchantments").HasMaxLength(512).IsRequired();
        entity.Property(r => r.RandomPropertyId).HasColumnName("random_property_id");
        entity.Property(r => r.Durability).HasColumnName("durability");
        entity.Property(r => r.Text).HasColumnName("text");
        entity.HasIndex(r => r.OwnerGuid);
    }

    private static T[] ParseList<T>(string value, Func<string, IFormatProvider, T> parse)
        => value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(v => parse(v, CultureInfo.InvariantCulture)).ToArray();
}

/// <summary>
/// Where an item sits (vmangos <c>character_inventory</c>: guid = character, bag = container
/// item GUID or 0, slot, item_guid, item_id).
/// </summary>
public sealed class CharacterInventoryRow
{
    public int Guid { get; set; }
    public uint Bag { get; set; }
    public byte Slot { get; set; }
    public uint ItemGuid { get; set; }
    public uint ItemId { get; set; }

    internal static void Configure(EntityTypeBuilder<CharacterInventoryRow> entity)
    {
        entity.ToTable("character_inventory");
        entity.HasKey(r => r.ItemGuid);
        entity.Property(r => r.ItemGuid).HasColumnName("item_guid").ValueGeneratedNever();
        entity.Property(r => r.Guid).HasColumnName("guid");
        entity.Property(r => r.Bag).HasColumnName("bag");
        entity.Property(r => r.Slot).HasColumnName("slot");
        entity.Property(r => r.ItemId).HasColumnName("item_id");
        entity.HasIndex(r => r.Guid);
    }
}
