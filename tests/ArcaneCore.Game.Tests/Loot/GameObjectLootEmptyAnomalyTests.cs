using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.Loot;

/// <summary>
/// <c>loot/gameobject-loot-empty</c>: a chest opened with a real loot id that generates nothing, when its template names no rows, only unknown
/// items, or guarantees an item no recipient can get. Never for loot id 0, a 100% row nobody passes the condition of, or a quest-only template
/// nobody is on. Each chest entry is used by one test so concurrent tests do not see each other's notices.
/// </summary>
public sealed class GameObjectLootEmptyAnomalyTests
{
    private const string Key = "loot/gameobject-loot-empty";
    private const uint UnknownItem = 99_999_999;

    private sealed class Rig
    {
        public required WorldRuntime World { get; init; }
        public required GameObjectMapSystem System { get; init; }
        public required LootService Loot { get; init; }
        public required FakeQuestJournal Quests { get; init; }

        public Player Join(uint guid)
        {
            (Player player, _) = Player(guid, 0, 0);
            World.AddPlayer(player);
            World.RunTick(50);
            return player;
        }

        public GameObject Single(uint entry) => System.GameObjects.Single(g => g.Entry == entry);
    }

    /// <summary>One chest of <paramref name="entry"/> whose data1 is <paramref name="lootId"/>, loot rows <paramref name="rows"/>.</summary>
    private static Rig CreateRig(uint entry, uint lootId, params LootStoreRow[] rows)
    {
        var content = new GameObjectContent([GoTemplate(entry, GameObjectType.Chest, (1, lootId))], [GoSpawn(entry, entry, 3, 0)], [], [], []);
        var lootContent = new LootContent([.. rows.Select(r => (LootTableKind.GameObject, r))], []);
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var quests = new FakeQuestJournal();
        var service = new LootService(lootContent, new LootOptions(), new Random(3)) { Items = ItemStore, Quests = quests, Groups = new FakeGroups() };
        var system = new GameObjectMapSystem(map, content, service, quests);
        map.AddUpdater(system);
        return new Rig { World = world, System = system, Loot = service, Quests = quests };
    }

    private static AnomalyCapture CaptureFor(uint entry)
        => AnomalyLedger.Shared.Capture(n => n.Key == Key && n.Subject.StartsWith($"gameobject entry {entry} ", StringComparison.Ordinal));

    [Fact]
    public void ChestWhoseLootIdHasNoRows_IsReportedAsAnError_WithoutThePlayerName()
    {
        // Reasoning: before the change no code reports this key, so the count would be 0 and the first assertion fails.
        Rig rig = CreateRig(7_101_001, 9001);
        Player player = rig.Join(7101);
        using AnomalyCapture cap = CaptureFor(7_101_001);
        GameObject chest = rig.Single(7_101_001);

        rig.System.Use(player, chest.Guid);

        AnomalyNotice notice = Assert.Single(cap.Notices);
        Assert.Equal(AnomalySeverity.Error, notice.Severity);
        Assert.Contains("loot 9001", notice.Subject);
        Assert.Contains("reason no-rows", notice.Detail);
        Assert.Contains($"opener guid {player.Guid.Low}", notice.Detail);
        Assert.DoesNotContain(player.Name, notice.Detail);
    }

    [Fact]
    public void ChestWhoseRowsAreAllUnknownItems_IsReported()
    {
        Rig rig = CreateRig(7_101_002, 9002, Row(9002, UnknownItem, 100));
        Player player = rig.Join(7102);
        using AnomalyCapture cap = CaptureFor(7_101_002);

        rig.System.Use(player, rig.Single(7_101_002).Guid);

        Assert.Contains("reason all-unknown", Assert.Single(cap.Notices).Detail);
    }

    [Fact]
    public void ChestOpenedWithoutAnItemTemplateStore_DoesNotReportAllUnknown()
    {
        Rig rig = CreateRig(7_101_007, 9008, Row(9008, UnknownItem, 100));
        rig.Loot.Items = null;
        Player player = rig.Join(7107);
        using AnomalyCapture cap = CaptureFor(7_101_007);

        rig.System.Use(player, rig.Single(7_101_007).Guid);

        Assert.Empty(cap.Notices);
    }

    [Fact]
    public void ChestWithLootIdZero_NeverReports()
    {
        Rig rig = CreateRig(7_101_003, 0);
        Player player = rig.Join(7103);
        using AnomalyCapture cap = CaptureFor(7_101_003);

        rig.System.Use(player, rig.Single(7_101_003).Guid);

        Assert.Empty(cap.Notices);
    }

    [Fact]
    public void FullChanceRowWhoseConditionFailsForEveryone_DoesNotReport()
    {
        // The row is 100% and its item is known, but nobody passes condition 71, so an empty bag is correct.
        Rig rig = CreateRig(7_101_004, 9004, Row(9004, ItemTestData.ToughJerky, 100, condition: 71));
        rig.Loot.Conditions = (_, _) => false;
        Player player = rig.Join(7104);
        using AnomalyCapture cap = CaptureFor(7_101_004);

        rig.System.Use(player, rig.Single(7_101_004).Guid);

        Assert.Empty(cap.Notices);
    }

    [Fact]
    public void QuestOnlyTemplateWithNobodyOnTheQuest_DoesNotReport_ButOneNeedingItWouldBeGuaranteed()
    {
        Rig rig = CreateRig(7_101_005, 9005, Row(9005, QuestItem, -100));
        Player player = rig.Join(7105);
        using AnomalyCapture cap = CaptureFor(7_101_005);

        rig.System.Use(player, rig.Single(7_101_005).Guid);

        Assert.Empty(cap.Notices);
        Assert.Null(rig.Loot.GuaranteeOf(LootTableKind.GameObject, 9005, [player]));
        rig.Quests.Needs.Add((player.Guid, QuestItem));
        Assert.NotNull(rig.Loot.GuaranteeOf(LootTableKind.GameObject, 9005, [player]));
    }

    [Fact]
    public void GuaranteeOf_FollowsGroups_AndIgnoresChanceBelowAHundred()
    {
        Rig rig = CreateRig(7_101_006, 9006,
            Row(9006, ItemTestData.ToughJerky, 60, group: 1),
            Row(9006, Hide, 60, group: 1),
            Row(9007, ItemTestData.ToughJerky, 50));
        Player player = rig.Join(7106);

        Assert.NotNull(rig.Loot.GuaranteeOf(LootTableKind.GameObject, 9006, [player]));   // a group summing to 120
        Assert.Null(rig.Loot.GuaranteeOf(LootTableKind.GameObject, 9007, [player]));      // 50% is not a guarantee
    }
}
