using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.ItemTestData;

namespace ArcaneCore.Game.Tests.ItemMechanics;

/// <summary>
/// Timed items and map/area-limited items (vmangos Item.cpp:243-262, 1087-1107, Player.cpp:1155,
/// 6643-6656, 10881-10909, 19676-19695).
/// </summary>
public sealed class ItemMaintenanceTests
{
    private const uint TimedStone = 94001; // Duration 1800 (classic-db 4986 shape)
    private const uint GordokKey = 94002; // AreaBound 2557 (Dire Maul), zone-limited
    private const uint MapKey = 94003; // MapBound 429

    private static readonly ItemTemplateStore Store = new(
        [
            .. Templates,
            new ItemTemplate { Entry = TimedStone, Class = 12, Name = "Timed Stone", DisplayId = 1, Duration = 1800 },
            new ItemTemplate { Entry = GordokKey, Class = 13, Name = "Gordok Key", DisplayId = 1, AreaBound = 2557 },
            new ItemTemplate { Entry = MapKey, Class = 13, Name = "Map Key", DisplayId = 1, MapBound = 429 },
        ], []);

    private static (Player Player, FakeSession Session) Make(uint zone = 12, uint map = 0, uint guid = 1)
    {
        (Player player, FakeSession session) = CreatePlayer(guid);
        player.Inventory.Templates = Store;
        player.Inventory.Load([]);
        player.ZoneId = zone;
        player.MapId = map;
        return (player, session);
    }

    [Fact]
    public void TimedItem_TicksInSeconds_AndIsDestroyedAtZero()
    {
        (Player player, _) = Make();
        Item stone = Give(player.Inventory, TimedStone);
        Assert.Equal(1800u, stone.Duration);

        ItemMaintenance.Tick(player, 1000);
        ItemMaintenance.Tick(player, 1010);
        Assert.Equal(1790u, stone.Duration);
        ItemMaintenance.Tick(player, 1010); // same second: no change
        Assert.Equal(1790u, stone.Duration);

        ItemMaintenance.Tick(player, 1010 + 1790); // exactly the remainder: destroyed (Duration <= diff)
        Assert.Null(player.Inventory.GetItemByGuid(stone.Guid));
        Assert.Equal(0u, player.Inventory.GetItemCount(TimedStone));
    }

    [Fact]
    public void TimedItem_SendsTimeUpdateOnceWhenFirstSeen_WithGuidAndSeconds()
    {
        (Player player, FakeSession session) = Make();
        Item stone = Give(player.Inventory, TimedStone);
        session.Clear();

        ItemMaintenance.Tick(player, 500);
        ItemMaintenance.Tick(player, 501);
        var updates = session.Sent.Where(p => p.Opcode == WorldOpcode.SmsgItemTimeUpdate).ToList();
        (WorldOpcode _, byte[] payload) = Assert.Single(updates);
        Assert.Equal(12, payload.Length);
        Assert.Equal(stone.Guid.Value, BitConverter.ToUInt64(payload, 0));
        Assert.Equal(1800u, BitConverter.ToUInt32(payload, 8));
    }

    [Fact]
    public void TimedItem_MovedBetweenBags_KeepsItsRemainingTime_NotDuplicated()
    {
        (Player player, FakeSession session) = Make();
        PlayerInventory inv = player.Inventory;
        Item stone = Give(inv, TimedStone);
        Item pouch = Give(inv, SmallBrownPouch);
        Assert.True(inv.AutoEquipItem(pouch.BagSlot, pouch.Slot));
        ItemMaintenance.Tick(player, 100);
        ItemMaintenance.Tick(player, 160);
        Assert.Equal(1740u, stone.Duration);

        inv.SwapItem(stone.BagSlot, stone.Slot, pouch.Slot, 2);
        session.Clear();
        ItemMaintenance.Tick(player, 170);
        Assert.Equal(1730u, stone.Duration); // ticked once, not twice
        Assert.DoesNotContain(session.Sent, p => p.Opcode == WorldOpcode.SmsgItemTimeUpdate);
    }

    [Fact]
    public void ZoneBoundItem_DestroyedWhenAliveAndLeavingArea_KeptByGhost_DestroyedOnResurrect()
    {
        (Player player, _) = Make(zone: 2557);
        PlayerInventory inv = player.Inventory;
        Item key = Give(inv, GordokKey);
        ItemMaintenance.Tick(player, 10);
        Assert.Same(key, inv.GetItemByGuid(key.Guid)); // inside its area

        player.Health = 0; // ghost: not alive
        player.ZoneId = 12;
        ItemMaintenance.Tick(player, 11);
        Assert.Same(key, inv.GetItemByGuid(key.Guid)); // patch 1.7.0: a ghost keeps it

        player.Health = 10; // resurrect outside the zone
        ItemMaintenance.Tick(player, 12);
        Assert.Null(inv.GetItemByGuid(key.Guid));
    }

