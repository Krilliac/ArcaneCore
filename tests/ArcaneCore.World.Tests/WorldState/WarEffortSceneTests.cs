using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Kernel.WorldData.GameObjects;
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

/// <summary>The vmangos Silithus war: transport-day troops, formations, Cenarion Hold waves and Saurfang, on the saved war deadline.</summary>
public sealed class WarEffortSceneTests
{
    private const long End = 1_900_000_000;
    private const long WarStart = End - WarEffortCatalog.TenHourWarSeconds;
    private static WarEffortSnapshot War => new(WarEffortPhase.TenHourWar, End, new long[WarEffortCatalog.ResourceCount]);
    private static WarEffortSnapshot Moving(long deadline) => new(WarEffortPhase.Transporting, deadline, new long[WarEffortCatalog.ResourceCount]);

    [Fact]
    public void ScenesFollowTheVmangosStageTimesFromTheWarStart()
    {
        Assert.Equal(WarEffortScene.None, SceneAt(War, WarStart));
        Assert.Equal(WarEffortScene.None, SceneAt(War, WarStart + 4 * 3600 - 1));
        Assert.Equal(WarEffortScene.CenarionHoldAttack, SceneAt(War, WarStart + 4 * 3600));
        Assert.Equal(WarEffortScene.CenarionHoldAttack, SceneAt(War, WarStart + 8 * 3600 - 1));
        Assert.Equal(WarEffortScene.FinalBattle, SceneAt(War, WarStart + 8 * 3600));
        Assert.Equal(WarEffortScene.FinalBattle, SceneAt(War, End - 1));
        Assert.Equal(WarEffortScene.None, SceneAt(War, End));
        Assert.Equal(WarEffortScene.None, SceneAt(Moving(End), End - 100)); // no longer in the transport
        Assert.Equal(WarEffortScene.None, SceneAt(War with { PhaseEndsAtUnix = 0 }, End - 100));
        Assert.Equal(WarEffortScene.None, SceneAt(War with { Phase = WarEffortPhase.Done }, End - 100));
    }

    [Fact]
    public void TransportDayEventsAddOneDayAtATimeAndStayUntilTheWarIsOver()
    {
        long deadline = 1_800_000_000, start = deadline - 5 * 86_400;
        Assert.Equal(1, TransportDaysActive(Moving(deadline), start));
        Assert.Equal(1, TransportDaysActive(Moving(deadline), start + 86_399));
        Assert.Equal(2, TransportDaysActive(Moving(deadline), start + 86_400));
        Assert.Equal(5, TransportDaysActive(Moving(deadline), start + 4 * 86_400));
        Assert.Equal(5, TransportDaysActive(Moving(deadline), deadline + 10));
        Assert.Equal(5, TransportDaysActive(War with { Phase = WarEffortPhase.Gong, PhaseEndsAtUnix = 0 }, End));
        Assert.Equal(5, TransportDaysActive(War, End - 1));
        Assert.Equal(0, TransportDaysActive(War, End));
        Assert.Equal(0, TransportDaysActive(War with { Phase = WarEffortPhase.Done }, End - 1));
        Assert.Equal(0, TransportDaysActive(War with { Phase = WarEffortPhase.Gathering }, End - 1));
        Assert.Equal(0, TransportDaysActive(Moving(0), End));
    }

    [Fact]
    public void CatalogHoldsTheVmangosTroopEventsAndEveryEntryOnce()
    {
        Assert.Equal(98, WarEffortTroopCatalog.Creatures.Count(t => t.Event is >= 54 and <= 58));
        Assert.Equal(8, WarEffortTroopCatalog.Creatures.Count(t => t.Event == 61));
        Assert.Equal(32, WarEffortTroopCatalog.GameObjects.Count);
        // vmangos day 1 has 19 creatures; its Saurfang (custom entry 987000) is spawned by the scene, not the catalog.
        Assert.Equal([18, 24, 24, 19, 13], Enumerable.Range(54, 5).Select(e => WarEffortTroopCatalog.Creatures.Count(t => t.Event == e)).ToArray());
        Assert.Equal(WarEffortTroopCatalog.Creatures.Count, WarEffortTroopCatalog.Creatures.Select(t => t.Guid).Distinct().Count());
        Assert.Equal(10, WarEffortTroopCatalog.Creatures.Count(t => t.Entry == Priestess));
        Assert.DoesNotContain(WarEffortTroopCatalog.Creatures, t => t.Entry == Saurfang);
    }

