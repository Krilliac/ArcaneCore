using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static ArcaneCore.Kernel.WorldData.WorldState.WarEffortSceneSchedule;

namespace ArcaneCore.World.Tests.WorldState;

/// <summary>vmangos npc_aqwar_ch_attack waves and npc_aqwar_saurfang speech/ride, placed on the saved transport deadline.</summary>
public sealed class WarEffortSceneTests
{
    private const long End = 1_900_000_000;
    private static WarEffortSnapshot Moving => new(WarEffortPhase.Transporting, End, new long[WarEffortCatalog.ResourceCount]);

    [Fact]
    public void ScenesSitInTheLastFiveHoursOfTransport()
    {
        Assert.Equal(WarEffortScene.None, SceneAt(Moving, End - AttackStartsBeforeEndSeconds - 1));
        Assert.Equal(WarEffortScene.CenarionHoldAttack, SceneAt(Moving, End - AttackStartsBeforeEndSeconds));
        Assert.Equal(WarEffortScene.CenarionHoldAttack, SceneAt(Moving, End - FinalBattleBeforeEndSeconds - 1));
        Assert.Equal(WarEffortScene.FinalBattle, SceneAt(Moving, End - FinalBattleBeforeEndSeconds));
        Assert.Equal(WarEffortScene.None, SceneAt(Moving, End));
        Assert.Equal(WarEffortScene.None, SceneAt(Moving with { Phase = WarEffortPhase.Gathering }, End - 100));
        Assert.Equal(WarEffortScene.None, SceneAt(Moving with { PhaseEndsAtUnix = 0 }, End - 100));
    }

    [Fact]
    public void ElevenWavesFifteenMinutesApartAfterAOneMinuteDelay()
    {
        long start = End - AttackStartsBeforeEndSeconds;
        Assert.Equal(-1, LatestWaveDue(Moving, start + 59));
        Assert.Equal(0, LatestWaveDue(Moving, start + 60));
        Assert.Equal(0, LatestWaveDue(Moving, start + 60 + 899));
        Assert.Equal(1, LatestWaveDue(Moving, start + 60 + 900));
        Assert.Equal(10, LatestWaveDue(Moving, start + 60 + 10 * 900));
        Assert.Equal(10, LatestWaveDue(Moving, End - FinalBattleBeforeEndSeconds - 1));
        Assert.Equal(-1, LatestWaveDue(Moving, End - FinalBattleBeforeEndSeconds));
    }

    [Fact]
    public void SaurfangSpeaksFourteenLinesTenSecondsApartAfterTwoMinutes()
    {
        long start = End - FinalBattleBeforeEndSeconds;
        Assert.Equal(0, SpeechLinesDue(Moving, start + 119));
        Assert.Equal(1, SpeechLinesDue(Moving, start + 120));
        Assert.Equal(2, SpeechLinesDue(Moving, start + 130));
        Assert.Equal(14, SpeechLinesDue(Moving, start + 120 + 130));
        Assert.Equal(14, SpeechLinesDue(Moving, End - 1));
        Assert.Equal(14, SaurfangSpeech.Count);
        Assert.Equal(12, GatePath.Count);
    }

    [Fact]
    public void WavesSpawnOncePerDueWaveAndARestartOnlyRestoresTheCurrentWave()
    {
        long start = End - AttackStartsBeforeEndSeconds;
        long now = start + 60 + 3 * 900 + 5; // a restart during wave 4
        using Rig rig = new(Moving, () => now);
        rig.World.RunTick(100);
        Assert.Equal(MobsPerWave, rig.War.WaveCreatures.Count);
        Assert.All(rig.War.WaveCreatures, c => Assert.Contains(c.Entry, new[] { ColossalAnubisath, QirajiDestroyer }));
        Assert.All(rig.War.WaveCreatures, c => Assert.Equal(WaveTarget.X, c.Home.X));

        rig.World.RunTick(100);
        Assert.Equal(MobsPerWave, rig.War.WaveCreatures.Count);
        now += 900;
        rig.World.RunTick(100);
        Assert.Equal(2 * MobsPerWave, rig.War.WaveCreatures.Count);
        Assert.Null(rig.War.SceneSaurfang);
    }

