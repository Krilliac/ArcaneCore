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

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// mangos-classic zulgurub.cpp SetData: TYPE_MARLI FAIL respawns every stored spider egg and TYPE_ARLOKK FAIL respawns the gong of Bethekk.
/// </summary>
public sealed class ZulGurubFailRespawnTests
{
    private const uint Dungeon = InstanceFixture.Dungeon;
    private const uint TypeMarli = 2, TypeArlokk = 4;

    private sealed record Run(InstanceFixture Fixture) : IDisposable
    {
        public Map Map => Fixture.World.Maps.First(m => m.MapId == Dungeon);
        public ZulGurubInstance Data => Assert.IsType<ZulGurubInstance>(InstanceManager.InstanceDataOf(Map));
        public GameObjectMapSystem Objects => Map.FindUpdater<GameObjectMapSystem>()!;
        public GameObject Go(uint entry) => Objects.GameObjects.Single(go => go.Entry == entry);
        public void Dispose() => Fixture.Dispose();
    }

    private static Run Enter()
    {
        var fixture = new InstanceFixture();
        fixture.Manager.Scripts = new InstanceScriptRegistry().Register(Dungeon, map => new ZulGurubInstance(map));
        uint[] entries = [ZulGurubInstance.GoSpiderEgg, ZulGurubInstance.GoGongOfBethekk];
        var creatures = new CreatureContent([], [], [], [], []);
        var rows = entries.Select((entry, i) => GameObjectTestKit.GoSpawn((uint)i + 1, entry, -10f + (i * 5f), -383.07f) with { MapId = Dungeon, Z = 61.78f });
        // door data3 (noDamageImmune) set, so GetDespawnPossibility is true and the spawn is not NODESPAWN (GameObject::LoadFromDB,
        // GameObject.cpp:985-991): a door template without it never despawns, and Despawn below would leave the object in the world.
        var templates = entries.Select(e => GameObjectTestKit.GoTemplate(e, GameObjectType.Door, (3, 1))).ToArray();
        var objects = new GameObjectContent(templates, [.. rows], [], [], []);
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
    public void MarliFail_RespawnsADespawnedEgg_AndReadiesAUsedOne()
    {
        using Run run = Enter();
        GameObject egg = run.Go(ZulGurubInstance.GoSpiderEgg);
        Assert.Contains(egg.Guid, run.Data.SpiderEggs);
        egg.State = GameObjectState.Active;

        run.Data.SetData(TypeMarli, EncounterState.Fail);
        Assert.True(egg.IsSpawned);
        Assert.Equal(GameObjectState.Ready, egg.State);

        run.Objects.Despawn(egg);
        Assert.False(egg.IsSpawned);
        run.Data.SetData(TypeMarli, EncounterState.NotStarted);
        run.Data.SetData(TypeMarli, EncounterState.Fail);
        Assert.True(egg.IsSpawned);
    }

    [Fact]
    public void MarliDone_DoesNotRespawnTheEggs()
    {
        using Run run = Enter();
        GameObject egg = run.Go(ZulGurubInstance.GoSpiderEgg);
        run.Objects.Despawn(egg);

        run.Data.SetData(TypeMarli, EncounterState.Done);
        Assert.False(egg.IsSpawned);
    }

    [Fact]
    public void ArlokkFail_RespawnsADespawnedGong_ButDoneDoesNot()
    {
        using Run run = Enter();
        GameObject gong = run.Go(ZulGurubInstance.GoGongOfBethekk);
        run.Objects.Despawn(gong);
        Assert.False(gong.IsSpawned);

        run.Data.SetData(TypeArlokk, EncounterState.Done);
        Assert.False(gong.IsSpawned);

        run.Data.SetData(TypeArlokk, EncounterState.Fail);
        Assert.True(gong.IsSpawned);
    }
}