    [Fact]
    public void ElevenWavesFifteenMinutesApartAfterAOneMinuteDelay()
    {
        long start = WarStart + AttackAfterWarStartSeconds;
        Assert.Equal(-1, LatestWaveDue(War, start + 59));
        Assert.Equal(0, LatestWaveDue(War, start + 60));
        Assert.Equal(0, LatestWaveDue(War, start + 60 + 899));
        Assert.Equal(1, LatestWaveDue(War, start + 60 + 900));
        Assert.Equal(10, LatestWaveDue(War, start + 60 + 10 * 900));
        Assert.Equal(10, LatestWaveDue(War, WarStart + FinalBattleAfterWarStartSeconds - 1));
        Assert.Equal(-1, LatestWaveDue(War, WarStart + FinalBattleAfterWarStartSeconds));
    }

    [Fact]
    public void SaurfangSpeaksFourteenLinesTenSecondsApartAfterTwoMinutes()
    {
        long start = WarStart + FinalBattleAfterWarStartSeconds;
        Assert.Equal(0, SpeechLinesDue(War, start + 119));
        Assert.Equal(1, SpeechLinesDue(War, start + 120));
        Assert.Equal(2, SpeechLinesDue(War, start + 130));
        Assert.Equal(14, SpeechLinesDue(War, start + 120 + 130));
        Assert.Equal(14, SpeechLinesDue(War, End - 1));
        Assert.Equal(14, SaurfangSpeech.Count);
        Assert.Equal(12, GatePath.Count);
    }

    [Fact]
    public void TransportDaysSpawnTheirTroopsAndSaurfangInTheWarRoomAndTheEndRemovesThem()
    {
        long deadline = 1_800_000_000, start = deadline - 5 * 86_400;
        long now = start + 2 * 86_400 + 5; // day 3
        using Rig rig = new(Moving(deadline), () => now);
        rig.World.RunTick(100);
        int day3 = WarEffortTroopCatalog.Creatures.Count(t => t.Event is >= 54 and <= 56);
        Assert.Equal(day3, rig.War.Troops.Count);
        Creature saurfang = Assert.IsType<Creature>(rig.War.SceneSaurfang);
        Assert.IsType<WarEffortFeature.SaurfangWarAi>(saurfang.AI);
        Assert.Equal(FactionMightOfKalimdor, saurfang.FactionTemplate);
        Assert.Equal(SaurfangWarRoom.X, saurfang.X);

        now = start + 4 * 86_400; // day 5: the rest arrive, nothing doubles
        rig.World.RunTick(100);
        Assert.Equal(98, rig.War.Troops.Count);
        Assert.Equal(98, rig.Creatures.Creatures.Count(c => c.Entry != Saurfang));
        rig.World.RunTick(100);
        Assert.Equal(98, rig.Creatures.Creatures.Count(c => c.Entry != Saurfang));

        rig.Store.State = War with { Phase = WarEffortPhase.Done };
        rig.War.Reload();
        rig.World.RunTick(100);
        Assert.Empty(rig.War.Troops);
        Assert.Null(rig.War.SceneSaurfang);
        Assert.Empty(rig.Creatures.Creatures);
    }

    [Fact]
    public void ADeadTroopComesBackOnceItsCorpseIsGone()
    {
        long now = WarStart + 60;
        using Rig rig = new(War, () => now);
        rig.World.RunTick(100);
        (uint guid, Creature first) = rig.War.Troops.First();
        rig.Creatures.Despawn(first);
        rig.World.RunTick(100);
        Assert.NotSame(first, rig.War.Troops[guid]);
        Assert.True(rig.War.Troops[guid].IsInWorld);
    }

