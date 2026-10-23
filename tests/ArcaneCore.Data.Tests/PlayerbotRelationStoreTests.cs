using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Playerbots;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

public sealed class PlayerbotRelationStoreTests
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

    private static PlayerbotRelation Row(Guid bot, int characterId, ulong other, short affinity = 10) =>
        new(bot, characterId, other, other % 2 == 0, affinity, 4, 500, PlayerbotRelationFlags.Grouped | PlayerbotRelationFlags.Rival,
            1_760_000_000_000, "greeting", "Said hello.");

    [Fact]
    public async Task RoundTrip_AndReplaceIsAtomicPerBot_AndCharacterDeleteCleans()
    {
        var (connection, db, characterId) = await OpenAsync("Relbot");
        await using var _ = connection;
        await using var __ = db;
        var store = new EfPlayerbotRelationStore(db);
        Guid botA = Guid.NewGuid(), botB = Guid.NewGuid();
        PlayerbotRelation[] a = [Row(botA, characterId, 0xFFFF_FFFF_FFFF_0001UL), Row(botA, characterId, 2, -40)];
        PlayerbotRelation[] b = [Row(botB, characterId, 5)];
        await store.ReplaceAsync(botA, a);
        await store.ReplaceAsync(botB, b);
        Assert.Equal(3, (await store.LoadAllAsync()).Count);
        Assert.Equal(a.OrderBy(r => r.OtherGuid), (await store.LoadAllAsync()).Where(r => r.BotId == botA).OrderBy(r => r.OtherGuid));

        await store.ReplaceAsync(botA, [Row(botA, characterId, 9, 33)]);
        IReadOnlyList<PlayerbotRelation> all = await store.LoadAllAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal(b, all.Where(r => r.BotId == botB).ToArray());
        Assert.Equal((short)33, all.Single(r => r.BotId == botA).Affinity);

        await new PlayerbotRelationDataModule().DeleteCharacterDataAsync(db, characterId, default);
        Assert.Empty(await store.LoadAllAsync());
    }

    [Fact]
    public async Task Validation_RejectsBadSnapshots_AndLeavesRowsUntouched()
    {
        var (connection, db, characterId) = await OpenAsync("Badrelbot");
        await using var _ = connection;
        await using var __ = db;
        var store = new EfPlayerbotRelationStore(db);
        Guid bot = Guid.NewGuid();
        await store.ReplaceAsync(bot, [Row(bot, characterId, 1)]);

        var tooMany = Enumerable.Range(1, 1025).Select(i => Row(bot, characterId, (ulong)i)).ToArray();
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(bot, tooMany));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(bot, [Row(Guid.NewGuid(), characterId, 1)]));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(bot, [Row(bot, characterId, 1, 101)]));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(bot, [Row(bot, characterId, 1, -101)]));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(bot, [Row(bot, characterId, 1) with { TrustPermille = 1001 }]));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(bot, [Row(bot, characterId, 1) with { LastTopic = new string('x', 33) }]));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(bot, [Row(bot, characterId, 1), Row(bot, characterId, 1)]));
        Assert.Single(await store.LoadAllAsync());

        // The 1024-row boundary itself is allowed.
        await store.ReplaceAsync(bot, Enumerable.Range(1, 1024).Select(i => Row(bot, characterId, (ulong)i)).ToArray());
        Assert.Equal(1024, (await store.LoadAllAsync()).Count);
    }
}
