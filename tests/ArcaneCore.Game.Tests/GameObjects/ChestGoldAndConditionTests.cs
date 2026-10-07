using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>
/// Chest money from <c>gameobject_template.mingold..maxgold</c> and the <see cref="LootService.Conditions"/> seam on
/// conditioned loot rows (lane L9). Reference behaviour: mangos Object/PlayerLoot.cpp:222-229 (money rolled right after the
/// object's loot is filled, only with a loot id), WorldHandlers/LootHandler.cpp:344-385 (non-item money of a grouped looter is
/// split), Object/LootMgr.cpp:505 (a conditioned row is hidden from a player who does not meet its condition).
/// </summary>
public sealed class ChestGoldAndConditionTests
{
    private const uint ChestEntry = 201;
    private const uint IdleChestEntry = 202;   // a gold range but no loot id
    private const uint FreeChestEntry = 203;   // a loot id but no gold
    private const uint ChestLoot = 600;
    private const uint ConditionedLoot = 601;
    private const uint MeetsCondition = 70;
    private const uint FailsCondition = 71;

    private sealed class Rig
    {
        public required WorldRuntime World { get; init; }
        public required GameObjectMapSystem System { get; init; }
        public required LootService Loot { get; init; }
        public required FakeGroups Groups { get; init; }

        public (Player Player, FakeSession Session) Join(uint guid, float x = 0, float y = 0)
        {
            (Player player, FakeSession session) = Player(guid, x, y);
            World.AddPlayer(player);
            World.RunTick(50);
            return (player, session);
        }

        public GameObject Single(uint entry) => System.GameObjects.Single(g => g.Entry == entry);
    }

    private static Rig CreateRig(IEnumerable<GameObjectSpawn> spawns, uint minGold = 30, uint maxGold = 40, float moneyRate = 1f, int seed = 3)
    {
        GameObjectTemplate[] templates =
        [
            GoTemplate(ChestEntry, GameObjectType.Chest, (1, ChestLoot)) with { MinGold = minGold, MaxGold = maxGold },
            GoTemplate(IdleChestEntry, GameObjectType.Chest) with { MinGold = minGold, MaxGold = maxGold },
            GoTemplate(FreeChestEntry, GameObjectType.Chest, (1, ConditionedLoot)),
        ];
        var content = new GameObjectContent(templates, spawns, [], [], []);
        var lootContent = new LootContent(
        [
            (LootTableKind.GameObject, Row(ChestLoot, ItemTestData.ToughJerky, 100, minOrRef: 2, max: 2)),
            (LootTableKind.GameObject, Row(ConditionedLoot, ItemTestData.ToughJerky, 100)),
            (LootTableKind.GameObject, Row(ConditionedLoot, Hide, 100, condition: MeetsCondition)),
            (LootTableKind.GameObject, Row(ConditionedLoot, PartyItem, 100, condition: FailsCondition)),
        ], []);
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var groups = new FakeGroups();
        var quests = new FakeQuestJournal();
        var service = new LootService(lootContent, new LootOptions { MoneyRate = moneyRate }, new Random(seed)) { Items = ItemStore, Quests = quests, Groups = groups };
        var system = new GameObjectMapSystem(map, content, service, quests);
        map.AddUpdater(system);
        return new Rig { World = world, System = system, Loot = service, Groups = groups };
    }

    private static ParsedLoot LootResponse(FakeSession session)
        => ParsedLoot.Parse(Assert.Single(Packets(session, WorldOpcode.SmsgLootResponse)).Payload);

    // --- chest money ---------------------------------------------------------------------------

    [Fact]
    public void Chest_RollsItsTemplateGold_OnFirstOpen_AndKeepsItUntilTaken()
    {
        Rig rig = CreateRig([GoSpawn(1, ChestEntry, 3, 0)]);
        (Player alice, FakeSession aliceSession) = rig.Join(1);
        (Player bob, FakeSession bobSession) = rig.Join(2, 1, 0);
        GameObject chest = rig.Single(ChestEntry);
        aliceSession.Clear();

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(alice, chest.Guid));
        ParsedLoot loot = LootResponse(aliceSession);
        Assert.InRange(loot.Gold, 30u, 40u);
        Assert.Equal(loot.Gold, chest.Loot!.Gold);
        uint rolled = loot.Gold;

