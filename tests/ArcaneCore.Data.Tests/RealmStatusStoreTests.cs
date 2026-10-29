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

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task OnlineOfflineAndPopulation_ChangeOnlyTheirColumns_AndAnUnknownRealmIsReported(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        int id;
        await using (AuthDbContext seed = TestContexts.Create<AuthDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(seed, AuthDbContext.Schema);
            var realm = new RealmEntry
            {
                Name = "Status", Address = "127.0.0.1:8085", Flags = RealmFlags.Offline | RealmFlags.Recommended, Population = 1.5f, Category = 1,
            };
            seed.Realms.Add(realm);
            await seed.SaveChangesAsync();
            id = realm.Id;
        }

        Assert.True(await WithStoreAsync(cs, s => s.SetOnlineAsync(id)));
        RealmEntry online = await ReadAsync(cs, id);
        Assert.Equal(RealmFlags.Recommended, online.Flags);
        Assert.Equal(0f, online.Population);
        Assert.Equal("127.0.0.1:8085", online.Address);

        Assert.True(await WithStoreAsync(cs, s => s.SetPopulationAsync(id, 0.4f)));
        Assert.Equal(0.4f, (await ReadAsync(cs, id)).Population, 3);

        Assert.True(await WithStoreAsync(cs, s => s.SetOfflineAsync(id)));
        RealmEntry offline = await ReadAsync(cs, id);
        Assert.Equal(RealmFlags.Offline | RealmFlags.Recommended, offline.Flags);
        Assert.Equal(0.4f, offline.Population, 3);

        Assert.False(await WithStoreAsync(cs, s => s.SetOnlineAsync(id + 1000)));
        Assert.False(await WithStoreAsync(cs, s => s.SetOfflineAsync(id + 1000)));
        Assert.False(await WithStoreAsync(cs, s => s.SetPopulationAsync(id + 1000, 1f)));
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
