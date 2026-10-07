using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Creatures;

public sealed class CreatureTextTemplateRow
{
    public uint Id { get; set; }
    public int TargetId { get; set; }
    public uint Chance { get; set; }
}

/// <summary>World27 stores CMaNGOS random text template choices (type0 only).</summary>
public sealed class CreatureTextTemplateDataModule : IDataModule
{
    public const int Version = 37; // allocated as 27 on the Codex line; renumbered at the 2026-10-07 integration
    public const string Table = "creature_ai_text_template";
    public DatabaseComponent Component => DatabaseComponent.World;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];
    public void AddServices(IServiceCollection services) { }
    public void ConfigureModel(ModelBuilder builder) => builder.Entity<CreatureTextTemplateRow>(entity =>
    {
        entity.ToTable(Table);
        entity.HasKey(row => new { row.Id, row.TargetId });
        entity.Property(row => row.Id).ValueGeneratedNever();
        entity.Property(row => row.TargetId).ValueGeneratedNever();
    });
}
