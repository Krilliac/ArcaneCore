using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Playerbots;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Characters;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>The playerbot trade counter store (characters schema 71, table playerbot_trade_counter): the counters that survive a restart. Round trip, atomic replace, bounds.</summary>
public sealed class PlayerbotTradeCounterStoreTests
{
    private static async Task<(SqliteConnection Connection, CharacterDbContext Db)> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<CharacterDbContext> options = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite(connection).Options;
        var db = new CharacterDbContext(options);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        return (connection, db);
    }

    private static PlayerbotTradeCounter Row(PlayerbotTradeCounterKind kind, Guid bot, ulong key, long stamp, long amount) => new(kind, bot, key, stamp, amount);

    private static PlayerbotTradeCounter[] Sorted(IEnumerable<PlayerbotTradeCounter> rows)
        => [.. rows.OrderBy(row => row.Kind).ThenBy(row => row.BotId).ThenBy(row => row.Key).ThenBy(row => row.Stamp)];

    [Fact]
    public void TheModule_IsCharactersSchema71_AndOwnsThePlayerbotTradeCounterTable()
    {
        var module = new PlayerbotTradeCounterDataModule();
        Assert.Equal(71, PlayerbotTradeCounterDataModule.Version);
        Assert.Equal(DatabaseComponent.Characters, module.Component);
        Assert.Equal("playerbot_trade_counter", PlayerbotTradeCounterDataModule.Table);
        Assert.Contains(module.SchemaChanges, change => change is CreateTableChange);
    }

    /// <summary>
    /// Every kind of counter row is stored and read back unchanged (a guid key above long.MaxValue included), a second save replaces the first completely, and an empty save clears the
    /// table. Broken: rows that survive a replace (a counter that never goes down), or a key that loses its top bit.
    /// </summary>
    [Fact]
    public async Task RoundTrip_ReplaceIsComplete_AndAnEmptySaveClears()
    {
        var (connection, db) = await OpenAsync();
        await using var _ = connection;
        await using var __ = db;
        var store = new EfPlayerbotTradeCounterStore(db);
        Guid botA = Guid.NewGuid(), botB = Guid.NewGuid();
        PlayerbotTradeCounter[] first =
        [
            Row(PlayerbotTradeCounterKind.BotTrades, botA, 0, 1_760_000_000_123, 2),
            Row(PlayerbotTradeCounterKind.BotTrades, botA, 0, 1_760_000_000_456, 1),
            Row(PlayerbotTradeCounterKind.AccountTrades, Guid.Empty, 77, 1_760_000_000_789, 3),
            Row(PlayerbotTradeCounterKind.BotOutflow, botB, 0, 20_370, 1500),
            Row(PlayerbotTradeCounterKind.AccountOutflow, Guid.Empty, 77, 20_370, 1500),
            Row(PlayerbotTradeCounterKind.ServerOutflow, Guid.Empty, 0, 20_370, 4000),
            Row(PlayerbotTradeCounterKind.GiftsReceived, botA, 0xFFFF_FFFF_FFFF_0001UL, 1_760_000_000_999, 4),
        ];

        await store.ReplaceAllAsync(first);
        Assert.Equal(Sorted(first), Sorted(await store.LoadAllAsync()));

        PlayerbotTradeCounter[] second = [Row(PlayerbotTradeCounterKind.BotOutflow, botA, 0, 20_371, 9)];
        await store.ReplaceAllAsync(second);
        Assert.Equal(second, await store.LoadAllAsync());

        await store.ReplaceAllAsync([]);
        Assert.Empty(await store.LoadAllAsync());
    }

    /// <summary>A bad snapshot (a duplicate identity, a zero or negative amount or stamp, an unknown kind, too many rows) is refused and leaves the stored rows untouched.</summary>
    [Fact]
    public async Task Validation_RejectsBadSnapshots_AndLeavesRowsUntouched()
    {
        var (connection, db) = await OpenAsync();
        await using var _ = connection;
        await using var __ = db;
        var store = new EfPlayerbotTradeCounterStore(db);
        Guid bot = Guid.NewGuid();
        PlayerbotTradeCounter[] kept = [Row(PlayerbotTradeCounterKind.BotTrades, bot, 0, 1000, 1)];
        await store.ReplaceAllAsync(kept);

        PlayerbotTradeCounter good = kept[0];
        PlayerbotTradeCounter[][] bad =
        [
            [good, good],
            [good with { Amount = 0 }],
            [good with { Amount = -1 }],
            [good with { Stamp = -1 }],
            [good with { Kind = (PlayerbotTradeCounterKind)99 }],
            [.. Enumerable.Range(0, PlayerbotTradeCounter.MaxRows + 1).Select(i => good with { Stamp = i })],
        ];
        foreach (PlayerbotTradeCounter[] rows in bad)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAllAsync(rows));
            Assert.Equal(kept, await store.LoadAllAsync());
        }
    }
}
