using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.SpawnGroups;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;
using static ArcaneCore.Game.Tests.SpawnGroups.ClassicDbSpawnGroupRows;

namespace ArcaneCore.Game.Tests.SpawnGroups;

/// <summary>
/// Spawn groups and alternative entries in the creature and game object map systems, over classic-db z2815 rows
/// (<see cref="ClassicDbSpawnGroupRows"/>): a member exists only when its group chose it, with the entry the group chose (cmangos
/// Maps/SpawnGroup.cpp, Entities/Creature.cpp:1632-1640, Entities/GameObject.cpp:898-908).
/// </summary>
public sealed class SpawnGroupMapTests
{
    private static CreatureContent GroupContent(
        IEnumerable<CreatureTemplate> templates, IEnumerable<CreatureSpawn> spawns, SpawnGroupDefinition group, IEnumerable<(uint, uint)>? spawnEntries = null)
        => new(templates, spawns, [], [], [], spawnEntries: spawnEntries) { SpawnGroups = new SpawnGroupCatalog([group]) };

    private static Creature[] Members(CreatureMapSystem system, IEnumerable<uint> guids)
        => [.. system.Creatures.Where(c => c.Spawn is { } s && guids.Contains(s.Guid))];

    private static CreatureOptions ShortCorpses() => new() { CorpseDecayNormalSeconds = 60 };

