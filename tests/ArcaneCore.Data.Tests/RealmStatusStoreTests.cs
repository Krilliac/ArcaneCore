using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Realms;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// <see cref="EfRealmStatusStore"/>: the world's online/offline and population writes to its realmlist row (vmangos Master.cpp:215,
/// :330; World.cpp:340-353). Run on every engine available; locally only SQLite (MariaDB/PostgreSQL run on hosted CI).
/// </summary>
public sealed class RealmStatusStoreTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    private static readonly DateTime Beat = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task OnlineStoppedAndPopulation_ChangeOnlyTheirColumns_AndAnUnknownRealmIsReported(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        int id = await SeedAsync(cs, new RealmEntry
        {
            Name = "Status", Address = "127.0.0.1:8085", Flags = RealmFlags.Offline | RealmFlags.Recommended, Population = 1.5f, Category = 1, AllowedSecurityLevel = 2,
        });

        Assert.True(await WithStoreAsync(cs, s => s.SetOnlineAsync(id, Beat)));
        RealmEntry online = await ReadAsync(cs, id);
        Assert.Equal(RealmFlags.Recommended, online.Flags);
        Assert.Equal(0f, online.Population);
        Assert.Equal(RealmRuntimeState.Online, online.State);
        Assert.Equal(Beat, online.HeartbeatUtc);
        Assert.Equal("127.0.0.1:8085", online.Address);
        Assert.Equal(2, online.AllowedSecurityLevel);

        Assert.True(await WithStoreAsync(cs, s => s.SetPopulationAsync(id, 0.4f)));
        RealmEntry populated = await ReadAsync(cs, id);
        Assert.Equal(0.4f, populated.Population, 3);
        Assert.Equal(RealmRuntimeState.Online, populated.State);

        Assert.True(await WithStoreAsync(cs, s => s.SetStoppedAsync(id)));
        RealmEntry stopped = await ReadAsync(cs, id);
        Assert.Equal(RealmFlags.Offline | RealmFlags.Recommended, stopped.Flags);
        Assert.Equal(RealmRuntimeState.Stopped, stopped.State);
        Assert.Equal(0.4f, stopped.Population, 3);
        Assert.Equal(Beat, stopped.HeartbeatUtc); // a clean stop keeps the last beat: the realm daemon reads Stopped, not the age

        Assert.False(await WithStoreAsync(cs, s => s.SetOnlineAsync(id + 1000, Beat)));
        Assert.False(await WithStoreAsync(cs, s => s.SetStartingAsync(id + 1000, Beat)));
        Assert.False(await WithStoreAsync(cs, s => s.SetStateAsync(id + 1000, RealmRuntimeState.Restarting, Beat)));
        Assert.False(await WithStoreAsync(cs, s => s.BeatAsync(id + 1000, Beat)));
        Assert.False(await WithStoreAsync(cs, s => s.SetStoppedAsync(id + 1000)));
        Assert.False(await WithStoreAsync(cs, s => s.SetPopulationAsync(id + 1000, 1f)));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task StartingStateAndBeat_TouchOnlyTheirColumns(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        int id = await SeedAsync(cs, new RealmEntry
        {
            Name = "Beat", Address = "10.0.0.1:8085", Flags = RealmFlags.NewPlayers, Population = 0.7f, Category = 2, AllowedSecurityLevel = 1,
        });

        Assert.True(await WithStoreAsync(cs, s => s.SetStartingAsync(id, Beat)));
        RealmEntry starting = await ReadAsync(cs, id);
        Assert.Equal(RealmFlags.NewPlayers | RealmFlags.Offline, starting.Flags);
        Assert.Equal(RealmRuntimeState.Starting, starting.State);
        Assert.Equal(Beat, starting.HeartbeatUtc);
        Assert.Equal(0.7f, starting.Population, 3);

        DateTime later = Beat.AddSeconds(10);
        Assert.True(await WithStoreAsync(cs, s => s.BeatAsync(id, later)));
        RealmEntry beaten = await ReadAsync(cs, id);
        Assert.Equal(later, beaten.HeartbeatUtc);
        Assert.Equal(RealmRuntimeState.Starting, beaten.State); // a beat writes the heartbeat and nothing else
        Assert.Equal(RealmFlags.NewPlayers | RealmFlags.Offline, beaten.Flags);

        Assert.True(await WithStoreAsync(cs, s => s.SetStateAsync(id, RealmRuntimeState.Restarting, later.AddSeconds(10))));
        RealmEntry restarting = await ReadAsync(cs, id);
        Assert.Equal(RealmRuntimeState.Restarting, restarting.State);
        Assert.Equal(later.AddSeconds(10), restarting.HeartbeatUtc);
        Assert.Equal(RealmFlags.NewPlayers | RealmFlags.Offline, restarting.Flags); // the state write leaves the flags alone
        Assert.Equal(1, restarting.AllowedSecurityLevel);
        Assert.Equal("10.0.0.1:8085", restarting.Address);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CharacterCounts_AreKeptPerAccountAndRealm(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (AuthDbContext seed = TestContexts.Create<AuthDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(seed, AuthDbContext.Schema);
        }

        await WithCountsAsync(cs, s => s.SetCountAsync(1, 7, 2));
        await WithCountsAsync(cs, s => s.SetCountAsync(2, 7, 5));
        await WithCountsAsync(cs, s => s.SetCountAsync(1, 8, 9));
        await WithCountsAsync(cs, s => s.SetCountAsync(1, 7, 3)); // replaced, not added
        await WithCountsAsync(cs, s => s.SetCountAsync(1, 7, 3)); // the same value again is a no-op, not a key clash

        IReadOnlyDictionary<int, int> seven = await ReadCountsAsync(cs, 7);
        Assert.Equal(2, seven.Count);
        Assert.Equal(3, seven[1]);
        Assert.Equal(5, seven[2]);
        Assert.Equal(9, (await ReadCountsAsync(cs, 8))[1]);
        Assert.Empty(await ReadCountsAsync(cs, 99));

        await WithCountsAsync(cs, s => s.SetCountAsync(1, 7, -4));
        Assert.Equal(0, (await ReadCountsAsync(cs, 7))[1]); // a negative count is stored as 0
    }

    private async Task<int> SeedAsync(DatabaseConnectionOptions cs, RealmEntry realm)
    {
        await using AuthDbContext seed = TestContexts.Create<AuthDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(seed, AuthDbContext.Schema);
        seed.Realms.Add(realm);
        await seed.SaveChangesAsync();
        return realm.Id;
    }

    private static async Task WithCountsAsync(DatabaseConnectionOptions cs, Func<IRealmCharacterCountStore, Task> call)
    {
        await using AuthDbContext db = TestContexts.Create<AuthDbContext>(cs);
        await call(new EfRealmCharacterCountStore(db));
    }

    private static async Task<IReadOnlyDictionary<int, int>> ReadCountsAsync(DatabaseConnectionOptions cs, int accountId)
    {
        await using AuthDbContext db = TestContexts.Create<AuthDbContext>(cs);
        return await new EfRealmCharacterCountStore(db).GetCountsAsync(accountId);
    }

    private static async Task<bool> WithStoreAsync(DatabaseConnectionOptions cs, Func<IRealmStatusStore, Task<bool>> call)
    {
        await using AuthDbContext db = TestContexts.Create<AuthDbContext>(cs);
        return await call(new EfRealmStatusStore(db));
    }

    private static async Task<RealmEntry> ReadAsync(DatabaseConnectionOptions cs, int id)
    {
        await using AuthDbContext db = TestContexts.Create<AuthDbContext>(cs);
        return await db.Realms.AsNoTracking().SingleAsync(r => r.Id == id);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _databases.DisposeAsync();
}