        // Released with the money untaken: the chest stays activated and the same roll is shown to the next opener (no reroll).
        rig.Loot.Release(alice, chest.Guid);
        Assert.Equal(GameObjectLootState.Activated, chest.LootState);
        bobSession.Clear();
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(bob, chest.Guid));
        Assert.Equal(rolled, LootResponse(bobSession).Gold);

        uint before = bob.Money;
        Assert.True(rig.Loot.TakeMoney(bob));
        Assert.Equal(before + rolled, bob.Money);
        Assert.Equal(0u, chest.Loot.Gold);
        Assert.Contains(Packets(bobSession, WorldOpcode.SmsgLootClearMoney), p => p.Payload.Length == 0);
        Assert.False(rig.Loot.TakeMoney(bob));
    }

    [Fact]
    public void Chest_WhoseItemsAreTaken_StaysUntilTheMoneyIsTaken_ThenDespawns()
    {
        Rig rig = CreateRig([GoSpawn(1, ChestEntry, 3, 0)]);
        (Player player, FakeSession session) = rig.Join(1);
        GameObject chest = rig.Single(ChestEntry);
        session.Clear();

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, chest.Guid));
        ParsedLootItem jerky = Assert.Single(LootResponse(session).Items);
        Assert.Equal(Items.InventoryResult.Ok, rig.Loot.TakeItem(player, jerky.Slot));
        rig.Loot.Release(player, chest.Guid);
        Assert.Equal(GameObjectLootState.Activated, chest.LootState); // money left: not looted out (vmangos Loot::isLooted counts gold)

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, chest.Guid));
        Assert.True(rig.Loot.TakeMoney(player));
        rig.Loot.Release(player, chest.Guid);
        Assert.Equal(GameObjectLootState.JustDeactivated, chest.LootState);
        rig.World.RunTick(50);
        Assert.False(chest.IsSpawned);
    }

    [Fact]
    public void Chest_WithoutALootId_PaysNothing_AndAChestWithoutAGoldRangePaysNothing()
    {
        Rig rig = CreateRig([GoSpawn(1, IdleChestEntry, 3, 0), GoSpawn(2, FreeChestEntry, -3, 0)]);
        (Player player, FakeSession session) = rig.Join(1);

        // No loot id: the reference breaks out before generateMoneyLoot, so the window is empty although the template names a range.
        GameObject idle = rig.Single(IdleChestEntry);
        session.Clear();
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, idle.Guid));
        ParsedLoot empty = LootResponse(session);
        Assert.Equal((0u, 0), (empty.Gold, empty.Items.Count));
        Assert.Equal(0u, idle.Loot!.Gold);
        rig.Loot.Release(player, idle.Guid);

        GameObject free = rig.Single(FreeChestEntry);
        session.Clear();
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, free.Guid));
        Assert.Equal(0u, LootResponse(session).Gold);
    }

    [Fact]
    public void ChestGold_AppliesTheMoneyRate_AndIsClampedToMaxMoney()
    {
        Rig rate = CreateRig([GoSpawn(1, ChestEntry, 3, 0)], minGold: 10, maxGold: 10, moneyRate: 2.5f);
        (Player player, _) = rate.Join(1);
        Assert.Equal(GameObjectUseResult.Ok, rate.System.Use(player, rate.Single(ChestEntry).Guid));
        Assert.Equal(25u, rate.Single(ChestEntry).Loot!.Gold);

        Rig huge = CreateRig([GoSpawn(1, ChestEntry, 3, 0)], minGold: uint.MaxValue, maxGold: uint.MaxValue);
        (Player rich, _) = huge.Join(1);
        Assert.Equal(GameObjectUseResult.Ok, huge.System.Use(rich, huge.Single(ChestEntry).Guid));
        Assert.Equal(LootService.MaxMoneyAmount, huge.Single(ChestEntry).Loot!.Gold);
    }

    [Fact]
    public void ChestMoney_PaysTheLooterWhole_EvenInAGroup_RecordedDeviation()
    {
        // The reference splits non-item money among the looter's group in range (LootHandler.cpp:344-385); ArcaneCore keeps
        // chest money whole for the taker until the split can tell group members from ungrouped late openers (area doc, known gaps).
        Rig rig = CreateRig([GoSpawn(1, ChestEntry, 3, 0)], minGold: 30, maxGold: 30);
        (Player alice, FakeSession aliceSession) = rig.Join(1);
        (Player bob, FakeSession bobSession) = rig.Join(2, 1, 0);
        rig.Groups.Create(LootMethod.FreeForAll, alice, bob);
        GameObject chest = rig.Single(ChestEntry);

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(alice, chest.Guid));
        Assert.Equal(2, chest.Loot!.Recipients.Count);
        uint aliceBefore = alice.Money;
        uint bobBefore = bob.Money;
        aliceSession.Clear();
        bobSession.Clear();

        Assert.True(rig.Loot.TakeMoney(alice));
        Assert.Equal((aliceBefore + 30, bobBefore), (alice.Money, bob.Money));
        List<(WorldOpcode Opcode, byte[] Payload)> toAlice = NonUpdatePackets(aliceSession); // the drain consumes the queue: one read per session
        List<(WorldOpcode Opcode, byte[] Payload)> toBob = NonUpdatePackets(bobSession);
        Assert.DoesNotContain(toAlice, p => p.Opcode == WorldOpcode.SmsgLootMoneyNotify);
        Assert.DoesNotContain(toBob, p => p.Opcode == WorldOpcode.SmsgLootMoneyNotify);
        Assert.Single(toAlice, p => p.Opcode == WorldOpcode.SmsgLootClearMoney); // only the viewer is told the money is gone
        Assert.DoesNotContain(toBob, p => p.Opcode == WorldOpcode.SmsgLootClearMoney);
    }

    // --- conditioned rows ----------------------------------------------------------------------

    [Fact]
    public void ConditionedRow_IsGenerated_OnlyWhenTheSeamAcceptsItForARecipient()
    {
        Rig rig = CreateRig([GoSpawn(1, FreeChestEntry, 3, 0)]);
        (Player player, FakeSession session) = rig.Join(1);
        var asked = new List<(Player Player, uint Condition)>();
        rig.Loot.Conditions = (p, condition) =>
        {
            asked.Add((p, condition));
            return condition == MeetsCondition;
        };

        session.Clear();
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, rig.Single(FreeChestEntry).Guid));
        ParsedLoot loot = LootResponse(session);
        Assert.Equal([ItemTestData.ToughJerky, Hide], loot.Items.Select(i => i.ItemId).Order());
        Assert.Equal([(player, MeetsCondition), (player, FailsCondition)], asked.OrderBy(a => a.Condition)); // the unconditioned row never asks
    }

    [Fact]
    public void ConditionedRow_IsSkipped_WithoutASeam()
    {
        Rig rig = CreateRig([GoSpawn(1, FreeChestEntry, 3, 0)]);
        (Player player, FakeSession session) = rig.Join(1);
        Assert.Null(rig.Loot.Conditions);

        session.Clear();
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, rig.Single(FreeChestEntry).Guid));
        Assert.Equal(ItemTestData.ToughJerky, Assert.Single(LootResponse(session).Items).ItemId);
    }

    [Fact]
    public void ConditionedRow_InAGroup_IsGeneratedWhenAnyRecipientMeetsIt_AndOnlyThoseRecipientsMayLootIt()
    {
        Rig rig = CreateRig([GoSpawn(1, FreeChestEntry, 3, 0)]);
        (Player alice, FakeSession aliceSession) = rig.Join(1);
        (Player bob, _) = rig.Join(2, 1, 0);
        rig.Groups.Create(LootMethod.FreeForAll, alice, bob);
        rig.Loot.Conditions = (p, condition) => condition == MeetsCondition && ReferenceEquals(p, bob);

        aliceSession.Clear();
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(alice, rig.Single(FreeChestEntry).Guid));
        // The row drops because bob meets it, but it is decided per viewer (vmangos LootItem::AllowedForPlayer): alice does not see it.
        Assert.Equal([ItemTestData.ToughJerky], LootResponse(aliceSession).Items.Select(i => i.ItemId).Order());
        LootItem hide = Assert.Single(rig.Loot.FindLoot(rig.Single(FreeChestEntry).Guid)!.Items, i => i.ItemId == Hide);
        Assert.Equal([bob.Guid], hide.AllowedLooters);
    }
}
