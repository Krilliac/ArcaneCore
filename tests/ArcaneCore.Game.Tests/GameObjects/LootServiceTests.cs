using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.GameObjects;

public sealed class LootServiceTests
{
    private const uint WolfLoot = 299;
    private const uint SkinLoot = 7299;
    private const uint BareEntry = 300;

    private sealed class Rig
    {
        public required WorldRuntime World { get; init; }
        public required Map Map { get; init; }
        public required CreatureMapSystem Creatures { get; init; }
        public required LootService Loot { get; init; }
        public required FakeQuestJournal Quests { get; init; }
        public required FakeGroups Groups { get; init; }

        public (Player Player, FakeSession Session) Join(uint guid, float x = 0, float y = 0)
        {
            (Player player, FakeSession session) = Player(guid, x, y);
            World.AddPlayer(player);
            World.RunTick(50);
            session.Clear();
            return (player, session);
        }

        public Creature Wolf => Creatures.Creatures.Single(c => c.Entry == WolfEntry);

        public Creature KillWolf(Player killer)
        {
            Creature wolf = Wolf;
            Map.Combat.DealDamage(killer, wolf, wolf.Health, direct: false);
            Assert.Equal(CreatureDeathState.Corpse, wolf.DeathState);
            return wolf;
        }
    }

    private static Rig CreateRig(
        IEnumerable<(LootTableKind, LootStoreRow)>? rows = null, uint minGold = 10, uint maxGold = 10, uint skin = SkinLoot,
        CreatureOptions? creatureOptions = null)
    {
        CreatureContent creatures = Content([Template(), Template(BareEntry)], [Spawn(1, WolfEntry, 3, 0), Spawn(2, BareEntry, -3, 0)]);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateSystem(creatures, creatureOptions);
        var content = new LootContent(rows ??
            [
                (LootTableKind.Creature, Row(WolfLoot, ItemTestData.ToughJerky, 100, minOrRef: 3, max: 3)),
                (LootTableKind.Creature, Row(WolfLoot, QuestItem, -100)),
                (LootTableKind.Skinning, Row(SkinLoot, Hide, 100)),
            ],
            [new CreatureLootInfo(WolfEntry, WolfLoot, skin, minGold, maxGold)]);
        var quests = new FakeQuestJournal();
        var groups = new FakeGroups();
        var loot = new LootService(content, random: new Random(11)) { Items = ItemStore, Quests = quests, Groups = groups, CreatureOptions = creatureOptions ?? new CreatureOptions() };
        map.Combat.UnitKilled += (killer, victim) => loot.OnCreatureKilled(killer, victim);
        return new Rig { World = world, Map = map, Creatures = system, Loot = loot, Quests = quests, Groups = groups };
    }

    private static ParsedLoot LootResponse(FakeSession session)
        => ParsedLoot.Parse(Assert.Single(Packets(session, WorldOpcode.SmsgLootResponse)).Payload);

    private static uint DynFlagsSeenBy(FakeSession session, Creature creature)
        => DrainBlocks(session).Last(b => b.Guids.Contains(creature.Guid.Value) && b.Values.ContainsKey(UpdateFields.UnitDynamicFlags))
            .Values[UpdateFields.UnitDynamicFlags];

    [Fact]
    public void Kill_MakesTheCorpseLootableForTheKillerOnly()
    {
        Rig rig = CreateRig();
        (Player killer, FakeSession killerSession) = rig.Join(1);
        (_, FakeSession bystander) = rig.Join(2, 1, 0);
        Creature wolf = rig.KillWolf(killer);
        rig.World.RunTick(50);

        Assert.Equal(LootService.UnitDynFlagLootable, DynFlagsSeenBy(killerSession, wolf) & LootService.UnitDynFlagLootable);
        Assert.Equal(0u, DynFlagsSeenBy(bystander, wolf) & LootService.UnitDynFlagLootable);
        LootBag bag = rig.Loot.FindLoot(wolf.Guid)!;
        Assert.Equal(10u, bag.Gold);
        Assert.Equal([killer.Guid], bag.Recipients);
    }

