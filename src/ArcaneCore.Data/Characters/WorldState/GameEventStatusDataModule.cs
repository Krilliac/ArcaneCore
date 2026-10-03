using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.WorldState;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.WorldState;

/// <summary>One <c>game_event_status</c> row: an event that is running (vmangos <c>sql/characters.sql:499-502</c>, one <c>event</c> column).</summary>
public sealed class GameEventStatusRow
{
    public int Event { get; set; }
}

/// <summary>
/// Characters schema module for <c>game_event_status</c>: the set of running game events, kept so a restart resumes them
/// (docs/areas/game-events-weather.md). The table is global, not per character, so
/// <see cref="DeleteCharacterDataAsync"/> has nothing to delete; the guard test requires every characters module to
/// say how it is deleted, and "nothing" is the documented answer here. The version is one constant the integrator renumbers.
/// </summary>
public sealed class GameEventStatusDataModule : IDataModule, ICharacterDataCleanup
{
    /// <summary>The next free characters schema version at this base (the highest before it is the petition step, 20).</summary>
    public const int Version = 21;

    public const string Table = "game_event_status";

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<GameEventStatusRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(r => r.Event);
            entity.Property(r => r.Event).HasColumnName("event").ValueGeneratedNever();
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<IGameEventStatusStore, EfGameEventStatusStore>();

    /// <summary>The running-event set belongs to the world, not to a character: nothing to delete.</summary>
    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// EF Core implementation of <see cref="IGameEventStatusStore"/>. <see cref="ReplaceActiveAsync"/> deletes every row and inserts the
/// new set in one transaction; calls are serialised in the process (the world daemon is the table's only writer), so two
/// replaces never interleave their delete and insert on a provider whose transactions would let them (MariaDB repeatable
/// read would fail one of them on the key). Database-level advisory locks are not used: PostgreSQL pooling returns the same
/// physical connection to concurrent callers, which makes them re-entrant, and MariaDB DDL commits implicitly.
/// </summary>
public sealed class EfGameEventStatusStore(CharacterDbContext db) : IGameEventStatusStore
{
    private static readonly SemaphoreSlim s_writers = new(1, 1);

    public async Task<IReadOnlyList<int>> LoadActiveAsync(CancellationToken cancellationToken = default)
        => await db.Set<GameEventStatusRow>().AsNoTracking().OrderBy(r => r.Event).Select(r => r.Event)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task ReplaceActiveAsync(IReadOnlyCollection<int> events, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        await s_writers.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await db.Set<GameEventStatusRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                foreach (int id in events)
                {
                    db.Set<GameEventStatusRow>().Add(new GameEventStatusRow { Event = id });
                }

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }
        finally
        {
            s_writers.Release();
        }
    }
}
