using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>
/// The special-source seams of <see cref="LootService"/> (fishing bobbers and holes, pickpocket, disenchant):
/// the loot type the client is sent (vmangos Player.cpp:7980-7995), a registered bag with its own release handler,
/// the distance exemption (LootHandler.cpp:56-70) and the money-share rule (LootHandler.cpp:274-290).
/// </summary>
public sealed class LootSpecialBagTests
{
    private const uint SourceEntry = 700;

    private sealed class Recorder : ILootReleaseHandler
    {
        public List<(Player Player, LootBag Bag)> Released { get; } = [];

        public void OnReleased(Player player, LootBag bag) => Released.Add((player, bag));
    }

    private sealed class Rig
    {
        public required WorldRuntime World { get; init; }
        public required GameObjectMapSystem System { get; init; }
        public required LootService Loot { get; init; }
        public required GameObject Source { get; init; }
        public required Player Player { get; init; }
        public required FakeSession Session { get; init; }
    }

    private static Rig CreateRig(float playerX = 0)
    {
        var content = new GameObjectContent([GoTemplate(SourceEntry, GameObjectType.Generic)], [], [], [], []);
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var loot = new LootService(new LootContent([], []), random: new Random(5)) { Items = ItemStore };
        var system = new GameObjectMapSystem(map, content, loot);
        map.AddUpdater(system);
        (Player player, FakeSession session) = Player(1, playerX, 0);
        world.AddPlayer(player);
        world.RunTick(50);
        GameObject source = system.Summon(SourceEntry, 0, 0, player.Z, 0)!;
        world.RunTick(50);
        session.Clear();
        return new Rig { World = world, System = system, Loot = loot, Source = source, Player = player, Session = session };
    }

    private static LootBag Bag(Rig rig, LootType type, LootSourceKind kind = LootSourceKind.GameObject, uint gold = 0, params uint[] items)
    {
        var bag = new LootBag(rig.Source.Guid, kind, type) { Gold = gold };
        bag.Recipients.Add(rig.Player.Guid);
        byte slot = 0;
        foreach (uint item in items)
        {
            bag.Add(new LootItem(slot++, item, 1, false, false, 4));
        }

        return bag;
    }

    [Theory]
    [InlineData(LootType.Corpse, 1)]
    [InlineData(LootType.Pickpocketing, 2)]
    [InlineData(LootType.Fishing, 3)]
    [InlineData(LootType.Disenchanting, 4)]
    [InlineData(LootType.Skinning, 2)]
    [InlineData(LootType.Insignia, 2)]
    [InlineData(LootType.FishingHole, 3)]
    [InlineData(LootType.FishingFail, 3)]
    public void WireType_FollowsVmangosSendLoot(LootType type, byte wire)
    {
        Rig rig = CreateRig();
        LootBag bag = Bag(rig, type);
        Assert.Equal(wire, LootTypes.ToWire(type));
        Assert.Equal(wire, LootPackets.LootResponse(bag, rig.Player)[8]);
    }

    [Fact]
    public void ShowSpecial_OpensTheWindow_WithTheFishingWireType_AndTracksTheBag()
    {
        Rig rig = CreateRig();
        LootBag bag = Bag(rig, LootType.FishingHole, items: ItemTestData.ToughJerky);

        Assert.Equal(LootResult.Ok, rig.Loot.ShowSpecial(rig.Player, rig.Source, bag));

        ParsedLoot window = ParsedLoot.Parse(Assert.Single(Packets(rig.Session, WorldOpcode.SmsgLootResponse)).Payload);
        Assert.Equal((rig.Source.Guid.Value, LootType.Fishing), (window.Guid, window.Type));
        Assert.Equal(ItemTestData.ToughJerky, Assert.Single(window.Items).ItemId);
        Assert.Same(bag, rig.Loot.OpenLootOf(rig.Player));
        Assert.Same(bag, rig.Loot.FindLoot(rig.Source.Guid));
    }

