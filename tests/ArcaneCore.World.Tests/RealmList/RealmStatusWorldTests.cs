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
/// The world marks its realm offline while it starts, online once it serves, and offline again when it stops (vmangos Master.cpp:215, :330;
/// the liveness part is <see cref="RealmLivenessService"/>), and writes the population 2 x sessions / PlayerLimit (World.cpp:340-353).
/// Before this the realm list kept whatever the seed said, so a world that was down still showed online.
/// </summary>
public sealed class RealmStatusWorldTests
{
    [Fact]
    public async Task WorldStartAndStop_MarkTheRealmStartingOnlineAndStopped()
    {
        var store = new RealmRowStore(new RealmEntry { Id = 3, Name = "R", Address = "a", Flags = RealmFlags.Offline | RealmFlags.NewPlayers, Population = 1.2f });
        var clock = new HandClock();
        await using ServiceProvider services = Services(store, new RealmStatusOptions { Id = 3 }, clock);
        RealmLivenessService liveness = services.GetRequiredService<RealmLivenessService>();
        WorldHost host = Host(services, out WorldRuntime world);
        try
        {
            // The hosted service runs before the world host: the row says Starting before the world has done anything.
            await liveness.StartAsync(CancellationToken.None);
            await liveness.StartWrite;
            Assert.Equal(RealmRuntimeState.Starting, store.Row.State);
            Assert.True(store.Row.Flags.HasFlag(RealmFlags.Offline));

            await host.StartAsync(CancellationToken.None);
            Assert.True(services.GetRequiredService<RealmStatusFeature>().Enabled);
            Assert.Equal(RealmRuntimeState.Starting, store.Row.State); // the world started, but nothing accepts connections yet

            // The listener binds and the world loop ticks (it runs for real): now it is online.
            liveness.MarkListenerBound();
            await WaitAsync(() => world.TicksStarted > 0, "the world loop ticks");
            clock.Advance(TimeSpan.FromSeconds(1));
            await liveness.WakeAsync(CancellationToken.None);
            Assert.Equal(RealmRuntimeState.Online, store.Row.State);
            Assert.Equal(RealmFlags.NewPlayers, store.Row.Flags);
            Assert.Equal(0f, store.Row.Population);

            await host.StopAsync(CancellationToken.None);
            Assert.Equal(RealmRuntimeState.Stopped, store.Row.State);
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
        var store = new RealmRowStore(new RealmEntry { Id = 1, Name = "R", Address = "a", Flags = RealmFlags.Offline });
        await using ServiceProvider services = Services(store, new RealmStatusOptions { Id = 0 }, new HandClock());
        WorldHost host = Host(services, out WorldRuntime world);
        try
        {
            await services.GetRequiredService<RealmLivenessService>().StartAsync(CancellationToken.None);
            await services.GetRequiredService<RealmLivenessService>().StartWrite;
            await host.StartAsync(CancellationToken.None);
            Assert.False(services.GetRequiredService<RealmStatusFeature>().Enabled);
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
        var store = new RealmRowStore(new RealmEntry { Id = 1, Name = "R", Address = "a" });
        await using ServiceProvider services = Services(store, new RealmStatusOptions { Id = 1, PlayerLimit = 100 }, new HandClock());
        WorldHost host = Host(services, out WorldRuntime world);
        try
        {
            await services.GetRequiredService<RealmLivenessService>().StartAsync(CancellationToken.None);
            await services.GetRequiredService<RealmLivenessService>().StartWrite;
            await host.StartAsync(CancellationToken.None);
            RealmStatusFeature feature = services.GetRequiredService<RealmStatusFeature>();
            feature.RequestPopulation(25);
            await WaitAsync(() => store.Row.Population != 0f, "the population write");

            Assert.Equal(0.5f, store.Row.Population);
            Assert.Equal(2f, RealmStatusFeature.Population(100, 100));
            await host.StopAsync(CancellationToken.None);
        }
        finally
        {
            world.Dispose();
        }
    }

    private static async Task WaitAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for: " + what);
            await Task.Delay(5);
        }
    }

    private sealed class WorldBox
    {
        public WorldRuntime? World { get; set; }
    }

    private static ServiceProvider Services(RealmRowStore store, RealmStatusOptions options, TimeProvider clock)
        => new ServiceCollection()
            .AddSingleton<ICharacterStore>(new InMemoryCharacterStore())
            .AddSingleton<IRealmStatusStore>(store)
            .AddSingleton(Options.Create(options))
            .AddSingleton(clock)
            .AddSingleton<WorldBox>()
            .AddSingleton(sp => new WorldLifecycleHooks())
            .AddSingleton<IWorldLifecycleHooks>(sp => sp.GetRequiredService<WorldLifecycleHooks>())
            .AddSingleton(sp => new RealmLivenessService(
                sp, NullLogger<RealmLivenessService>.Instance, sp.GetRequiredService<IOptions<RealmStatusOptions>>().Value, clock, () => sp.GetRequiredService<WorldBox>().World?.TicksStarted ?? 0)
            { StartLoop = false })
            .AddSingleton(sp => new RealmStatusFeature(sp, NullLogger<RealmStatusFeature>.Instance))
            .AddSingleton<IWorldFeature>(sp => sp.GetRequiredService<RealmStatusFeature>())
            .BuildServiceProvider();

    private static WorldHost Host(ServiceProvider services, out WorldRuntime world)
    {
        IServiceScopeFactory scopes = services.GetRequiredService<IServiceScopeFactory>();
        var queue = new CharacterSaveQueue(scopes, NullLogger<CharacterSaveQueue>.Instance);
        world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, queue, NullLogger<WorldRuntime>.Instance);
        services.GetRequiredService<WorldBox>().World = world;
        return new WorldHost(world, queue, new CharacterDirectory(), scopes, NullLogger<WorldHost>.Instance) { ListenerExpected = false };
    }
}
