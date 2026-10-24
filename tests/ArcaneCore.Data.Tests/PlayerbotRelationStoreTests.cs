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
            1_760_000_000_000, "greeting", "Said hello.", (short)(-affinity), PlayerbotRelationFlags.WarSeeded | PlayerbotRelationFlags.Rival,
            FamiliarityExact: 4.0 + 1.0 / 3.0, AffinityExact: affinity + 0.123456789012345, WarAffinityExact: -affinity - 0.5,
            DecayAnchorUnixMs: 1_759_990_000_001, WarDecayAnchorUnixMs: 1_759_980_000_002);

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
    public async Task TheExactValuesAndAnchors_RoundTripBitForBit_AndARowWithoutThemGetsTheWholeOnes()
    {
        var (connection, db, characterId) = await OpenAsync("Exactbot");
        await using var _ = connection;
        await using var __ = db;
        var store = new EfPlayerbotRelationStore(db);
        Guid bot = Guid.NewGuid();
        PlayerbotRelation exact = Row(bot, characterId, 7);
        PlayerbotRelation whole = new(bot, characterId, 8, false, 12, 3, 500, PlayerbotRelationFlags.None, 1_760_000_000_000, "", "", -4, PlayerbotRelationFlags.WarSeeded);
        await store.ReplaceAsync(bot, [exact, whole]);

        IReadOnlyList<PlayerbotRelation> loaded = await store.LoadAllAsync();
        PlayerbotRelation back = loaded.Single(r => r.OtherGuid == 7);
        Assert.Equal(exact, back);
        Assert.Equal(BitConverter.DoubleToInt64Bits(exact.AffinityExact), BitConverter.DoubleToInt64Bits(back.AffinityExact));
        PlayerbotRelation filled = loaded.Single(r => r.OtherGuid == 8);
        Assert.Equal((3.0, 12.0, -4.0), (filled.FamiliarityExact, filled.AffinityExact, filled.WarAffinityExact));
        Assert.Equal((1_760_000_000_000, 1_760_000_000_000), (filled.DecayAnchorUnixMs, filled.WarDecayAnchorUnixMs));
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

        var tooMany = Enumerable.Range(1, 4097).Select(i => Row(bot, characterId, (ulong)i)).ToArray();
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(bot, tooMany));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(bot, [Row(Guid.NewGuid(), characterId, 1)]));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(bot, [Row(bot, characterId, 1, 101)]));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(bot, [Row(bot, characterId, 1, -101)]));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(bot, [Row(bot, characterId, 1) with { TrustPermille = 1001 }]));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(bot, [Row(bot, characterId, 1) with { AffinityExact = 100.5 }]));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(bot, [Row(bot, characterId, 1) with { FamiliarityExact = double.PositiveInfinity }]));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(bot, [Row(bot, characterId, 1) with { DecayAnchorUnixMs = -1 }]));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(bot, [Row(bot, characterId, 1) with { LastTopic = new string('x', 33) }]));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(bot, [Row(bot, characterId, 1), Row(bot, characterId, 1)]));
        // Unknown bits (the lowest above Interacted, and everything outside KnownFlags), WarSeeded in the normal flags and a normal-only bit in the war flags.
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(bot, [Row(bot, characterId, 1) with { Flags = (PlayerbotRelationFlags)4096 }]));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(bot, [Row(bot, characterId, 1) with { Flags = PlayerbotRelationFlags.WarSeeded }]));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(bot, [Row(bot, characterId, 1) with { WarFlags = (PlayerbotRelationFlags)(-1) }]));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(bot, [Row(bot, characterId, 1) with { WarFlags = PlayerbotRelationFlags.Grouped }]));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(bot, [Row(bot, characterId, 1) with { WarFlags = PlayerbotRelationFlags.Interacted }]));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(bot, [Row(bot, characterId, 1) with { Flags = PlayerbotRelationFlags.Interacted | (PlayerbotRelationFlags)(-1 << 12) }]));
        // An exact value that differs from the whole one, with no anchor to say when it was true.
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(bot, [Row(bot, characterId, 1) with { DecayAnchorUnixMs = 0 }]));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceAsync(bot, [Row(bot, characterId, 1) with { WarDecayAnchorUnixMs = 0 }]));
        Assert.Single(await store.LoadAllAsync());

        // The 4096-row boundary itself is allowed (four times the largest MaxRelationsPerBot: the war-only rows are the rest).
        await store.ReplaceAsync(bot, Enumerable.Range(1, 4096).Select(i => Row(bot, characterId, (ulong)i)).ToArray());
        Assert.Equal(4096, (await store.LoadAllAsync()).Count);
    }

    [Fact]
    public async Task AWarOnlyRow_AndARowWithWholeValuesAndNoAnchor_AreAccepted()
    {
        var (connection, db, characterId) = await OpenAsync("Warrelbot");
        await using var _ = connection;
        await using var __ = db;
        var store = new EfPlayerbotRelationStore(db);
        Guid bot = Guid.NewGuid();
        PlayerbotRelation warOnly = Row(bot, characterId, 3) with { Flags = PlayerbotRelationFlags.WarOnly };
        PlayerbotRelation noAnchor = new(bot, characterId, 4, false, 12, 3, 500, PlayerbotRelationFlags.None, 1_760_000_000_000, "", "", 0, PlayerbotRelationFlags.None,
            FamiliarityExact: 3, AffinityExact: 12, WarAffinityExact: 0);
        await store.ReplaceAsync(bot, [warOnly, noAnchor]);
        Assert.Equal(PlayerbotRelationFlags.WarOnly, (await store.LoadAllAsync()).Single(r => r.OtherGuid == 3).Flags);
    }

    [Fact]
    public async Task TheInteractedBit_RoundTrips_WithTheOtherFlags()
    {
        var (connection, db, characterId) = await OpenAsync("Interbot");
        await using var _ = connection;
        await using var __ = db;
        var store = new EfPlayerbotRelationStore(db);
        Guid bot = Guid.NewGuid();
        PlayerbotRelation row = Row(bot, characterId, 6) with { Flags = PlayerbotRelationFlags.Grouped | PlayerbotRelationFlags.Interacted };
        await store.ReplaceAsync(bot, [row]);
        Assert.Equal(PlayerbotRelationFlags.Grouped | PlayerbotRelationFlags.Interacted, (await store.LoadAllAsync()).Single().Flags);
    }
}
