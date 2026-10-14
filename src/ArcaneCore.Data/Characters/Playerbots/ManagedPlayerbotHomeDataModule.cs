using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Playerbots;

/// <summary>A managed playerbot's persisted home area (<c>managed_playerbot_home</c>).</summary>
public sealed class ManagedPlayerbotHomeRow
{
    public Guid BotId { get; set; }
    public int CharacterId { get; set; }
    public uint MapId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Radius { get; set; }
    public string? Place { get; set; }
    public long SetUnix { get; set; }

    /// <summary>
    /// True when the home is NOT clipped to its zone (<c>.playerbot home ... nozone</c>; characters schema 58). Stored inverted so the
    /// rows of schema 56-57, which the added column gives the CLR default (false), are zone-clipped like every new home.
    /// </summary>
    public bool IgnoreZone { get; set; }
}

/// <summary>
/// Characters schema 56 (wave 19): the managed playerbots' home areas. A table of its own, not columns on
/// <c>managed_playerbot</c>, whose revision CAS the lifecycle writes on every state change and a GM home write must not race.
/// </summary>
public sealed class ManagedPlayerbotHomeDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 56;
    public const string Table = "managed_playerbot_home";

    public DatabaseComponent Component => DatabaseComponent.Characters;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<ManagedPlayerbotHomeRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(row => row.BotId);
            entity.Property(row => row.BotId).ValueGeneratedNever();
            entity.Property(row => row.Place).HasMaxLength(PlayerbotHomeBounds.MaxPlaceLength);
            entity.HasIndex(row => row.CharacterId).IsUnique();
        });
    }

    public void AddServices(IServiceCollection services)
        => services.AddScoped<IManagedPlayerbotHomeStore, EfManagedPlayerbotHomeStore>();

    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
        => db.Set<ManagedPlayerbotHomeRow>().Where(row => row.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken);
}

/// <summary>EF store of the playerbot homes; the upsert runs in one transaction and validates every field.</summary>
public sealed class EfManagedPlayerbotHomeStore(CharacterDbContext db) : IManagedPlayerbotHomeStore
{
    public async Task<IReadOnlyList<PlayerbotHome>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        List<ManagedPlayerbotHomeRow> rows = await db.Set<ManagedPlayerbotHomeRow>().AsNoTracking().OrderBy(row => row.BotId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(row => new PlayerbotHome(row.BotId, row.CharacterId, row.MapId, row.X, row.Y, row.Z,
            row.Radius, row.Place, row.SetUnix, ZoneClipped: !row.IgnoreZone))];
    }

    public async Task SetAsync(PlayerbotHome home, CancellationToken cancellationToken = default)
    {
        Validate(home);
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // A character has at most one home (unique CharacterId): drop a stale row of another bot id first, then upsert.
        await db.Set<ManagedPlayerbotHomeRow>().Where(row => row.CharacterId == home.CharacterId && row.BotId != home.BotId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        int changed = await db.Set<ManagedPlayerbotHomeRow>().Where(row => row.BotId == home.BotId)
            .ExecuteUpdateAsync(update => update
                .SetProperty(row => row.CharacterId, home.CharacterId)
                .SetProperty(row => row.MapId, home.MapId)
                .SetProperty(row => row.X, home.X)
                .SetProperty(row => row.Y, home.Y)
                .SetProperty(row => row.Z, home.Z)
                .SetProperty(row => row.Radius, home.Radius)
                .SetProperty(row => row.Place, home.Place)
                .SetProperty(row => row.SetUnix, home.SetUnix)
                .SetProperty(row => row.IgnoreZone, !home.ZoneClipped), cancellationToken).ConfigureAwait(false);
        if (changed == 0)
        {
            db.Set<ManagedPlayerbotHomeRow>().Add(new ManagedPlayerbotHomeRow
            {
                BotId = home.BotId, CharacterId = home.CharacterId, MapId = home.MapId, X = home.X, Y = home.Y, Z = home.Z,
                Radius = home.Radius, Place = home.Place, SetUnix = home.SetUnix, IgnoreZone = !home.ZoneClipped,
            });
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                db.ChangeTracker.Clear();
                throw;
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ClearAsync(Guid botId, CancellationToken cancellationToken = default)
        => botId != Guid.Empty && await CharacterWrites.InTransactionAsync(
            db, () => db.Set<ManagedPlayerbotHomeRow>().Where(row => row.BotId == botId).ExecuteDeleteAsync(cancellationToken),
            cancellationToken).ConfigureAwait(false) == 1;

    private static void Validate(PlayerbotHome home)
    {
        ArgumentNullException.ThrowIfNull(home);
        if (home.BotId == Guid.Empty || home.CharacterId <= 0 || home.SetUnix < 0
            || !float.IsFinite(home.X) || !float.IsFinite(home.Y) || !float.IsFinite(home.Z)
            || !float.IsFinite(home.Radius) || home.Radius < PlayerbotHomeBounds.MinRadius || home.Radius > PlayerbotHomeBounds.MaxRadius
            || home.Place is { Length: > PlayerbotHomeBounds.MaxPlaceLength })
            throw new ArgumentException("Playerbot home identity, centre, radius or place is invalid.", nameof(home));
    }
}

/// <summary>
/// Characters schema 58 (wave 20): the zone clip of a playerbot home. A circle cannot follow a zone border (Sentinel Hill radius 750
/// reaches Duskwood's Raven Hill), so a home is clipped to the zone of its centre unless it was set with <c>nozone</c>. The column is
/// <see cref="ManagedPlayerbotHomeRow.IgnoreZone"/>, false for the existing rows: an upgraded home is clipped.
/// </summary>
public sealed class ManagedPlayerbotHomeZoneDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 58;

    public DatabaseComponent Component => DatabaseComponent.Characters;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
        [new AddColumnChange(ManagedPlayerbotHomeDataModule.Table, nameof(ManagedPlayerbotHomeRow.IgnoreZone))];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        // The column is a property of ManagedPlayerbotHomeRow, mapped by convention with the table (schema 56's module).
    }

    public void AddServices(IServiceCollection services)
    {
        // The home store of schema 56 reads and writes the column.
    }

    // The column lives on the home row, which schema 56's module removes with the character.
    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken) => Task.CompletedTask;
}