    [Fact]
    public void ZoneBoundItem_AliveLeavingTheArea_IsDestroyedAtOnce()
    {
        (Player player, _) = Make(zone: 2557);
        PlayerInventory inv = player.Inventory;
        Item key = Give(inv, GordokKey);
        ItemMaintenance.Tick(player, 10);
        player.ZoneId = 40;
        ItemMaintenance.Tick(player, 11);
        Assert.Null(inv.GetItemByGuid(key.Guid));
    }

    [Fact]
    public void MapBoundItem_DestroyedOutsideItsMap_KeptInside()
    {
        (Player inside, _) = Make(map: 429);
        Item kept = Give(inside.Inventory, MapKey);
        ItemMaintenance.Tick(inside, 10);
        Assert.Same(kept, inside.Inventory.GetItemByGuid(kept.Guid));

        (Player outside, _) = Make(map: 0);
        Item gone = Give(outside.Inventory, MapKey);
        ItemMaintenance.Tick(outside, 10);
        Assert.Null(outside.Inventory.GetItemByGuid(gone.Guid));
    }

    [Fact]
    public void ExpiredBag_TakesItsContentsWithIt()
    {
        (Player player, _) = Make();
        PlayerInventory inv = player.Inventory;
        var timedBag = new ItemTemplateStore(
            [.. Templates, new ItemTemplate { Entry = 94010, Class = 1, Name = "Timed Bag", DisplayId = 1, InventoryType = 18, ContainerSlots = 4, Duration = 5 }], []);
        inv.Templates = timedBag;
        Item bag = Give(inv, 94010);
        Assert.True(inv.AutoEquipItem(bag.BagSlot, bag.Slot));
        Item shirt = Give(inv, RecruitsShirt);
        inv.SwapItem(shirt.BagSlot, shirt.Slot, bag.Slot, 0);

        ItemMaintenance.Tick(player, 100);
        ItemMaintenance.Tick(player, 106);
        Assert.Null(inv.GetItemByGuid(bag.Guid));
        Assert.Equal(0u, inv.GetItemCount(RecruitsShirt));
    }

    [Fact]
    public void Updater_RunsOnlyEveryConfiguredInterval()
    {
        (Player player, _) = Make();
        Item stone = Give(player.Inventory, TimedStone);
        var clock = new FixedClock(1000);
        var updater = new ItemMaintenanceUpdater(new ItemMechanicsOptions { ZoneLimitCheckMs = 1000 }, clock);
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        world.AddPlayer(player);

        updater.Update(map, 400);
        clock.Seconds = 1005;
        updater.Update(map, 400); // 800 ms: not yet
        Assert.Equal(1800u, stone.Duration);
        updater.Update(map, 300); // 1100 ms: first run only tracks
        clock.Seconds = 1007;
        updater.Update(map, 1000);
        Assert.Equal(1798u, stone.Duration);
    }

    [Fact]
    public void PendingSettlement_FreezesDurations_UntilItEnds_ThenAppliesTheElapsedTime()
    {
        (Player player, _) = Make();
        Item stone = Give(player.Inventory, TimedStone);
        ItemMaintenance.Tick(player, 1000);

        Guid operation = Guid.NewGuid();
        Assert.True(player.BeginQuestSettlement(operation));
        ItemMaintenance.Tick(player, 1010);
        ItemMaintenance.Tick(player, 1020);
        Assert.Equal(1800u, stone.Duration);

        Assert.True(player.EndQuestSettlement(operation));
        ItemMaintenance.Tick(player, 1030);
        Assert.Equal(1770u, stone.Duration); // the held seconds are not lost
    }

