using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Economy;
using ArcaneCore.Data.Instances;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Social;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Ids;

/// <summary>
/// Durable high-water mark row for a named character-database ID allocation scope.
/// <see cref="NextId"/> stores the next unreserved ID (or <c>0</c> before the first reservation).
/// </summary>
public sealed class CharacterIdRangeRow
{
    public string Scope { get; set; } = string.Empty;
    public long NextId { get; set; }
}

/// <summary>
/// Characters schema module (<c>character_id_ranges</c>) backing cluster-safe durable ID block reservations
/// for items, groups, guilds, instances, mail, auctions, and item text.
/// </summary>
public sealed class CharacterIdRangeDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 61;
    public const string Table = "character_id_ranges";

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange(Table),
        .. IdRangeScopes.All.Select(scope => new EnsureRowChange(
            Table,
            "scope",
            scope,
            new Dictionary<string, object?>
            {
                ["next_id"] = 0L,
            })),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<CharacterIdRangeRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(r => r.Scope);
            entity.Property(r => r.Scope).HasColumnName("scope").HasMaxLength(64).ValueGeneratedNever();
            entity.Property(r => r.NextId).HasColumnName("next_id");
            entity.HasData(IdRangeScopes.All.Select(scope => new CharacterIdRangeRow
            {
                Scope = scope,
                NextId = 0L,
            }));
        });
    }

    public void AddServices(IServiceCollection services)
        => services.AddScoped<IIdRangeReservationStore, EfCharacterIdRangeStore>();

    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

/// <summary>
/// EF Core implementation of <see cref="IIdRangeReservationStore"/> that atomically reserves contiguous,
/// non-overlapping <see cref="IdRangeBlock"/> ranges and seeds above any existing rows in the database.
/// </summary>
public sealed class EfCharacterIdRangeStore(CharacterDbContext db) : IIdRangeReservationStore
{
    private const int MaxCasAttempts = 128;

