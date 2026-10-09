using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.WorldState;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.WorldState;
using Xunit;

namespace ArcaneCore.Data.Tests;

public sealed class ScourgeInvasionStoreTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();
    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task StartDefeatRestartAndStopKeepSixZoneFactsDurable(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        Assert.True(await Start(connection));
        Assert.False(await Start(connection));
        ScourgeInvasionSnapshot state = await Load(connection);
        Assert.Equal(ScourgeInvasionState.Enabled, state.State);
        Assert.Equal(6, state.Zones.Count);
        Assert.Equal(14, state.Zones.Sum(z => z.Remaining));
        Assert.Equal(true, state.WorldScriptCondition(2260));

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.True(await Kill(connection, 16, 97592, now));
        Assert.False(await Kill(connection, 16, 97592, now));
        Assert.True(await Kill(connection, 16, 97593, now));
        state = await Load(connection);
        Assert.Equal(0, state.Remaining(16));
        Assert.Equal(false, state.WorldScriptCondition(2260));
        Assert.Equal(1, state.BattlesWon);
        Assert.Equal(16u, state.LastAttackZone);
        Assert.InRange(Assert.Single(state.Zones, z => z.ZoneId == 16).NextAttackUnix, now + 2700, now + 3600);
        Assert.False(await Restart(connection, 16, now + 3601)); // five zones are still under attack

        uint guid = 10_000;
        foreach (ScourgeInvasionZone zone in ScourgeInvasionCatalog.Zones.Where(z => z.ZoneId != 16))
            for (int i = 0; i < zone.Necropolises; i++)
                Assert.True(await Kill(connection, zone.ZoneId, guid++, now));
        Assert.Equal(6, (await Load(connection)).BattlesWon);
        Assert.True(await Restart(connection, 16, now + 3601));
        Assert.Equal(2, (await Load(connection)).Remaining(16));
        Assert.True(await Kill(connection, 16, 97592, now + 3601)); // this zone's old death ledger was cleared

        await Stop(connection);
        state = await Load(connection);
        Assert.Equal(ScourgeInvasionState.Disabled, state.State);
        Assert.Equal(0, state.BattlesWon);
        Assert.All(state.Zones, zone => Assert.Equal(0, zone.Remaining));
        Assert.True(await Start(connection));
        Assert.Equal(14, (await Load(connection)).Zones.Sum(z => z.Remaining));
    }

    private static async Task<ScourgeInvasionSnapshot> Load(DatabaseConnectionOptions connection)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        return await new EfScourgeInvasionStateStore(db).LoadAsync();
    }

    private static async Task<bool> Start(DatabaseConnectionOptions connection)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        return await new EfScourgeInvasionStateStore(db).StartAsync();
    }

    private static async Task Stop(DatabaseConnectionOptions connection)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await new EfScourgeInvasionStateStore(db).StopAsync();
    }

    private static async Task<bool> Kill(DatabaseConnectionOptions connection, uint zone, uint guid, long now)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        return await new EfScourgeInvasionStateStore(db).NecropolisDestroyedAsync(zone, guid, now, 3000);
    }

    private static async Task<bool> Restart(DatabaseConnectionOptions connection, uint zone, long now)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        return await new EfScourgeInvasionStateStore(db).RestartZoneAsync(zone, now);
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}
