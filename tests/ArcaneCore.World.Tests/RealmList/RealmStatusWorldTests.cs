using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.RealmList;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    [Fact]
    public async Task GatewayWorkerMode_WritesNothing()
    {
        var store = new RealmRowStore(new RealmEntry { Id = 1, Name = "R", Address = "a", Flags = RealmFlags.Offline, Population = 0.7f });
        using var registry = new SessionRegistry();
        await using ServiceProvider services = Services(
            store, new RealmStatusOptions { Id = 1, PlayerLimit = 100 }, new HandClock(),
            configure: c => c.AddSingleton(Options.Create(new ArcaneCore.World.Cluster.Gateway.GatewayWorkerOptions { Enabled = true })).AddSingleton(registry));
        WorldHost host = Host(services, out WorldRuntime world);
        try
        {
            RealmLivenessService liveness = services.GetRequiredService<RealmLivenessService>();
            await liveness.StartAsync(CancellationToken.None);
            await liveness.StartWrite;
            await host.StartAsync(CancellationToken.None);
            liveness.MarkListenerBound();
            await WaitAsync(() => world.TicksStarted > 0, "the world loop ticks");
            await liveness.WakeAsync(CancellationToken.None);
            registry.Register(NewSession(services, world, registry, accountId: 21)); // no population write: the feature never subscribed
            await host.StopAsync(CancellationToken.None);

            Assert.False(liveness.Enabled);
            Assert.False(services.GetRequiredService<RealmStatusFeature>().Enabled);
            Assert.Equal(0, store.Writes);
            Assert.Equal(RealmFlags.Offline, store.Row.Flags);
            Assert.Equal(0.7f, store.Row.Population);
        }
        finally
        {
            world.Dispose();
        }
    }

    [Fact]
    public async Task MissingRealmRow_WarnsOnce_PerWriter_AndTouchesNoOtherRow()
    {
        var store = new RealmRowStore(new RealmEntry { Id = 9, Name = "Other", Address = "a", Population = 0.3f });
        var livenessLog = new LineLogger();
        var featureLog = new LineLogger();
        await using ServiceProvider services = Services(store, new RealmStatusOptions { Id = 1, PlayerLimit = 100 }, new HandClock(), livenessLog, featureLog);
        WorldHost host = Host(services, out WorldRuntime world);
        try
        {
            RealmLivenessService liveness = services.GetRequiredService<RealmLivenessService>();
            await liveness.StartAsync(CancellationToken.None);
            await liveness.StartWrite;
            await host.StartAsync(CancellationToken.None);

            RealmStatusFeature feature = services.GetRequiredService<RealmStatusFeature>();
            feature.RequestPopulation(5);
            await WaitAsync(() => store.Calls.Count(c => c == "population") == 1, "the first population write");
            feature.RequestPopulation(6);
            await WaitAsync(() => store.Calls.Count(c => c == "population") == 2, "the second population write");
            await host.StopAsync(CancellationToken.None); // waits for the population writer, then writes the stopped state

            Assert.Single(featureLog.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("has no row", StringComparison.Ordinal));
            Assert.Single(livenessLog.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("has no row", StringComparison.Ordinal));
            Assert.Equal(0.3f, store.Row.Population);
            Assert.Equal(RealmFlags.None, store.Row.Flags);
            Assert.Equal(RealmRuntimeState.Unknown, store.Row.State);
        }
        finally
        {
            world.Dispose();
        }
    }

    [Fact]
    public async Task ASessionAddedToTheRegistry_RequestsThePopulation_UntilTheFeatureStops()
    {
        var store = new RealmRowStore(new RealmEntry { Id = 1, Name = "R", Address = "a" });
        using var registry = new SessionRegistry();
        await using ServiceProvider services = Services(
            store, new RealmStatusOptions { Id = 1, PlayerLimit = 100 }, new HandClock(), configure: c => c.AddSingleton(registry));
        WorldHost host = Host(services, out WorldRuntime world);
        try
        {
            await services.GetRequiredService<RealmLivenessService>().StartAsync(CancellationToken.None);
            await services.GetRequiredService<RealmLivenessService>().StartWrite;
            await host.StartAsync(CancellationToken.None);
            Assert.True(services.GetRequiredService<RealmStatusFeature>().Enabled);
            Assert.DoesNotContain("population", store.Calls); // nothing is written until a session arrives

            registry.Register(NewSession(services, world, registry, accountId: 11));
            await WaitAsync(() => store.Row.Population != 0f, "the population write for the first session");
            Assert.Equal(RealmStatusFeature.Population(1, 100), store.Row.Population);

            registry.Register(NewSession(services, world, registry, accountId: 12));
            await WaitAsync(() => store.Row.Population == RealmStatusFeature.Population(2, 100), "the population write for the second session");

            await host.StopAsync(CancellationToken.None);
            int writes = store.Writes;
            registry.Register(NewSession(services, world, registry, accountId: 13));
            await Task.Delay(100);
            Assert.Equal(writes, store.Writes); // the feature unsubscribed and is stopping: a late session writes nothing
        }
        finally
        {
            world.Dispose();
        }
    }

    [Fact]
    public async Task PopulationWrittenBeforeTheOnlineWrite_IsReissuedAfterIt()
    {
        var store = new RealmRowStore(new RealmEntry { Id = 1, Name = "R", Address = "a", Flags = RealmFlags.Offline });
        var clock = new HandClock();
        await using ServiceProvider services = Services(store, new RealmStatusOptions { Id = 1, PlayerLimit = 100 }, clock, configure: c => c.AddSingleton(new SessionRegistry()));
        WorldHost host = Host(services, out WorldRuntime world);
        try
        {
            RealmLivenessService liveness = services.GetRequiredService<RealmLivenessService>();
            await liveness.StartAsync(CancellationToken.None);
            await liveness.StartWrite;
            await host.StartAsync(CancellationToken.None);
            liveness.MarkListenerBound();
            await WaitAsync(() => world.TicksStarted > 0, "the world loop ticks");

            // The Online write is held in the store while a session's population write goes through ahead of it.
            using var onlineEntered = new ManualResetEventSlim(false);
            using var releaseOnline = new ManualResetEventSlim(false);
            store.OnCall = call =>
            {
                if (call == "online")
                {
                    onlineEntered.Set();
                    releaseOnline.Wait(TimeSpan.FromSeconds(30));
                }
            };
            Task wake = Task.Run(() => liveness.WakeAsync(CancellationToken.None));
            Assert.True(onlineEntered.Wait(TimeSpan.FromSeconds(10)), "the Online write reached the store");

            services.GetRequiredService<RealmStatusFeature>().RequestPopulation(25);
            await WaitAsync(() => store.Row.Population == 0.5f, "the early population write");

            releaseOnline.Set(); // Online lands now and resets the population to 0 (vmangos Master.cpp:215)
            await wake.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(RealmRuntimeState.Online, store.Row.State);

            await WaitAsync(() => store.Calls.Count(c => c == "population") == 2, "the population is written again after Online");
            await host.StopAsync(CancellationToken.None); // finishes the re-issued write before the row goes offline
            Assert.Equal(0.5f, store.Row.Population);
            string[] calls = store.Calls;
            Assert.True(Array.IndexOf(calls, "online") < Array.LastIndexOf(calls, "population"), string.Join(",", calls));
        }
        finally
        {
            world.Dispose();
            services.GetRequiredService<SessionRegistry>().Dispose();
        }
    }

    [Fact]
    public async Task OnlineWithNoPopulationRequestYet_WritesNoPopulation()
    {
        var store = new RealmRowStore(new RealmEntry { Id = 1, Name = "R", Address = "a", Flags = RealmFlags.Offline });
        await using ServiceProvider services = Services(store, new RealmStatusOptions { Id = 1, PlayerLimit = 100 }, new HandClock(), configure: c => c.AddSingleton(new SessionRegistry()));
        WorldHost host = Host(services, out WorldRuntime world);
        try
        {
            RealmLivenessService liveness = services.GetRequiredService<RealmLivenessService>();
            await liveness.StartAsync(CancellationToken.None);
            await liveness.StartWrite;
            await host.StartAsync(CancellationToken.None);
            liveness.MarkListenerBound();
            await WaitAsync(() => world.TicksStarted > 0, "the world loop ticks");
            await liveness.WakeAsync(CancellationToken.None);
            Assert.Equal(RealmRuntimeState.Online, store.Row.State);

            await host.StopAsync(CancellationToken.None);
            Assert.DoesNotContain("population", store.Calls); // nothing was ever requested, so there is nothing to re-issue
        }
        finally
        {
            world.Dispose();
            services.GetRequiredService<SessionRegistry>().Dispose();
        }
    }

    private static WorldSession NewSession(IServiceProvider services, WorldRuntime world, SessionRegistry registry, int accountId)
    {
        WorldSession session = WorldSession.CreateConsole(services, new OpcodeTable(), world, registry, new WorldSessionOptions(), NullLogger.Instance);
        typeof(WorldSession).GetProperty(nameof(WorldSession.AccountId))!.SetValue(session, accountId); // an authenticated account, without a socket
        return session;
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

    private sealed class TypedLog<T>(ILogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => inner.Log(logLevel, eventId, state, exception, formatter);
    }

    private static ServiceProvider Services(
        RealmRowStore store, RealmStatusOptions options, TimeProvider clock,
        ILogger? livenessLog = null, ILogger? featureLog = null, Action<IServiceCollection>? configure = null)
    {
        var collection = new ServiceCollection()
            .AddSingleton<ICharacterStore>(new InMemoryCharacterStore())
            .AddSingleton<IRealmStatusStore>(store)
            .AddSingleton(Options.Create(options))
            .AddSingleton(clock)
            .AddSingleton<WorldBox>()
            .AddSingleton(sp => new WorldLifecycleHooks())
            .AddSingleton<IWorldLifecycleHooks>(sp => sp.GetRequiredService<WorldLifecycleHooks>())
            .AddSingleton(sp => new RealmLivenessService(
                sp, livenessLog ?? NullLogger<RealmLivenessService>.Instance, sp.GetRequiredService<IOptions<RealmStatusOptions>>().Value, clock, () => sp.GetRequiredService<WorldBox>().World?.TicksStarted ?? 0)
            { StartLoop = false })
            .AddSingleton(sp => new RealmStatusFeature(sp, featureLog is null ? NullLogger<RealmStatusFeature>.Instance : new TypedLog<RealmStatusFeature>(featureLog)))
            .AddSingleton<IWorldFeature>(sp => sp.GetRequiredService<RealmStatusFeature>());
        configure?.Invoke(collection);
        return collection.BuildServiceProvider();
    }

    private static WorldHost Host(ServiceProvider services, out WorldRuntime world)
    {
        IServiceScopeFactory scopes = services.GetRequiredService<IServiceScopeFactory>();
        var queue = new CharacterSaveQueue(scopes, NullLogger<CharacterSaveQueue>.Instance);
        world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, queue, NullLogger<WorldRuntime>.Instance);
        services.GetRequiredService<WorldBox>().World = world;
        return new WorldHost(world, queue, new CharacterDirectory(), scopes, NullLogger<WorldHost>.Instance) { ListenerExpected = false };
    }
}
