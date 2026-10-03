using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Reputation;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Reputation;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Reputation;

/// <summary>
/// Character reputation (characters schema v7) across the SQLite, MariaDB and PostgreSQL
/// matrix. Every operation uses a fresh context so state must survive in the database.
/// </summary>
public sealed class ReputationStoreTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task FactionRows_RoundTrip_UpdateInPlace_AndStayPerCharacter(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, CharacterRecord[] characters) = await CreateAsync(provider, "Reputed", "Otherrep");
        int id = characters[0].Id;
        int other = characters[1].Id;
        CharacterReputationRow[] first =
        [
            new(id, 72, 3500, 0x11),
            new(id, 21, -45000, 0x03), // extreme negative standing relative to a positive base
            new(id, 469, 0, 0x01),
        ];
        await WriteAsync(connection, store => store.SaveFactionsAsync(id, first));
        await WriteAsync(connection, store => store.SaveFactionsAsync(other, [new(other, 72, 12, 0x01)]));

        CharacterReputationData loaded = await LoadAsync(connection, id);
        Assert.Equal(first.OrderBy(r => r.Faction), loaded.Factions);
        Assert.Equal(-1, loaded.WatchedFaction);

        // Update one row in place, add another, and resend a duplicate: the last write per faction wins.
        await WriteAsync(connection, store => store.SaveFactionsAsync(id,
            [new(id, 72, 100, 0x13), new(id, 529, 42999, 0x01), new(id, 72, 9000, 0x31)]));
        loaded = await LoadAsync(connection, id);
        Assert.Equal([new(id, 21, -45000, 0x03), new(id, 72, 9000, 0x31), new(id, 469, 0, 0x01), new(id, 529, 42999, 0x01)], loaded.Factions);
        Assert.Equal([new CharacterReputationRow(other, 72, 12, 0x01)], (await LoadAsync(connection, other)).Factions);
        await using CharacterDbContext verify = TestContexts.Create<CharacterDbContext>(connection);
        Assert.Equal(5, await verify.Set<CharacterReputationEntity>().CountAsync());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task WatchedFaction_RoundTrips_AndClears(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, CharacterRecord[] characters) = await CreateAsync(provider, "Watcher");
        int id = characters[0].Id;
        await WriteAsync(connection, store => store.SaveWatchedFactionAsync(id, 13));
        Assert.Equal(13, (await LoadAsync(connection, id)).WatchedFaction);
        await WriteAsync(connection, store => store.SaveWatchedFactionAsync(id, -1));
        Assert.Equal(-1, (await LoadAsync(connection, id)).WatchedFaction);
        await WriteAsync(connection, store => store.SaveWatchedFactionAsync(id, 0));
        Assert.Equal(0, (await LoadAsync(connection, id)).WatchedFaction);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task MissingCharacter_IsIgnored_AndDeleteRemovesOnlyThatCharacter(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, CharacterRecord[] characters) = await CreateAsync(provider, "Deleted", "Kept");
        int id = characters[0].Id;
        int kept = characters[1].Id;
        await WriteAsync(connection, store => store.SaveFactionsAsync(9999, [new(9999, 72, 1, 1)]));
        await WriteAsync(connection, store => store.SaveWatchedFactionAsync(9999, 3));
        Assert.Empty((await LoadAsync(connection, 9999)).Factions);

        await WriteAsync(connection, store => store.SaveFactionsAsync(id, [new(id, 72, 5, 1)]));
        await WriteAsync(connection, store => store.SaveWatchedFactionAsync(id, 4));
        await WriteAsync(connection, store => store.SaveFactionsAsync(kept, [new(kept, 72, 6, 1)]));
        await WriteAsync(connection, store => store.DeleteCharacterAsync(id));
        CharacterReputationData gone = await LoadAsync(connection, id);
        Assert.Empty(gone.Factions);
        Assert.Equal(-1, gone.WatchedFaction);
        Assert.Single((await LoadAsync(connection, kept)).Factions);
        await WriteAsync(connection, store => store.DeleteCharacterAsync(id)); // idempotent

        // Rows for another character in one call are refused before anything is written.
        await Assert.ThrowsAsync<ArgumentException>(() => WriteAsync(connection, store => store.SaveFactionsAsync(kept, [new(id, 72, 1, 1)])));
        Assert.Equal(6, (await LoadAsync(connection, kept)).Factions.Single().Standing);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private async Task<(DatabaseConnectionOptions Connection, CharacterRecord[] Characters)> CreateAsync(
        DatabaseProvider provider, params string[] names)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        ICharacterStore store = new EfCharacterStore(db);
        var characters = new List<CharacterRecord>();
        foreach (string name in names)
        {
            characters.Add(await store.CreateAsync(new CharacterRecord { AccountId = 78, Name = name, Race = 1, Class = 1, Level = 10 }));
        }

        return (connection, [.. characters]);
    }

    private static async Task WriteAsync(DatabaseConnectionOptions connection, Func<ICharacterReputationStore, Task> write)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await write(new EfCharacterReputationStore(db));
    }

    private static async Task<CharacterReputationData> LoadAsync(DatabaseConnectionOptions connection, int characterId)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        return await new EfCharacterReputationStore(db).LoadAsync(characterId);
    }
}
