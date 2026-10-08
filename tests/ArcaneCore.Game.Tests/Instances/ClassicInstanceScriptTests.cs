using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Classic;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using static ArcaneCore.Game.Tests.Instances.InstanceFixture;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// The ScriptDev2 instance scripts behind classic-db z2815's EventAI ACTION_T_SET_INST_DATA rows (mangos-classic
/// AI/ScriptDevAI/scripts/...), run inside a dungeon instance (the fixture's map 36 stands in for the script's own map) with creatures,
/// their EventAI rows and the instance's doors, so the whole path is exercised: the creature's death, its EventAI row, SetData, the door
/// and the saved state the next instance map loads.
/// </summary>
public sealed class ClassicInstanceScriptTests
{
    private const float EntryX = -16.4f;
    private const float EntryY = -383.07f;
    private const float EntryZ = 61.78f;

    private sealed record Run(InstanceFixture F, Player Player) : IDisposable
    {
        public Map Map => Player.Map!;

        public CreatureMapSystem Creatures => Map.FindUpdater<CreatureMapSystem>()!;

        public GameObjectMapSystem Objects => Map.FindUpdater<GameObjectMapSystem>()!;

        public InstanceData Data => InstanceManager.InstanceDataOf(Map)!;

        public Creature Creature(uint spawnGuid) => Creatures.Creatures.Single(c => c.Spawn?.Guid == spawnGuid);

        public GameObject Object(uint entry) => Objects.GameObjects.Single(g => g.Entry == entry);

        public void Kill(uint spawnGuid) => Map.Combat.Kill(Player, Creature(spawnGuid));

        public void Dispose() => F.Dispose();
    }

    /// <summary>An EventAI row of <paramref name="entry"/>: on death (6), SET_INST_DATA(field, data).</summary>
    private static CreatureAiEvent DeathSetsData(uint id, uint entry, uint field, uint data)
        => new()
        {
            Id = id,
            CreatureId = entry,
            EventType = (byte)EventAiEventType.Death,
            Chance = 100,
            Action1 = new CreatureAiAction(34, (int)field, (int)data, 0),
        };

    private static Run Enter(Func<Map, InstanceData> script, IEnumerable<CreatureAiEvent> rows, IEnumerable<CreatureSpawn> spawns,
        IEnumerable<(uint Entry, float X)> doors)
    {
        var f = new InstanceFixture(new InstanceOptions { UnloadDelayMs = 1000 });
        f.Manager.Scripts = new InstanceScriptRegistry().Register(Dungeon, script);
        CreatureSpawn[] creatureSpawns = [.. spawns];
        uint[] entries = [.. creatureSpawns.Select(s => s.Entry).Distinct()];
        var creatures = new CreatureContent(
            [.. entries.Select(e => Template(e, t => t.AIName = CreatureAiFactory.EventAIName))],
            creatureSpawns, [], [], [], new CreatureAiContent(rows, []));
        (uint Entry, float X)[] doorList = [.. doors];
        var objects = new GameObjectContent(
            [.. doorList.Select(d => GameObjectTestKit.GoTemplate(d.Entry, GameObjectType.Door))],
            [.. doorList.Select((d, i) => GameObjectTestKit.GoSpawn(500 + (uint)i, d.Entry, d.X, EntryY + 5) with { MapId = Dungeon, Z = EntryZ })],
            [], [], []);
        f.World.MapCreated += map =>
        {
            if (map.MapId == Dungeon)
            {
                map.AddUpdater(new CreatureMapSystem(map, creatures, random: new Random(1), aiServices: new CreatureAiServices()));
                map.AddUpdater(new GameObjectMapSystem(map, objects));
            }
        };

        Player player = f.AddPlayer(1);
        Assert.True(f.EnterDungeon(player));
        f.Tick();
        return new Run(f, player);
    }

    private static CreatureSpawn At(uint guid, uint entry, float dx) => Spawn(guid, entry, EntryX + dx, EntryY, EntryZ, mapId: Dungeon);

