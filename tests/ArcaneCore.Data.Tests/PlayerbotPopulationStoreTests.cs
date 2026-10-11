using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Playerbots;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Characters;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>The population manager's membership table (<c>playerbot_population_member</c>, characters schema 57).</summary>
public sealed class PlayerbotPopulationStoreTests
{
    private static readonly Guid BotA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid BotB = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid BotC = Guid.Parse("00000000-0000-0000-0000-00000000000c");

    private static async Task<(SqliteConnection Connection, CharacterDbContext Db)> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new CharacterDbContext(new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite(connection).Options);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        return (connection, db);
    }

    private static PlayerbotPopulationMember Member(Guid bot, int character, uint zone = 12, byte band = 0, long created = 100, long updated = 100)
        => new(bot, character, zone, band, created, updated);

    [Fact]
    public async Task Sqlite_UpgradeFromTheMainVersion_CreatesTheMemberTable()
    {
        (SqliteConnection connection, CharacterDbContext db) = await OpenAsync();
        await using (connection) await using (db)
        {
            await db.Set<SchemaVersionRow>().ExecuteUpdateAsync(update => update.SetProperty(row => row.Version, PlayerbotPopulationDataModule.Version - 1));
            await db.Database.ExecuteSqlRawAsync("DROP TABLE playerbot_population_member");

            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);

            Assert.True(CharacterDbContext.Schema.CurrentVersion >= PlayerbotPopulationDataModule.Version);
            Assert.Equal(57, new PlayerbotPopulationDataModule().SchemaVersion);
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = 'playerbot_population_member'").SingleAsync());
        }
    }

    [Fact]
    public async Task Sqlite_UpsertInserts_AndLoadAllReturnsTheRowsInBotIdOrder()
    {
        (SqliteConnection connection, CharacterDbContext db) = await OpenAsync();
        await using (connection) await using (db)
        {
            var store = new EfPlayerbotPopulationStore(db);
            Assert.Empty(await store.LoadAllAsync());
            await store.UpsertAsync(Member(BotC, 3, zone: 14, band: 2));
            await store.UpsertAsync(Member(BotA, 1));
            await store.UpsertAsync(Member(BotB, 2, zone: 85, band: 1));

            IReadOnlyList<PlayerbotPopulationMember> all = await store.LoadAllAsync();
            Assert.Equal([BotA, BotB, BotC], all.Select(m => m.BotId));
            Assert.Equal(Member(BotB, 2, zone: 85, band: 1), all[1]);
        }
    }

    [Fact]
    public async Task Sqlite_UpsertOfAKnownMember_ReplacesZoneBandAndUpdatedButNotTheCreatedTime()
    {
        (SqliteConnection connection, CharacterDbContext db) = await OpenAsync();
        await using (connection) await using (db)
        {
            var store = new EfPlayerbotPopulationStore(db);
            await store.UpsertAsync(Member(BotA, 1, zone: 12, band: 0, created: 100, updated: 100));

            await store.UpsertAsync(Member(BotA, 1, zone: 40, band: 3, created: 200, updated: 250));

            PlayerbotPopulationMember row = Assert.Single(await store.LoadAllAsync());
            Assert.Equal(new PlayerbotPopulationMember(BotA, 1, 40, 3, 100, 250), row);
        }
    }

    [Fact]
    public async Task Sqlite_UpsertRefusesAnIdentityConflict_AndLeavesTheOwnerRowAlone()
    {
        (SqliteConnection connection, CharacterDbContext db) = await OpenAsync();
        await using (connection) await using (db)
        {
            var store = new EfPlayerbotPopulationStore(db);
            await store.UpsertAsync(Member(BotA, 1));

            // The same bot with another character, and another bot with the same character: neither may take the row over.
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpsertAsync(Member(BotA, 2, zone: 99)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpsertAsync(Member(BotB, 1, zone: 99)));

            Assert.Equal(Member(BotA, 1), Assert.Single(await store.LoadAllAsync()));
        }
    }

    [Fact]
    public async Task Sqlite_UpsertRefusesAnInvalidMember()
    {
        (SqliteConnection connection, CharacterDbContext db) = await OpenAsync();
        await using (connection) await using (db)
        {
            var store = new EfPlayerbotPopulationStore(db);
            await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertAsync(Member(Guid.Empty, 1)));
            await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertAsync(Member(BotA, 0)));
            await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertAsync(Member(BotA, 1, created: -1, updated: 5)));
            await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertAsync(Member(BotA, 1, created: 100, updated: 99)));
            Assert.Empty(await store.LoadAllAsync());
        }
    }

    [Fact]
    public async Task Sqlite_Delete_RemovesTheRowOnce()
    {
        (SqliteConnection connection, CharacterDbContext db) = await OpenAsync();
        await using (connection) await using (db)
        {
            var store = new EfPlayerbotPopulationStore(db);
            await store.UpsertAsync(Member(BotA, 1));
            await store.UpsertAsync(Member(BotB, 2));

            Assert.True(await store.DeleteAsync(BotA));
            Assert.False(await store.DeleteAsync(BotA));
            Assert.False(await store.DeleteAsync(BotC));
            Assert.Equal(BotB, Assert.Single(await store.LoadAllAsync()).BotId);
        }
    }

    [Fact]
    public async Task Sqlite_DeleteCharacterCleanup_RemovesOnlyThatCharactersMembership()
    {
        (SqliteConnection connection, CharacterDbContext db) = await OpenAsync();
        await using (connection) await using (db)
        {
            var store = new EfPlayerbotPopulationStore(db);
            await store.UpsertAsync(Member(BotA, 1));
            await store.UpsertAsync(Member(BotB, 2));
            Assert.IsAssignableFrom<ICharacterDataCleanup>(new PlayerbotPopulationDataModule());

            await new PlayerbotPopulationDataModule().DeleteCharacterDataAsync(db, 1, default);

            Assert.Equal(BotB, Assert.Single(await store.LoadAllAsync()).BotId);
        }
    }
}
