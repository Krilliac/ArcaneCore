using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Playerbots;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

public sealed class ManagedPlayerbotStoreTests
{
    [Fact]
    public async Task Sqlite_UpgradeFromThePreviousCharactersVersion_CreatesManagedPlayerbotTable()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<CharacterDbContext> options = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite(connection).Options;
        await using CharacterDbContext db = new(options);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        await db.Set<SchemaVersionRow>().ExecuteUpdateAsync(update => update.SetProperty(row => row.Version, ManagedPlayerbotDataModule.Version - 1));
        await db.Database.ExecuteSqlRawAsync("DROP TABLE managed_playerbot");

        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);

        Assert.True(CharacterDbContext.Schema.CurrentVersion >= ManagedPlayerbotDataModule.Version);
        Assert.Equal(CharacterDbContext.Schema.CurrentVersion,
            (await db.Set<SchemaVersionRow>().AsNoTracking().SingleAsync()).Version);
        Assert.Equal(ManagedPlayerbotDataModule.Version, new ManagedPlayerbotDataModule().SchemaVersion);
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = 'managed_playerbot'").SingleAsync());
    }

    [Fact]
    public async Task Sqlite_CreateLoadAndCasUpdate_RejectsStaleAndIdentityMutation()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<CharacterDbContext> options = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite(connection).Options;
        await using CharacterDbContext db = new(options);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        CharacterRecord character = await new EfCharacterStore(db).CreateAsync(new CharacterRecord { AccountId = 7, Name = "Botone", Race = 1, Class = 1, Level = 1 });
        var store = new EfManagedPlayerbotStore(db);
        ManagedPlayerbot initial = Bot(character.Id, character.AccountId);

        await store.CreateAsync(initial);
        ManagedPlayerbot running = initial with { State = ManagedPlayerbotState.Running, DesiredEnabled = true, Revision = 1, UpdatedUnix = 101 };
        Assert.True(await store.UpdateAsync(running, expectedRevision: 0));
        Assert.False(await store.UpdateAsync(running with { Revision = 2, UpdatedUnix = 102 }, expectedRevision: 0));
        Assert.False(await store.UpdateAsync(running with { AccountName = "other", Revision = 2, UpdatedUnix = 102 }, expectedRevision: 1));

        ManagedPlayerbot? loaded = await store.FindAsync(initial.BotId);
        Assert.Equal(running, loaded);
    }

    [Fact]
    public async Task Sqlite_Create_RejectsDuplicateAccountAndCharacterAndMismatchedOwner()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<CharacterDbContext> options = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite(connection).Options;
        await using CharacterDbContext db = new(options);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        CharacterRecord first = await new EfCharacterStore(db).CreateAsync(new CharacterRecord { AccountId = 7, Name = "Bota", Race = 1, Class = 1, Level = 1 });
        CharacterRecord second = await new EfCharacterStore(db).CreateAsync(new CharacterRecord { AccountId = 8, Name = "Botb", Race = 1, Class = 1, Level = 1 });
        var store = new EfManagedPlayerbotStore(db);
        await store.CreateAsync(Bot(first.Id, first.AccountId));

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(Bot(second.Id, first.AccountId)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(Bot(first.Id, second.AccountId)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(Bot(first.Id, first.AccountId) with { BotId = Guid.NewGuid() }));
    }

    [Fact]
    public async Task Sqlite_DeleteCharacterCleanup_RemovesOwnerRow()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<CharacterDbContext> options = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite(connection).Options;
        await using CharacterDbContext db = new(options);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        CharacterRecord character = await new EfCharacterStore(db).CreateAsync(new CharacterRecord { AccountId = 9, Name = "Botcleanup", Race = 1, Class = 1, Level = 1 });
        ManagedPlayerbot bot = Bot(character.Id, character.AccountId);
        await new EfManagedPlayerbotStore(db).CreateAsync(bot);

        await new ManagedPlayerbotDataModule().DeleteCharacterDataAsync(db, character.Id, default);

        Assert.Null(await new EfManagedPlayerbotStore(db).FindAsync(bot.BotId));
    }

    private static ManagedPlayerbot Bot(int characterId, int accountId)
        => new(Guid.NewGuid(), accountId, characterId, $"account{accountId}", false,
            ManagedPlayerbotState.Stopped, PlayerbotGoalKind.Explore, 0, 0, 0, 100, 100);
}