    private static void ReEnter(Run run)
    {
        Map first = run.Map;
        run.F.LeaveToContinent(run.Player);
        run.F.Tick(600);
        run.F.Tick(600);
        Assert.True(first.IsUnloaded);
        Assert.True(run.F.EnterDungeon(run.Player));
        run.F.Tick();
    }

    // --- Razorfen Kraul: the ward keepers ------------------------------------------------------

    [Fact]
    public void RazorfenKraul_TheWardOpens_WhenTheLastCountedWardKeeperDies_AndStaysOpenInTheNextInstanceMap()
    {
        const uint Keeper = RazorfenKraulInstance.NpcWardKeeper;
        using Run run = Enter(map => new RazorfenKraulInstance(map),
            [DeathSetsData(462501, Keeper, RazorfenKraulInstance.TypeAgathelos, EncounterState.Done)],
            [At(1, Keeper, 3), At(2, Keeper, 6)],
            [(RazorfenKraulInstance.GoAgathelosWard, EntryX + 4)]);
        var data = (RazorfenKraulInstance)run.Data;
        Assert.Equal(2u, data.WardKeepersRemaining);

        run.Kill(1);
        Assert.Equal(GameObjectState.Ready, run.Object(RazorfenKraulInstance.GoAgathelosWard).State);
        Assert.Equal(EncounterState.NotStarted, data.GetData(RazorfenKraulInstance.TypeAgathelos));

        run.Kill(2);
        Assert.Equal(GameObjectState.Active, run.Object(RazorfenKraulInstance.GoAgathelosWard).State);
        Assert.Equal(EncounterState.Done, data.GetData(RazorfenKraulInstance.TypeAgathelos));
        Assert.Equal("3", run.F.Manager.FindSave(run.Map.InstanceId)!.Data);

        ReEnter(run);
        Assert.Equal(EncounterState.Done, run.Data.GetData(RazorfenKraulInstance.TypeAgathelos));
        Assert.Equal(GameObjectState.Active, run.Object(RazorfenKraulInstance.GoAgathelosWard).State); // created open (OnObjectCreate)
    }

    // --- Shadowfang Keep: Arugal's door ---------------------------------------------------------------

    [Fact]
    public void ShadowfangKeep_ArugalsDoorOpens_WhenWolfMasterNandosDies()
    {
        const uint Nandos = 3927;
        using Run run = Enter(map => new ShadowfangKeepInstance(map),
            [DeathSetsData(392705, Nandos, ShadowfangKeepInstance.TypeNandos, EncounterState.Done)],
            [At(1, Nandos, 3)],
            [(ShadowfangKeepInstance.GoArugalDoor, EntryX + 4), (ShadowfangKeepInstance.GoCourtyardDoor, EntryX + 8)]);

        run.Kill(1);

        Assert.Equal(GameObjectState.Active, run.Object(ShadowfangKeepInstance.GoArugalDoor).State);
        Assert.Equal(GameObjectState.Ready, run.Object(ShadowfangKeepInstance.GoCourtyardDoor).State);
        Assert.Equal(EncounterState.Done, run.Data.GetData(ShadowfangKeepInstance.TypeNandos));
        Assert.Equal("0 0 0 3 0 0", run.F.Manager.FindSave(run.Map.InstanceId)!.Data);
    }

    [Fact]
    public void ShadowfangKeep_TheSorcerersDoorOpensAfterTheFourthVoidwalker()
    {
        using Run run = Enter(map => new ShadowfangKeepInstance(map), [], [], [(ShadowfangKeepInstance.GoSorcererDoor, EntryX + 4)]);
        for (int i = 0; i < 3; i++)
        {
            run.Data.SetData(ShadowfangKeepInstance.TypeVoidwalker, EncounterState.Done);
        }

        Assert.Equal(GameObjectState.Ready, run.Object(ShadowfangKeepInstance.GoSorcererDoor).State);
        run.Data.SetData(ShadowfangKeepInstance.TypeVoidwalker, EncounterState.Done);
        Assert.Equal(GameObjectState.Active, run.Object(ShadowfangKeepInstance.GoSorcererDoor).State);
        Assert.Equal(0u, run.Data.GetData(ShadowfangKeepInstance.TypeVoidwalker)); // not readable, as in the original
    }

