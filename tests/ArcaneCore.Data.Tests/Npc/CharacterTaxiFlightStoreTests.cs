using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Npc;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Npc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Npc;

/// <summary>SQLite locally; the shared provider list adds hosted MariaDB and PostgreSQL.</summary>
public sealed class CharacterTaxiFlightStoreTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Route_UpsertsReloadsAndClears(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var store = new EfCharacterTaxiFlightStore(db);
        await store.SaveAsync(42, new TaxiFlightRoute([1, 2, 3], [10, 11], [0, 48]));
        Assert.Equal(new uint[] { 1, 2, 3 }, (await store.LoadAsync(42))!.Nodes);
        Assert.Equal(new uint[] { 0, 48 }, (await store.LoadAsync(42))!.LegCosts);
        await store.SaveAsync(42, new TaxiFlightRoute([2, 3], [11], [0]));
        Assert.Equal(new uint[] { 2, 3 }, (await store.LoadAsync(42))!.Nodes);
        await store.DeleteAsync(42);
        Assert.Null(await store.LoadAsync(42));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}
