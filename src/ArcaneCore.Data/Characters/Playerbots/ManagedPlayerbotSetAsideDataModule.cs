using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Playerbots;

public sealed class ManagedPlayerbotSetAsideRow
{
    public Guid BotId { get; set; }
    public int CharacterId { get; set; }
    public PlayerbotSetAsideKind Kind { get; set; }
    public uint TargetId { get; set; }
    public long ExpiresUnixMs { get; set; }
    public int RepeatCount { get; set; }
}

/// <summary>Characters schema 59: expiring local and shared playerbot set-asides.</summary>
public sealed class ManagedPlayerbotSetAsideDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 59;
    public const string Table = "managed_playerbot_set_aside";

    public DatabaseComponent Component => DatabaseComponent.Characters;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ManagedPlayerbotSetAsideRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(row => new { row.BotId, row.Kind, row.TargetId });
            entity.Property(row => row.BotId).ValueGeneratedNever();
            entity.HasIndex(row => row.CharacterId);
        });
    }

    public void AddServices(IServiceCollection services)
        => services.AddScoped<IManagedPlayerbotSetAsideStore, EfManagedPlayerbotSetAsideStore>();

    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
        => db.Set<ManagedPlayerbotSetAsideRow>().Where(row => row.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken);
}

public sealed class EfManagedPlayerbotSetAsideStore(CharacterDbContext db) : IManagedPlayerbotSetAsideStore
{
    public async Task<IReadOnlyList<PlayerbotSetAside>> LoadAllAsync(CancellationToken cancellationToken = default)
        => (await db.Set<ManagedPlayerbotSetAsideRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(row => new PlayerbotSetAside(row.BotId, row.CharacterId, row.Kind, row.TargetId,
                row.ExpiresUnixMs, row.RepeatCount)).ToArray();

    public async Task ReplaceAsync(Guid botId, IReadOnlyList<PlayerbotSetAside> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count > 512 || rows.Any(row => row.BotId != botId || row.ExpiresUnixMs <= 0
            || row.RepeatCount is < 0 or > 256 || (row.Kind == PlayerbotSetAsideKind.Training ? row.TargetId != 0 : row.TargetId == 0)
            || (botId == Guid.Empty ? row.CharacterId != 0 || row.Kind is not (PlayerbotSetAsideKind.SharedEntry or PlayerbotSetAsideKind.SharedQuest)
                : row.CharacterId <= 0 || row.Kind is PlayerbotSetAsideKind.SharedEntry or PlayerbotSetAsideKind.SharedQuest)))
            throw new ArgumentException("Invalid or unbounded playerbot set-aside snapshot.", nameof(rows));

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<ManagedPlayerbotSetAsideRow>().Where(row => row.BotId == botId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        db.Set<ManagedPlayerbotSetAsideRow>().AddRange(rows.Select(row => new ManagedPlayerbotSetAsideRow
        {
            BotId = row.BotId, CharacterId = row.CharacterId, Kind = row.Kind, TargetId = row.TargetId,
            ExpiresUnixMs = row.ExpiresUnixMs, RepeatCount = row.RepeatCount,
        }));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { db.ChangeTracker.Clear(); }
    }
}
