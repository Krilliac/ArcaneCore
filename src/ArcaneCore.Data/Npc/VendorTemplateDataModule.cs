using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Kernel.Npc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Npc;

/// <summary>World schema 53: ClassicDB/vendor-template stock and creature_template's shared vendor id.</summary>
public sealed class VendorTemplateDataModule : IDataModule
{
    public const int Version = 53;
    public DatabaseComponent Component => DatabaseComponent.World;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange("npc_vendor_template"),
        new AddColumnChange("creature_template", nameof(CreatureTemplateRow.VendorTemplateId)),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<VendorTemplateItemRow>(entity =>
        {
            entity.ToTable("npc_vendor_template");
            entity.HasKey(r => new { r.Entry, r.Item });
            entity.Property(r => r.Entry).HasColumnName("entry");
            entity.Property(r => r.Item).HasColumnName("item");
            entity.Property(r => r.MaxCount).HasColumnName("maxcount");
            entity.Property(r => r.IncrTime).HasColumnName("incrtime");
            entity.Property(r => r.Slot).HasColumnName("slot");
            entity.Property(r => r.ConditionId).HasColumnName("condition_id");
        });
    }

    public void AddServices(IServiceCollection services) { }
}

/// <summary>npc_vendor_template: same stock fields as npc_vendor, with entry identifying a template.</summary>
public sealed class VendorTemplateItemRow
{
    public uint Entry { get; set; }
    public uint Item { get; set; }
    public uint MaxCount { get; set; }
    public uint IncrTime { get; set; }
    public uint Slot { get; set; }
    public uint ConditionId { get; set; }

    public VendorItem ToVendorItem() => new()
    {
        Entry = Entry, Item = Item, MaxCount = MaxCount, IncrTime = IncrTime, Slot = Slot, ConditionId = ConditionId,
    };
}
