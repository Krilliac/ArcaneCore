using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Playerbots;

/// <summary>A <c>playerbot_population_member</c> row: a managed bot the population manager owns.</summary>
public sealed class PlayerbotPopulationMemberRow
{
    public Guid BotId { get; set; }
    public int CharacterId { get; set; }
    public uint ZoneId { get; set; }
    public byte Band { get; set; }
    public long CreatedUnix { get; set; }
    public long UpdatedUnix { get; set; }
}

/// <summary>
/// Characters schema 57 (wave 19, bot-population lane): which managed playerbots the population manager created and may park or
/// relocate. Written as 56 (the next after main's 55); renumbered to 57 in the w19 train because the bot-home lane took 56.
/// </summary>
public sealed class PlayerbotPopulationDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 57;
    public const string Table = "playerbot_population_member";

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<PlayerbotPopulationMemberRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(row => row.BotId);
            entity.Property(row => row.BotId).ValueGeneratedNever();
            entity.HasIndex(row => row.CharacterId).IsUnique();
        });
    }

    public void AddServices(IServiceCollection services)
        => services.AddScoped<IPlayerbotPopulationStore, EfPlayerbotPopulationStore>();

    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
        => db.Set<PlayerbotPopulationMemberRow>().Where(row => row.CharacterId == characterId).ExecuteDeleteAsync(cancellationToken);
}

/// <summary>EF store of the population membership.</summary>
public sealed class EfPlayerbotPopulationStore(CharacterDbContext db) : IPlayerbotPopulationStore
{
    public async Task<IReadOnlyList<PlayerbotPopulationMember>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        List<PlayerbotPopulationMemberRow> rows = await db.Set<PlayerbotPopulationMemberRow>().AsNoTracking().OrderBy(row => row.BotId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(row => new PlayerbotPopulationMember(row.BotId, row.CharacterId, row.ZoneId, row.Band, row.CreatedUnix, row.UpdatedUnix))];
    }

    public async Task UpsertAsync(PlayerbotPopulationMember member, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (member.BotId == Guid.Empty || member.CharacterId <= 0 || member.CreatedUnix < 0 || member.UpdatedUnix < member.CreatedUnix)
            throw new ArgumentException("Population member identity or timestamps are invalid.", nameof(member));

        await CharacterWrites.InTransactionAsync(db, async () =>
        {
            int changed = await db.Set<PlayerbotPopulationMemberRow>().Where(row => row.BotId == member.BotId && row.CharacterId == member.CharacterId)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(row => row.ZoneId, member.ZoneId)
                    .SetProperty(row => row.Band, member.Band)
                    .SetProperty(row => row.UpdatedUnix, member.UpdatedUnix), cancellationToken).ConfigureAwait(false);
            if (changed == 1) return;
            if (await db.Set<PlayerbotPopulationMemberRow>().AnyAsync(row => row.BotId == member.BotId || row.CharacterId == member.CharacterId, cancellationToken)
                .ConfigureAwait(false))
                throw new InvalidOperationException("The population member identity is already owned by another row.");
            db.Set<PlayerbotPopulationMemberRow>().Add(new PlayerbotPopulationMemberRow
            {
                BotId = member.BotId, CharacterId = member.CharacterId, ZoneId = member.ZoneId, Band = member.Band,
                CreatedUnix = member.CreatedUnix, UpdatedUnix = member.UpdatedUnix,
            });
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(Guid botId, CancellationToken cancellationToken = default)
        => await CharacterWrites.InTransactionAsync(
            db, () => db.Set<PlayerbotPopulationMemberRow>().Where(row => row.BotId == botId).ExecuteDeleteAsync(cancellationToken),
            cancellationToken).ConfigureAwait(false) == 1;
}
