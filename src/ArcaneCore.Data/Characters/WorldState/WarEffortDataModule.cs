using System.Data;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Quests;
using ArcaneCore.Kernel.WorldData.WorldState;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.WorldState;

/// <summary>Global AQ war-effort phase and deadline, stored with the realm's character data.</summary>
public sealed class WarEffortPhaseRow
{
    public int Id { get; set; }
    public byte Phase { get; set; }
    public long PhaseEndsAtUnix { get; set; }
}

/// <summary>One of the 30 resource counters in mangos-classic AhnQirajData.</summary>
public sealed class WarEffortCounterRow
{
    public int ResourceId { get; set; }
    public long Count { get; set; }
}

public sealed class WarEffortDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 43;
    public const string PhaseTable = "world_war_effort_phase";
    public const string CounterTable = "world_war_effort_counter";

    public DatabaseComponent Component => DatabaseComponent.Characters;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
        [new CreateTableChange(PhaseTable), new CreateTableChange(CounterTable)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WarEffortPhaseRow>(e =>
        {
            e.ToTable(PhaseTable);
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.Phase).HasColumnName("phase");
            e.Property(r => r.PhaseEndsAtUnix).HasColumnName("phase_ends_at_unix");
        });
        modelBuilder.Entity<WarEffortCounterRow>(e =>
        {
            e.ToTable(CounterTable);
            e.HasKey(r => r.ResourceId);
            e.Property(r => r.ResourceId).HasColumnName("resource_id").ValueGeneratedNever();
            e.Property(r => r.Count).HasColumnName("count");
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<IWarEffortStateStore, EfWarEffortStateStore>();

    // The war effort belongs to the realm, not an individual character.
    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class EfWarEffortStateStore(CharacterDbContext db) : IWarEffortStateStore
{
    public async Task<WarEffortSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        WarEffortPhaseRow? phase = await db.Set<WarEffortPhaseRow>().AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == 1, cancellationToken).ConfigureAwait(false);
        long[] counters = new long[WarEffortCatalog.ResourceCount];
        foreach (WarEffortCounterRow row in await db.Set<WarEffortCounterRow>().AsNoTracking()
            .ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (row.ResourceId is < 0 or >= WarEffortCatalog.ResourceCount || row.Count < 0)
                throw new InvalidOperationException($"invalid AQ resource row {row.ResourceId}={row.Count}");
            counters[row.ResourceId] = row.Count;
        }

        if (phase is not null && !Enum.IsDefined((WarEffortPhase)phase.Phase))
            throw new InvalidOperationException($"invalid AQ phase {phase.Phase}");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new WarEffortSnapshot(phase is null ? WarEffortPhase.Disabled : (WarEffortPhase)phase.Phase,
            phase?.PhaseEndsAtUnix ?? 0, counters);
    }

    public async Task SetPhaseAsync(WarEffortPhase phase, long phaseEndsAtUnix, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(phase) || phaseEndsAtUnix < 0)
            throw new ArgumentOutOfRangeException(nameof(phase));
        await using SqliteRewardWriterCoordinator.Lease writer =
            await SqliteRewardWriterCoordinator.AcquireAsync(db, cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        WarEffortPhaseRow? row = await db.Set<WarEffortPhaseRow>()
            .SingleOrDefaultAsync(r => r.Id == 1, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new WarEffortPhaseRow { Id = 1 };
            db.Add(row);
        }

        row.Phase = (byte)phase;
        row.PhaseEndsAtUnix = phaseEndsAtUnix;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