    // --- creatures --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void QirajiMajors_EntryZeroSpawnsWithoutSpawnEntryRows_BecomeTheGroupsEntries()
    {
        // Four of the 568 classic-db creatures that only a spawn group resolves: before spawn groups they were skipped ("missing template 0").
        CreatureContent content = GroupContent(
            [Template(QirajiMajor), Template(MajorHealie)], QirajiSpawns.Select((g, i) => Spawn(g, 0, 5 + (i * 3), 0, respawnSeconds: 600)), QirajiMajors);
        (WorldRuntime w, _, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = w;
        system.SpawnGroupCondition = group => group.WorldStateCondition == 2099; // "Game Event 123 Active"
        AddPlayer(world, 1, 0, 0);
        Run(world, 200);

        Creature[] majors = Members(system, QirajiSpawns);
        Assert.Equal(4, majors.Length);
        Assert.Equal(1, majors.Count(c => c.Template.Entry == MajorHealie));
        Assert.Equal(3, majors.Count(c => c.Template.Entry == QirajiMajor));
        Assert.All(majors, c => Assert.Equal(c.Template.Entry, c.Guid.Entry));
    }

    [Fact]
    public void QirajiMajors_WithoutAConditionSeam_StayOut()
    {
        CreatureContent content = GroupContent(
            [Template(QirajiMajor), Template(MajorHealie)], QirajiSpawns.Select((g, i) => Spawn(g, 0, 5 + (i * 3), 0)), QirajiMajors);
        (WorldRuntime w, _, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = w;
        AddPlayer(world, 1, 0, 0);
        Run(world, 200);

        Assert.Empty(Members(system, QirajiSpawns)); // fail closed: the condition cannot be decided
    }

    [Fact]
    public void Balgaras_OneOfSevenSpots_AndAfterHisDeathNoneUntilHisRespawnTime()
    {
        CreatureContent content = GroupContent(
            [Template(Balgaras)], BalgarasSpawns.Select((g, i) => Spawn(g, 0, 5 + (i * 3), 0, respawnSeconds: 300)), BalgarasTheFoul,
            BalgarasSpawns.Select(g => (g, Balgaras)));
        (WorldRuntime w, _, CreatureMapSystem system) = CreateAiSystem(content, options: ShortCorpses());
        using WorldRuntime world = w;
        AddPlayer(world, 1, 0, 0);
        Run(world, 200);

        Creature balgaras = Assert.Single(Members(system, BalgarasSpawns));
        Assert.Equal(Balgaras, balgaras.Template.Entry);

        system.KillCreature(balgaras);
        Run(world, 120_000, 1000); // the corpse decayed after 60 s
        Assert.Empty(Members(system, BalgarasSpawns));
        Assert.NotNull(system.PendingRespawnAt(balgaras.Spawn!.Guid));

        Run(world, 190_000, 1000); // 310 s after the death
        Creature back = Assert.Single(Members(system, BalgarasSpawns));
        Assert.True(back.IsAlive);
        Assert.Single(system.SpawnGroupObjects(BalgarasTheFoul.Id));
    }

    [Fact]
    public void RandomKodos_AggroTogether_PullsEveryKodoIntoTheFight_EvenOutOfAssistanceRange()
    {
        CreatureContent content = GroupContent(
            [.. KodoEntries.Select(e => Template(e))], KodoSpawns.Select((g, i) => Spawn(g, 0, 5 + (i * 40), 0)), RandomKodos,
            KodoSpawns.SelectMany(g => KodoEntries.Select(e => (g, e))));
        (WorldRuntime w, Map map, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = w;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Run(world, 200);
        Creature[] kodos = Members(system, KodoSpawns);
        Assert.Equal(4, kodos.Length);
        Assert.All(kodos, k => Assert.Contains(k.Template.Entry, KodoEntries));
        Creature first = kodos.Single(k => k.Spawn!.Guid == KodoSpawns[0]);

        map.Combat.DealDamage(player, first, 1, direct: false);
        Run(world, 200);

        Assert.All(kodos, k => Assert.Same(player, k.Combat.Victim));
    }

    [Fact]
    public void WithoutAggroTogether_TheSameKodosStayOutOfTheFight()
    {
        CreatureContent content = GroupContent(
            [.. KodoEntries.Select(e => Template(e))], KodoSpawns.Select((g, i) => Spawn(g, 0, 5 + (i * 40), 0)), RandomKodos with { Flags = SpawnGroupFlags.None },
            KodoSpawns.SelectMany(g => KodoEntries.Select(e => (g, e))));
        (WorldRuntime w, Map map, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = w;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Run(world, 200);
        Creature[] kodos = Members(system, KodoSpawns);
        Creature first = kodos.Single(k => k.Spawn!.Guid == KodoSpawns[0]);

        map.Combat.DealDamage(player, first, 1, direct: false);
        Run(world, 200);

        Assert.Same(player, first.Combat.Victim);
        Assert.All(kodos.Where(k => k != first), k => Assert.Null(k.Combat.Victim));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Kargath_RespawnTogether_ADeadMemberComesBackWhenTheGroupReturnsHome(bool respawnTogether)
    {
        CreatureContent content = GroupContent(
            [.. KargathSpawns.Select(s => Template(s.Entry))], KargathSpawns.Select((s, i) => Spawn(s.Guid, s.Entry, 5 + (i * 3), 0)),
            respawnTogether ? KargathExpeditionaryForce : KargathExpeditionaryForce with { Flags = SpawnGroupFlags.AggroTogether });
        (WorldRuntime w, _, CreatureMapSystem system) = CreateAiSystem(content, options: ShortCorpses());
        using WorldRuntime world = w;
        AddPlayer(world, 1, 0, 0);
        Run(world, 200);
        Creature[] force = Members(system, KargathSpawns.Select(s => s.Guid));
        Assert.Equal(KargathSpawns.OrderBy(s => s.Guid), force.Select(c => (c.Spawn!.Guid, c.Template.Entry)).OrderBy(s => s.Guid));
        Creature fallen = force[0];
        Creature survivor = force[1];

        system.KillCreature(fallen);
        Run(world, 70_000, 1000); // its corpse is gone; 230 s of its respawn time are left
        Assert.DoesNotContain(fallen.Spawn!.Guid, Members(system, KargathSpawns.Select(s => s.Guid)).Select(c => c.Spawn!.Guid));

        system.EnterEvadeMode(survivor); // the group resets: CREATURE_GROUP_EVENT_HOME when it is home
        Run(world, 5_000, 100);

        bool back = Members(system, KargathSpawns.Select(s => s.Guid)).Any(c => c.Spawn!.Guid == fallen.Spawn.Guid && c.IsAlive);
        Assert.Equal(respawnTogether, back);
    }

    // --- game objects -----------------------------------------------------------------------------------------------------------

    private static (WorldRuntime World, GameObjectMapSystem System) StartObjects(GameObjectContent content, int seed = 1)
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var system = new GameObjectMapSystem(map, content) { Random = new Random(seed) };
        map.AddUpdater(system);
        (Player player, _) = Player(1);
        world.AddPlayer(player);
        world.RunTick(50);
        return (world, system);
    }

    private static GameObject[] Objects(GameObjectMapSystem system, IEnumerable<uint> guids)
        => [.. system.GameObjects.Where(g => g.Spawn is { } s && guids.Contains(s.Guid))];

    [Fact]
    public void MagentaCapClusters_ASpawnWithEntryZero_BecomesOneOfItsGameObjectSpawnEntries()
    {
        var seen = new HashSet<uint>();
        for (int seed = 0; seed < 30; seed++)
        {
            var content = new GameObjectContent(
                [GoTemplate(MagentaCapA, GameObjectType.Chest), GoTemplate(MagentaCapB, GameObjectType.Chest)],
                [GoSpawn(11427, 0, 5, 0, spawnTimeSeconds: 300)], [], [], [],
                spawnEntries: [(11427u, MagentaCapA), (11427u, MagentaCapB)]);
            WorldRuntime world = TestWorld.CreateRuntime();
            using (world)
            {
                Map map = world.GetMap(0);
                var system = new GameObjectMapSystem(map, content) { Random = new Random(seed) };
                map.AddUpdater(system);
                world.AddPlayer(Player(1).Player);
                world.RunTick(50);

                GameObject cap = Assert.Single(system.GameObjects);
                Assert.True(cap.IsSpawned);
                Assert.Contains(cap.Entry, new[] { MagentaCapA, MagentaCapB });
                Assert.Same(cap, system.FindBySpawn(11427));
                seen.Add(cap.Entry);

                // An in-place respawn keeps the entry (cmangos picks only when it creates the object; neither entry is a dynamic-guid one).
                uint entry = cap.Entry;
                system.Despawn(cap);
                world.RunTick(301_000);
                Assert.True(cap.IsSpawned);
                Assert.Equal(entry, cap.Entry);
            }
        }

        Assert.Equal([MagentaCapA, MagentaCapB], seen.Order());
    }

    [Fact]
    public void MustyTome_TenSpots_OneRealTome()
    {
        var content = new GameObjectContent(
            [GoTemplate(RealTome, GameObjectType.Chest), GoTemplate(FakeTome, GameObjectType.Chest)],
            [.. TomeSpawns.Select((g, i) => GoSpawn(g, 0, 5 + (i * 2), 0))], [], [], [])
        { SpawnGroups = new SpawnGroupCatalog([MustyTome]) };
        (WorldRuntime w, GameObjectMapSystem system) = StartObjects(content);
        using WorldRuntime world = w;

        GameObject[] tomes = Objects(system, TomeSpawns);
        Assert.Equal(10, tomes.Length);
        Assert.All(tomes, t => Assert.True(t.IsSpawned));
        Assert.Equal(1, tomes.Count(t => t.Entry == RealTome));
        Assert.Equal(10, system.SpawnGroupMaxCount(MustyTome.Id));
    }

    [Fact]
    public void Ore_OneNodeOfFive_ADespawnedNodeComesBackAfterItsRespawnTime_PossiblyElsewhere()
    {
        var content = new GameObjectContent(
            [GoTemplate(GoldVein, GameObjectType.Chest), GoTemplate(MithrilDeposit, GameObjectType.Chest), GoTemplate(TruesilverDeposit, GameObjectType.Chest)],
            [.. OreSpawns.Select((g, i) => GoSpawn(g, 0, 5 + (i * 4), 0, spawnTimeSeconds: 300))], [], [], [])
        { SpawnGroups = new SpawnGroupCatalog([Ore]) };
        (WorldRuntime w, GameObjectMapSystem system) = StartObjects(content);
        using WorldRuntime world = w;
        var spots = new HashSet<uint>();

        for (int i = 0; i < 12; i++)
        {
            GameObject node = Assert.Single(Objects(system, OreSpawns));
            Assert.True(node.IsSpawned);
            Assert.Contains(node.Entry, new[] { GoldVein, MithrilDeposit, TruesilverDeposit });
            spots.Add(node.Spawn!.Guid);

            system.Despawn(node); // mined out
            world.RunTick(50);
            Assert.Empty(Objects(system, OreSpawns)); // a rare-style group of one: nothing while the mined spot waits
            world.RunTick(301_000);
        }

        Assert.True(spots.Count > 1);
    }

    [Fact]
    public void WithoutItsGroup_AnEntryZeroOreSpawnNeverAppears()
    {
        var content = new GameObjectContent(
            [GoTemplate(MithrilDeposit, GameObjectType.Chest)], [.. OreSpawns.Select((g, i) => GoSpawn(g, 0, 5 + (i * 4), 0))], [], [], []);
        (WorldRuntime w, GameObjectMapSystem system) = StartObjects(content);
        using WorldRuntime world = w;

        Assert.Empty(Objects(system, OreSpawns));
    }
}
