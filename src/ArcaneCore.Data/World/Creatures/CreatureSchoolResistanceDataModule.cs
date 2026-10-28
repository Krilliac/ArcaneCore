using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Creatures;

/// <summary>
/// World schema 57: the six creature school resistances on <c>creature_template</c> (ClassicDB <c>ResistanceHoly</c>..<c>ResistanceArcane</c>,
/// vmangos <c>holy_res</c>..<c>arcane_res</c>). <c>Creature.InitializeFields</c> writes them to <c>UnitFieldResistances + 1..6</c>, which the
/// spell resist roll reads. A database upgraded to 57 keeps every creature at 0 until the content importer is re-run with the columns.
/// </summary>
public sealed class CreatureSchoolResistanceDataModule : IDataModule
{
    /// <summary>The world schema version this module arrives in.</summary>
    public const int Version = 57;

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new AddColumnChange("creature_template", nameof(CreatureTemplateRow.ResistanceHoly)),
        new AddColumnChange("creature_template", nameof(CreatureTemplateRow.ResistanceFire)),
        new AddColumnChange("creature_template", nameof(CreatureTemplateRow.ResistanceNature)),
        new AddColumnChange("creature_template", nameof(CreatureTemplateRow.ResistanceFrost)),
        new AddColumnChange("creature_template", nameof(CreatureTemplateRow.ResistanceShadow)),
        new AddColumnChange("creature_template", nameof(CreatureTemplateRow.ResistanceArcane)),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        // The columns are added to a populated table, so they need a default; a fresh database gets the same default the step file gives.
        modelBuilder.Entity<CreatureTemplateRow>(entity =>
        {
            entity.Property(r => r.ResistanceHoly).HasDefaultValue(0);
            entity.Property(r => r.ResistanceFire).HasDefaultValue(0);
            entity.Property(r => r.ResistanceNature).HasDefaultValue(0);
            entity.Property(r => r.ResistanceFrost).HasDefaultValue(0);
            entity.Property(r => r.ResistanceShadow).HasDefaultValue(0);
            entity.Property(r => r.ResistanceArcane).HasDefaultValue(0);
        });
    }

    public void AddServices(IServiceCollection services)
    {
    }
}
