using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.WorldState;

/// <summary>vmangos scarab_gongAI gate opening: timed steps from the saved first ring, permanent open after the war.</summary>
public sealed class WarEffortGateTests
{
    private const long Rung = 1_900_000_000;

    private sealed class MemoryStore : IWarEffortStateStore
    {
        public WarEffortSnapshot State { get; set; } = WarEffortSnapshot.Disabled;
        public Task<WarEffortSnapshot> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);

        public Task SetPhaseAsync(WarEffortPhase phase, long phaseEndsAtUnix, CancellationToken cancellationToken = default)
        {
            State = State with { Phase = phase, PhaseEndsAtUnix = phaseEndsAtUnix };
            return Task.CompletedTask;
        }

        public Task<bool> MarkBossKilledAsync(int bossIndex, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private static WarEffortSnapshot War(long rungAt = Rung) => new(WarEffortPhase.TenHourWar,
        rungAt + WarEffortCatalog.WarStartsAfterSeconds + WarEffortCatalog.TenHourWarSeconds,
        new long[WarEffortCatalog.ResourceCount], GongRingCount: 1, GongFirstRungAtUnix: rungAt, GongFirstRingerId: 7);

    [Fact]
    public void GateStepsFollowTheScarabGongTimelineFromTheSavedRing()
    {
        WarEffortSnapshot war = War();
        Assert.Equal(WarEffortGateState.Closed, war.GateAt(Rung));
        Assert.Equal(new WarEffortGateState(true, false, false), war.GateAt(Rung + 1));
        Assert.Equal(new WarEffortGateState(true, false, false), war.GateAt(Rung + 5));
        Assert.Equal(new WarEffortGateState(true, true, false), war.GateAt(Rung + 6));
        Assert.Equal(WarEffortGateState.AllOpen, war.GateAt(Rung + 14));
        Assert.Equal(WarEffortGateState.AllOpen, war.GateAt(Rung + 9 * 3_600));
    }

    [Fact]
    public void GateIsClosedBeforeTheRingAndPermanentlyOpenOnceDone()
    {
        var none = new long[WarEffortCatalog.ResourceCount];
        foreach (WarEffortPhase phase in new[] { WarEffortPhase.Disabled, WarEffortPhase.Gathering, WarEffortPhase.Transporting, WarEffortPhase.Gong })
            Assert.Equal(WarEffortGateState.Closed, new WarEffortSnapshot(phase, 0, none).GateAt(Rung + 100));
        // A war phase without a saved ring (a GM-set phase) does not open the gate on its own.
        Assert.Equal(WarEffortGateState.Closed, new WarEffortSnapshot(WarEffortPhase.TenHourWar, Rung, none).GateAt(Rung + 100));
        Assert.Equal(WarEffortGateState.AllOpen, new WarEffortSnapshot(WarEffortPhase.Done, 0, none).GateAt(0));
        Assert.Equal(WarEffortGateState.AllOpen, (War() with { Phase = WarEffortPhase.Done }).GateAt(Rung));
    }

    [Fact]
    public void RestartMidOpeningResumesAtTheSavedStepAndFinishesTheSequence()
    {
        using Rig rig = new(War());
        long now = Rung + 7; // a new process starts between the runes and barrier steps
        rig.War.UtcNowUnix = () => now;
        rig.World.RunTick(5_000);
        Assert.Equal(GameObjectState.Active, rig.Roots.State);
        Assert.Equal(GameObjectState.Active, rig.Runes.State);
        Assert.Equal(GameObjectState.Ready, rig.Barrier.State);
        Assert.True(rig.Events.IsActiveEvent(123));

        now = Rung + 14;
        rig.World.RunTick(100);
        Assert.Equal(GameObjectState.Active, rig.Barrier.State);
    }

    [Fact]
    public void TenHourDeadlineAfterRestartLeavesTheGatePermanentlyOpen()
    {
        using Rig rig = new(War());
        rig.War.UtcNowUnix = () => Rung + WarEffortCatalog.WarStartsAfterSeconds + WarEffortCatalog.TenHourWarSeconds + 1;
        rig.World.RunTick(5_000);
        Assert.Equal(WarEffortPhase.Done, rig.Store.State.Phase);
        Assert.True(rig.Events.IsActiveEvent(124));
        Assert.All(new[] { rig.Roots, rig.Runes, rig.Barrier }, go => Assert.Equal(GameObjectState.Active, go.State));

        // Something else closing a piece (a GM .gobject activate, a reset) is undone: the open state is the saved one.
        rig.Barrier.State = GameObjectState.Ready;
        rig.World.RunTick(100);
        Assert.Equal(GameObjectState.Active, rig.Barrier.State);
    }

    [Fact]
    public void GatesStayClosedThroughGatheringTransportAndGong()
    {
        using Rig rig = new(new WarEffortSnapshot(WarEffortPhase.Gong, 0, new long[WarEffortCatalog.ResourceCount]));
        rig.Roots.State = GameObjectState.Active; // a stale open state from before the realm reset
        rig.World.RunTick(5_000);
        Assert.All(new[] { rig.Roots, rig.Runes, rig.Barrier }, go => Assert.Equal(GameObjectState.Ready, go.State));
        Assert.True(rig.Events.IsActiveEvent(122));
    }

    [Fact]
    public void FiveDayCountdownResumesAfterRestartAndOpensTheGongPhaseOnlyWhenItExpires()
    {
        long endsAt = Rung + WarEffortCatalog.TransportSeconds;
        using Rig rig = new(new WarEffortSnapshot(WarEffortPhase.Transporting, endsAt, new long[WarEffortCatalog.ResourceCount]));
        long now = endsAt - 1;
        rig.War.UtcNowUnix = () => now;
        rig.World.RunTick(5_000);
        Assert.Equal(WarEffortPhase.Transporting, rig.Store.State.Phase);
        Assert.True(rig.Events.IsActiveEvent(121));
        Assert.Equal(true, rig.Store.State.WorldScriptCondition(WarEffortCatalog.DaysLeftCondition, 1,
            DateTimeOffset.FromUnixTimeSeconds(now)));

        now = endsAt;
        rig.World.RunTick(5_000);
        Assert.Equal(WarEffortPhase.Gong, rig.Store.State.Phase);
        Assert.False(rig.Events.IsActiveEvent(121));
        Assert.True(rig.Events.IsActiveEvent(122));
        Assert.All(new[] { rig.Roots, rig.Runes, rig.Barrier }, go => Assert.Equal(GameObjectState.Ready, go.State));
    }

    [Fact]
    public void ChampionIsAnnouncedOnceForAFreshRingAndNotReplayedForAnOldOne()
    {
        using (Rig fresh = new(War()))
        {
            fresh.War.UtcNowUnix = () => Rung + 3;
            fresh.World.RunTick(5_000);
            Assert.Equal("champion has rung the Scarab Gong.", fresh.War.LastChampionAnnouncement);
        }

        using Rig late = new(War());
        late.War.UtcNowUnix = () => Rung + WarEffortFeature.ChampionAnnounceWindowSeconds + 1;
        late.World.RunTick(5_000);
        Assert.Null(late.War.LastChampionAnnouncement);
    }

    private sealed class Rig : IDisposable
    {
        private readonly ServiceProvider _provider;
        public WorldRuntime World { get; }
        public WarEffortFeature War { get; }
        public GameEventFeature Events { get; }
        public MemoryStore Store { get; }
        public GameObject Roots { get; }
        public GameObject Runes { get; }
        public GameObject Barrier { get; }

        public Rig(WarEffortSnapshot saved)
        {
            var services = new ServiceCollection();
            services.AddSingleton(new MemoryStore { State = saved });
            services.AddScoped<IWarEffortStateStore>(sp => sp.GetRequiredService<MemoryStore>());
            services.AddScoped<IGameEventDataStore, PhaseEvents>();
            services.AddSingleton(sp => new GameEventFeature(sp, NullLogger<GameEventFeature>.Instance));
            services.AddSingleton(sp => new WarEffortFeature(sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<GameEventFeature>(), NullLogger<WarEffortFeature>.Instance));
            _provider = services.BuildServiceProvider();
            Store = _provider.GetRequiredService<MemoryStore>();
            World = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 },
                new CharacterSaveQueue(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance),
                NullLogger<WorldRuntime>.Instance);
            Map kalimdor = World.GetMap(1);
            var objects = new GameObjectMapSystem(kalimdor, new GameObjectContent(
                [Door(WarEffortCatalog.GateRoots), Door(WarEffortCatalog.GateRunes), Door(WarEffortCatalog.GateBarrier)], [], [], [], []));
            kalimdor.AddUpdater(objects);
            Roots = objects.Summon(WarEffortCatalog.GateRoots, -8133, 1525, 17, 0)!;
            Runes = objects.Summon(WarEffortCatalog.GateRunes, -8133, 1525, 17, 0)!;
            Barrier = objects.Summon(WarEffortCatalog.GateBarrier, -8133, 1525, 17, 0)!;
            Events = _provider.GetRequiredService<GameEventFeature>();
            Events.Attach(World);
            War = _provider.GetRequiredService<WarEffortFeature>();
            War.Attach(World);
        }

        private static GameObjectTemplate Door(uint entry) => new()
        {
            Entry = entry, Type = (uint)GameObjectType.Door, DisplayId = 1000, Name = $"AQ gate {entry}", Size = 1.0f,
            Data = new uint[GameObjectTemplate.DataCount],
        };

        public void Dispose()
        {
            World.Dispose();
            _provider.Dispose();
        }
    }

    private sealed class PhaseEvents : IGameEventDataStore
    {
        public Task<GameEventContent> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new GameEventContent(
                [.. Enumerable.Range(120, 8).Select(id => new GameEventRecord((ushort)id, 0, 525600, 1, 0, 0, $"AQ {id}"))],
                [], [], [], [], [], []));

        public Task SetDisabledAsync(uint entry, bool disabled, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
