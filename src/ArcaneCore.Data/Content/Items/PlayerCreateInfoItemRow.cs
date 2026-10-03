using ArcaneCore.Kernel.Items;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ArcaneCore.Data.Content.Items;

/// <summary>
/// One starting item (vmangos <c>playercreateinfo_item</c>: race, class, itemid, amount).
/// vmangos builds the whole starting outfit from this table (Player::AddStartingItems); there is
/// no CharStartOutfit.dbc lookup.
/// </summary>
public sealed class PlayerCreateInfoItemRow
{
    public byte Race { get; set; }
    public byte Class { get; set; }
    public uint ItemId { get; set; }
    public uint Amount { get; set; } = 1;

    public StartingItem ToStartingItem() => new(Race, Class, ItemId, Amount);

    internal static void Configure(EntityTypeBuilder<PlayerCreateInfoItemRow> entity)
    {
        entity.ToTable("playercreateinfo_item");
        entity.HasKey(r => new { r.Race, r.Class, r.ItemId });
        entity.Property(r => r.Race).HasColumnName("race");
        entity.Property(r => r.Class).HasColumnName("class");
        entity.Property(r => r.ItemId).HasColumnName("itemid");
        entity.Property(r => r.Amount).HasColumnName("amount");
    }
}
