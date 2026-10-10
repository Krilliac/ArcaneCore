using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.WorldState;

/// <summary>The saved three-bit Silithus Colossus death set of mangos-classic AhnQirajData.</summary>
public sealed class WarEffortBossKillRow
{
    public int BossId { get; set; }
}

public sealed class WarEffortBossDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 44;
    public const string Table = "world_war_effort_boss_kill";
    public DatabaseComponent Component => DatabaseComponent.Characters;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
        => modelBuilder.Entity<WarEffortBossKillRow>(e =>
        {
            e.ToTable(Table);
            e.HasKey(r => r.BossId);
            e.Property(r => r.BossId).HasColumnName("boss_id").ValueGeneratedNever();
        });

    public void AddServices(IServiceCollection services) { }
    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken) => Task.CompletedTask;
}