    [Fact]
    public void FullCorpseFlow_OpenTakeMoneyRelease_EndsSkinnable()
    {
        Rig rig = CreateRig();
        (Player player, FakeSession session) = rig.Join(1);
        Creature wolf = rig.KillWolf(player);
        rig.World.RunTick(50);
        session.Clear();

        Assert.Equal(LootResult.Ok, rig.Loot.Open(player, wolf.Guid));
        Assert.True(player.UnitFlags.HasFlag(UnitFlags.Looting));
        ParsedLoot loot = LootResponse(session);
        Assert.Equal((wolf.Guid.Value, LootType.Corpse, 10u), (loot.Guid, loot.Type, loot.Gold));
        ParsedLootItem jerky = Assert.Single(loot.Items); // the quest drop is not generated: nobody needs it
        Assert.Equal((ItemTestData.ToughJerky, 3u, ItemTestData.Get(ItemTestData.ToughJerky).DisplayId), (jerky.ItemId, jerky.Count, jerky.DisplayId));

        Assert.Equal(InventoryResult.Ok, rig.Loot.TakeItem(player, jerky.Slot));
        Assert.Equal(3u, player.Inventory.GetItemCount(ItemTestData.ToughJerky));
        Assert.Equal([(player.Guid, ItemTestData.ToughJerky, 3u)], rig.Quests.Looted);
        Assert.Equal(InventoryResult.AlreadyLooted, rig.Loot.TakeItem(player, jerky.Slot));
        Assert.Equal(InventoryResult.LootCantLootThatNow, rig.Loot.TakeItem(player, 9));

        uint before = player.Money;
        Assert.True(rig.Loot.TakeMoney(player));
        Assert.Equal(before + 10, player.Money);
        Assert.False(rig.Loot.TakeMoney(player));
        List<(WorldOpcode Opcode, byte[] Payload)> packets = NonUpdatePackets(session);
        Assert.Contains(packets, p => p.Opcode == WorldOpcode.SmsgLootRemoved);
        Assert.Contains(packets, p => p.Opcode == WorldOpcode.SmsgLootClearMoney);
        Assert.DoesNotContain(packets, p => p.Opcode == WorldOpcode.SmsgLootMoneyNotify); // solo: no notify
        Assert.Equal([player.Guid], rig.Quests.MoneyEvents);

        rig.Loot.Release(player, wolf.Guid);
        Assert.False(player.UnitFlags.HasFlag(UnitFlags.Looting));
        byte[] release = Assert.Single(Packets(session, WorldOpcode.SmsgLootReleaseResponse)).Payload;
        Assert.Equal(wolf.Guid.Value, BitConverter.ToUInt64(release, 0));
        Assert.Equal(1, release[8]);
        Assert.True(wolf.UnitFlags.HasFlag(UnitFlags.Skinnable));
        Assert.Equal(0u, wolf.GetUInt32(UpdateFields.UnitDynamicFlags) & LootService.UnitDynFlagLootable);
        Assert.Equal(LootResult.NotAllowed, rig.Loot.Open(player, wolf.Guid));
    }

    [Fact]
    public void LootedOutCorpse_WithoutSkinning_DecaysSooner()
    {
        var options = new CreatureOptions { CorpseDecayNormalSeconds = 100 };
        Rig rig = CreateRig(skin: 0, creatureOptions: options);
        (Player player, _) = rig.Join(1);
        Creature wolf = rig.KillWolf(player);
        Assert.Equal(100_000u, wolf.CorpseDecayMs);
        rig.Loot.Open(player, wolf.Guid);
        rig.Loot.TakeItem(player, 0);
        rig.Loot.TakeMoney(player);
        rig.Loot.Release(player, wolf.Guid);
        Assert.Equal(50_000u, wolf.CorpseDecayMs);
        Assert.False(wolf.UnitFlags.HasFlag(UnitFlags.Skinnable));
    }

    [Fact]
    public void CreatureWithNothingToDrop_IsNeverLootable_AndImmediatelySkinnableWhenItHasSkinLoot()
    {
        Rig rig = CreateRig(rows: [], minGold: 0, maxGold: 0);
        (Player player, _) = rig.Join(1);
        Creature wolf = rig.KillWolf(player);
        Assert.Equal(0u, wolf.GetUInt32(UpdateFields.UnitDynamicFlags) & LootService.UnitDynFlagLootable);
        Assert.True(wolf.UnitFlags.HasFlag(UnitFlags.Skinnable));

        Creature bare = rig.Creatures.Creatures.Single(c => c.Entry == BareEntry);
        rig.Map.Combat.DealDamage(player, bare, bare.Health, direct: false);
        Assert.True(rig.Loot.FindLoot(bare.Guid)!.IsEmpty);
        Assert.False(bare.UnitFlags.HasFlag(UnitFlags.Skinnable));
    }

