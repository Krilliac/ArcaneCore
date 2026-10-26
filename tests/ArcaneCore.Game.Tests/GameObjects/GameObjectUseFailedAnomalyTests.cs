using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>
/// <c>gameobject/use-failed</c>: a refusal that is a server or content defect (Unsupported, a plainly interactable object that is NotUsable,
/// a lock id with no lock row) is reported at once; a refusal the player caused (TooFar, a NoInteract flag) is not. Each entry is used by one
/// test so concurrent tests do not see each other's notices.
/// </summary>
public sealed class GameObjectUseFailedAnomalyTests
{
    private const string Key = "gameobject/use-failed";
    private const string UnknownLockKey = "gameobject/use-failed-unknown-lock";

    private static (WorldRuntime World, GameObjectMapSystem System, Player Player) Create(uint playerId, GameObjectTemplate template, float x)
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var system = new GameObjectMapSystem(map, new GameObjectContent([template], [GoSpawn(template.Entry, template.Entry, x, 0)], [], [], []));
        map.AddUpdater(system);
        Player player = TestWorld.CreatePlayer(playerId, 0, 0, new FakeSession());
        world.AddPlayer(player);
        world.RunTick(50);
        return (world, system, player);
    }

    private static AnomalyCapture CaptureFor(uint entry)
        => AnomalyLedger.Shared.Capture(n => n.Key is Key or UnknownLockKey && n.Subject.StartsWith($"gameobject entry {entry} ", StringComparison.Ordinal));

    [Fact]
    public void ChestWithNoLootService_ReportsUnsupported_WithoutThePlayerName()
    {
        // Reasoning: before the change nothing reports this key, so the single-notice assertion fails with zero notices.
        GameObjectTemplate chest = GoTemplate(7_201_001, GameObjectType.Chest, (1, 5000));
        (WorldRuntime world, GameObjectMapSystem system, Player player) = Create(7201, chest, 3);
        using (world)
        {
            using AnomalyCapture cap = CaptureFor(7_201_001);

            Assert.Equal(GameObjectUseResult.Unsupported, system.Use(player, system.GameObjects.Single().Guid));

            AnomalyNotice notice = Assert.Single(cap.Notices);
            Assert.Equal(AnomalySeverity.Warning, notice.Severity);
            Assert.Contains("(Unsupported)", notice.Detail);
            Assert.Contains($"player guid {player.Guid.Low}", notice.Detail);
            Assert.DoesNotContain(player.Name, notice.Detail);
        }
    }

    [Fact]
    public void QuestGiverWithNoSeamWired_ReportsUnsupported()
    {
        GameObjectTemplate giver = GoTemplate(7_201_006, GameObjectType.QuestGiver);
        (WorldRuntime world, GameObjectMapSystem system, Player player) = Create(7206, giver, 3);
        using (world)
        {
            using AnomalyCapture cap = CaptureFor(7_201_006);

            Assert.Equal(GameObjectUseResult.Unsupported, system.Use(player, system.GameObjects.Single().Guid));

            Assert.Contains("no quest giver service is wired", Assert.Single(cap.Notices).Detail);
        }
    }

    [Fact]
    public void QuestGiverWhoseWiredSeamRefuses_IsNotADefect_ItMayBeNotReady()
    {
        GameObjectTemplate giver = GoTemplate(7_201_007, GameObjectType.QuestGiver);
        (WorldRuntime world, GameObjectMapSystem system, Player player) = Create(7207, giver, 3);
        using (world)
        {
            system.QuestGiver = new RefusingQuestGiver();
            using AnomalyCapture cap = CaptureFor(7_201_007);

            Assert.Equal(GameObjectUseResult.Unsupported, system.Use(player, system.GameObjects.Single().Guid));

            Assert.Empty(cap.Notices);
        }
    }

    private sealed class RefusingQuestGiver : IGameObjectQuestGiver
    {
        public bool OpenQuestMenu(Player player, GameObject go) => false;
    }

    [Fact]
    public void TooFar_IsThePlayersDoing_AndDoesNotReport()
    {
        GameObjectTemplate chest = GoTemplate(7_201_002, GameObjectType.Chest, (1, 5000));
        (WorldRuntime world, GameObjectMapSystem system, Player player) = Create(7202, chest, 200);
        using (world)
        {
            using AnomalyCapture cap = CaptureFor(7_201_002);

            Assert.Equal(GameObjectUseResult.TooFar, system.Use(player, system.GameObjects.Single().Guid));

            Assert.Empty(cap.Notices);
        }
    }

    [Fact]
    public void NotUsable_ReportsOnlyForAnInteractableTypeThatIsNotFlaggedUnusable()
    {
        GameObjectTemplate plain = GoTemplate(7_201_003, GameObjectType.Chest, (1, 5000));
        GameObjectTemplate flagged = GoTemplate(7_201_004, GameObjectType.Chest, (1, 5000)) with { Flags = (uint)GameObjectFlags.NoInteract };
        (WorldRuntime world, GameObjectMapSystem system, Player player) = Create(7203, plain, 3);
        using (world)
        {
            GameObject go = system.GameObjects.Single();
            using AnomalyCapture cap = CaptureFor(7_201_003);
            system.RecordUseAttemptOutcome(player, go.Guid, go, GameObjectUseResult.NotUsable);
            Assert.Contains("(NotUsable)", Assert.Single(cap.Notices).Detail);
        }

        (WorldRuntime world2, GameObjectMapSystem system2, Player player2) = Create(7204, flagged, 3);
        using (world2)
        {
            GameObject go = system2.GameObjects.Single();
            using AnomalyCapture cap = CaptureFor(7_201_004);
            Assert.Equal(GameObjectUseResult.NotUsable, system2.Use(player2, go.Guid));
            Assert.Empty(cap.Notices);
        }
    }

    [Fact]
    public void LockedWithAnUnknownLockId_Reports_ButAKnownPlayerRefusalDoesNot()
    {
        GameObjectTemplate chest = GoTemplate(7_201_005, GameObjectType.Chest, (0, 9_999), (1, 5000));
        (WorldRuntime world, GameObjectMapSystem system, Player player) = Create(7205, chest, 3);
        using (world)
        {
            GameObject go = system.GameObjects.Single();
            using AnomalyCapture cap = CaptureFor(7_201_005);

            system.RecordUseAttemptOutcome(player, go.Guid, go, GameObjectUseResult.MissingKey);
            system.RecordUseAttemptOutcome(player, go.Guid, go, GameObjectUseResult.SkillTooLow);
            Assert.Empty(cap.Notices);

            system.RecordUseAttemptOutcome(player, go.Guid, go, GameObjectUseResult.Locked);
            AnomalyNotice notice = Assert.Single(cap.Notices);
            Assert.Equal(UnknownLockKey, notice.Key);
            Assert.Equal(GameObjectMapSystem.UseFailedUnknownLockCode, "use-failed-unknown-lock");
            Assert.Contains("lock 9999 has no lock_template row", notice.Detail);
        }
    }
}
