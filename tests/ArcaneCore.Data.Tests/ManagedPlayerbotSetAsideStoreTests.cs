using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Playerbots;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

public sealed class ManagedPlayerbotSetAsideStoreTests
{
    [Fact]
    public async Task FreshAndUpgrade_CreateTable_AndSnapshotsReplaceAtomically()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<CharacterDbContext> options = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite(connection).Options;
        await using var db = new CharacterDbContext(options);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        CharacterRecord character = await new EfCharacterStore(db).CreateAsync(new CharacterRecord
            { AccountId = 7, Name = "Asidebot", Race = 1, Class = 1, Level = 1 });
        var store = new EfManagedPlayerbotSetAsideStore(db);
        Guid bot = Guid.NewGuid();
        var first = new PlayerbotSetAside(bot, character.Id, PlayerbotSetAsideKind.Quest, 1361, 123456);
        var shared = new PlayerbotSetAside(Guid.Empty, 0, PlayerbotSetAsideKind.SharedQuest, 6605, 234567, 1);
        await store.ReplaceAsync(bot, [first]);
        await store.ReplaceAsync(Guid.Empty, [shared]);
        Assert.Equal(2, (await store.LoadAllAsync()).Count);
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(Guid.Empty, [first]));
        Assert.Equal(2, (await store.LoadAllAsync()).Count);
        await store.ReplaceAsync(bot, []);
        Assert.Equal([shared], await store.LoadAllAsync());

        await db.Set<SchemaVersionRow>().ExecuteUpdateAsync(update => update.SetProperty(row => row.Version, ManagedPlayerbotSetAsideDataModule.Version - 1));
        await db.Database.ExecuteSqlRawAsync("DROP TABLE managed_playerbot_set_aside");
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        Assert.Empty(await store.LoadAllAsync());
        Assert.NotNull(await db.Characters.AsNoTracking().SingleOrDefaultAsync(row => row.Id == character.Id));

        await store.ReplaceAsync(bot, [first]);
        await store.ReplaceAsync(Guid.Empty, [shared]);
        await new ManagedPlayerbotSetAsideDataModule().DeleteCharacterDataAsync(db, character.Id, default);
        Assert.Equal([shared], await store.LoadAllAsync());
    }

    [Fact]
    public async Task UnreachableQuestRow_NeverExpiring_RoundTrips()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<CharacterDbContext> options = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite(connection).Options;
        await using var db = new CharacterDbContext(options);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        CharacterRecord character = await new EfCharacterStore(db).CreateAsync(new CharacterRecord
            { AccountId = 8, Name = "Unreachbot", Race = 1, Class = 1, Level = 1 });
        var store = new EfManagedPlayerbotSetAsideStore(db);
        Guid bot = Guid.NewGuid();
        var row = new PlayerbotSetAside(bot, character.Id, PlayerbotSetAsideKind.UnreachableQuest, 1361, long.MaxValue);
        Assert.Equal((byte)5, (byte)PlayerbotSetAsideKind.UnreachableQuest);
        await store.ReplaceAsync(bot, [row]);
        Assert.Equal([row], await store.LoadAllAsync());
    }

    [Fact]
    public async Task RoundTrip_FiniteUnreachableQuest_WithRepeatCount()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<CharacterDbContext> options = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite(connection).Options;
        await using var db = new CharacterDbContext(options);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        CharacterRecord character = await new EfCharacterStore(db).CreateAsync(new CharacterRecord
            { AccountId = 9, Name = "Finitebot", Race = 1, Class = 1, Level = 1 });
        var store = new EfManagedPlayerbotSetAsideStore(db);
        Guid bot = Guid.NewGuid();
        PlayerbotSetAside[] rows =
        [
            new(bot, character.Id, PlayerbotSetAsideKind.UnreachableQuest, 1361, 1_760_000_120_000, 3),
            new(bot, character.Id, PlayerbotSetAsideKind.Entry, 266, 1_760_001_200_000, 2),
        ];
        await store.ReplaceAsync(bot, rows);
        Assert.Equal(rows.OrderBy(row => row.TargetId), (await store.LoadAllAsync()).OrderBy(row => row.TargetId));
    }
}
