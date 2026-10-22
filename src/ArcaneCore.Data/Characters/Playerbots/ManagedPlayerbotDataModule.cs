using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Playerbots;

/// <summary>Durable server ownership for managed autonomous-player characters.</summary>
public sealed class ManagedPlayerbotRow
{
    public Guid BotId { get; set; }
    public int AccountId { get; set; }
    public int CharacterId { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public bool DesiredEnabled { get; set; }
    public ManagedPlayerbotState State { get; set; }
    public PlayerbotGoalKind Goal { get; set; }
    public uint TargetEntry { get; set; }
    public uint QuestId { get; set; }
    public long Revision { get; set; }
    public long CreatedUnix { get; set; }
    public long UpdatedUnix { get; set; }
    public string? ErrorCode { get; set; }
}

/// <summary>Characters schema step allocated by the integration owner for managed playerbots.</summary>
public sealed class ManagedPlayerbotDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 33; // allocated as 25 on the Codex line; renumbered at the 2026-10-07 integration
    public const string Table = "managed_playerbot";

    public DatabaseComponent Component => DatabaseComponent.Characters;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<ManagedPlayerbotRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(row => row.BotId);
            entity.Property(row => row.BotId).ValueGeneratedNever();
            entity.Property(row => row.AccountName).HasMaxLength(32).IsRequired();
            entity.Property(row => row.ErrorCode).HasMaxLength(128);
            entity.HasIndex(row => row.AccountId).IsUnique();
            entity.HasIndex(row => row.CharacterId).IsUnique();
        });
    }

    public void AddServices(IServiceCollection services)
        => services.AddScoped<IManagedPlayerbotStore, EfManagedPlayerbotStore>();

    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
        => db.Set<ManagedPlayerbotRow>().Where(row => row.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken);
}

/// <summary>EF store with ownership validation and optimistic revision updates.</summary>
public sealed class EfManagedPlayerbotStore(CharacterDbContext db) : IManagedPlayerbotStore
{
    public async Task<IReadOnlyList<ManagedPlayerbot>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        List<ManagedPlayerbotRow> rows = await db.Set<ManagedPlayerbotRow>().AsNoTracking().OrderBy(row => row.BotId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(ToRecord).ToArray();
    }

