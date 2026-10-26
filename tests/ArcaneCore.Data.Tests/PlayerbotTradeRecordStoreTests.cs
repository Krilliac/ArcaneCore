using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Playerbots;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>The P4 trade ledger store (characters schema 66, table playerbot_trade_record): round trip, atomic replace, bounds, cleanup.</summary>
public sealed class PlayerbotTradeRecordStoreTests
{
    private static async Task<(SqliteConnection Connection, CharacterDbContext Db, int CharacterId)> OpenAsync(string name)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<CharacterDbContext> options = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite(connection).Options;
        var db = new CharacterDbContext(options);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        CharacterRecord character = await new EfCharacterStore(db).CreateAsync(new CharacterRecord
            { AccountId = 11, Name = name, Race = 1, Class = 1, Level = 1 });
        return (connection, db, character.Id);
    }

    private static PlayerbotTradeRecord Row(Guid bot, int characterId, ulong other, int fair = 2, int scams = 1) =>
        new(bot, characterId, other, fair, scams, 1_760_000_000_123, 1_760_000_500_456, 20_000);

    [Fact]
    public void TheModule_IsCharactersSchema66_AndOwnsThePlayerbotTradeRecordTable()
    {
        var module = new PlayerbotTradeRecordDataModule();
        Assert.Equal(66, PlayerbotTradeRecordDataModule.Version);
        Assert.Equal(DatabaseComponent.Characters, module.Component);
        Assert.Equal("playerbot_trade_record", PlayerbotTradeRecordDataModule.Table);
        Assert.Contains(module.SchemaChanges, change => change is CreateTableChange);
    }

    [Fact]
    public async Task RoundTrip_AndReplaceIsAtomicPerBot_AndCharacterDeleteCleans()
    {
        var (connection, db, characterId) = await OpenAsync("Tradebot");
        await using var _ = connection;
        await using var __ = db;
        var store = new EfPlayerbotTradeRecordStore(db);
        Guid botA = Guid.NewGuid(), botB = Guid.NewGuid();
        PlayerbotTradeRecord[] a = [Row(botA, characterId, 0xFFFF_FFFF_FFFF_0001UL), Row(botA, characterId, 2, 0, 0)];
        PlayerbotTradeRecord[] b = [Row(botB, characterId, 5)];
        await store.ReplaceAsync(botA, a);
        await store.ReplaceAsync(botB, b);
        Assert.Equal(3, (await store.LoadAllAsync()).Count);
        Assert.Equal(a.OrderBy(r => r.OtherGuid), (await store.LoadAllAsync()).Where(r => r.BotId == botA).OrderBy(r => r.OtherGuid));

        // Replacing one bot's rows leaves the other bot's untouched.
        await store.ReplaceAsync(botA, [Row(botA, characterId, 9, 7, 3)]);
        IReadOnlyList<PlayerbotTradeRecord> all = await store.LoadAllAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal(b, all.Where(r => r.BotId == botB).ToArray());
        PlayerbotTradeRecord replaced = all.Single(r => r.BotId == botA);
        Assert.Equal((9UL, 7, 3), (replaced.OtherGuid, replaced.FairDeals, replaced.Scams));

        // An empty snapshot clears the bot.
        await store.ReplaceAsync(botA, []);
        Assert.Equal(b, await store.LoadAllAsync());

        await new PlayerbotTradeRecordDataModule().DeleteCharacterDataAsync(db, characterId, default);
        Assert.Empty(await store.LoadAllAsync());
    }

    [Fact]
    public async Task Validation_RejectsBadSnapshots_AndLeavesRowsUntouched()
    {
        var (connection, db, characterId) = await OpenAsync("Badtradebot");
        await using var _ = connection;
        await using var __ = db;
        var store = new EfPlayerbotTradeRecordStore(db);
        Guid bot = Guid.NewGuid();
        await store.ReplaceAsync(bot, [Row(bot, characterId, 1)]);

        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(bot, Enumerable.Range(1, 4097).Select(i => Row(bot, characterId, (ulong)i)).ToArray()));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(bot, [Row(Guid.NewGuid(), characterId, 1)])); // another bot's row
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(Guid.Empty, [Row(Guid.Empty, characterId, 1)]));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(bot, [Row(bot, 0, 1)]));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(bot, [Row(bot, characterId, 1) with { FairDeals = -1 }]));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(bot, [Row(bot, characterId, 1) with { Scams = -1 }]));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(bot, [Row(bot, characterId, 1) with { LastScamUnixMs = -1 }]));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(bot, [Row(bot, characterId, 1) with { LastNinjaUnixMs = -1 }]));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(bot, [Row(bot, characterId, 1) with { LastGiftDay = -1 }]));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(bot, [Row(bot, characterId, 1), Row(bot, characterId, 1)])); // duplicate partner
        PlayerbotTradeRecord kept = Assert.Single(await store.LoadAllAsync());
        Assert.Equal(Row(bot, characterId, 1), kept);

        // The 4096-row boundary itself is allowed.
        await store.ReplaceAsync(bot, Enumerable.Range(1, 4096).Select(i => Row(bot, characterId, (ulong)i)).ToArray());
        Assert.Equal(4096, (await store.LoadAllAsync()).Count);
    }

    [Fact]
    public async Task ZeroCounts_AndNeverTimes_AreValidRows()
    {
        var (connection, db, characterId) = await OpenAsync("Zerotradebot");
        await using var _ = connection;
        await using var __ = db;
        var store = new EfPlayerbotTradeRecordStore(db);
        Guid bot = Guid.NewGuid();
        var blank = new PlayerbotTradeRecord(bot, characterId, 77, 0, 0, 0, 0, 0);
        await store.ReplaceAsync(bot, [blank]);
        Assert.Equal(blank, Assert.Single(await store.LoadAllAsync()));
    }
}