    [Fact]
    public void WavesSpawnOncePerDueWaveAndARestartOnlyRestoresTheCurrentWave()
    {
        long start = WarStart + AttackAfterWarStartSeconds;
        long now = start + 60 + 3 * 900 + 5; // a restart during wave 4
        using Rig rig = new(War, () => now);
        rig.World.RunTick(100);
        Assert.Equal(MobsPerWave, rig.War.WaveCreatures.Count);
        Assert.All(rig.War.WaveCreatures, c => Assert.Contains(c.Entry, new[] { ColossalAnubisath, QirajiDestroyer }));
        Assert.All(rig.War.WaveCreatures, c => Assert.Equal(WaveTarget.X, c.Home.X));

        rig.World.RunTick(100);
        Assert.Equal(MobsPerWave, rig.War.WaveCreatures.Count);
        now += 900;
        rig.World.RunTick(100);
        Assert.Equal(2 * MobsPerWave, rig.War.WaveCreatures.Count);
        var ai = Assert.IsType<WarEffortFeature.SaurfangWarAi>(rig.War.SceneSaurfang!.AI);
        Assert.Equal(SaurfangPost.X, rig.War.SceneSaurfang.Home.X); // moved to his wave post
        Assert.False(ai.LastWave);
        now = start + 60 + 10 * 900;
        rig.World.RunTick(100);
        Assert.True(ai.LastWave);
    }

    [Fact]
    public void CenarionHoldAttackTurnsTheFormationsAndTheFinalBattleMakesThemFollowSaurfang()
    {
        long now = WarStart + AttackAfterWarStartSeconds + 5;
        using Rig rig = new(War, () => now);
        rig.World.RunTick(100);
        rig.World.RunTick(100);
        WarEffortTroop ironforge = WarEffortTroopCatalog.Creatures.First(t => t.Entry == IronforgeInfantry);
        Creature dwarf = rig.War.Troops[ironforge.Guid];
        var dwarfAi = Assert.IsType<WarEffortFeature.TroopAi>(dwarf.AI);
        Assert.True(dwarfAi.MovedIntoPosition);
        // clockwise quarter turn about the Ironforge origin
        Assert.Equal(IronforgeOrigin.X + (ironforge.Y - IronforgeOrigin.Y), dwarf.Home.X, 3);
        Assert.Equal(IronforgeOrigin.Y - (ironforge.X - IronforgeOrigin.X), dwarf.Home.Y, 3);

        WarEffortTroop orc = WarEffortTroopCatalog.Creatures.First(t => t.Entry == OrgrimmarInfantry);
        Creature grunt = rig.War.Troops[orc.Guid];
        Assert.Equal(OrgrimmarOrigin.X - (orc.Y - OrgrimmarOrigin.Y), grunt.Home.X, 3);

        Creature[] priestesses = WarEffortTroopCatalog.Creatures.Where(t => t.Entry == Priestess).Select(t => rig.War.Troops[t.Guid]).ToArray();
        Assert.Equal(Enumerable.Range(1, 10), priestesses.Select(p => ((WarEffortFeature.TroopAi)p.AI!).PriestessIndex).Order());
        Assert.Contains(priestesses, p => Math.Abs(p.Home.X - PriestessEnd.X) < 0.01f);

        // A static, unscripted troop entry stays without the formation script.
        WarEffortTroop cavalry = WarEffortTroopCatalog.Creatures.First(t => t.Entry == 15854);
        Assert.IsNotType<WarEffortFeature.TroopAi>(rig.War.Troops[cavalry.Guid].AI);

        now = WarStart + FinalBattleAfterWarStartSeconds + 5;
        rig.World.RunTick(100);
        rig.World.RunTick(100);
        Assert.True(dwarfAi.Following);
        Assert.Equal(PriestessMount, WarEffortFeature.MountOf(priestesses[0]));
        Assert.Equal(8, rig.War.Troops.Values.Count(c => c.Entry is ColossalAnubisath or QirajiDestroyer)); // event 61 at the gate
    }

