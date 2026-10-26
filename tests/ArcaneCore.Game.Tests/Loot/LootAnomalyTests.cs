using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Loot;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestKit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.Loot;

/// <summary>H1 outcome checks of loot (subsystem loot).</summary>
public sealed class LootAnomalyTests
{
    private const uint WolfLoot = 4399;
    private const uint BlueSword = 92101;

    private static readonly ItemTemplateStore Items = new(
    [
        .. ItemTestData.Templates,
        new ItemTemplate { Entry = BlueSword, Class = 15, Name = "Test Blue", DisplayId = 301, Quality = 3 },
    ], ItemTestData.StartingItems);

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => _ticks;

        public void Advance(long ms) => _ticks += ms;
    }

    private sealed class Rig
    {
        public required WorldRuntime World { get; init; }
        public required Map Map { get; init; }
        public required CreatureMapSystem Creatures { get; init; }
        public required LootService Loot { get; init; }
        public required FakeGroups Groups { get; init; }

        public Player Join(uint guid, float x = 0)
        {
            (Player player, FakeSession session) = Player(guid, x, 0);
            player.Inventory.Templates = Items;
            World.AddPlayer(player);
            World.RunTick(50);
            session.Clear();
            return player;
        }

        public Creature Wolf => Creatures.Creatures.Single(c => c.Entry == WolfEntry);

        public Creature Kill(Player killer)
        {
            Creature wolf = Wolf;
            Map.Combat.DealDamage(killer, wolf, wolf.Health, direct: false);
            return wolf;
        }

        public AnomalyCapture Capture(Creature wolf)
        {
            string id = wolf.Guid.Value.ToString("X");
            return AnomalyLedger.Shared.Capture(n => n.Subsystem == "loot" && n.Subject.Contains(id));
        }
    }

    private static Rig CreateRig(uint spawnGuid, uint gold, params uint[] drops)
    {
        CreatureContent creatures = Content([Template()], [Spawn(spawnGuid, WolfEntry, 3, 0)]);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateSystem(creatures);
        var content = new LootContent(
            [.. drops.Select(item => (LootTableKind.Creature, Row(WolfLoot, item, 100)))],
            [new CreatureLootInfo(WolfEntry, WolfLoot, 0, gold, gold)]);
        var groups = new FakeGroups();
        var loot = new LootService(content, random: new Random(5)) { Items = Items, Quests = new FakeQuestJournal(), Groups = groups };
        map.Combat.UnitKilled += (killer, victim) => loot.OnCreatureKilled(killer, victim);
        map.AddUpdater(loot.Rolls);
        return new Rig { World = world, Map = map, Creatures = system, Loot = loot, Groups = groups };
    }

    private static LootService Service(params LootStoreRow[] rows)
        => new(new LootContent([.. rows.Select(r => (LootTableKind.Creature, r))], []), random: new Random(1)) { Items = Items };

    private static AnomalyCapture TableCapture(uint entry)
        => AnomalyLedger.Shared.Capture(n => n.Subject == $"loot table Creature entry {entry}");

    [Fact]
    public void GuaranteedRow_ReportsOnlyForAnUnconditionedUngroupedKnownItemWithFullChance()
    {
        using AnomalyCapture cap = TableCapture(7001);
        Service(Row(7001, ItemTestData.ToughJerky, 100)).CheckGuaranteedDrops(default, LootTableKind.Creature, 7001);
        Assert.Equal(1, cap.Count("loot/guaranteed-drop-missing"));

        foreach ((uint entry, LootStoreRow row) in new (uint, LootStoreRow)[]
        {
            (7002, Row(7002, ItemTestData.ToughJerky, 50)),
            (7003, Row(7003, ItemTestData.ToughJerky, 100, group: 1)),
            (7004, Row(7004, ItemTestData.ToughJerky, 100, condition: 5)),
            (7005, Row(7005, 99999999, 100)),
            (7006, Row(7006, ItemTestData.ToughJerky, -100)),
        })
        {
            using AnomalyCapture quiet = TableCapture(entry);
            Service(row).CheckGuaranteedDrops(default, LootTableKind.Creature, entry);
            Assert.Empty(quiet.Notices);
        }
    }

    [Fact]
    public void NormalGeneration_DoesNotReportAGuaranteedDrop()
    {
        using AnomalyCapture cap = TableCapture(7010);
        LootService loot = Service(Row(7010, ItemTestData.ToughJerky, 100));
        LootBag bag = loot.Generate(default, LootSourceKind.Creature, LootType.Corpse, LootTableKind.Creature, 7010, []);
        Assert.Single(bag.Items);
        Assert.Empty(cap.Notices);
    }

    [Fact]
    public void ItemAwardedTwice_IsRefusedAndReported_FirstTakeIsNot()
    {
        Rig rig = CreateRig(7101, 0, ItemTestData.ToughJerky);
        Player alice = rig.Join(7101);
        Creature wolf = rig.Kill(alice);
        using AnomalyCapture cap = rig.Capture(wolf);
        Assert.Equal(LootResult.Ok, rig.Loot.Open(alice, wolf.Guid));
        LootBag bag = rig.Loot.FindLoot(wolf.Guid)!;
        LootItem item = bag.Items.Single();
        Assert.Equal(InventoryResult.Ok, rig.Loot.TakeItem(alice, item.Slot));
        Assert.Empty(cap.Notices);
        Assert.Equal(InventoryResult.AlreadyLooted, rig.Loot.AwardItem(alice, bag, item));
        Assert.Equal(1, cap.Count("loot/item-duplicated"));
    }

    [Fact]
    public void MoneyOfferedAgainAfterPayout_IsRefusedAndReported_SinglePayoutIsNot()
    {
        Rig rig = CreateRig(7102, 100, ItemTestData.ToughJerky);
        Player alice = rig.Join(7102);
        Creature wolf = rig.Kill(alice);
        using AnomalyCapture cap = rig.Capture(wolf);
        rig.Loot.Open(alice, wolf.Guid);
        LootBag bag = rig.Loot.FindLoot(wolf.Guid)!;
        Assert.True(bag.Gold > 0);
        Assert.True(rig.Loot.TakeMoney(alice));
        Assert.False(rig.Loot.TakeMoney(alice));
        Assert.Empty(cap.Notices);
        uint money = alice.Money;
        bag.Gold = 50;
        Assert.False(rig.Loot.TakeMoney(alice));
        Assert.Equal(money, alice.Money);
        Assert.Equal(1, cap.Count("loot/money-looted-twice"));
    }

    [Fact]
    public void WindowOpenOnAGoneSource_IsReportedAndClosed_IntactCorpseKeepsItsWindow()
    {
        Rig rig = CreateRig(7103, 0, ItemTestData.ToughJerky);
        Player alice = rig.Join(7103);
        Creature wolf = rig.Kill(alice);
        using AnomalyCapture cap = rig.Capture(wolf);
        Assert.Equal(LootResult.Ok, rig.Loot.Open(alice, wolf.Guid));
        for (int i = 0; i < 4; i++) rig.Loot.Rolls.Update(rig.Map, 1000);
        Assert.NotNull(rig.Loot.OpenLootOf(alice));
        Assert.Empty(cap.Notices);

        wolf.DeathState = CreatureDeathState.Alive; // respawned without the loot being forgotten
        rig.Loot.Rolls.Update(rig.Map, 1000);
        Assert.Empty(cap.Notices);
        rig.Loot.Rolls.Update(rig.Map, 1000);
        Assert.Equal(1, cap.Count("loot/window-left-open"));
        Assert.Null(rig.Loot.OpenLootOf(alice));
    }

    [Fact]
    public void ALooterWhoMoves_LosesACorpseWindow_SoItsViewersEmpty()
    {
        // vmangos HandleMoverRelocation (MovementHandler.cpp:1108-1109): moving closes a non-item loot window.
        Rig rig = CreateRig(7106, 0, ItemTestData.ToughJerky);
        Player alice = rig.Join(7106);
        Creature wolf = rig.Kill(alice);
        Assert.Equal(LootResult.Ok, rig.Loot.Open(alice, wolf.Guid));
        LootBag bag = rig.Loot.OpenLootOf(alice)!;
        Assert.Single(bag.Viewers);

        rig.Loot.ReleaseOnMovement(alice);

        Assert.Null(rig.Loot.OpenLootOf(alice));
        Assert.Empty(bag.Viewers);
        Assert.Equal((UnitFlags)0, alice.UnitFlags & UnitFlags.Looting);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ADeadOrFarLooter_IsReleasedAfterTwoAudits_WithoutAnAnomaly(bool dead)
    {
        Rig rig = CreateRig(dead ? 7107u : 7108u, 0, ItemTestData.ToughJerky);
        Player alice = rig.Join(dead ? 7107u : 7108u);
        Creature wolf = rig.Kill(alice);
        using AnomalyCapture cap = rig.Capture(wolf);
        Assert.Equal(LootResult.Ok, rig.Loot.Open(alice, wolf.Guid));
        LootBag bag = rig.Loot.OpenLootOf(alice)!;
        for (int i = 0; i < 4; i++) rig.Loot.Rolls.Update(rig.Map, 1000);
        Assert.Empty(cap.Notices); // an intact window of a live looter beside the corpse is left alone

        if (dead)
        {
            alice.Health = 0;
        }
        else
        {
            alice.SetPosition(alice.X + 200f, alice.Y, alice.Z, 0f);
        }

        rig.Loot.Rolls.Update(rig.Map, 1000);
        Assert.NotNull(rig.Loot.OpenLootOf(alice)); // the first strike
        rig.Loot.Rolls.Update(rig.Map, 1000);
        Assert.Empty(cap.Notices); // dying or walking off with the window open is an ordinary event, not window-left-open
        Assert.Equal(0, cap.Count("loot/window-left-open"));
        Assert.Null(rig.Loot.OpenLootOf(alice));
        Assert.Empty(bag.Viewers);
    }

    [Fact]
    public void RollPastItsTimeout_IsReportedWhenTheHeldItemIsTouched_YoungRollIsNot()
    {
        Rig rig = CreateRig(7104, 0, ItemTestData.ToughJerky, BlueSword);
        var clock = new ManualClock();
        rig.Loot.Rolls.Clock = clock;
        Player alice = rig.Join(7104);
        Player bob = rig.Join(7105, 1);
        rig.Groups.Create(LootMethod.GroupLoot, alice, bob);
        Creature wolf = rig.Kill(alice);
        using AnomalyCapture cap = rig.Capture(wolf);
        Assert.Equal(LootResult.Ok, rig.Loot.Open(alice, wolf.Guid));
        Assert.Equal(1, rig.Loot.Rolls.ActiveRollCount);
        byte slot = rig.Loot.FindLoot(wolf.Guid)!.Items.Single(i => i.ItemId == BlueSword).Slot;

        clock.Advance(30_000);
        rig.Loot.TakeItem(alice, slot);
        Assert.Empty(cap.Notices);
        clock.Advance(40_000); // the map update never ran: 70 s > 60 s timeout + 5 s grace
        rig.Loot.TakeItem(alice, slot);
        rig.Loot.TakeItem(alice, slot);
        Assert.Equal(1, cap.Count("loot/roll-unresolved"));
    }
}
