using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Npc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Npc;

/// <summary>
/// The <c>conditions</c> table (cmangos classic-db layout) that gossip options and menu texts, vendor rows, trainer
/// rows and quest <c>RequiredCondition</c> refer to, and the <see cref="IConditionContentStore"/> that reads it.
/// The schema version lives in <see cref="Version"/> only (the integrator renumbers it); tests refer to it or to
/// <c>WorldDbContext.Schema.CurrentVersion</c>, never to a literal. No cleanup registration: the world schema holds no
/// per-character rows (docs/integration/npc-quest-fidelity.md).
/// </summary>
public sealed class ConditionsWorldModule : IDataModule
{
    /// <summary>The world schema version that introduces <c>conditions</c> (World 9 is the index repair step, 10 the quest reputation columns).</summary>
    public const int Version = 13;

    public const string Table = "conditions";

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ConditionRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(r => r.ConditionEntry);
            entity.Property(r => r.ConditionEntry).HasColumnName("condition_entry").ValueGeneratedNever();
            entity.Property(r => r.Type).HasColumnName("type");
            entity.Property(r => r.Value1).HasColumnName("value1");
            entity.Property(r => r.Value2).HasColumnName("value2");
            entity.Property(r => r.Value3).HasColumnName("value3");
            entity.Property(r => r.Value4).HasColumnName("value4");
            entity.Property(r => r.Flags).HasColumnName("flags");
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<IConditionContentStore, EfConditionContentStore>();
}