    [Fact]
    public void FinalBattleResumesSaurfangsSpeechMountedAndImmuneThenRidesTheGatePath()
    {
        long start = WarStart + FinalBattleAfterWarStartSeconds;
        long now = start + 120 + 45; // restart after five lines
        using Rig rig = new(War, () => now);
        rig.World.RunTick(100);
        Creature saurfang = Assert.IsType<Creature>(rig.War.SceneSaurfang);
        var ai = Assert.IsType<WarEffortFeature.SaurfangWarAi>(saurfang.AI);
        ai.OnUpdate(0);
        Assert.Equal(5, ai.Spoken); // earlier lines are not replayed
        Assert.Equal(SaurfangMount, WarEffortFeature.MountOf(saurfang));
        Assert.True((saurfang.UnitFlags & UnitFlags.ImmuneToPlayer) != 0);
        Assert.Empty(rig.War.WaveCreatures);

        now = start + 120 + 130;
        ai.OnUpdate(0);
        Assert.Equal(14, ai.Spoken);
        Assert.Equal("[broadcast_text 11619]", rig.War.LastWorldBroadcast);
        Assert.True((saurfang.UnitFlags & (UnitFlags.ImmuneToPlayer | UnitFlags.ImmuneToNpc)) == 0);
        Assert.True(ai.RidingToGate);
        Assert.Equal(0, ai.NextPoint);
        Assert.True(ai.Moving);
        Assert.Equal(GatePath[0].X, saurfang.Home.X);
        ai.OnMovementInform(MovementGeneratorType.Point, 0);
        ai.OnUpdate(0);
        Assert.Equal(1, ai.NextPoint);
        Assert.Equal(GatePath[1].X, saurfang.Home.X);
        for (int i = 1; i < GatePath.Count; i++)
        {
            ai.OnMovementInform(MovementGeneratorType.Point, (uint)i);
            ai.OnUpdate(0);
        }

        Assert.False(ai.RidingToGate);
        Assert.Equal(0u, WarEffortFeature.MountOf(saurfang)); // dismounts at the gate

        rig.World.RunTick(100); // still alive: not summoned twice
        Assert.Same(saurfang, rig.War.SceneSaurfang);
    }

    [Fact]
    public void SaurfangCombatUnmountsPausesTheRideRollsTheSourceTimersAndCheersTheLastWave()
    {
        long now = WarStart + FinalBattleAfterWarStartSeconds + 120 + 140;
        using Rig rig = new(War, () => now);
        rig.World.RunTick(100);
        Creature saurfang = rig.War.SceneSaurfang!;
        var ai = (WarEffortFeature.SaurfangWarAi)saurfang.AI!;
        ai.OnUpdate(0);
        Assert.True(ai.RidingToGate);
        (uint ms, uint cleave, uint charge, uint roar) = ai.Timers;
        Assert.InRange(ms, 1000u, 15000u);
        Assert.InRange(cleave, 3000u, 9000u);
        Assert.InRange(charge, 0u, 4000u);
        Assert.InRange(roar, 4000u, 12000u);

        Creature mob = rig.Creatures.SummonForInstance(ColossalAnubisath, saurfang.X + 2, saurfang.Y, saurfang.Z, 0)!;
        ai.OnAggro(mob);
        Assert.Equal(0u, WarEffortFeature.MountOf(saurfang));
        ai.OnMovementInform(MovementGeneratorType.Point, 0);
        ai.OnUpdate(0); // paused: no next point while fighting
        Assert.False(ai.Moving);
        Assert.Equal(1, ai.NextPoint);

        ai.LastWave = true;
        Assert.False(ai.OnEnterEvadeMode()); // the engine still evades
        Assert.False(ai.LastWave); // the victory line is said once
        ai.OnReachedHome();
        Assert.Equal(SaurfangMount, WarEffortFeature.MountOf(saurfang)); // remounts on the way
        ai.OnUpdate(0);
        Assert.True(ai.Moving);
        Assert.Equal(GatePath[1].X, saurfang.Home.X);
        Assert.Equal(new[] { 11527, 11528, 11538, 11540, 11541, 11614, 11615, 11616, 11648 }, AggroTexts);
    }

    [Fact]
    public void LaterVmangosMigrationsAreAppliedToTheCatalog()
    {
        WarEffortTroop priestess = WarEffortTroopCatalog.Creatures.Single(t => t.Guid == 113015); // 20230314125943
        Assert.Equal(-6834.13f, priestess.X);
        Assert.Equal(-8046.22f, WarEffortTroopCatalog.Creatures.Single(t => t.Guid == 112831).X); // 20230125042234
        Assert.Equal(-6775.07f, SaurfangWarRoom.X); // 20231111021653
        Assert.DoesNotContain(WarEffortTroopCatalog.GameObjects, g => g.Guid is 220204 or 220205 or 220206);
        Assert.Equal([(8152u, (byte)57), (8153u, (byte)58), (8154u, (byte)56), (8155u, (byte)55)],
            WarEffortTroopCatalog.GameObjects.Where(g => g.Entry == 180744).Select(g => (g.Guid, g.Event)).OrderBy(x => x.Guid));
        Assert.Equal(-6947.34f, WarEffortTroopCatalog.GameObjects.Single(g => g.Guid == 220202).X); // 20241226150330
        Assert.Equal(-6779.12f, WarEffortTroopCatalog.GameObjects.Single(g => g.Guid == 220200).X); // 20241228161610
        Assert.Equal(23, WarEffortTroopCatalog.GameObjects.Count(g => g.Entry == 180714 && g.MapId == 0 && g.Event == 58));
    }

