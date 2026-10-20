using ArcaneCore.Game.Maps;
using ArcaneCore.Game.OutdoorPvP;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.OutdoorPvP;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.WorldState;

/// <summary>
/// The saved-variable writers that left the world thread (Dragons of Nightmare, elemental invasion, Silithyst; Fishing
/// Extravaganza is in <see cref="StateSaveQueueTests"/>) and War Effort's shutdown: the change returns at once while the store
/// is held, StopAsync waits for the write, and the store ends with the changed value.
/// </summary>
public sealed class WorldStateSaveFlushTests
{
    /// <summary>A store gate: closed, every save waits on it (a held SQLite write lock).</summary>
    private sealed class Gate
    {
        public readonly ManualResetEventSlim Open = new(false);
        public void Pass() => Open.Wait(TimeSpan.FromSeconds(30));
    }

    private sealed class DragonStore(Gate gate) : INightmareDragonsStore
    {
        public NightmareDragonsState? Saved;
        public Task<NightmareDragonsState?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Saved);
        public Task SaveAsync(NightmareDragonsState state, CancellationToken cancellationToken = default)
        {
            gate.Pass();
            Saved = state;
            return Task.CompletedTask;
        }
    }

    private sealed class InvasionStore(Gate gate) : IElementalInvasionStore
    {
        public readonly List<ElementalInvasionState> Saved = [];
        public Task<IReadOnlyList<ElementalInvasionState>> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ElementalInvasionState>>([]);
        public Task SaveAsync(ElementalInvasionState state, CancellationToken cancellationToken = default)
        {
            gate.Pass();
            lock (Saved) Saved.Add(state);
            return Task.CompletedTask;
        }
    }

    private sealed class SilithystStore(Gate gate) : ISilithystStore
    {
        public SilithystState? Saved;
        public Task<SilithystState?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<SilithystState?>(null);
        public Task SaveAsync(SilithystState state, CancellationToken cancellationToken = default)
        {
            gate.Pass();
            Saved = state;
            return Task.CompletedTask;
        }
    }

    private static async Task StopWaitsForTheHeldWrite(Func<Task> stop, Gate gate)
    {
        Task stopping = stop();
        await Task.Delay(100);
        Assert.False(stopping.IsCompleted, "StopAsync returned while the store still held the queued write");
        gate.Open.Set();
        await stopping.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task NightmareDragonsStartIsQueuedAndStopAsyncWritesIt()
    {
        var gate = new Gate();
        var store = new DragonStore(gate);
        ServiceProvider provider = new ServiceCollection().AddSingleton<INightmareDragonsStore>(store).BuildServiceProvider();
        var feature = new NightmareDragonsFeature(provider.GetRequiredService<IServiceScopeFactory>())
        {
            NowUnix = () => 1_000_000,
            StatusOverride = _ => NightmareDragonsFeature.DragonStatus.Alive,
            SpawnOverride = (_, _) => { },
            DespawnOverride = _ => { },
        };

        feature.Update(); // the respawn time has passed: StartEvent(66), saved
        Assert.True(feature.State.Active); // memory at once
        Assert.Null(store.Saved); // the write waits for the store

        await StopWaitsForTheHeldWrite(feature.StopAsync, gate);
        Assert.Equal(feature.State, store.Saved);
    }

    [Fact]
    public async Task ElementalInvasionStageIsQueuedAndStopAsyncWritesIt()
    {
        var gate = new Gate();
        var store = new InvasionStore(gate);
        ServiceProvider provider = new ServiceCollection().AddSingleton<IElementalInvasionStore>(store).BuildServiceProvider();
        var events = new GameEventFeature(provider, NullLogger<GameEventFeature>.Instance);
        var feature = new HolidayObjectsFeature(provider.GetRequiredService<IServiceScopeFactory>(), events);
        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 },
            new CharacterSaveQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance),
            NullLogger<WorldRuntime>.Instance);
        feature.Attach(world);

        Assert.True(feature.Invasion.TryAdvance(0, hourElapsed: true)); // the rift's hour ran out: stage 1 -> 2 (an invader kill saves the same way)
        Assert.Empty(store.Saved);

        await StopWaitsForTheHeldWrite(feature.StopAsync, gate);
        Assert.Equal([new ElementalInvasionState(0, feature.Invasion.Stage(0), feature.Invasion.Kills(0))], store.Saved);
    }

    [Fact]
    public async Task SilithystTurnInIsQueuedAndStopAsyncWritesIt()
    {
        var gate = new Gate();
        var store = new SilithystStore(gate);
        ServiceProvider provider = new ServiceCollection().AddLogging().AddSingleton<ISilithystStore>(store).BuildServiceProvider();
        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 },
            new CharacterSaveQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance),
            NullLogger<WorldRuntime>.Instance);
        var feature = new OutdoorPvPFeature(provider, NullLogger<OutdoorPvPFeature>.Instance);
        feature.Attach(world);
        world.RunTick(1);
        world.GetMap(1);
        SilithusZone zone = Assert.IsType<SilithusZone>(feature.Silithus);

        zone.Saved!(3, 4, zone.MaxResources); // what UpdateWorldState hands the feature after a turn-in
        Assert.Null(store.Saved);

        await StopWaitsForTheHeldWrite(feature.StopAsync, gate);
        Assert.Equal(new SilithystState(3, 4, zone.MaxResources), store.Saved);
    }

    private sealed class WarStore(Gate gate) : IWarEffortStateStore
    {
        public WarEffortSnapshot State = WarEffortSnapshot.Disabled;
        public int PhaseWrites;
        public bool Hold;
        public Task<WarEffortSnapshot> LoadAsync(CancellationToken cancellationToken = default)
        {
            if (Hold) gate.Pass();
            return Task.FromResult(State);
        }
        public Task SetPhaseAsync(WarEffortPhase phase, long phaseEndsAtUnix, CancellationToken cancellationToken = default)
        {
            if (Hold) gate.Pass();
            Interlocked.Increment(ref PhaseWrites);
            State = State with { Phase = phase, PhaseEndsAtUnix = phaseEndsAtUnix };
            return Task.CompletedTask;
        }
        public Task<bool> MarkBossKilledAsync(int bossIndex, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private static (WorldRuntime World, WarEffortFeature War, ServiceProvider Provider) War(WarStore store)
    {
        ServiceProvider provider = new ServiceCollection().AddSingleton<IWarEffortStateStore>(store).BuildServiceProvider();
        var events = new GameEventFeature(provider, NullLogger<GameEventFeature>.Instance);
        var war = new WarEffortFeature(provider.GetRequiredService<IServiceScopeFactory>(), events, NullLogger<WarEffortFeature>.Instance);
        var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 },
            new CharacterSaveQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance),
            NullLogger<WorldRuntime>.Instance);
        war.Attach(world); // the startup load, gate open
        store.Hold = true;
        return (world, war, provider);
    }

    [Fact]
    public async Task WarEffortPhaseCarriedByTheJobInFlightIsWrittenOnceAtStop()
    {
        var gate = new Gate();
        var store = new WarStore(gate);
        (WorldRuntime world, WarEffortFeature war, ServiceProvider provider) = War(store);
        using (world)
        using (provider)
        {
            war.OnEventChanged(WarEffortCatalog.GatheringEvent, active: true, resume: false); // .event start 120: a job, stuck
            Assert.True(war.StoreWork!.Busy);

            // The world stops before the job's apply runs: the phase is still "queued" on the world thread's side.
            await StopWaitsForTheHeldWrite(war.StopAsync, gate);
            Assert.Equal(WarEffortPhase.Gathering, store.State.Phase);
            Assert.Equal(1, store.PhaseWrites); // the job wrote it; StopAsync did not write it again
        }
    }

    [Fact]
    public async Task WarEffortPhaseQueuedBehindAStuckJobIsWrittenAtStop()
    {
        var gate = new Gate();
        var store = new WarStore(gate);
        (WorldRuntime world, WarEffortFeature war, ServiceProvider provider) = War(store);
        using (world)
        using (provider)
        {
            world.RunTick(5_000); // the five-second refresh: a job stuck in LoadAsync
            Assert.True(war.StoreWork!.Busy);
            war.OnEventChanged(WarEffortCatalog.GatheringEvent, active: true, resume: false); // queued behind it
            Assert.Equal(0, store.PhaseWrites);

            await StopWaitsForTheHeldWrite(war.StopAsync, gate);
            Assert.Equal(WarEffortPhase.Gathering, store.State.Phase);
            Assert.Equal(1, store.PhaseWrites);
        }
    }
}