    [Fact]
    public void Open_IsRefused_ForStrangers_TheDead_TheFar_AndLivingCreatures()
    {
        Rig rig = CreateRig();
        (Player killer, _) = rig.Join(1);
        (Player stranger, FakeSession strangerSession) = rig.Join(2, 1, 0);
        Assert.Equal(LootResult.NotFound, rig.Loot.Open(killer, rig.Wolf.Guid)); // alive
        Creature wolf = rig.KillWolf(killer);

        Assert.Equal(LootResult.NotAllowed, rig.Loot.Open(stranger, wolf.Guid));
        Assert.Single(Packets(strangerSession, WorldOpcode.SmsgLootReleaseResponse));
        Assert.False(stranger.UnitFlags.HasFlag(UnitFlags.Looting));
        Assert.Equal(LootResult.NotFound, rig.Loot.Open(killer, new ObjectGuid(0xF130000000001234)));

        killer.Relocate(30, 0, 83.5f, 0, 0);
        Assert.Equal(LootResult.TooFar, rig.Loot.Open(killer, wolf.Guid));
        killer.Relocate(0, 0, 83.5f, 0, 0);
        killer.Health = 0;
        Assert.Equal(LootResult.Dead, rig.Loot.Open(killer, wolf.Guid));
        Assert.Null(rig.Loot.OpenLootOf(killer));
    }

    [Fact]
    public void TakeItem_WithFullBags_KeepsTheItemInTheLoot()
    {
        Rig rig = CreateRig();
        (Player player, FakeSession session) = rig.Join(1);
        for (int i = 0; i < 16; i++)
        {
            ItemTestData.Give(player.Inventory, ItemTestData.WornShortsword);
        }

        Creature wolf = rig.KillWolf(player);
        rig.Loot.Open(player, wolf.Guid);
        session.Clear();
        Assert.Equal(InventoryResult.InventoryFull, rig.Loot.TakeItem(player, 0));
        Assert.Contains(NonUpdatePackets(session), p => p.Opcode == WorldOpcode.SmsgInventoryChangeFailure);
        Assert.False(rig.Loot.FindLoot(wolf.Guid)!.Items[0].IsLooted);
        Assert.Empty(rig.Quests.Looted);
    }

    [Fact]
    public void TakeItem_AfterMovingAway_ClosesTheWindow()
    {
        Rig rig = CreateRig();
        (Player player, FakeSession session) = rig.Join(1);
        Creature wolf = rig.KillWolf(player);
        rig.Loot.Open(player, wolf.Guid);
        player.Relocate(40, 0, 83.5f, 0, 0);
        session.Clear();

        Assert.Equal(InventoryResult.LootCantLootThatNow, rig.Loot.TakeItem(player, 0));
        Assert.Single(Packets(session, WorldOpcode.SmsgLootReleaseResponse));
        Assert.Null(rig.Loot.OpenLootOf(player));
        Assert.Equal(0u, player.Inventory.GetItemCount(ItemTestData.ToughJerky));
        Assert.False(rig.Loot.TakeMoney(player));
    }

    [Fact]
    public void QuestDrop_OnlyForPlayersWhoNeedIt()
    {
        Rig rig = CreateRig();
        (Player needy, FakeSession needySession) = rig.Join(1);
        (Player other, FakeSession otherSession) = rig.Join(2, 1, 0);
        rig.Groups.Create(LootMethod.FreeForAll, needy, other);
        rig.Quests.Needs.Add((needy.Guid, QuestItem));
        Creature wolf = rig.KillWolf(needy);

        rig.Loot.Open(needy, wolf.Guid);
        rig.Loot.Open(other, wolf.Guid);
        ParsedLoot forNeedy = LootResponse(needySession);
        ParsedLoot forOther = LootResponse(otherSession);
        Assert.Contains(forNeedy.Items, i => i.ItemId == QuestItem);
        Assert.DoesNotContain(forOther.Items, i => i.ItemId == QuestItem);
        byte questSlot = forNeedy.Items.Single(i => i.ItemId == QuestItem).Slot;
        Assert.Equal(InventoryResult.LootCantLootThatNow, rig.Loot.TakeItem(other, questSlot));
        Assert.Equal(InventoryResult.Ok, rig.Loot.TakeItem(needy, questSlot));
        Assert.Empty(Packets(otherSession, WorldOpcode.SmsgLootRemoved)); // a personal slot: only the taker is told
    }

