using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The unique index on characters.name surfaces as <see cref="CharacterNameTakenException"/> on every
/// engine (SQLite extended code 2067, MariaDB error 1062, PostgreSQL SQLSTATE 23505), so a creation
/// that loses a name race answers CHAR_CREATE_NAME_IN_USE instead of a generic error. Index
/// collation differs per engine: MariaDB general_ci also rejects a name that differs only by case or
/// accent, SQLite and PostgreSQL compare bytes, so the handler's lower-case IsNameTakenAsync stays
/// the authority and this test only asserts the exact duplicate.
/// </summary>
public sealed class CharacterNameUniqueTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DuplicateName_IsReportedAsNameTaken_AndLeavesOneRow(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var store = new EfCharacterStore(db);

        await store.CreateAsync(new CharacterRecord { AccountId = 1, Name = "Thrall" });
        CharacterNameTakenException ex = await Assert.ThrowsAsync<CharacterNameTakenException>(
            () => store.CreateAsync(new CharacterRecord { AccountId = 2, Name = "Thrall" }));
        Assert.Equal("Thrall", ex.CharacterName);

        // The failed insert left nothing behind and the scope is still usable.
        Assert.Equal(1, await store.CountByAccountAsync(1) + await store.CountByAccountAsync(2));
        await store.CreateAsync(new CharacterRecord { AccountId = 2, Name = "Jaina" });
        Assert.Equal(2, await store.CountByAccountAsync(1) + await store.CountByAccountAsync(2));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ConcurrentCreatesOfOneName_LeaveExactlyOneRow(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (CharacterDbContext boot = TestContexts.Create<CharacterDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(boot, CharacterDbContext.Schema);
        }

        Task<bool>[] attempts = [.. Enumerable.Range(0, 6).Select(i => Task.Run(async () =>
        {
            await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
            try
            {
                await new EfCharacterStore(db).CreateAsync(new CharacterRecord { AccountId = i + 1, Name = "Racer" });
                return true;
            }
            catch (CharacterNameTakenException)
            {
                return false;
            }
        }))];

        bool[] results = await Task.WhenAll(attempts);
        Assert.Equal(1, results.Count(r => r));

        await using CharacterDbContext check = TestContexts.Create<CharacterDbContext>(cs);
        Assert.True(await new EfCharacterStore(check).IsNameTakenAsync("racer"));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}
