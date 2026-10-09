using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Creatures;

/// <summary>World 43: creature waypoint script steps and the script ids on spawn, entry and named paths.</summary>
public sealed class MovementScriptDataModule : IDataModule
{
    public const int Version = 43;

    public DatabaseComponent Component => DatabaseComponent.World;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange(DbScriptDataModule.CreatureMovementTable),
        new AddColumnChange("creature_movement", nameof(CreatureMovementRow.ScriptId)),
        new AddColumnChange("creature_movement_template", nameof(CreatureMovementTemplateRow.ScriptId)),
        new CreateTableChange("spell_script_target"),
        new CreateTableChange("creature_linking"),
        new CreateTableChange("creature_linking_template"),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CreatureMovementScriptRow>(entity =>
        {
            entity.ToTable(DbScriptDataModule.CreatureMovementTable);
            entity.HasKey(r => new { r.Id, r.Ordinal });
            entity.Property(r => r.Id).ValueGeneratedNever();
            entity.Property(r => r.Ordinal).ValueGeneratedNever();
        });
        modelBuilder.Entity<SpellScriptTargetRow>(entity =>
        {
            entity.ToTable("spell_script_target");
            entity.HasKey(r => new { r.SpellId, r.Type, r.TargetEntry, r.InverseEffectMask });
        });
        modelBuilder.Entity<CreatureLinkRow>(entity =>
        {
            entity.ToTable("creature_linking");
            entity.HasKey(r => r.SlaveGuid);
        });
        modelBuilder.Entity<CreatureTemplateLinkRow>(entity =>
        {
            entity.ToTable("creature_linking_template");
            entity.HasKey(r => new { r.SlaveEntry, r.MapId });
        });
    }

    public void AddServices(IServiceCollection services) { }
}