    public async Task<ManagedPlayerbot?> FindAsync(Guid botId, CancellationToken cancellationToken = default)
    {
        if (botId == Guid.Empty) return null;
        ManagedPlayerbotRow? row = await db.Set<ManagedPlayerbotRow>().AsNoTracking()
            .SingleOrDefaultAsync(r => r.BotId == botId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToRecord(row);
    }

    public async Task CreateAsync(ManagedPlayerbot bot, CancellationToken cancellationToken = default)
    {
        Validate(bot);
        if (bot.Revision != 0 || bot.CreatedUnix != bot.UpdatedUnix)
            throw new ArgumentException("A new managed playerbot must start at revision zero with matching timestamps.", nameof(bot));

        CharacterRecord? character = await db.Characters.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == bot.CharacterId, cancellationToken).ConfigureAwait(false);
        if (character is null || character.AccountId != bot.AccountId)
            throw new InvalidOperationException("The managed playerbot character does not belong to its account.");

        if (await db.Set<ManagedPlayerbotRow>().AnyAsync(row => row.BotId == bot.BotId
            || row.AccountId == bot.AccountId || row.CharacterId == bot.CharacterId, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The managed playerbot identity is already owned.");

        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        db.Set<ManagedPlayerbotRow>().Add(FromRecord(bot));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            FaultLedger.Shared.Record("data.managed-playerbot", ex);
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("The managed playerbot identity is already owned.", ex);
        }
    }

    public async Task<bool> UpdateAsync(ManagedPlayerbot bot, long expectedRevision, CancellationToken cancellationToken = default)
    {
        Validate(bot);
        if (expectedRevision < 0 || bot.Revision != expectedRevision + 1)
            return false;

        int changed = await CharacterWrites.InTransactionAsync(db, () => db.Set<ManagedPlayerbotRow>()
            .Where(row => row.BotId == bot.BotId && row.Revision == expectedRevision && row.AccountId == bot.AccountId
                && row.CharacterId == bot.CharacterId && row.AccountName == bot.AccountName && row.CreatedUnix == bot.CreatedUnix)
            .ExecuteUpdateAsync(update => update
                .SetProperty(row => row.DesiredEnabled, bot.DesiredEnabled)
                .SetProperty(row => row.State, bot.State)
                .SetProperty(row => row.Goal, bot.Goal)
                .SetProperty(row => row.TargetEntry, bot.TargetEntry)
                .SetProperty(row => row.QuestId, bot.QuestId)
                .SetProperty(row => row.Revision, bot.Revision)
                .SetProperty(row => row.UpdatedUnix, bot.UpdatedUnix)
                .SetProperty(row => row.ErrorCode, bot.ErrorCode), cancellationToken), cancellationToken).ConfigureAwait(false);
        return changed == 1;
    }

    public async Task<IReadOnlyList<bool>> UpdateManyAsync(IReadOnlyList<ManagedPlayerbotUpdate> updates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updates);
        foreach (ManagedPlayerbotUpdate update in updates) Validate(update.Bot);
        var results = new bool[updates.Count];
        for (int start = 0; start < updates.Count; start += ManagedPlayerbotUpdate.MaxBatch)
        {
            int end = Math.Min(updates.Count, start + ManagedPlayerbotUpdate.MaxBatch);
            // One EF transaction per chunk (so SlowTransactionInterceptor sees it): one fsync for up to MaxBatch rows. The row's identity
            // check is part of its WHERE, so a mismatch or a stale revision is a zero-row update, not a failure of the chunk.
            await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
                await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            // SQLite: one UPDATE text built once and run as raw SQL per row (SqliteRowCommand), not an ExecuteUpdate expression per row, so the write lock
            // is held for the statements rather than for EF's per-row query work. Other providers keep the LINQ form.
            using SqliteRowCommand? command = db.Database.IsSqlite()
                ? SqliteRowCommand.Update(db, typeof(ManagedPlayerbotRow), UpdateSet, UpdateWhere)
                : null;
            for (int i = start; i < end; i++)
            {
                ManagedPlayerbot bot = updates[i].Bot;
                long expected = updates[i].ExpectedRevision;
                if (expected < 0 || bot.Revision != expected + 1) continue;
                if (command is not null)
                {
                    results[i] = await command.ExecuteAsync(
                        [bot.DesiredEnabled, bot.State, bot.Goal, bot.TargetEntry, bot.QuestId, bot.Revision, bot.UpdatedUnix, bot.ErrorCode,
                            bot.BotId, expected, bot.AccountId, bot.CharacterId, bot.AccountName, bot.CreatedUnix],
                        cancellationToken).ConfigureAwait(false) == 1;
                    continue;
                }

                results[i] = await db.Set<ManagedPlayerbotRow>()
                    .Where(row => row.BotId == bot.BotId && row.Revision == expected && row.AccountId == bot.AccountId
                        && row.CharacterId == bot.CharacterId && row.AccountName == bot.AccountName && row.CreatedUnix == bot.CreatedUnix)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(row => row.DesiredEnabled, bot.DesiredEnabled)
                        .SetProperty(row => row.State, bot.State)
                        .SetProperty(row => row.Goal, bot.Goal)
                        .SetProperty(row => row.TargetEntry, bot.TargetEntry)
                        .SetProperty(row => row.QuestId, bot.QuestId)
                        .SetProperty(row => row.Revision, bot.Revision)
                        .SetProperty(row => row.UpdatedUnix, bot.UpdatedUnix)
                        .SetProperty(row => row.ErrorCode, bot.ErrorCode), cancellationToken).ConfigureAwait(false) == 1;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    /// <summary>The columns <see cref="UpdateManyAsync"/> sets, then the identity and revision its WHERE checks (as the LINQ form).</summary>
    private static readonly string[] UpdateSet = [nameof(ManagedPlayerbotRow.DesiredEnabled), nameof(ManagedPlayerbotRow.State),
        nameof(ManagedPlayerbotRow.Goal), nameof(ManagedPlayerbotRow.TargetEntry), nameof(ManagedPlayerbotRow.QuestId),
        nameof(ManagedPlayerbotRow.Revision), nameof(ManagedPlayerbotRow.UpdatedUnix), nameof(ManagedPlayerbotRow.ErrorCode)];

    private static readonly string[] UpdateWhere = [nameof(ManagedPlayerbotRow.BotId), nameof(ManagedPlayerbotRow.Revision),
        nameof(ManagedPlayerbotRow.AccountId), nameof(ManagedPlayerbotRow.CharacterId), nameof(ManagedPlayerbotRow.AccountName),
        nameof(ManagedPlayerbotRow.CreatedUnix)];

    public async Task<bool> DeleteAsync(Guid botId, int accountId, int characterId, CancellationToken cancellationToken = default)
        => await CharacterWrites.InTransactionAsync(db, () => db.Set<ManagedPlayerbotRow>()
            .Where(row => row.BotId == botId && row.AccountId == accountId && row.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken), cancellationToken).ConfigureAwait(false) == 1;

    private static ManagedPlayerbot ToRecord(ManagedPlayerbotRow row)
        => new(row.BotId, row.AccountId, row.CharacterId, row.AccountName, row.DesiredEnabled, row.State,
            row.Goal, row.TargetEntry, row.QuestId, row.Revision, row.CreatedUnix, row.UpdatedUnix, row.ErrorCode);

    private static ManagedPlayerbotRow FromRecord(ManagedPlayerbot value)
        => new()
        {
            BotId = value.BotId, AccountId = value.AccountId, CharacterId = value.CharacterId,
            AccountName = value.AccountName, DesiredEnabled = value.DesiredEnabled, State = value.State,
            Goal = value.Goal, TargetEntry = value.TargetEntry, QuestId = value.QuestId,
            Revision = value.Revision, CreatedUnix = value.CreatedUnix, UpdatedUnix = value.UpdatedUnix,
            ErrorCode = value.ErrorCode,
        };

    private static void Validate(ManagedPlayerbot value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.BotId == Guid.Empty || value.AccountId <= 0 || value.CharacterId <= 0
            || string.IsNullOrWhiteSpace(value.AccountName) || value.AccountName.Length > 32
            || value.Revision < 0 || value.CreatedUnix < 0 || value.UpdatedUnix < value.CreatedUnix
            || value.ErrorCode is { Length: > 128 }
            || !Enum.IsDefined(value.State) || !Enum.IsDefined(value.Goal))
            throw new ArgumentException("Managed playerbot identity, state, goal or bounded fields are invalid.", nameof(value));
    }
}