    [Fact]
    public void DayObjectsSpawnOnTheirMapIncludingTheIronforgeCratesOnDayFive()
    {
        long deadline = 1_800_000_000, start = deadline - 5 * 86_400;
        long now = start + 5;
        using Rig rig = new(Moving(deadline), () => now);
        rig.World.RunTick(100);
        Assert.Equal(WarEffortTroopCatalog.GameObjects.Count(g => g.Event == 54), rig.War.TroopObjects.Count);
        Assert.Empty(rig.EkObjects.GameObjects);

        now = start + 4 * 86_400;
        rig.World.RunTick(100);
        Assert.Equal(32, rig.War.TroopObjects.Count);
        Assert.Equal(23, rig.EkObjects.GameObjects.Count(o => o.Entry == 180714));

        rig.Store.State = War with { Phase = WarEffortPhase.Done };
        rig.War.Reload();
        rig.World.RunTick(100);
        Assert.Empty(rig.War.TroopObjects);
        Assert.DoesNotContain(rig.EkObjects.GameObjects, o => o.Entry == 180714 && o.IsSpawned);
    }

    [Fact]
    public void TheClassicDbWarLayoutsSaurfangAndInfantryAreAdoptedNotDoubled()
    {
        CreatureSpawn[] db =
        [
            new() { Guid = 900001, Entry = Saurfang, MapId = 1, X = -6983.31f, Y = 961.757f, Z = 11f },
            new() { Guid = 900002, Entry = IronforgeInfantry, MapId = 1, X = -6960f, Y = 950f, Z = 15f, Orientation = 4f },
            new() { Guid = 900003, Entry = Saurfang, MapId = 1, X = 1565.79f, Y = -4395.27f, Z = 7f }, // Orgrimmar
        ];
        long now = WarStart + 60;
        using Rig rig = new(War, () => now, db);
        rig.World.RunTick(100);
        rig.World.RunTick(100);
        Creature saurfang = Assert.IsType<Creature>(rig.War.SceneSaurfang);
        Assert.Equal(900001u, saurfang.Spawn?.Guid);
        Assert.IsType<WarEffortFeature.SaurfangWarAi>(saurfang.AI);
        Assert.Single(rig.Creatures.Creatures, c => c.Entry == Saurfang && WarEffortFeature.InSilithus(c));
        Assert.DoesNotContain(rig.Creatures.Creatures, c => c.Entry == Saurfang && c.Spawn is null);
        Assert.IsNotType<WarEffortFeature.SaurfangWarAi>(rig.Creatures.Creatures.Single(c => c.Spawn?.Guid == 900003).AI);

        Assert.DoesNotContain(rig.War.Troops.Values, c => c.Entry is IronforgeInfantry or OrgrimmarInfantry);
        Assert.Contains(rig.War.Troops.Values, c => c.Entry == Priestess);
        Creature dbDwarf = rig.Creatures.Creatures.Single(c => c.Spawn?.Guid == 900002);
        Assert.IsType<WarEffortFeature.TroopAi>(dbDwarf.AI);

        now = WarStart + AttackAfterWarStartSeconds + 5;
        rig.World.RunTick(100);
        Assert.Equal(IronforgeOrigin.X + (950f - IronforgeOrigin.Y), dbDwarf.Home.X, 3); // the database soldier turns too
        Assert.Equal(SaurfangPost.X, saurfang.Home.X);
    }

