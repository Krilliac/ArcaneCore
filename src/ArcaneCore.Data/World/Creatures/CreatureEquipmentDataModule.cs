using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Creatures;

/// <summary>
/// <c>creature_equip_template</c>: the item entries (not display ids) a creature template holds in its main hand, off hand and ranged
/// slot (ClassicDB <c>equipentry1..3</c>, vmangos <c>item1..3</c>). Keyed by <see cref="Entry"/>, which
/// <see cref="CreatureTemplateRow.EquipmentTemplateId"/> names.
/// </summary>
public sealed class CreatureEquipmentRow
{
    public uint Entry { get; set; }

    public uint EquipEntry1 { get; set; }

    public uint EquipEntry2 { get; set; }

    public uint EquipEntry3 { get; set; }
}

/// <summary>
/// World schema 56: <c>creature_equip_template</c> and <c>creature_template.EquipmentTemplateId</c>, so creatures hold their weapons,
/// shields and ranged items (virtual item update fields). Filled by the content importer from a ClassicDB / cmangos / vmangos dump; a
/// database upgraded to 56 keeps the creatures without equipment until <c>arcane-db refresh</c> backfills them
/// (<see cref="CreatureDumpImporter.RefreshEquipmentAsync"/>).
/// </summary>
public sealed class CreatureEquipmentDataModule : IDataModule
{
    /// <summary>The world schema version this module arrives in.</summary>
    public const int Version = 56;

    public const string EquipTemplateTable = "creature_equip_template";

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange(EquipTemplateTable),
        new AddColumnChange("creature_template", nameof(CreatureTemplateRow.EquipmentTemplateId)),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CreatureEquipmentRow>(entity =>
        {
            entity.ToTable(EquipTemplateTable);
            entity.HasKey(r => r.Entry);
            entity.Property(r => r.Entry).ValueGeneratedNever();
        });

        // The column is added to a populated table, so it needs a default; a fresh database gets the same default the step file gives.
        modelBuilder.Entity<CreatureTemplateRow>().Property(r => r.EquipmentTemplateId).HasDefaultValue(0u);
    }

    public void AddServices(IServiceCollection services)
    {
    }
}