    [Fact]
    public void TickBetweenStageAndPublish_DoesNotInvalidateTheEconomyStage()
    {
        (Player player, _) = Make();
        PlayerInventory inv = player.Inventory;
        Item stone = Give(inv, TimedStone);
        Item shirt = Give(inv, RecruitsShirt);
        ItemMaintenance.Tick(player, 1000);

        Assert.Equal(InventoryResult.Ok, inv.TryStageEconomyTransfer([shirt.Guid], [], out EconomyInventoryStage? stage, trade: true));
        Guid operation = Guid.NewGuid();
        Assert.True(player.BeginQuestSettlement(operation)); // what EconomySettlements.TryStart does
        ItemMaintenance.Tick(player, 1005); // lands while the DB commit is in flight

        using (player.BeginQuestSettlementPublication(operation))
        {
            inv.ApplyEconomyTransfer(stage!);
        }

        Assert.Null(inv.GetItemByGuid(shirt.Guid));
        Assert.Equal(1800u, stone.Duration);
    }

    [Fact]
    public void PendingSettlement_DoesNotThrow_DoesNotDestroy_AndDoesNotStarveOtherPlayers()
    {
        (Player held, _) = Make(guid: 1);
        (Player other, _) = Make(guid: 2);
        var timedShort = new ItemTemplateStore(
            [.. Templates, new ItemTemplate { Entry = 94011, Class = 12, Name = "Short Timer", DisplayId = 1, Duration = 5 },
                new ItemTemplate { Entry = TimedStone, Class = 12, Name = "Timed Stone", DisplayId = 1, Duration = 1800 }], []);
        held.Inventory.Templates = timedShort;
        other.Inventory.Templates = timedShort;
        Item expiring = Give(held.Inventory, 94011);
        Item stone = Give(other.Inventory, TimedStone);
        var clock = new FixedClock(1000);
        var updater = new ItemMaintenanceUpdater(new ItemMechanicsOptions { ZoneLimitCheckMs = 1000 }, clock);
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        world.AddPlayer(held);
        world.AddPlayer(other);
        updater.Update(map, 1000); // tracks

        Assert.True(held.BeginQuestSettlement(Guid.NewGuid()));
        clock.Seconds = 1010;
        updater.Update(map, 1000);

        Assert.Same(expiring, held.Inventory.GetItemByGuid(expiring.Guid)); // held: untouched, no exception
        Assert.Equal(1790u, stone.Duration); // the other player still ticked
    }

    [Fact]
    public void StaleZoneAfterTeleport_NeverDestroysAnAreaBoundItem_UntilTheZoneIsRefreshed()
    {
        (Player player, _) = Make(zone: 12, map: 0);
        PlayerInventory inv = player.Inventory;
        var updater = new ItemMaintenanceUpdater(new ItemMechanicsOptions(), new FixedClock(1000));
        ItemMaintenance.Tick(player, 10);
        Item key = Give(inv, GordokKey); // carried across the teleport
        Assert.Same(key, inv.GetItemByGuid(key.Guid));

        updater.OnPlayerRemoved(null!, player); // Map.RemovePlayer during the far teleport
        player.MapId = 429; // arrival; no terrain, so ZoneId is still the old map's value
        ItemMaintenance.Tick(player, 11);
        Assert.Same(key, inv.GetItemByGuid(key.Guid)); // stale zone 12 must not destroy it

        player.ZoneId = 2557; // CMSG_ZONEUPDATE: inside its area
        ItemMaintenance.Tick(player, 12);
        Assert.Same(key, inv.GetItemByGuid(key.Guid));
    }

    [Fact]
    public void RefreshedZoneAfterTeleport_OutsideTheArea_DestroysTheItem_EvenWhenTheZoneIdIsUnchanged()
    {
        (Player player, _) = Make(zone: 12, map: 0);
        PlayerInventory inv = player.Inventory;
        var updater = new ItemMaintenanceUpdater(new ItemMechanicsOptions(), new FixedClock(1000));
        ItemMaintenance.Tick(player, 10);
        Item key = Give(inv, GordokKey);

        updater.OnPlayerRemoved(null!, player);
        player.MapId = 1;
        ItemMaintenance.Tick(player, 11);
        Assert.Same(key, inv.GetItemByGuid(key.Guid));

        player.ZoneId = 12; // the client confirms the same zone value
        ItemMaintenance.Tick(player, 12);
        Assert.Null(inv.GetItemByGuid(key.Guid));
    }

    [Fact]
    public void ZoneId_EveryAssignmentIsARefresh_EvenOfTheSameValue()
    {
        (Player player, _) = Make(zone: 12);
        uint before = player.ZoneRevision;
        player.ZoneId = 12;
        Assert.Equal(before + 1, player.ZoneRevision);
        Assert.Equal(12u, player.ZoneId);
    }

    private sealed class FixedClock(long seconds) : TimeProvider
    {
        public long Seconds { get; set; } = seconds;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Seconds);
    }
}
