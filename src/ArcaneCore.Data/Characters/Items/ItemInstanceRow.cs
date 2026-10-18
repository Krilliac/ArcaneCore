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

    private const string FiveZeroCharges = "0 0 0 0 0";
    private const string TwentyOneZeroEnchantments = "0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0";

    public ItemInstanceData ToData() => new()
    {
        Guid = Guid,
        Entry = ItemId,
        Count = Count,
        Creator = CreatorGuid,
        GiftCreator = GiftCreatorGuid,
        Duration = Duration,
        Charges = ParseCharges(Charges),
        Flags = Flags,
        Enchantments = ParseEnchantments(Enchantments),
        RandomPropertyId = RandomPropertyId,
        Durability = Durability,
        TextId = Text,
        GiftEntry = GiftEntry,
        GiftFlags = GiftFlags,
    };

    internal bool Matches(int ownerId, ItemInstanceData data)
        => OwnerGuid == ownerId
            && ItemId == data.Entry
            && CreatorGuid == data.Creator
            && GiftCreatorGuid == data.GiftCreator
            && Count == data.Count
            && Duration == data.Duration
            && Flags == data.Flags
            && RandomPropertyId == data.RandomPropertyId
            && Durability == data.Durability
            && Text == data.TextId
            && GiftEntry == data.GiftEntry
            && GiftFlags == data.GiftFlags
            && string.Equals(Charges, FormatCharges(data.Charges, Charges), StringComparison.Ordinal)
            && string.Equals(Enchantments, FormatEnchantments(data.Enchantments, Enchantments), StringComparison.Ordinal);

    public void CopyFrom(int ownerId, ItemInstanceData data)
    {
        OwnerGuid = ownerId;
        ItemId = data.Entry;
        CreatorGuid = data.Creator;
        GiftCreatorGuid = data.GiftCreator;
        Count = data.Count;
        Duration = data.Duration;
        Charges = FormatCharges(data.Charges, Charges);
        Flags = data.Flags;
        Enchantments = FormatEnchantments(data.Enchantments, Enchantments);
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

    private static string FormatCharges(IReadOnlyList<int> charges, string existing)
    {
        int count = charges.Count;
        if (count == 0)
        {
            return string.Empty;
        }

        if (count == 5 && (charges[0] | charges[1] | charges[2] | charges[3] | charges[4]) == 0)
        {
            return existing == FiveZeroCharges ? existing : FiveZeroCharges;
        }

        Span<char> buffer = stackalloc char[128];
        if (TryFormatList(charges, buffer, out int written))
        {
            ReadOnlySpan<char> formatted = buffer[..written];
            return existing.AsSpan().SequenceEqual(formatted) ? existing : new string(formatted);
        }

        return string.Join(' ', charges.Select(c => c.ToString(CultureInfo.InvariantCulture)));
    }

    private static string FormatEnchantments(IReadOnlyList<uint> enchantments, string existing)
    {
        int count = enchantments.Count;
        if (count == 0)
        {
            return string.Empty;
        }

        if (count == 21)
        {
            uint any = 0;
            for (int i = 0; i < 21; i++)
            {
                any |= enchantments[i];
            }

            if (any == 0)
            {
                return existing == TwentyOneZeroEnchantments ? existing : TwentyOneZeroEnchantments;
            }
        }

        Span<char> buffer = stackalloc char[256];
        if (TryFormatList(enchantments, buffer, out int written))
        {
            ReadOnlySpan<char> formatted = buffer[..written];
            return existing.AsSpan().SequenceEqual(formatted) ? existing : new string(formatted);
        }

        return string.Join(' ', enchantments.Select(e => e.ToString(CultureInfo.InvariantCulture)));
    }

    private static bool TryFormatList<T>(IReadOnlyList<T> values, Span<char> destination, out int written)
        where T : ISpanFormattable
    {
        written = 0;
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                if (written >= destination.Length)
                {
                    return false;
                }

                destination[written++] = ' ';
            }

            if (!values[i].TryFormat(destination[written..], out int charsWritten, default, CultureInfo.InvariantCulture))
            {
                return false;
            }

            written += charsWritten;
        }

        return true;
    }

    private static int[] ParseCharges(string value)
        => string.IsNullOrEmpty(value) ? [] : value == FiveZeroCharges ? new int[5] : ParseList<int>(value.AsSpan());

    private static uint[] ParseEnchantments(string value)
        => string.IsNullOrEmpty(value) ? [] : value == TwentyOneZeroEnchantments ? new uint[21] : ParseList<uint>(value.AsSpan());

    private static T[] ParseList<T>(ReadOnlySpan<char> span)
        where T : ISpanParsable<T>
    {
        int count = 0;
        foreach (Range range in span.Split(' '))
        {
            if (range.End.Value > range.Start.Value)
            {
                count++;
            }
        }

        if (count == 0)
        {
            return [];
        }

        var result = new T[count];
        int index = 0;
        foreach (Range range in span.Split(' '))
        {
            ReadOnlySpan<char> token = span[range];
            if (!token.IsEmpty)
            {
                result[index++] = T.Parse(token, CultureInfo.InvariantCulture);
            }
        }

        return result;
    }
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