    public async Task<IdRangeBlock> ReserveBlockAsync(string scope, uint count, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scope))
            throw new ArgumentException("Scope must be non-empty.", nameof(scope));
        if (scope.Length > 64)
            throw new ArgumentOutOfRangeException(nameof(scope), scope.Length, "Scope length must not exceed 64 characters.");
        if (count == 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, "Count must be at least 1.");

        ulong minFirstId = IdRangeScopes.GetMinFirstId(scope);
        ulong maxId = IdRangeScopes.GetMaxId(scope);
        ulong maxStored = await GetMaxStoredIdAsync(scope, ct).ConfigureAwait(false);
        ulong floor = Math.Max(minFirstId, maxStored + 1UL);

        using IDisposable exempt = CharacterWriteFence.Exempt();

        for (int attempt = 0; attempt < MaxCasAttempts; attempt++)
        {
            CharacterIdRangeRow? current = await db.Set<CharacterIdRangeRow>()
                .AsNoTracking()
                .SingleOrDefaultAsync(r => r.Scope == scope, ct)
                .ConfigureAwait(false);

            if (current is null)
            {
                await TryInsertMissingScopeRowAsync(scope, ct).ConfigureAwait(false);
                continue;
            }

            ulong currentNext = current.NextId > 0 ? (ulong)current.NextId : 0UL;
            ulong start = Math.Max(currentNext, floor);

            if (start > maxId || count - 1UL > maxId - start)
            {
                throw new IdRangeExhaustedException(
                    scope,
                    $"Cannot reserve {count} ID(s) for scope '{scope}': start={start} exceeds remaining ID space (MaxId={maxId}).");
            }

            ulong endInclusive = start + count - 1UL;
            long expectedNextId = current.NextId;
            long updatedNextId = (long)(endInclusive + 1UL);

            int changed = await db.Set<CharacterIdRangeRow>()
                .Where(r => r.Scope == scope && r.NextId == expectedNextId && r.NextId < updatedNextId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(r => r.NextId, updatedNextId),
                    ct)
                .ConfigureAwait(false);

            if (changed > 0)
            {
                return new IdRangeBlock(scope, (uint)start, (uint)endInclusive);
            }
        }

        throw new InvalidOperationException(
            $"Failed to reserve ID range block for scope '{scope}' after {MaxCasAttempts} concurrent attempts.");
    }

    private async Task TryInsertMissingScopeRowAsync(string scope, CancellationToken ct)
    {
        try
        {
            db.Set<CharacterIdRangeRow>().Add(new CharacterIdRangeRow
            {
                Scope = scope,
                NextId = 0L,
            });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Another concurrent caller inserted the row first; clear tracked state and retry CAS.
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    private async Task<ulong> GetMaxStoredIdAsync(string scope, CancellationToken ct)
    {
        return scope switch
        {
            IdRangeScopes.Item => MaxOf(
                await MaxUintOrZeroAsync(db.Set<ItemInstanceRow>().AsNoTracking().Select(r => (uint?)r.Guid), ct).ConfigureAwait(false),
                await MaxUintOrZeroAsync(db.Set<CharacterInventoryRow>().AsNoTracking().Select(r => (uint?)r.ItemGuid), ct).ConfigureAwait(false),
                await MaxUintOrZeroAsync(db.Set<CharacterInventoryRow>().AsNoTracking().Select(r => (uint?)r.Bag), ct).ConfigureAwait(false),
                await MaxUintOrZeroAsync(db.Set<MailRow>().AsNoTracking().Select(r => (uint?)r.ItemGuid), ct).ConfigureAwait(false),
                await MaxUintOrZeroAsync(db.Set<AuctionRow>().AsNoTracking().Select(r => (uint?)r.ItemGuid), ct).ConfigureAwait(false)),

            IdRangeScopes.Group => MaxOf(
                await MaxLongOrZeroAsync(db.Set<GroupRow>().AsNoTracking().Select(r => (long?)r.GroupId), ct).ConfigureAwait(false),
                await MaxLongOrZeroAsync(db.Set<GroupMemberRow>().AsNoTracking().Select(r => (long?)r.GroupId), ct).ConfigureAwait(false)),

            IdRangeScopes.Guild => MaxOf(
                await MaxIntOrZeroAsync(db.Set<GuildRow>().AsNoTracking().Select(r => (int?)r.Id), ct).ConfigureAwait(false),
                await MaxIntOrZeroAsync(db.Set<GuildRankRow>().AsNoTracking().Select(r => (int?)r.GuildId), ct).ConfigureAwait(false),
                await MaxIntOrZeroAsync(db.Set<GuildMemberRow>().AsNoTracking().Select(r => (int?)r.GuildId), ct).ConfigureAwait(false)),

            IdRangeScopes.Instance => MaxOf(
                await MaxIntOrZeroAsync(db.Set<InstanceRow>().AsNoTracking().Select(r => (int?)r.Id), ct).ConfigureAwait(false),
                await MaxIntOrZeroAsync(db.Set<CharacterInstanceRow>().AsNoTracking().Select(r => (int?)r.InstanceId), ct).ConfigureAwait(false),
                await MaxIntOrZeroAsync(db.Set<GroupInstanceRow>().AsNoTracking().Select(r => (int?)r.InstanceId), ct).ConfigureAwait(false),
                await MaxIntOrZeroAsync(db.Set<CharacterLastInstanceRow>().AsNoTracking().Select(r => (int?)r.InstanceId), ct).ConfigureAwait(false)),

            IdRangeScopes.Mail =>
                await MaxUintOrZeroAsync(db.Set<MailRow>().AsNoTracking().Select(r => (uint?)r.Id), ct).ConfigureAwait(false),

            IdRangeScopes.Auction =>
                await MaxUintOrZeroAsync(db.Set<AuctionRow>().AsNoTracking().Select(r => (uint?)r.Id), ct).ConfigureAwait(false),

            IdRangeScopes.ItemText => MaxOf(
                await MaxUintOrZeroAsync(db.Set<ItemTextRow>().AsNoTracking().Select(r => (uint?)r.Id), ct).ConfigureAwait(false),
                await MaxUintOrZeroAsync(db.Set<MailRow>().AsNoTracking().Select(r => (uint?)r.ItemTextId), ct).ConfigureAwait(false),
                await MaxUintOrZeroAsync(db.Set<ItemInstanceRow>().AsNoTracking().Select(r => (uint?)r.Text), ct).ConfigureAwait(false)),

            _ => 0UL,
        };
    }

    private static async Task<ulong> MaxUintOrZeroAsync(IQueryable<uint?> query, CancellationToken ct)
    {
        uint? value = await query.MaxAsync(ct).ConfigureAwait(false);
        return value ?? 0UL;
    }

    private static async Task<ulong> MaxIntOrZeroAsync(IQueryable<int?> query, CancellationToken ct)
    {
        int? value = await query.MaxAsync(ct).ConfigureAwait(false);
        return value is > 0 ? (ulong)value.Value : 0UL;
    }

    private static async Task<ulong> MaxLongOrZeroAsync(IQueryable<long?> query, CancellationToken ct)
    {
        long? value = await query.MaxAsync(ct).ConfigureAwait(false);
        return value is > 0 ? (ulong)value.Value : 0UL;
    }

    private static ulong MaxOf(params ReadOnlySpan<ulong> values)
    {
        ulong max = 0;
        foreach (ulong v in values)
        {
            if (v > max)
                max = v;
        }

        return max;
    }
}
