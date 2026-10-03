using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.WorldState;
using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.WorldState;

/// <summary>
/// <c>game_event_status</c> (<see cref="GameEventStatusDataModule"/>) on the SQLite / MariaDB / PostgreSQL matrix (the two server
/// providers run only on hosted CI; the local box ran SQLite). The replace is one transaction; calls are serialised in the process
/// instead of relying on database advisory locks (Npgsql pooling hands the same physical connection to concurrent callers,
/// which makes those re-entrant).
/// </summary>
public sealed class GameEventStatusStoreTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    [Fact]
    public void Module_HasItsConstantVersion_AndADocumentedNoOpCleanup()
    {
        IDataModule module = Assert.Single(DataModules.All, m => m is GameEventStatusDataModule);
        Assert.Equal(DatabaseComponent.Characters, module.Component);
        Assert.Equal(GameEventStatusDataModule.Version, module.SchemaVersion);
        Assert.Contains(CharacterDataCleanups.All, c => c is GameEventStatusDataModule);
        Assert.Empty(CharacterDataCleanups.Missing);
    }

    private async Task<DatabaseConnectionOptions> CreateAsync(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        return connection;
    }

    private static async Task<IReadOnlyList<int>> LoadAsync(DatabaseConnectionOptions connection)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        return await new EfGameEventStatusStore(db).LoadActiveAsync();
    }

    private static async Task ReplaceAsync(DatabaseConnectionOptions connection, params int[] events)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await new EfGameEventStatusStore(db).ReplaceActiveAsync(events);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Replace_MakesTheStoredSetExactlyTheGivenOne(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await CreateAsync(provider);
        Assert.Empty(await LoadAsync(connection));

        await ReplaceAsync(connection, 12, 1, 400);
        Assert.Equal([1, 12, 400], await LoadAsync(connection));

        await ReplaceAsync(connection, 400, 27, 1);
        Assert.Equal([1, 27, 400], await LoadAsync(connection));

        await ReplaceAsync(connection);
        Assert.Empty(await LoadAsync(connection));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AFailingReplace_KeepsTheOldSet(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await CreateAsync(provider);
        await ReplaceAsync(connection, 1, 2, 3);

        // a duplicate id fails on the primary key after the delete ran: the transaction must take the delete back with it
        await Assert.ThrowsAnyAsync<Exception>(() => ReplaceAsync(connection, 7, 8, 7));

        Assert.Equal([1, 2, 3], await LoadAsync(connection));
        await ReplaceAsync(connection, 4); // and the store is still usable afterwards
        Assert.Equal([4], await LoadAsync(connection));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ConcurrentReplaces_Serialise_AndLeaveOneOfThemIntact(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await CreateAsync(provider);
        int[] first = [1, 2, 3, 4, 5];
        int[] second = [3, 4, 5, 6, 7, 8];

        // Real concurrency, no timing: whichever replace runs last wins whole. The invariant is "exactly one of the two sets".
        for (int round = 0; round < 10; round++)
        {
            await Task.WhenAll(
                Task.Run(() => ReplaceAsync(connection, first)),
                Task.Run(() => ReplaceAsync(connection, second)));

            IReadOnlyList<int> stored = await LoadAsync(connection);
            Assert.True(stored.SequenceEqual(first) || stored.SequenceEqual(second), $"round {round}: stored {string.Join(',', stored)} is neither set");
        }
    }
}
