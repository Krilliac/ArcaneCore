using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Playerbots;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

public sealed class ManagedPlayerbotHomeStoreTests
{
    private static async Task<(SqliteConnection Connection, CharacterDbContext Db)> OpenAsync()
    {
        SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<CharacterDbContext> options = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite(connection).Options;
        CharacterDbContext db = new(options);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        return (connection, db);
    }

    [Fact]
    public async Task Sqlite_UpgradeFromThePreviousCharactersVersion_CreatesManagedPlayerbotHomeTable()
    {
        (SqliteConnection connection, CharacterDbContext db) = await OpenAsync();
        await using (connection)
        await using (db)
        {
            await db.Set<SchemaVersionRow>().ExecuteUpdateAsync(update => update.SetProperty(row => row.Version, ManagedPlayerbotHomeDataModule.Version - 1));
            await db.Database.ExecuteSqlRawAsync("DROP TABLE managed_playerbot_home");

            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);

            Assert.Equal(56, ManagedPlayerbotHomeDataModule.Version);
            Assert.Equal(CharacterDbContext.Schema.CurrentVersion,
                (await db.Set<SchemaVersionRow>().AsNoTracking().SingleAsync()).Version);
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = 'managed_playerbot_home'").SingleAsync());
        }
    }

    [Fact]
    public async Task Sqlite_UpgradeFrom57_AddsTheZoneClipColumn_AndAnExistingHomeIsZoneClipped()
    {
        (SqliteConnection connection, CharacterDbContext db) = await OpenAsync();
        await using (connection)
        await using (db)
        {
            // A schema-57 database: the home table without the zone clip column, holding a home set before the clip existed.
            await db.Set<SchemaVersionRow>().ExecuteUpdateAsync(update => update.SetProperty(row => row.Version, ManagedPlayerbotHomeZoneDataModule.Version - 1));
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE managed_playerbot_home DROP COLUMN IgnoreZone");
            Guid bot = Guid.NewGuid();
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO managed_playerbot_home (BotId, CharacterId, MapId, X, Y, Z, Radius, Place, SetUnix) VALUES ({0}, 9, 0, -10630, 1080, 40, 750, 'SentinelHill', 5)",
                bot);

            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);

            Assert.Equal(58, ManagedPlayerbotHomeZoneDataModule.Version);
            Assert.Equal(CharacterDbContext.Schema.CurrentVersion, (await db.Set<SchemaVersionRow>().AsNoTracking().SingleAsync()).Version);
            PlayerbotHome home = Assert.Single(await new EfManagedPlayerbotHomeStore(db).LoadAllAsync());
            Assert.Equal((bot, 750f, "SentinelHill"), (home.BotId, home.Radius, home.Place));
            Assert.True(home.ZoneClipped);
        }
    }

    [Fact]
    public async Task Sqlite_ZoneClip_RoundTrips_OnInsertAndOnReplace()
    {
        (SqliteConnection connection, CharacterDbContext db) = await OpenAsync();
        await using (connection)
        await using (db)
        {
            var store = new EfManagedPlayerbotHomeStore(db);
            PlayerbotHome unclipped = new(Guid.NewGuid(), 12, 0, -10630f, 1080f, 40f, 750f, "SentinelHill", 1, ZoneClipped: false);
            await store.SetAsync(unclipped);
            Assert.Equal([unclipped], await store.LoadAllAsync());
            Assert.True(await db.Set<ManagedPlayerbotHomeRow>().AsNoTracking().Select(row => row.IgnoreZone).SingleAsync());

            PlayerbotHome clipped = unclipped with { ZoneClipped = true, SetUnix = 2 };
            await store.SetAsync(clipped);
            Assert.Equal([clipped], await store.LoadAllAsync());

            await store.SetAsync(unclipped);
            Assert.False(Assert.Single(await store.LoadAllAsync()).ZoneClipped);
        }
    }

    [Fact]
    public async Task Sqlite_SetLoadReplaceAndClear_RoundTripsTheHome()
    {
        (SqliteConnection connection, CharacterDbContext db) = await OpenAsync();
        await using (connection)
        await using (db)
        {
            var store = new EfManagedPlayerbotHomeStore(db);
            Guid bot = Guid.NewGuid();
            PlayerbotHome first = new(bot, 11, 0, -9465.1f, 47.5f, 56.8f, 600f, "Westfall", 100);

            await store.SetAsync(first);
            Assert.Equal([first], await store.LoadAllAsync());

            PlayerbotHome second = first with { MapId = 1, X = 10f, Radius = 1200f, Place = null, SetUnix = 200 };
            await store.SetAsync(second);
            Assert.Equal([second], await store.LoadAllAsync());

            Assert.True(await store.ClearAsync(bot));
            Assert.False(await store.ClearAsync(bot));
            Assert.Empty(await store.LoadAllAsync());
        }
    }

    [Fact]
    public async Task Sqlite_Set_RefusesNonFiniteCentreAndOutOfRangeRadius()
    {
        (SqliteConnection connection, CharacterDbContext db) = await OpenAsync();
        await using (connection)
        await using (db)
        {
            var store = new EfManagedPlayerbotHomeStore(db);
            PlayerbotHome good = new(Guid.NewGuid(), 5, 0, 1f, 2f, 3f, 600f, "x", 1);

            await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync(good with { X = float.NaN }));
            await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync(good with { Y = float.PositiveInfinity }));
            await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync(good with { Radius = 49f }));
            await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync(good with { Radius = 5001f }));
            await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync(good with { Radius = float.NaN }));
            await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync(good with { BotId = Guid.Empty }));
            await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync(good with { CharacterId = 0 }));
            await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync(good with { Place = new string('p', 65) }));
            Assert.Empty(await store.LoadAllAsync());

            await store.SetAsync(good with { Radius = PlayerbotHomeBounds.MinRadius });
            await store.SetAsync(good with { Radius = PlayerbotHomeBounds.MaxRadius });
            Assert.Single(await store.LoadAllAsync());
        }
    }

    [Fact]
    public async Task Sqlite_DeleteCharacterCleanup_RemovesTheHomeRow()
    {
        (SqliteConnection connection, CharacterDbContext db) = await OpenAsync();
        await using (connection)
        await using (db)
        {
            var store = new EfManagedPlayerbotHomeStore(db);
            await store.SetAsync(new PlayerbotHome(Guid.NewGuid(), 7, 0, 1f, 2f, 3f, 600f, null, 1));
            await store.SetAsync(new PlayerbotHome(Guid.NewGuid(), 8, 0, 1f, 2f, 3f, 600f, null, 1));

            await new ManagedPlayerbotHomeDataModule().DeleteCharacterDataAsync(db, 7, default);

            PlayerbotHome remaining = Assert.Single(await store.LoadAllAsync());
            Assert.Equal(8, remaining.CharacterId);
        }
    }
}
