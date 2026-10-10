using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.WorldState;

/// <summary>
/// vmangos VAR_WE_GONG_BANG_TIMES and VAR_WE_GONG_TIME: how many Bang a Gong! rewards committed, and when and by whom the
/// first ring opened the gate. The single row is written in the quest reward transaction.
/// </summary>
public sealed class WarEffortGongRow
{
    public int Id { get; set; }
    public long RingCount { get; set; }
    public long FirstRungAtUnix { get; set; }
    public int FirstRingerId { get; set; }
}

public sealed class WarEffortGongDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 46;
    public const string Table = "world_war_effort_gong";
    public DatabaseComponent Component => DatabaseComponent.Characters;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
        => modelBuilder.Entity<WarEffortGongRow>(e =>
        {
            e.ToTable(Table);
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.RingCount).HasColumnName("ring_count");
            e.Property(r => r.FirstRungAtUnix).HasColumnName("first_rung_at_unix");
            e.Property(r => r.FirstRingerId).HasColumnName("first_ringer_id");
        });

    public void AddServices(IServiceCollection services) { }

    // The champion id is a historical realm fact (vmangos keeps the saved variable too); deleting the character keeps it.
    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken) => Task.CompletedTask;
}