    // --- Blackrock Depths: the Tomb of the Seven ------------------------------------------------------

    [Fact]
    public void BlackrockDepths_TheTomb_IgnoresARepeatedValue_UsesTheEntranceDoorOnEachChange_AndFailRespawnsTheDeadDwarves()
    {
        const uint Anger = 9035, Hate = 9034;
        using Run run = Enter(map => new BlackrockDepthsInstance(map), [], [At(1, Anger, 3), At(2, Hate, 6)],
            [(BlackrockDepthsInstance.GoTombEnter, EntryX + 4), (BlackrockDepthsInstance.GoTombExit, EntryX + 8)]);
        GameObject enter = run.Object(BlackrockDepthsInstance.GoTombEnter);
        InstanceData data = run.Data;

        data.SetData(BlackrockDepthsInstance.TypeTombOfSeven, EncounterState.InProgress);
        Assert.Equal(GameObjectState.Active, enter.State);
        data.SetData(BlackrockDepthsInstance.TypeTombOfSeven, EncounterState.InProgress); // "Don't set the same data twice"
        Assert.Equal(GameObjectState.Active, enter.State);

        run.Kill(2);
        for (int i = 0; i < 60 && run.Creature(2).DeathState != CreatureDeathState.Dead; i++)
        {
            run.F.Tick(10_000); // the corpse decays: the dwarf is out of the map but still the creature system's
        }

        Assert.Equal(CreatureDeathState.Dead, run.Creature(2).DeathState);
        Assert.Null(run.Map.FindObject(run.Creature(2).Guid));
        run.Kill(1); // a fresh corpse
        Assert.Equal(CreatureDeathState.Corpse, run.Creature(1).DeathState);

        data.SetData(BlackrockDepthsInstance.TypeTombOfSeven, EncounterState.Fail); // the EventAI row of a dwarf that reached home
        Assert.Equal(GameObjectState.Ready, enter.State);
        Assert.True(run.Creature(2).IsAlive);
        Assert.True(run.Creature(1).IsAlive);
        Assert.Equal(EncounterState.Fail, data.GetData(BlackrockDepthsInstance.TypeTombOfSeven));

        data.SetData(BlackrockDepthsInstance.TypeTombOfSeven, EncounterState.Done);
        Assert.Equal(GameObjectState.Active, run.Object(BlackrockDepthsInstance.GoTombExit).State);
        Assert.Equal("0 0 0 3 0 0 0 0 0 0 0 0 0", run.F.Manager.FindSave(run.Map.InstanceId)!.Data);
    }

    [Fact]
    public void BlackrockDepths_Load_KeepsIndexSixInProgress_AsTheOriginalComparesTheIndexWithTypeIronHall()
    {
        using InstanceFixture f = new();
        var data = new BlackrockDepthsInstance(f.World.GetMap(230, 4242));
        data.Initialize();
        data.Load("1 1 1 1 1 1 1 1 1 1 1 1 1");
        Assert.Equal([0u, 0u, 0u, 0u, 0u, 0u, 1u, 0u, 0u, 0u, 0u, 0u, 0u], data.EncounterStates);
    }

    // --- Dire Maul: Alzzin --------------------------------------------------------------------------------

