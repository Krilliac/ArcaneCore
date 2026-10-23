using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>The quest dynamic-flag viewer filter follows a live gameobject_template reload (ReplaceContent), including its rollback.</summary>
public sealed class GameObjectQuestFlagReloadTests
{
    private const uint GooberEntry = 104;
    private const uint ChestEntry = 105;
    private const uint ChestLoot = 501;
    private const uint QuestId = 77;
    private const uint Sparkle = (uint)(GameObjectDynFlags.Activate | GameObjectDynFlags.Sparkle);

    private sealed record ReloadRig(WorldRuntime World, GameObjectMapSystem System, FakeQuestJournal Quests, Player Viewer, FakeSession Session);

    private static GameObjectContent Content(GameObjectTemplate template, GameObjectSpawn spawn)
        => new([template], [spawn], [], [], []);

    private static ReloadRig CreateRig(GameObjectTemplate template, GameObjectSpawn spawn, LootContent? lootContent = null)
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var quests = new FakeQuestJournal();
        lootContent ??= new LootContent([(LootTableKind.GameObject, Row(ChestLoot, QuestItem, -100))], []);
        var loot = new LootService(lootContent, random: new Random(1)) { Items = ItemStore, Quests = quests };
        var system = new GameObjectMapSystem(map, Content(template, spawn), loot, quests);
        map.AddUpdater(system);
        (Player viewer, FakeSession session) = Player(1, 1, 0);
        world.AddPlayer(viewer);
        world.RunTick(50);
        world.RunTick(1000); // the first refresh has run
        session.Clear();
        return new ReloadRig(world, system, quests, viewer, session);
    }

    private static List<ParsedBlock> DynFlagUpdates(ReloadRig rig, GameObject go)
        => [.. DrainBlocks(rig.Session).Where(b => b.Type == ObjectUpdateType.Values
            && b.Guids.Contains(go.Guid.Value)
            && b.Values.ContainsKey(UpdateFields.GameobjectDynFlags))];

    [Fact]
    public void Goober_GainingAQuestIdInAReload_SparklesForAViewerWhoseQuestIsIncomplete()
    {
        GameObjectSpawn spawn = GoSpawn(1, GooberEntry, 3, 0);
        ReloadRig rig = CreateRig(GoTemplate(GooberEntry, GameObjectType.Goober), spawn);
        GameObject goober = rig.System.GameObjects.Single();
        rig.Quests.Incomplete.Add((rig.Viewer.Guid, QuestId));

        rig.System.ReplaceContent(Content(GoTemplate(GooberEntry, GameObjectType.Goober, (1, QuestId)), spawn));
        rig.World.RunTick(1000);

        ParsedBlock update = Assert.Single(DynFlagUpdates(rig, goober));
        Assert.Equal(Sparkle, update.Values[UpdateFields.GameobjectDynFlags]);
    }

    [Fact]
    public void Chest_GainingAQuestInAReload_SparklesForAViewerWhoseQuestIsIncomplete()
    {
        GameObjectSpawn spawn = GoSpawn(1, ChestEntry, 3, 0);
        ReloadRig rig = CreateRig(GoTemplate(ChestEntry, GameObjectType.Chest), spawn);
        GameObject chest = rig.System.GameObjects.Single();
        rig.Quests.Incomplete.Add((rig.Viewer.Guid, QuestId));

        rig.System.ReplaceContent(Content(GoTemplate(ChestEntry, GameObjectType.Chest, (8, QuestId)), spawn));
        rig.World.RunTick(1000);

        ParsedBlock update = Assert.Single(DynFlagUpdates(rig, chest));
        Assert.Equal(Sparkle, update.Values[UpdateFields.GameobjectDynFlags]);
    }

    [Fact]
    public void Goober_LosingItsQuestIdInAReload_ClearsTheSparkleTheViewerWasSent()
    {
        GameObjectSpawn spawn = GoSpawn(1, GooberEntry, 3, 0);
        ReloadRig rig = CreateRig(GoTemplate(GooberEntry, GameObjectType.Goober, (1, QuestId)), spawn);
        GameObject goober = rig.System.GameObjects.Single();
        rig.Quests.Incomplete.Add((rig.Viewer.Guid, QuestId));
        rig.World.RunTick(1000);
        Assert.Single(DynFlagUpdates(rig, goober)); // the viewer now holds Activate|Sparkle

        rig.System.ReplaceContent(Content(GoTemplate(GooberEntry, GameObjectType.Goober), spawn));
        rig.World.RunTick(1000);

        ParsedBlock update = Assert.Single(DynFlagUpdates(rig, goober));
        Assert.Equal(0u, update.Values[UpdateFields.GameobjectDynFlags]);
    }

    [Fact]
    public void Chest_GainingQuestLootInALootReload_SparklesForAViewerWhoNeedsTheItem()
    {
        GameObjectSpawn spawn = GoSpawn(1, ChestEntry, 3, 0);
        // No quest row for ChestLoot at the start, so the chest is created without a viewer filter.
        ReloadRig rig = CreateRig(GoTemplate(ChestEntry, GameObjectType.Chest, (1, ChestLoot)), spawn, LootContent.Empty);
        GameObject chest = rig.System.GameObjects.Single();
        Assert.Null(chest.ViewerFieldFilter);
        rig.Quests.Needs.Add((rig.Viewer.Guid, QuestItem));

        rig.System.Loot!.ReplaceContent(new LootContent([(LootTableKind.GameObject, Row(ChestLoot, QuestItem, -100))], []));
        rig.System.RefreshChestQuestFilters();
        rig.World.RunTick(1000);

        ParsedBlock update = Assert.Single(DynFlagUpdates(rig, chest));
        Assert.Equal(Sparkle, update.Values[UpdateFields.GameobjectDynFlags]);
    }
}
