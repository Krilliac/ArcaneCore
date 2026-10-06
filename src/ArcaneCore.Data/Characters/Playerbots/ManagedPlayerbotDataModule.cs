using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Characters;
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
    public const int Version = 25;
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
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("The managed playerbot identity is already owned.", ex);
        }
    }

    public async Task<bool> UpdateAsync(ManagedPlayerbot bot, long expectedRevision, CancellationToken cancellationToken = default)
    {
        Validate(bot);
        if (expectedRevision < 0 || bot.Revision != expectedRevision + 1)
            return false;

        ManagedPlayerbotRow? current = await db.Set<ManagedPlayerbotRow>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.BotId == bot.BotId, cancellationToken).ConfigureAwait(false);
        if (current is null || current.AccountId != bot.AccountId || current.CharacterId != bot.CharacterId
            || current.AccountName != bot.AccountName || current.CreatedUnix != bot.CreatedUnix)
            return false;

        int changed = await db.Set<ManagedPlayerbotRow>()
            .Where(row => row.BotId == bot.BotId && row.Revision == expectedRevision)
            .ExecuteUpdateAsync(update => update
                .SetProperty(row => row.DesiredEnabled, bot.DesiredEnabled)
                .SetProperty(row => row.State, bot.State)
                .SetProperty(row => row.Goal, bot.Goal)
                .SetProperty(row => row.TargetEntry, bot.TargetEntry)
                .SetProperty(row => row.QuestId, bot.QuestId)
                .SetProperty(row => row.Revision, bot.Revision)
                .SetProperty(row => row.UpdatedUnix, bot.UpdatedUnix)
                .SetProperty(row => row.ErrorCode, bot.ErrorCode), cancellationToken).ConfigureAwait(false);
        return changed == 1;
    }

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