    [Fact]
    public void ReleaseHandler_IsCalledInsteadOfTheChestSettlement()
    {
        Rig rig = CreateRig();
        var handler = new Recorder();
        LootBag bag = Bag(rig, LootType.Fishing, items: ItemTestData.ToughJerky);
        bag.ReleaseHandler = handler;
        rig.Loot.ShowSpecial(rig.Player, rig.Source, bag);

        rig.Loot.Release(rig.Player, rig.Source.Guid);

        (Player releasedBy, LootBag releasedBag) = Assert.Single(handler.Released);
        Assert.Same(rig.Player, releasedBy);
        Assert.Same(bag, releasedBag);
        Assert.Empty(bag.Viewers);
        Assert.Null(rig.Loot.OpenLootOf(rig.Player));
        Assert.Equal(GameObjectLootState.Ready, rig.Source.LootState); // the chest rule (empty => JustDeactivated) did not run
    }

    [Fact]
    public void AnOrdinaryGameObjectBag_IsRefusedFromFarAway_ButIgnoreDistanceTakesIt()
    {
        Rig far = CreateRig();
        LootBag normal = Bag(far, LootType.Corpse, items: ItemTestData.ToughJerky);
        far.Loot.ShowSpecial(far.Player, far.Source, normal);
        far.Player.Relocate(60, 0, far.Player.Z, 0, 0);
        Assert.NotEqual(InventoryResult.Ok, far.Loot.TakeItem(far.Player, 0));

        Rig bobber = CreateRig();
        LootBag exempt = Bag(bobber, LootType.Fishing, items: ItemTestData.ToughJerky);
        exempt.IgnoreDistance = true;
        bobber.Loot.ShowSpecial(bobber.Player, bobber.Source, exempt);
        bobber.Player.Relocate(60, 0, bobber.Player.Z, 0, 0);
        Assert.Equal(InventoryResult.Ok, bobber.Loot.TakeItem(bobber.Player, 0));
    }

    [Fact]
    public void SourceCheck_ReplacesTheDefaultValidity()
    {
        Rig rig = CreateRig();
        bool valid = false;
        LootBag bag = Bag(rig, LootType.Pickpocketing, items: ItemTestData.ToughJerky);
        bag.SourceCheck = _ => valid;
        rig.Loot.ShowSpecial(rig.Player, rig.Source, bag);

        Assert.NotEqual(InventoryResult.Ok, rig.Loot.TakeItem(rig.Player, 0));
        rig.Player.Session.Send(WorldOpcode.SmsgLootReleaseResponse, []);
        rig.Loot.ShowSpecial(rig.Player, rig.Source, bag);
        valid = true;
        Assert.Equal(InventoryResult.Ok, rig.Loot.TakeItem(rig.Player, 0));
    }

    [Fact]
    public void MoneyShare_CanBeSwitchedOff_ForAMultiRecipientCreatureBag()
    {
        foreach ((bool share, uint expected) in new[] { (true, 50u), (false, 100u) })
        {
            Rig rig = CreateRig();
            (Player other, FakeSession otherSession) = Player(2, 1, 0);
            rig.World.AddPlayer(other);
            rig.World.RunTick(50);
            otherSession.Clear();
            var groups = new FakeGroups();
            groups.Create(LootMethod.FreeForAll, rig.Player, other);
            rig.Loot.Groups = groups;
            LootBag bag = Bag(rig, LootType.Corpse, LootSourceKind.Creature, gold: 100);
            bag.Recipients.Add(other.Guid);
            bag.ShareMoney = share;
            rig.Loot.ShowSpecial(rig.Player, rig.Source, bag);
            uint before = rig.Player.Money;

            Assert.True(rig.Loot.TakeMoney(rig.Player));

            Assert.Equal(expected, rig.Player.Money - before);
        }
    }
}