    [Fact]
    public void DireMaul_AlzzinSpecialBreaksTheWallOnce_AndDoneOpensTheVine()
    {
        using Run run = Enter(map => new DireMaulInstance(map), [], [],
            [(DireMaulInstance.GoCrumbleWall, EntryX + 4), (DireMaulInstance.GoCorruptVine, EntryX + 8)]);
        var data = (DireMaulInstance)run.Data;

        data.SetData(DireMaulInstance.TypeAlzzin, EncounterState.Special); // the 40% health row
        Assert.Equal(GameObjectState.Active, run.Object(DireMaulInstance.GoCrumbleWall).State);
        Assert.True(data.WallDestroyed);
        Assert.Null(run.F.Manager.FindSave(run.Map.InstanceId)!.Data);

        data.SetData(DireMaulInstance.TypeAlzzin, EncounterState.Done); // the death row: the wall is not used again
        Assert.Equal(GameObjectState.Active, run.Object(DireMaulInstance.GoCrumbleWall).State);
        Assert.Equal(GameObjectState.Active, run.Object(DireMaulInstance.GoCorruptVine).State);
        Assert.StartsWith("3 0 0", run.F.Manager.FindSave(run.Map.InstanceId)!.Data, StringComparison.Ordinal);

        ReEnter(run);
        Assert.True(((DireMaulInstance)run.Data).WallDestroyed);
        Assert.Equal(GameObjectState.Active, run.Object(DireMaulInstance.GoCrumbleWall).State);
        Assert.Equal(GameObjectState.Active, run.Object(DireMaulInstance.GoCorruptVine).State);
    }

    // --- state-only scripts -------------------------------------------------------------------------------

    [Fact]
    public void WailingCaverns_TheDiscipleBecomesSpecial_OnceTheFourFanglordsAreDone()
    {
        using InstanceFixture f = new();
        var data = new WailingCavernsInstance(f.World.GetMap(WailingCavernsInstance.MapId, 4242));
        data.Initialize();
        foreach (uint fanglord in new[] { WailingCavernsInstance.TypeAnacondra, WailingCavernsInstance.TypeCobrahn, WailingCavernsInstance.TypePythas })
        {
            data.SetData(fanglord, EncounterState.Done);
        }

        Assert.Equal(EncounterState.NotStarted, data.GetData(WailingCavernsInstance.TypeDisciple));
        data.SetData(WailingCavernsInstance.TypeSerpentis, EncounterState.Done);
        Assert.Equal(EncounterState.Special, data.GetData(WailingCavernsInstance.TypeDisciple));
        Assert.Equal("3 3 3 3 4 0", data.GetSaveData());
    }

    [Fact]
    public void BlackfathomDeeps_KelrisOnlyEverBecomesDone()
    {
        using InstanceFixture f = new();
        var data = new BlackfathomDeepsInstance(f.World.GetMap(BlackfathomDeepsInstance.MapId, 4242));
        data.Initialize();
        data.SetData(BlackfathomDeepsInstance.TypeKelris, EncounterState.InProgress);
        Assert.Equal(EncounterState.NotStarted, data.GetData(BlackfathomDeepsInstance.TypeKelris));
        data.SetData(BlackfathomDeepsInstance.TypeKelris, EncounterState.Done);
        data.SetData(BlackfathomDeepsInstance.TypeKelris, EncounterState.Fail);
        Assert.Equal(EncounterState.Done, data.GetData(BlackfathomDeepsInstance.TypeKelris));
    }

    [Fact]
    public void SunkenTemple_AvatarSpecialChangesNoState_AndLoadKeepsOnlyDone()
    {
        using InstanceFixture f = new();
        var data = new SunkenTempleInstance(f.World.GetMap(SunkenTempleInstance.MapId, 4242));
        data.Initialize();
        data.SetData(SunkenTempleInstance.TypeAvatar, EncounterState.Special);
        Assert.Equal(EncounterState.NotStarted, data.GetData(SunkenTempleInstance.TypeAvatar));

        data.Load("3 2 4 1 3");
        Assert.Equal([3u, 0u, 0u, 0u, 3u], data.EncounterStates);
    }

    [Fact]
    public void ZulGurub_OhganKeepsTheSpecialAVilebranchSpeakerSets()
    {
        using InstanceFixture f = new();
        var data = new ZulGurubInstance(f.World.GetMap(ZulGurubInstance.MapId, 4242));
        data.Initialize();
        data.SetData(ZulGurubInstance.TypeOhgan, EncounterState.Special);
        Assert.Equal(EncounterState.Special, data.GetData(ZulGurubInstance.TypeOhgan));
        Assert.Equal(0u, data.GetData(0));
    }
}
