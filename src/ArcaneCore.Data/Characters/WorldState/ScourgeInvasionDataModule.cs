using System.Data;
using ArcaneCore.Data.Quests;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.WorldState;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.WorldState;

public sealed class ScourgeInvasionStateRow
{
    public int Id { get; set; }
    public byte State { get; set; }
    public int BattlesWon { get; set; }
    public uint LastAttackZone { get; set; }
}

public sealed class ScourgeInvasionZoneRow
{
    public uint ZoneId { get; set; }
    public int Remaining { get; set; }
    public long NextAttackUnix { get; set; }
}

public sealed class ScourgeInvasionKillRow
{
    public uint SpawnGuid { get; set; }
    public uint ZoneId { get; set; }
}

/// <summary>Realm-wide Scourge state: remaining necropolises, defeated zones and idempotent spawn deaths.</summary>
public sealed class ScourgeInvasionDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 45;
    public const string StateTable = "world_scourge_invasion_state";
    public const string ZoneTable = "world_scourge_invasion_zone";
    public const string KillTable = "world_scourge_invasion_necropolis_kill";
    public DatabaseComponent Component => DatabaseComponent.Characters;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
        [new CreateTableChange(StateTable), new CreateTableChange(ZoneTable), new CreateTableChange(KillTable)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ScourgeInvasionStateRow>(e =>
        {
            e.ToTable(StateTable);
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.State).HasColumnName("state");
            e.Property(r => r.BattlesWon).HasColumnName("battles_won");
            e.Property(r => r.LastAttackZone).HasColumnName("last_attack_zone");
        });
        modelBuilder.Entity<ScourgeInvasionZoneRow>(e =>
        {
            e.ToTable(ZoneTable);
            e.HasKey(r => r.ZoneId);
            e.Property(r => r.ZoneId).HasColumnName("zone_id").ValueGeneratedNever();
            e.Property(r => r.Remaining).HasColumnName("remaining");
            e.Property(r => r.NextAttackUnix).HasColumnName("next_attack_unix");
        });
        modelBuilder.Entity<ScourgeInvasionKillRow>(e =>
        {
            e.ToTable(KillTable);
            e.HasKey(r => r.SpawnGuid);
            e.Property(r => r.SpawnGuid).HasColumnName("spawn_guid").ValueGeneratedNever();
            e.Property(r => r.ZoneId).HasColumnName("zone_id");
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<IScourgeInvasionStateStore, EfScourgeInvasionStateStore>();
    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class EfScourgeInvasionStateStore(CharacterDbContext db) : IScourgeInvasionStateStore
{
    public async Task<ScourgeInvasionSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        ScourgeInvasionStateRow? state = await db.Set<ScourgeInvasionStateRow>().AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == 1, cancellationToken).ConfigureAwait(false);
        var stored = await db.Set<ScourgeInvasionZoneRow>().AsNoTracking()
            .ToDictionaryAsync(r => r.ZoneId, cancellationToken).ConfigureAwait(false);
        uint[] destroyed = await db.Set<ScourgeInvasionKillRow>().AsNoTracking()
            .Select(r => r.SpawnGuid).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (state is not null && !Enum.IsDefined((ScourgeInvasionState)state.State))
            throw new InvalidOperationException($"invalid Scourge invasion state {state.State}");
        foreach (ScourgeInvasionZoneRow row in stored.Values)
        {
            if (ScourgeInvasionCatalog.ForZone(row.ZoneId) is null || row.Remaining < 0)
                throw new InvalidOperationException($"invalid Scourge invasion zone {row.ZoneId}={row.Remaining}");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ScourgeInvasionSnapshot(state is null ? ScourgeInvasionState.Disabled : (ScourgeInvasionState)state.State,
            state?.BattlesWon ?? 0, state?.LastAttackZone ?? 0,
            ScourgeInvasionCatalog.Zones.Select(z => stored.TryGetValue(z.ZoneId, out ScourgeInvasionZoneRow? row)
                ? new ScourgeInvasionZoneProgress(z.ZoneId, row.Remaining, row.NextAttackUnix)
                : new ScourgeInvasionZoneProgress(z.ZoneId, 0, 0)).ToArray())
        {
            DestroyedSpawnGuids = new HashSet<uint>(destroyed),
        };
    }

    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteRewardWriterCoordinator.Lease writer =
            await SqliteRewardWriterCoordinator.AcquireAsync(db, cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        ScourgeInvasionStateRow? state = await db.Set<ScourgeInvasionStateRow>()
            .SingleOrDefaultAsync(r => r.Id == 1, cancellationToken).ConfigureAwait(false);
        if (state?.State == (byte)ScourgeInvasionState.Enabled) return false;
        state ??= new ScourgeInvasionStateRow { Id = 1 };
        if (db.Entry(state).State == EntityState.Detached) db.Add(state);
        state.State = (byte)ScourgeInvasionState.Enabled;
        state.BattlesWon = 0;
        state.LastAttackZone = 0;
        await db.Set<ScourgeInvasionZoneRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<ScourgeInvasionKillRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        foreach (ScourgeInvasionZone zone in ScourgeInvasionCatalog.Zones)
            db.Add(new ScourgeInvasionZoneRow { ZoneId = zone.ZoneId, Remaining = zone.Necropolises });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteRewardWriterCoordinator.Lease writer =
            await SqliteRewardWriterCoordinator.AcquireAsync(db, cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        ScourgeInvasionStateRow? state = await db.Set<ScourgeInvasionStateRow>()
            .SingleOrDefaultAsync(r => r.Id == 1, cancellationToken).ConfigureAwait(false);
        if (state is not null)
        {
            state.State = (byte)ScourgeInvasionState.Disabled;
            state.BattlesWon = 0;
            state.LastAttackZone = 0;
        }
        await db.Set<ScourgeInvasionZoneRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<ScourgeInvasionKillRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> NecropolisDestroyedAsync(uint zoneId, uint spawnGuid, long nowUnix, int nextAttackSeconds,
        CancellationToken cancellationToken = default)
    {
        if (ScourgeInvasionCatalog.ForZone(zoneId) is null || spawnGuid == 0 || nextAttackSeconds is < 2700 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(zoneId));
        await using SqliteRewardWriterCoordinator.Lease writer =
            await SqliteRewardWriterCoordinator.AcquireAsync(db, cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        ScourgeInvasionStateRow? state = await db.Set<ScourgeInvasionStateRow>()
            .SingleOrDefaultAsync(r => r.Id == 1, cancellationToken).ConfigureAwait(false);
        ScourgeInvasionZoneRow? zone = await db.Set<ScourgeInvasionZoneRow>()
            .SingleOrDefaultAsync(r => r.ZoneId == zoneId, cancellationToken).ConfigureAwait(false);
        if (state?.State != (byte)ScourgeInvasionState.Enabled || zone is null || zone.Remaining <= 0
            || await db.Set<ScourgeInvasionKillRow>().AnyAsync(r => r.SpawnGuid == spawnGuid, cancellationToken)
                .ConfigureAwait(false)) return false;
        db.Add(new ScourgeInvasionKillRow { SpawnGuid = spawnGuid, ZoneId = zoneId });
        zone.Remaining--;
        if (zone.Remaining == 0)
        {
            state.BattlesWon++;
            state.LastAttackZone = zoneId;
            zone.NextAttackUnix = checked(nowUnix + nextAttackSeconds);
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> RestartZoneAsync(uint zoneId, long nowUnix, CancellationToken cancellationToken = default)
    {
        ScourgeInvasionZone? definition = ScourgeInvasionCatalog.ForZone(zoneId);
        if (definition is null) throw new ArgumentOutOfRangeException(nameof(zoneId));
        await using SqliteRewardWriterCoordinator.Lease writer =
            await SqliteRewardWriterCoordinator.AcquireAsync(db, cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        ScourgeInvasionStateRow? state = await db.Set<ScourgeInvasionStateRow>()
            .SingleOrDefaultAsync(r => r.Id == 1, cancellationToken).ConfigureAwait(false);
        ScourgeInvasionZoneRow? zone = await db.Set<ScourgeInvasionZoneRow>()
            .SingleOrDefaultAsync(r => r.ZoneId == zoneId, cancellationToken).ConfigureAwait(false);
        int activeZones = await db.Set<ScourgeInvasionZoneRow>().CountAsync(r => r.Remaining > 0, cancellationToken)
            .ConfigureAwait(false);
        if (state?.State != (byte)ScourgeInvasionState.Enabled || state.BattlesWon >= 150
            || state.LastAttackZone == zoneId || activeZones > 1 || zone is null || zone.Remaining != 0
            || zone.NextAttackUnix == 0 || zone.NextAttackUnix > nowUnix) return false;
        await db.Set<ScourgeInvasionKillRow>().Where(r => r.ZoneId == zoneId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        zone.Remaining = definition.Necropolises;
        zone.NextAttackUnix = 0;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