    [Fact]
    public void EachColossusResearcherAppearsOnlyOnceHisColossusIsDead()
    {
        CreatureSpawn[] db =
        [
            new() { Guid = 910001, Entry = 15797, MapId = 1, X = -6826.11f, Y = 813.571f, Z = 51f }, // Zora's
            new() { Guid = 910002, Entry = 15798, MapId = 1, X = -6824.03f, Y = 813.17f, Z = 51f },  // Ashi's
            new() { Guid = 910003, Entry = 15799, MapId = 1, X = -6825.01f, Y = 811.389f, Z = 51f }, // Regal's
        ];
        long now = WarStart + 60;
        using Rig rig = new(War, () => now, db);
        rig.World.RunTick(100);
        Assert.DoesNotContain(rig.Creatures.Creatures, c => WarEffortFeature.ColossusResearchers.Contains(c.Entry));

        rig.Store.State = War with { KilledBossMask = 0b001 }; // Ashi dead
        rig.War.Reload();
        rig.World.RunTick(100);
        Creature nestor = Assert.Single(rig.Creatures.Creatures, c => WarEffortFeature.ColossusResearchers.Contains(c.Entry));
        Assert.Equal(910002u, nestor.Spawn?.Guid);
        rig.World.RunTick(100);
        Assert.Single(rig.Creatures.Creatures, c => WarEffortFeature.ColossusResearchers.Contains(c.Entry)); // not doubled

        rig.Store.State = War with { KilledBossMask = 0b110 }; // a restart with Regal and Zora saved dead
        rig.War.Reload();
        rig.World.RunTick(100);
        Assert.Equal([910001u, 910003u], rig.Creatures.Creatures.Where(c => WarEffortFeature.ColossusResearchers.Contains(c.Entry))
            .Select(c => c.Spawn!.Guid).Order());

        rig.Store.State = War with { Phase = WarEffortPhase.Done, KilledBossMask = 0b111 };
        rig.War.Reload();
        rig.World.RunTick(100);
        Assert.DoesNotContain(rig.Creatures.Creatures, c => WarEffortFeature.ColossusResearchers.Contains(c.Entry));
    }

    [Fact]
    public void AStaticSaurfangSpawnElsewhereKeepsItsOwnAi()
    {
        long now = WarStart + FinalBattleAfterWarStartSeconds + 10;
        using Rig rig = new(War, () => now);
        rig.World.RunTick(100);
        Creature other = rig.Creatures.SummonForInstance(Saurfang, 1920, -4130, 40, 0)!; // Orgrimmar, not the scene
        Assert.IsNotType<WarEffortFeature.SaurfangWarAi>(other.AI);
        Creature grunt = rig.Creatures.SummonForInstance(OrgrimmarInfantry, 1920, -4130, 40, 0)!;
        Assert.IsNotType<WarEffortFeature.TroopAi>(grunt.AI);
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
        public MemoryStore Store { get; }

        public GameObjectMapSystem KalimdorObjects { get; }
        public GameObjectMapSystem EkObjects { get; }

        public Rig(WarEffortSnapshot saved, Func<long> clock, IReadOnlyList<CreatureSpawn>? dbSpawns = null)
        {
            var services = new ServiceCollection();
            Store = new MemoryStore(saved);
            services.AddSingleton(Store);
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
                WarEffortTroopCatalog.Creatures.Select(t => t.Entry).Append(Saurfang).Concat(WarEffortFeature.ColossusResearchers).Distinct().Select(Template).ToArray(),
                dbSpawns ?? [], [], [], []), random: new Random(3));
            kalimdor.AddUpdater(Creatures);
            var objectContent = new GameObjectContent(
                [.. WarEffortTroopCatalog.GameObjects.Select(g => g.Entry).Distinct().Select(ObjectTemplate)], [], [], [], []);
            KalimdorObjects = new GameObjectMapSystem(kalimdor, objectContent);
            kalimdor.AddUpdater(KalimdorObjects);
            Map ek = World.GetMap(0);
            EkObjects = new GameObjectMapSystem(ek, objectContent);
            ek.AddUpdater(EkObjects);
            if (dbSpawns is not null)
            {
                Creatures.RegisterScriptOnlySpawns(dbSpawns.Select(sp => sp.Guid));
                foreach (CreatureSpawn sp in dbSpawns) Creatures.SpawnScripted(sp.Guid);
            }
            _provider.GetRequiredService<GameEventFeature>().Attach(World);
            War = _provider.GetRequiredService<WarEffortFeature>();
            War.UtcNowUnix = clock;
            War.Attach(World);
        }

        private static GameObjectTemplate ObjectTemplate(uint entry) => new()
        {
            Entry = entry, Type = (uint)GameObjectType.Generic, DisplayId = 6500, Name = $"AQ war object {entry}", Size = 1.0f,
            Data = new uint[GameObjectTemplate.DataCount],
        };

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