    [Fact]
    public void FreeForAll_BothMembersLoot_AndMoneyIsSplitWithNotify()
    {
        Rig rig = CreateRig(minGold: 11, maxGold: 11);
        (Player alice, FakeSession aliceSession) = rig.Join(1);
        (Player bob, FakeSession bobSession) = rig.Join(2, 1, 0);
        rig.Groups.Create(LootMethod.FreeForAll, alice, bob);
        Creature wolf = rig.KillWolf(alice);
        rig.World.RunTick(50);
        Assert.Equal(LootService.UnitDynFlagLootable, DynFlagsSeenBy(bobSession, wolf) & LootService.UnitDynFlagLootable);

        Assert.Equal(LootResult.Ok, rig.Loot.Open(alice, wolf.Guid));
        Assert.Equal(LootResult.Ok, rig.Loot.Open(bob, wolf.Guid));
        uint aliceMoney = alice.Money;
        uint bobMoney = bob.Money;
        aliceSession.Clear();
        bobSession.Clear();
        Assert.True(rig.Loot.TakeMoney(bob));
        Assert.Equal(aliceMoney + 5, alice.Money); // 11 / 2, remainder lost as in vmangos
        Assert.Equal(bobMoney + 5, bob.Money);
        Assert.Equal(5u, BitConverter.ToUInt32(Assert.Single(Packets(aliceSession, WorldOpcode.SmsgLootMoneyNotify)).Payload));
        bobSession.Clear();
        Assert.Equal(InventoryResult.Ok, rig.Loot.TakeItem(bob, 0));
        Assert.Contains(NonUpdatePackets(aliceSession), p => p.Opcode == WorldOpcode.SmsgLootRemoved); // shared slot: every viewer is told
        Assert.Equal(InventoryResult.AlreadyLooted, rig.Loot.TakeItem(alice, 0));
    }

    [Fact]
    public void MoneySplit_SkipsMembersOutOfRange()
    {
        Rig rig = CreateRig(minGold: 10, maxGold: 10);
        (Player alice, _) = rig.Join(1);
        (Player bob, _) = rig.Join(2, 1, 0);
        rig.Groups.Create(LootMethod.FreeForAll, alice, bob);
        Creature wolf = rig.KillWolf(alice);
        bob.Relocate(90, 0, 83.5f, 0, 0);
        uint bobMoney = bob.Money;
        rig.Loot.Open(alice, wolf.Guid);
        rig.Loot.TakeMoney(alice);
        Assert.Equal(bobMoney, bob.Money);
        Assert.Equal(10u, alice.Money);
    }

    [Fact]
    public void RoundRobin_OnlyTheLooterUntilReleased_ThenEveryone_AndTheTurnAdvances()
    {
        Rig rig = CreateRig();
        (Player alice, _) = rig.Join(1);
        (Player bob, FakeSession bobSession) = rig.Join(2, 1, 0);
        Group group = rig.Groups.Create(LootMethod.RoundRobin, alice, bob);
        Creature wolf = rig.KillWolf(bob);
        LootBag bag = rig.Loot.FindLoot(wolf.Guid)!;
        Assert.Equal(alice.Guid, bag.Owner); // first member after the (empty) last looter
        Assert.Equal(alice.Guid, group.LooterGuid);
        rig.World.RunTick(50);
        Assert.Equal(0u, DynFlagsSeenBy(bobSession, wolf) & LootService.UnitDynFlagLootable);

        Assert.Equal(LootResult.NotAllowed, rig.Loot.Open(bob, wolf.Guid));
        Assert.Equal(LootResult.Ok, rig.Loot.Open(alice, wolf.Guid));
        rig.Loot.Release(alice, wolf.Guid);
        Assert.True(bag.Owner.IsEmpty);
        rig.World.RunTick(50);
        Assert.Equal(LootService.UnitDynFlagLootable, DynFlagsSeenBy(bobSession, wolf) & LootService.UnitDynFlagLootable);
        Assert.Equal(LootResult.Ok, rig.Loot.Open(bob, wolf.Guid));
        Assert.Equal(InventoryResult.Ok, rig.Loot.TakeItem(bob, 0));
        rig.Loot.Release(bob, wolf.Guid);

        rig.Creatures.ForceRespawn(wolf);
        rig.KillWolf(alice);
        Assert.Equal(bob.Guid, rig.Loot.FindLoot(wolf.Guid)!.Owner);
    }

