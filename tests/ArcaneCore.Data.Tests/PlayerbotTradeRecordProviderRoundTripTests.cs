using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Playerbots;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>The trade ledger's 64-bit columns (partner guid with the top bit set, unix ms) must come back exactly on every provider CI runs.</summary>
public sealed class PlayerbotTradeRecordProviderRoundTripTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task WideValues_RoundTripExactly_OnEveryProvider(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        Guid bot = Guid.NewGuid();
        int characterId;
        PlayerbotTradeRecord[] rows;
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
            CharacterRecord character = await new EfCharacterStore(db).CreateAsync(new CharacterRecord
                { AccountId = 11, Name = "Tradeprov", Race = 1, Class = 1, Level = 1 });
            characterId = character.Id;
            rows =
            [
                new(bot, characterId, ulong.MaxValue, int.MaxValue, int.MaxValue, long.MaxValue, long.MaxValue, int.MaxValue),
                new(bot, characterId, 0x8000_0000_0000_0001UL, 3, 1, 1_760_000_000_123, 1_760_000_500_456, 20_000),
                new(bot, characterId, 1, 0, 0, 0, 0, 0),
            ];
            await new EfPlayerbotTradeRecordStore(db).ReplaceAsync(bot, rows);
        }

        await using CharacterDbContext verify = TestContexts.Create<CharacterDbContext>(connection);
        IReadOnlyList<PlayerbotTradeRecord> loaded = await new EfPlayerbotTradeRecordStore(verify).LoadAllAsync();
        Assert.Equal(rows.OrderBy(r => r.OtherGuid), loaded.OrderBy(r => r.OtherGuid));
    }
}
