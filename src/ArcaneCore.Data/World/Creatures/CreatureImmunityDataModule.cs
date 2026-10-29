using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Creatures;

/// <summary>
/// <c>creature_immunities</c>: the static immunities of a creature entry, grouped in sets an EventAI action switches between (cmangos
/// <c>Entry, SetId, Type, Value</c>; mangos-classic ObjectMgr::LoadCreatureImmunities, ObjectMgr.cpp:946-976). <see cref="Type"/> is the
/// mangos <c>SpellImmunity</c> enum (0 effect, 1 aura type, 2 school mask, 3 damage school mask, 4 dispel type, 5 mechanic).
/// </summary>
public sealed class CreatureImmunityRow
{
    public uint Entry { get; set; }

    public uint SetId { get; set; }

    public byte Type { get; set; }

    public uint Value { get; set; }
}

/// <summary>
/// World schema 61: <c>creature_immunities</c> and the <c>creature_template</c> columns <c>MechanicImmuneMask</c> and <c>SchoolImmuneMask</c>, so
/// creatures are immune to the mechanics, schools, dispel types, effects and auras the reference databases give them. Filled by the content
/// importer from a ClassicDB / cmangos (table and masks) or vmangos (masks) dump; a database upgraded to 61 keeps its creatures without
/// immunities until <c>arcane-db refresh</c> backfills them (<see cref="CreatureDumpImporter.RefreshImmunitiesAsync"/>).
/// </summary>
public sealed class CreatureImmunityDataModule : IDataModule
{
    /// <summary>The world schema version this module arrives in.</summary>
    public const int Version = 61;

    public const string ImmunityTable = "creature_immunities";

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange(ImmunityTable),
        new AddColumnChange("creature_template", nameof(CreatureTemplateRow.MechanicImmuneMask)),
        new AddColumnChange("creature_template", nameof(CreatureTemplateRow.SchoolImmuneMask)),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CreatureImmunityRow>(entity =>
        {
            entity.ToTable(ImmunityTable);
            entity.HasKey(r => new { r.Entry, r.SetId, r.Type, r.Value });
        });

        // The columns are added to a populated table, so they need a default; a fresh database gets the same default the step file gives.
        modelBuilder.Entity<CreatureTemplateRow>().Property(r => r.MechanicImmuneMask).HasDefaultValue(0u);
        modelBuilder.Entity<CreatureTemplateRow>().Property(r => r.SchoolImmuneMask).HasDefaultValue(0u);
    }

    public void AddServices(IServiceCollection services)
    {
    }
}