    [Fact]
    public void PartyLootItem_EveryRecipientGetsACopy()
    {
        Rig rig = CreateRig(rows: [(LootTableKind.Creature, Row(WolfLoot, PartyItem, 100))], minGold: 0, maxGold: 0);
        (Player alice, _) = rig.Join(1);
        (Player bob, _) = rig.Join(2, 1, 0);
        rig.Groups.Create(LootMethod.FreeForAll, alice, bob);
        Creature wolf = rig.KillWolf(alice);

        rig.Loot.Open(alice, wolf.Guid);
        Assert.Equal(InventoryResult.Ok, rig.Loot.TakeItem(alice, 0));
        Assert.Equal(InventoryResult.LootCantLootThatNow, rig.Loot.TakeItem(alice, 0));
        rig.Loot.Release(alice, wolf.Guid);
        Assert.False(rig.Loot.FindLoot(wolf.Guid)!.IsEmpty);
        Assert.Equal(LootResult.Ok, rig.Loot.Open(bob, wolf.Guid));
        Assert.Equal(InventoryResult.Ok, rig.Loot.TakeItem(bob, 0));
        Assert.Equal(1u, alice.Inventory.GetItemCount(PartyItem));
        Assert.Equal(1u, bob.Inventory.GetItemCount(PartyItem));
        Assert.True(rig.Loot.FindLoot(wolf.Guid)!.IsEmpty);
    }

    [Fact]
    public void Skinning_RequiresALootedOutSkinnableCorpse_AndGivesSkinLoot()
    {
        Rig rig = CreateRig();
        (Player player, FakeSession session) = rig.Join(1);
        Creature wolf = rig.KillWolf(player);
        Assert.Equal(LootResult.NotLootable, rig.Loot.OpenSkinning(player, wolf));

        rig.Loot.Open(player, wolf.Guid);
        rig.Loot.TakeItem(player, 0);
        rig.Loot.TakeMoney(player);
        rig.Loot.Release(player, wolf.Guid);
        session.Clear();
        Assert.Equal(LootResult.Ok, rig.Loot.OpenSkinning(player, wolf));
        Assert.False(wolf.UnitFlags.HasFlag(UnitFlags.Skinnable));
        ParsedLoot skin = LootResponse(session);
        Assert.Equal(LootType.Skinning, skin.Type);
        Assert.Equal(Hide, Assert.Single(skin.Items).ItemId);
        Assert.Equal(InventoryResult.Ok, rig.Loot.TakeItem(player, 0));
        rig.Loot.Release(player, wolf.Guid);
        Assert.Null(rig.Loot.FindLoot(wolf.Guid));
        Assert.Equal(LootResult.NotLootable, rig.Loot.OpenSkinning(player, wolf));
    }

    [Fact]
    public void OpenItem_LootableContainer_IsDestroyedWhenEmptied_LockedAndPlainItemsAreRefused()
    {
        Rig rig = CreateRig(rows: [(LootTableKind.Item, Row(Lockbox, ItemTestData.ToughJerky, 100, minOrRef: 2, max: 2))]);
        (Player player, FakeSession session) = rig.Join(1);
        Item clam = ItemTestData.Give(player.Inventory, Lockbox);
        Item locked = ItemTestData.Give(player.Inventory, LockedBox);
        Item plain = ItemTestData.Give(player.Inventory, ItemTestData.WornShortsword);
        session.Clear();

        Assert.Equal(LootResult.NotLootable, rig.Loot.OpenItem(player, plain));
        Assert.Equal(LootResult.Locked, rig.Loot.OpenItem(player, locked));
        Assert.Contains(NonUpdatePackets(session), p => p.Opcode == WorldOpcode.SmsgInventoryChangeFailure);

        Assert.Equal(LootResult.Ok, rig.Loot.OpenItem(player, clam));
        ParsedLoot loot = LootResponse(session);
        Assert.Equal(clam.Guid.Value, loot.Guid);
        rig.Loot.Release(player, clam.Guid); // nothing taken yet: the item stays with its loot
        Assert.NotNull(player.Inventory.GetItemByGuid(clam.Guid));
        Assert.Equal(LootResult.Ok, rig.Loot.OpenItem(player, clam));
        Assert.Equal(InventoryResult.Ok, rig.Loot.TakeItem(player, loot.Items[0].Slot));
        rig.Loot.Release(player, clam.Guid);
        Assert.Null(player.Inventory.GetItemByGuid(clam.Guid));
        Assert.Equal(2u, player.Inventory.GetItemCount(ItemTestData.ToughJerky));
        player.Health = 0;
        Assert.Equal(LootResult.Dead, rig.Loot.OpenItem(player, locked));
    }

