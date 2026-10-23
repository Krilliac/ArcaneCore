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
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>Dire Maul North's Fixed Trap (quest 1193, go_fixed_trap, GuardSlipkikTrigger) in DireMaulSlipkikTrap.cs.</summary>
public sealed class DireMaulSlipkikTrapTests
{
    private const float Y = -383.07f, Z = 61.78f;

    private sealed record Run(InstanceFixture Fixture, Player Player) : IDisposable
    {
        public Map Map => Player.Map!;
        public DireMaulInstance Data => Assert.IsType<DireMaulInstance>(InstanceManager.InstanceDataOf(Map));
        public CreatureMapSystem Creatures => Map.FindUpdater<CreatureMapSystem>()!;
        public GameObjectMapSystem Objects => Map.FindUpdater<GameObjectMapSystem>()!;
        public Creature Slipkik => Creatures.Creatures.Single(c => c.Entry == DireMaulInstance.NpcSlipkik);
        public IEnumerable<GameObject> FixedTraps => Objects.GameObjects.Where(go => go.Entry == DireMaulInstance.GoFixedTrap);
        public void Dispose() => Fixture.Dispose();
    }

    private static Run Enter(float slipkikX, bool withTrapRow)
    {
        var fixture = new InstanceFixture();
        fixture.Manager.Scripts = new InstanceScriptRegistry().Register(InstanceFixture.Dungeon, map => new DireMaulInstance(map));
        CreatureSpawn[] spawns = [Spawn(1, DireMaulInstance.NpcSlipkik, slipkikX, Y, Z, mapId: InstanceFixture.Dungeon)];
        var creatures = new CreatureContent([Template(DireMaulInstance.NpcSlipkik)], spawns, [], [], []);
        GameObjectSpawn[] rows = withTrapRow
            ? [GameObjectTestKit.GoSpawn(4290049, DireMaulInstance.GoFixedTrap, 0, Y, spawnTimeSeconds: -7200) with { MapId = InstanceFixture.Dungeon, Z = Z }]
            : [];
        var objects = new GameObjectContent(
            [GameObjectTestKit.GoTemplate(DireMaulInstance.GoBrokenTrap, GameObjectType.Goober),
             GameObjectTestKit.GoTemplate(DireMaulInstance.GoFixedTrap, GameObjectType.Trap)], rows, [], [], []);
        fixture.World.MapCreated += map =>
        {
            if (map.MapId == InstanceFixture.Dungeon)
            {
                map.AddUpdater(new CreatureMapSystem(map, creatures, random: new Random(1)));
                map.AddUpdater(new GameObjectMapSystem(map, objects));
            }
        };

        Player player = fixture.AddPlayer(1);
        Assert.True(fixture.EnterDungeon(player));
        fixture.Tick();
        return new Run(fixture, player);
    }

    [Fact]
    public void BrokenTrapReward_ShowsTheFixedTrapRow_OnceAndSlipkikSpringsIt()
    {
        using Run run = Enter(slipkikX: 1f, withTrapRow: true);
        Assert.DoesNotContain(run.FixedTraps, go => go.IsSpawned);
        run.Fixture.Tick();
        Assert.NotEqual(DireMaulInstance.FactionHostile, run.Slipkik.FactionTemplate); // a trap that is not up does nothing
        Assert.Equal(0u, (uint)(run.Slipkik.UnitFlags & UnitFlags.ImmuneToPlayer));

        GameObject broken = run.Objects.Summon(DireMaulInstance.GoBrokenTrap, 0, Y, Z, 0)!;
        run.Data.OnGameObjectQuestRewarded(run.Player, broken, DireMaulInstance.QuestABrokenTrap);
        run.Data.OnGameObjectQuestRewarded(run.Player, broken, DireMaulInstance.QuestABrokenTrap);
        GameObject trap = Assert.Single(run.FixedTraps);
        Assert.True(trap.IsSpawned);
        Assert.Equal(4290049u, trap.Spawn!.Guid);

        run.Fixture.Tick();
        Assert.Equal(DireMaulInstance.FactionHostile, run.Slipkik.FactionTemplate);
        Assert.NotEqual(0u, (uint)(run.Slipkik.UnitFlags & UnitFlags.ImmuneToPlayer));
        Assert.True(trap.LootState == GameObjectLootState.JustDeactivated || !trap.IsSpawned);

        // King of the Gordok's trigger (31770) gives him the ogre faction back.
        run.Data.RestoreSlipkikFaction();
        Assert.Equal(DireMaulInstance.FactionOgre, run.Slipkik.FactionTemplate);
    }

    [Fact]
    public void FixedTrap_IgnoresSlipkikOutOfReach_AndOtherQuests_AndIsSummonedWithoutARow()
    {
        using Run run = Enter(slipkikX: 12f, withTrapRow: false);
        GameObject broken = run.Objects.Summon(DireMaulInstance.GoBrokenTrap, 0, Y, Z, 0)!;
        run.Data.OnGameObjectQuestRewarded(run.Player, broken, 1194);
        Assert.Empty(run.FixedTraps);

        run.Data.OnGameObjectQuestRewarded(run.Player, broken, DireMaulInstance.QuestABrokenTrap);
        GameObject trap = Assert.Single(run.FixedTraps);
        Assert.True(trap.IsSpawned);
        Assert.Null(trap.Spawn);

        run.Fixture.Tick();
        run.Fixture.Tick();
        Assert.True(trap.IsSpawned);
        Assert.NotEqual(GameObjectLootState.JustDeactivated, trap.LootState);
        Assert.Equal(0u, (uint)(run.Slipkik.UnitFlags & UnitFlags.ImmuneToPlayer));
    }
}
