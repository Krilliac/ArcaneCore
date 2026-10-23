using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Playerbots;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The social memory's exact values are the data layer's first double columns: decay keeps its fractions across restarts only if every
/// provider gives the stored double back bit for bit. CI runs this against MariaDB and PostgreSQL as well as sqlite.
/// </summary>
public sealed class PlayerbotRelationProviderRoundTripTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    // Doubles a text round-trip would lose: thirds, a long mantissa, the smallest step above a whole value, a subnormal, and decayed values.
    private static readonly double[] Awkward =
        [1.0 / 3.0, 0.123456789012345678, Math.BitIncrement(5.0), double.Epsilon, 4.0 * Math.Pow(0.5, 1.0 / 30.0), -99.99999999999997];

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ExactValuesAndAnchors_RoundTripBitForBit_OnEveryProvider(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        Guid bot = Guid.NewGuid();
        var rows = new List<PlayerbotRelation>();
        int characterId;
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
            CharacterRecord character = await new EfCharacterStore(db).CreateAsync(new CharacterRecord
                { AccountId = 11, Name = "Roundbot", Race = 1, Class = 1, Level = 1 });
            characterId = character.Id;
            for (int i = 0; i < Awkward.Length; i++)
            {
                double value = Awkward[i];
                short whole = (short)Math.Floor(value);
                rows.Add(new PlayerbotRelation(bot, characterId, (ulong)(i + 1), i % 2 == 0, whole, (int)Math.Floor(Math.Abs(value)), 500,
                    PlayerbotRelationFlags.Grouped, 1_760_000_000_000 + i, "greeting", "", whole, PlayerbotRelationFlags.WarSeeded,
                    FamiliarityExact: Math.Abs(value), AffinityExact: value, WarAffinityExact: value,
                    DecayAnchorUnixMs: 1_759_990_000_001 + i, WarDecayAnchorUnixMs: 1_759_980_000_002 + i));
            }

            await new EfPlayerbotRelationStore(db).ReplaceAsync(bot, rows);
        }

        await using (CharacterDbContext verify = TestContexts.Create<CharacterDbContext>(connection))
        {
            IReadOnlyList<PlayerbotRelation> loaded = await new EfPlayerbotRelationStore(verify).LoadAllAsync();
            Assert.Equal(rows.Count, loaded.Count);
            foreach (PlayerbotRelation expected in rows)
            {
                PlayerbotRelation back = loaded.Single(r => r.BotId == bot && r.OtherGuid == expected.OtherGuid);
                Assert.Equal(BitConverter.DoubleToInt64Bits(expected.AffinityExact), BitConverter.DoubleToInt64Bits(back.AffinityExact));
                Assert.Equal(BitConverter.DoubleToInt64Bits(expected.FamiliarityExact), BitConverter.DoubleToInt64Bits(back.FamiliarityExact));
                Assert.Equal(BitConverter.DoubleToInt64Bits(expected.WarAffinityExact), BitConverter.DoubleToInt64Bits(back.WarAffinityExact));
                Assert.Equal((expected.DecayAnchorUnixMs, expected.WarDecayAnchorUnixMs), (back.DecayAnchorUnixMs, back.WarDecayAnchorUnixMs));
                Assert.Equal(expected, back);
            }
        }
    }
}