    [Fact]
    public void FinalBattleSummonsTheScriptedSaurfangThatResumesSpeechAndRidesTheGatePath()
    {
        long start = End - FinalBattleBeforeEndSeconds;
        long now = start + 120 + 45; // restart after five lines
        using Rig rig = new(Moving, () => now);
        rig.World.RunTick(100);
        Creature saurfang = Assert.IsType<Creature>(rig.War.SceneSaurfang);
        var ai = Assert.IsType<WarEffortFeature.SaurfangSceneAi>(saurfang.AI);
        Assert.Equal(5, ai.Spoken); // earlier lines are not replayed
        Assert.Empty(rig.War.WaveCreatures);

        now = start + 120 + 130;
        ai.OnUpdate(0);
        Assert.Equal(14, ai.Spoken);
        Assert.Equal("[broadcast_text 11619]", rig.War.LastWorldBroadcast);
        Assert.Equal(0, ai.NextPoint);
        Assert.True(ai.Moving);
        Assert.Equal(GatePath[0].X, saurfang.Home.X);
        ai.OnMovementInform(MovementGeneratorType.Point, 0);
        ai.OnUpdate(0);
        Assert.Equal(1, ai.NextPoint);
        Assert.Equal(GatePath[1].X, saurfang.Home.X);

        rig.World.RunTick(100); // still alive: not summoned twice
        Assert.Same(saurfang, rig.War.SceneSaurfang);
    }

    [Fact]
    public void AStaticSaurfangSpawnElsewhereKeepsItsOwnAi()
    {
        long now = End - FinalBattleBeforeEndSeconds + 10;
        using Rig rig = new(Moving, () => now);
        rig.World.RunTick(100);
        Creature other = rig.Creatures.SummonForInstance(Saurfang, 1920, -4130, 40, 0)!; // Orgrimmar, not the scene
        Assert.IsNotType<WarEffortFeature.SaurfangSceneAi>(other.AI);
    }

    private sealed class MemoryStore(WarEffortSnapshot state) : IWarEffortStateStore
    {
        public WarEffortSnapshot State { get; set; } = state;
        public Task<WarEffortSnapshot> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);
        public Task SetPhaseAsync(WarEffortPhase phase, long phaseEndsAtUnix, CancellationToken cancellationToken = default)
        {
            State = State with { Phase = phase, PhaseEndsAtUnix = phaseEndsAtUnix };
            return Task.CompletedTask;
        }
        public Task<bool> MarkBossKilledAsync(int bossIndex, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class Rig : IDisposable
    {
        private readonly ServiceProvider _provider;
        public WorldRuntime World { get; }
        public WarEffortFeature War { get; }
        public CreatureMapSystem Creatures { get; }

        public Rig(WarEffortSnapshot saved, Func<long> clock)
        {
            var services = new ServiceCollection();
            services.AddSingleton(new MemoryStore(saved));
            services.AddScoped<IWarEffortStateStore>(sp => sp.GetRequiredService<MemoryStore>());
            services.AddScoped<IGameEventDataStore, NoEvents>();
            services.AddSingleton(sp => new GameEventFeature(sp, NullLogger<GameEventFeature>.Instance));
            services.AddSingleton(sp => new WarEffortFeature(sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<GameEventFeature>(), NullLogger<WarEffortFeature>.Instance));
            _provider = services.BuildServiceProvider();
            World = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 },
                new CharacterSaveQueue(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance),
                NullLogger<WorldRuntime>.Instance);
            Map kalimdor = World.GetMap(1);
            Creatures = new CreatureMapSystem(kalimdor, new CreatureContent(
                [Template(ColossalAnubisath), Template(QirajiDestroyer), Template(Saurfang)], [], [], [], []), random: new Random(3));
            kalimdor.AddUpdater(Creatures);
            _provider.GetRequiredService<GameEventFeature>().Attach(World);
            War = _provider.GetRequiredService<WarEffortFeature>();
            War.UtcNowUnix = clock;
            War.Attach(World);
        }

        private static CreatureTemplate Template(uint entry) => new()
        {
            Entry = entry, Name = $"AQ war {entry}", MinLevelHealth = 1000, MaxLevelHealth = 1000,
        };

        public void Dispose()
        {
            World.Dispose();
            _provider.Dispose();
        }
    }

    private sealed class NoEvents : IGameEventDataStore
    {
        public Task<GameEventContent> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new GameEventContent([], [], [], [], [], [], []));
        public Task SetDisabledAsync(uint entry, bool disabled, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
