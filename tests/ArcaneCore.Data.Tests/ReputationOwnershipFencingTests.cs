using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Reputation;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Reputation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>Character reputation and watched-faction writes must obey the character ownership fence.</summary>
public sealed class ReputationOwnershipFencingTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CurrentTokenWritesFactionsAndWatched_StaleAndMissingTokensLeaveBothTablesUnchanged(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        int characterId;
        await using (CharacterDbContext setup = TestContexts.Create<CharacterDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(setup, CharacterDbContext.Schema);
            characterId = (await new EfCharacterStore(setup).CreateAsync(new CharacterRecord
            {
                AccountId = 1, Name = "RepFence", Race = 1, Class = 1, Level = 10,
            })).Id;
        }

        CharacterOwnershipToken first;
        await using (CharacterDbContext claim = TestContexts.Create<CharacterDbContext>(connection))
            first = Assert.IsType<CharacterOwnershipToken>(await new EfCharacterOwnershipStore(claim)
                .TryAcquireAsync("world", "first", Guid.NewGuid()));

        var original = new CharacterReputationRow(characterId, 72, 500, 0x01);
        const int originalWatched = 3;
        await using (CharacterDbContext current = TestContexts.Create<CharacterDbContext>(connection))
        {
            var store = new EfCharacterReputationStore(current);
            await store.SaveFactionsAsync(characterId, [original], ownership: first);
            await store.SaveWatchedFactionAsync(characterId, originalWatched, ownership: first);
        }

        CharacterOwnershipToken second;
        await using (CharacterDbContext takeover = TestContexts.Create<CharacterDbContext>(connection))
            second = Assert.IsType<CharacterOwnershipToken>(await new EfCharacterOwnershipStore(takeover)
                .TryTakeoverAsync(first, "second", Guid.NewGuid()));

        CharacterReputationRow changed = original with { Standing = 9000, Flags = 0x11 };
        const int changedWatched = 7;

        // Even on an unfenced context, an explicit stale token is verified and rejected.
        await using (CharacterDbContext unfencedStale = TestContexts.Create<CharacterDbContext>(connection))
        {
            var store = new EfCharacterReputationStore(unfencedStale);
            await AssertRejectedAsync(() => store.SaveFactionsAsync(characterId, [changed], ownership: first));
            await AssertRejectedAsync(() => store.SaveWatchedFactionAsync(characterId, changedWatched, ownership: first));
        }

        // Under an active fence (Cluster:Fencing:Enabled), both stale and missing tokens are rejected.
        var settings = new Dictionary<string, string?>
        {
            ["Database:Provider"] = connection.Provider.ToString(),
            ["Database:ConnectionString"] = connection.ConnectionString,
            ["Cluster:Fencing:Enabled"] = "true",
        };
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCharacterDatabase(configuration);
        await using (ServiceProvider fenced = services.BuildServiceProvider())
        await using (AsyncServiceScope scope = fenced.CreateAsyncScope())
        {
            CharacterDbContext stale = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            Assert.True(CharacterOwnershipGuard.IsFenceActive(stale));
            var store = new EfCharacterReputationStore(stale);
            await AssertRejectedAsync(() => store.SaveFactionsAsync(characterId, [changed], ownership: first));
            await AssertRejectedAsync(() => store.SaveWatchedFactionAsync(characterId, changedWatched, ownership: first));
            await AssertRejectedAsync(() => store.SaveFactionsAsync(characterId, [changed]));
            await AssertRejectedAsync(() => store.SaveWatchedFactionAsync(characterId, changedWatched));
        }

        await using (CharacterDbContext verify = TestContexts.Create<CharacterDbContext>(connection))
        {
            CharacterReputationData saved = await new EfCharacterReputationStore(verify).LoadAsync(characterId);
            Assert.Equal([original], saved.Factions);
            Assert.Equal(originalWatched, saved.WatchedFaction);
        }

        // The active token remains usable after rejected attempts.
        await using CharacterDbContext active = TestContexts.Create<CharacterDbContext>(connection);
        var activeStore = new EfCharacterReputationStore(active);
        await activeStore.SaveFactionsAsync(characterId, [changed], ownership: second);
        await activeStore.SaveWatchedFactionAsync(characterId, changedWatched, ownership: second);
        CharacterReputationData updated = await activeStore.LoadAsync(characterId);
        Assert.Equal([changed], updated.Factions);
        Assert.Equal(changedWatched, updated.WatchedFaction);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CallerTransactionRollback_RollsBackFactionsAndWatchedWritesTogether(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        int characterId;
        await using (CharacterDbContext setup = TestContexts.Create<CharacterDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(setup, CharacterDbContext.Schema);
            characterId = (await new EfCharacterStore(setup).CreateAsync(new CharacterRecord
            {
                AccountId = 2, Name = "RepRollbk", Race = 1, Class = 1, Level = 10,
            })).Id;
        }

        CharacterOwnershipToken token;
        await using (CharacterDbContext claim = TestContexts.Create<CharacterDbContext>(connection))
            token = Assert.IsType<CharacterOwnershipToken>(await new EfCharacterOwnershipStore(claim)
                .TryAcquireAsync("world", "rollback", Guid.NewGuid()));

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
        {
            await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
                await db.Database.BeginTransactionAsync();
            var store = new EfCharacterReputationStore(db);
            await store.SaveFactionsAsync(characterId, [new CharacterReputationRow(characterId, 21, 3100, 0x03)], ownership: token);
            await store.SaveWatchedFactionAsync(characterId, 5, ownership: token);
            await transaction.RollbackAsync();
        }

        await using CharacterDbContext verify = TestContexts.Create<CharacterDbContext>(connection);
        CharacterReputationData saved = await new EfCharacterReputationStore(verify).LoadAsync(characterId);
        Assert.Empty(saved.Factions);
        Assert.Equal(-1, saved.WatchedFaction);
    }

    /// <summary>The ownership rejection, found at any depth of the thrown exception (EF may wrap what an interceptor throws).</summary>
    private static async Task AssertRejectedAsync(Func<Task> write)
    {
        Exception? thrown = await Record.ExceptionAsync(write);
        Assert.NotNull(thrown);
        for (Exception? e = thrown; e is not null; e = e.InnerException)
        {
            if (e is CharacterOwnershipRejectedException)
                return;
        }

        Assert.Fail("expected a CharacterOwnershipRejectedException but got " + thrown);
    }
}
