using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Classic;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>Dire Maul East and West instance states (Zevrim, Ironbark, Warpwood, Immol'thar, Prince, pylons) in DireMaulWest.cs.</summary>
public sealed class DireMaulInstanceTests
{
    private const uint Dungeon = InstanceFixture.Dungeon;
    private const float Y = -383.07f, Z = 61.78f;

    private sealed record Run(InstanceFixture Fixture) : IDisposable
    {
        public Map Map => Fixture.World.Maps.First(m => m.MapId == Dungeon);
        public DireMaulInstance Data => Assert.IsType<DireMaulInstance>(InstanceManager.InstanceDataOf(Map));
        public CreatureMapSystem Creatures => Map.FindUpdater<CreatureMapSystem>()!;
        public GameObjectMapSystem Objects => Map.FindUpdater<GameObjectMapSystem>()!;
        public Creature Npc(uint entry, int index = 0) => Creatures.Creatures.Where(c => c.Template.Entry == entry).ElementAt(index);
        public GameObject Go(uint entry) => Objects.GameObjects.Single(go => go.Entry == entry);
        public void Dispose() => Fixture.Dispose();
    }

    private static Run Enter()
    {
        var fixture = new InstanceFixture();
        fixture.Manager.Scripts = new InstanceScriptRegistry().Register(Dungeon, map => new DireMaulInstance(map));
        uint[] entries =
        [
            DireMaulInstance.NpcArcaneAberration, DireMaulInstance.NpcManaRemnant, DireMaulInstance.NpcImmolthar,
            DireMaulInstance.NpcPrinceTortheldrin, DireMaulInstance.NpcHighborneSummoner, DireMaulInstance.NpcZevrim,
            DireMaulInstance.NpcOldIronbark, DireMaulInstance.NpcIronbarkRedeemed, DireMaulInstance.NpcTendris,
        ];
        var spawns = new List<CreatureSpawn>();
        uint guid = 1;
        // Two guards (one of each entry) beside every generator, 50 yards apart.
        for (int i = 0; i < 5; i++)
        {
            spawns.Add(Spawn(guid++, DireMaulInstance.NpcArcaneAberration, i * 50f + 3f, Y, Z, mapId: Dungeon));
            spawns.Add(Spawn(guid++, DireMaulInstance.NpcManaRemnant, i * 50f - 3f, Y, Z, mapId: Dungeon));
        }

        foreach (uint entry in new[] { DireMaulInstance.NpcImmolthar, DireMaulInstance.NpcPrinceTortheldrin, DireMaulInstance.NpcHighborneSummoner,
                     DireMaulInstance.NpcOldIronbark, DireMaulInstance.NpcZevrim, DireMaulInstance.NpcTendris })
        {
            spawns.Add(Spawn(guid++, entry, 10f + guid, Y + 30f, Z, mapId: Dungeon));
        }

        var creatures = new CreatureContent([.. entries.Select(e => Template(e))], [.. spawns], [], [], []);

        var goRows = new List<GameObjectSpawn>();
        uint goGuid = 1;
        for (int i = 0; i < 5; i++)
        {
            goRows.Add(GameObjectTestKit.GoSpawn(goGuid++, DireMaulInstance.CrystalGenerators[i], i * 50f, Y) with { MapId = Dungeon, Z = Z });
        }

        uint[] others = [DireMaulInstance.GoForcefield, DireMaulInstance.GoWarpwoodDoor, DireMaulInstance.GoPrincesChestAura];
        foreach (uint entry in others)
        {
            goRows.Add(GameObjectTestKit.GoSpawn(goGuid++, entry, 10f + goGuid, Y + 40f) with { MapId = Dungeon, Z = Z });
        }

        var templates = DireMaulInstance.CrystalGenerators.Concat(others)
            .Select(e => GameObjectTestKit.GoTemplate(e, GameObjectType.Door)).ToArray();
        var objects = new GameObjectContent(templates, [.. goRows], [], [], []);
        fixture.World.MapCreated += map =>
        {
            if (map.MapId == Dungeon)
            {
                map.AddUpdater(new CreatureMapSystem(map, creatures, random: new Random(1)));
                map.AddUpdater(new GameObjectMapSystem(map, objects));
            }
        };

        var player = fixture.AddPlayer(1);
        Assert.True(fixture.EnterDungeon(player));
        fixture.Tick();
        return new Run(fixture);
    }

    [Fact]
    public void PylonGuards_LastDeathPerGenerator_OpensItsGeneratorAndTheForceFieldAfterAllFive()
    {
        using Run run = Enter();
        Assert.NotEqual(GameObjectState.Active, run.Go(DireMaulInstance.GoForcefield).State);

        for (int i = 0; i < 5; i++)
        {
            Creature first = run.Npc(DireMaulInstance.NpcArcaneAberration, i);
            Creature second = run.Npc(DireMaulInstance.NpcManaRemnant, i);
            run.Map.Combat.Kill(null, first);
            run.Data.OnCreatureDeath(first);
            Assert.NotEqual(EncounterState.Done, run.Data.GetData(DireMaulInstance.TypePylon1 + (uint)i)); // one guard still lives
            run.Map.Combat.Kill(null, second);
            run.Data.OnCreatureDeath(second);
            Assert.Equal(EncounterState.Done, run.Data.GetData(DireMaulInstance.TypePylon1 + (uint)i));
            Assert.Equal(GameObjectState.Active, run.Go(DireMaulInstance.CrystalGenerators[i]).State);
            Assert.Equal(i == 4, run.Go(DireMaulInstance.GoForcefield).State == GameObjectState.Active);
        }
    }

    [Fact]
    public void ImmolTharDeath_MakesThePrinceHostile_AndTendrisOpensTheWarpwoodDoor()
    {
        using Run run = Enter();
        Creature prince = run.Npc(DireMaulInstance.NpcPrinceTortheldrin);
        uint friendly = prince.FactionTemplate;
        Assert.NotEqual(DireMaulInstance.FactionHostile, friendly);

        run.Data.OnCreatureDeath(run.Npc(DireMaulInstance.NpcImmolthar));
        Assert.Equal(EncounterState.Done, run.Data.GetData(DireMaulInstance.TypeImmolthar));
        Assert.Equal(DireMaulInstance.FactionHostile, prince.FactionTemplate);

        Assert.NotEqual(GameObjectState.Active, run.Go(DireMaulInstance.GoWarpwoodDoor).State);
        run.Data.OnCreatureDeath(run.Npc(DireMaulInstance.NpcTendris));
        Assert.Equal(EncounterState.Done, run.Data.GetData(DireMaulInstance.TypeWarpwood));
        Assert.Equal(GameObjectState.Active, run.Go(DireMaulInstance.GoWarpwoodDoor).State);
    }

    [Fact]
    public void ZevrimDeath_RedeemsOldIronbark_AndTheRedeemedsDeathSetsIronbark()
    {
        using Run run = Enter();
        run.Data.OnCreatureDeath(run.Npc(DireMaulInstance.NpcZevrim));
        Assert.Equal(EncounterState.Done, run.Data.GetData(DireMaulInstance.TypeZevrim));
        Assert.Contains(run.Creatures.Creatures, c => c.Entry == DireMaulInstance.NpcIronbarkRedeemed);

        run.Data.SetData(DireMaulInstance.TypeIronbark, EncounterState.Done);
        Assert.Equal(EncounterState.Done, run.Data.GetData(DireMaulInstance.TypeIronbark));
    }
}
