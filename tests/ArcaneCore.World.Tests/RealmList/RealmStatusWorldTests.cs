using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.RealmList;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.RealmList;

/// <summary>
/// The world marks its realm online when it starts and offline when it stops (vmangos Master.cpp:215, :330), and writes the population
/// 2 x sessions / PlayerLimit (World.cpp:340-353). Before this the realm list kept whatever the seed said, so a world that was down
/// still showed online.
/// </summary>
public sealed class RealmStatusWorldTests
{
    [Fact]
    public async Task WorldStartAndStop_ClearAndSetTheOfflineFlag()
    {
        var store = new RowStore(new RealmEntry { Id = 3, Name = "R", Address = "a", Flags = RealmFlags.Offline | RealmFlags.NewPlayers, Population = 1.2f });
        await using ServiceProvider services = Services(store, new RealmStatusOptions { Id = 3 });
        WorldHost host = Host(services, out WorldRuntime world);
        try
        {
            await host.StartAsync(CancellationToken.None);
            Assert.True(services.GetRequiredService<RealmStatusFeature>().Enabled);
            Assert.Equal(RealmFlags.NewPlayers, store.Row.Flags);
            Assert.Equal(0f, store.Row.Population);

            await host.StopAsync(CancellationToken.None);
            Assert.Equal(RealmFlags.Offline | RealmFlags.NewPlayers, store.Row.Flags);
        }
        finally
        {
            world.Dispose();
        }
    }

    [Fact]
    public async Task IdZero_LeavesTheRealmListAlone()
    {
        var store = new RowStore(new RealmEntry { Id = 1, Name = "R", Address = "a", Flags = RealmFlags.Offline });
        await using ServiceProvider services = Services(store, new RealmStatusOptions { Id = 0 });
        WorldHost host = Host(services, out WorldRuntime world);
        try
        {
            await host.StartAsync(CancellationToken.None);
            Assert.Equal(RealmFlags.Offline, store.Row.Flags);
            await host.StopAsync(CancellationToken.None);
            Assert.Equal(0, store.Writes);
        }
        finally
        {
            world.Dispose();
        }
    }

    [Fact]
    public async Task APopulationRequest_WritesTheVmangosFormula()
    {
        var store = new RowStore(new RealmEntry { Id = 1, Name = "R", Address = "a" });
        await using ServiceProvider services = Services(store, new RealmStatusOptions { Id = 1, PlayerLimit = 100 });
        WorldHost host = Host(services, out WorldRuntime world);
        try
        {
            await host.StartAsync(CancellationToken.None);
            RealmStatusFeature feature = services.GetRequiredService<RealmStatusFeature>();
            feature.RequestPopulation(25);
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (store.Row.Population == 0f && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            Assert.Equal(0.5f, store.Row.Population);
            Assert.Equal(2f, RealmStatusFeature.Population(100, 100));
            await host.StopAsync(CancellationToken.None);
        }
        finally
        {
            world.Dispose();
        }
    }

    private static ServiceProvider Services(RowStore store, RealmStatusOptions options)
        => new ServiceCollection()
            .AddSingleton<ICharacterStore>(new InMemoryCharacterStore())
            .AddSingleton<IRealmStatusStore>(store)
            .AddSingleton(Options.Create(options))
            .AddSingleton(sp => new WorldLifecycleHooks())
            .AddSingleton<IWorldLifecycleHooks>(sp => sp.GetRequiredService<WorldLifecycleHooks>())
            .AddSingleton(sp => new RealmStatusFeature(sp, NullLogger<RealmStatusFeature>.Instance))
            .AddSingleton<IWorldFeature>(sp => sp.GetRequiredService<RealmStatusFeature>())
            .BuildServiceProvider();

    private static WorldHost Host(ServiceProvider services, out WorldRuntime world)
    {
        IServiceScopeFactory scopes = services.GetRequiredService<IServiceScopeFactory>();
        var queue = new CharacterSaveQueue(scopes, NullLogger<CharacterSaveQueue>.Instance);
        world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, queue, NullLogger<WorldRuntime>.Instance);
        return new WorldHost(world, queue, new CharacterDirectory(), scopes, NullLogger<WorldHost>.Instance) { ListenerExpected = false };
    }

    /// <summary>One realmlist row with the EF store's semantics (the EF store itself is covered in ArcaneCore.Data.Tests).</summary>
    private sealed class RowStore(RealmEntry row) : IRealmStatusStore
    {
        private readonly Lock _gate = new();

        public RealmEntry Row { get; } = row;

        public int Writes { get; private set; }

        public Task<bool> SetOnlineAsync(int realmId, CancellationToken cancellationToken = default)
            => Change(realmId, r => { r.Flags &= ~RealmFlags.Offline; r.Population = 0f; });

        public Task<bool> SetOfflineAsync(int realmId, CancellationToken cancellationToken = default)
            => Change(realmId, r => r.Flags |= RealmFlags.Offline);

        public Task<bool> SetPopulationAsync(int realmId, float population, CancellationToken cancellationToken = default)
            => Change(realmId, r => r.Population = population);

        private Task<bool> Change(int realmId, Action<RealmEntry> change)
        {
            lock (_gate)
            {
                Writes++;
                if (realmId != Row.Id)
                {
                    return Task.FromResult(false);
                }

                change(Row);
                return Task.FromResult(true);
            }
        }
    }
}
