using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Creatures;

/// <summary>
/// World schema 57: the six magic-school resistances of <c>creature_template</c> (cmangos <c>ResistanceHoly</c>..<c>ResistanceArcane</c>,
/// vmangos <c>holy_res</c>..<c>arcane_res</c>), so a creature is created with its template resistance instead of 0. Filled by the content
/// importer; a database upgraded to 57 keeps 0 for every creature until the content is imported again.
/// </summary>
public sealed class CreatureResistanceDataModule : IDataModule
{
    /// <summary>The world schema version this module arrives in.</summary>
    public const int Version = 57;

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new AddColumnChange("creature_template", nameof(CreatureTemplateRow.HolyRes)),
        new AddColumnChange("creature_template", nameof(CreatureTemplateRow.FireRes)),
        new AddColumnChange("creature_template", nameof(CreatureTemplateRow.NatureRes)),
        new AddColumnChange("creature_template", nameof(CreatureTemplateRow.FrostRes)),
        new AddColumnChange("creature_template", nameof(CreatureTemplateRow.ShadowRes)),
        new AddColumnChange("creature_template", nameof(CreatureTemplateRow.ArcaneRes)),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        // The columns are added to a populated table, so they need a default; a fresh database gets the same default the step file gives.
        modelBuilder.Entity<CreatureTemplateRow>(entity =>
        {
            entity.Property(r => r.HolyRes).HasDefaultValue(0);
            entity.Property(r => r.FireRes).HasDefaultValue(0);
            entity.Property(r => r.NatureRes).HasDefaultValue(0);
            entity.Property(r => r.FrostRes).HasDefaultValue(0);
            entity.Property(r => r.ShadowRes).HasDefaultValue(0);
            entity.Property(r => r.ArcaneRes).HasDefaultValue(0);
        });
    }

    public void AddServices(IServiceCollection services)
    {
    }
}