    [Fact]
    public void Release_OfAnUntrackedGuid_StillClosesTheClient_AndRespawnDropsTheLoot()
    {
        Rig rig = CreateRig();
        (Player player, FakeSession session) = rig.Join(1);
        rig.Loot.Release(player, new ObjectGuid(42));
        Assert.Single(Packets(session, WorldOpcode.SmsgLootReleaseResponse));

        Creature wolf = rig.KillWolf(player);
        rig.Loot.Open(player, wolf.Guid);
        rig.Creatures.ForceRespawn(wolf);
        Assert.Equal(InventoryResult.LootCantLootThatNow, rig.Loot.TakeItem(player, 0)); // the corpse is gone
        Assert.Null(rig.Loot.OpenLootOf(player));
        rig.Map.Combat.DealDamage(player, rig.Creatures.Creatures.Single(c => c.Entry == BareEntry), 1000, direct: false);
        Assert.Null(rig.Loot.FindLoot(wolf.Guid)); // pruned on the next death
        rig.Loot.OnPlayerLeft(player);
    }

    [Fact]
    public void OpeningAnotherLoot_ReleasesThePreviousWindow()
    {
        Rig rig = CreateRig(minGold: 5, maxGold: 5);
        (Player player, FakeSession session) = rig.Join(1);
        Creature wolf = rig.KillWolf(player);
        Item clam = ItemTestData.Give(player.Inventory, Lockbox);
        rig.Loot.Open(player, wolf.Guid);
        session.Clear();
        rig.Loot.OpenItem(player, clam);
        Assert.Same(rig.Loot.FindLoot(clam.Guid), rig.Loot.OpenLootOf(player));
        Assert.Empty(rig.Loot.FindLoot(wolf.Guid)!.Viewers);
        Assert.Single(Packets(session, WorldOpcode.SmsgLootReleaseResponse));
    }

    [Fact]
    public void LootResponse_ListsOnlyVisibleSlots_WithTheirStableIndexes()
    {
        (Player alice, _) = Player(1);
        (Player bob, _) = Player(2);
        var bag = new LootBag(new ObjectGuid(0xF130000000000001), LootSourceKind.Creature, LootType.Corpse) { Gold = 7 };
        bag.Recipients.Add(alice.Guid);
        bag.Recipients.Add(bob.Guid);
        bag.Add(new LootItem(0, 10, 1, false, false, 100));
        var quest = new LootItem(1, 11, 2, true, false, 101);
        quest.AllowedLooters.Add(bob.Guid);
        bag.Add(quest);
        bag.Items[0].IsLooted = true;

        ParsedLoot forAlice = ParsedLoot.Parse(LootPackets.LootResponse(bag, alice));
        ParsedLoot forBob = ParsedLoot.Parse(LootPackets.LootResponse(bag, bob));
        Assert.Empty(forAlice.Items);
        Assert.Equal(7u, forAlice.Gold);
        Assert.Equal(new ParsedLootItem(1, 11, 2, 101, LootSlotType.AllowLoot), Assert.Single(forBob.Items));

        bag.Owner = bob.Guid;
        Assert.Equal(0u, ParsedLoot.Parse(LootPackets.LootResponse(bag, alice)).Gold);
        Assert.False(bag.HasSomethingFor(alice));
        Assert.True(bag.HasSomethingFor(bob));
    }
}
