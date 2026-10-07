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
        map.Combat.DamageDealt += (attacker, victim, damage, _, _) => loot.OnCreatureDamaged(attacker, victim, damage);
        map.Combat.UnitKilled += (killer, victim) => loot.OnCreatureKilled(killer, victim);
        return new Rig { World = world, Map = map, Creatures = system, Loot = loot, Quests = quests, Groups = groups };
    }

    private static ParsedLoot LootResponse(FakeSession session)
        => ParsedLoot.Parse(Assert.Single(Packets(session, WorldOpcode.SmsgLootResponse)).Payload);

    private static uint DynFlagsSeenBy(FakeSession session, Creature creature)
        => DrainBlocks(session).Last(b => b.Guids.Contains(creature.Guid.Value) && b.Values.ContainsKey(UpdateFields.UnitDynamicFlags))
            .Values[UpdateFields.UnitDynamicFlags];

    [Fact]
    public void BossGold_UsesTheVmangosShiftedFormula_AndMaxBelowMinUsesMax()
    {
        // Onyxia-sized range (937551..1273511): vmangos GenerateMoneyLoot shifts by 8 bits (LootMgr.cpp:735-746).
        Rig boss = CreateRig(minGold: 937551, maxGold: 1273511);
        (Player killer, _) = boss.Join(1);
        Creature wolf = boss.KillWolf(killer);
        uint gold = boss.Loot.FindLoot(wolf.Guid)!.Gold;
        Assert.Equal(0u, gold % 256);
        Assert.InRange(gold, 937551u >> 8 << 8, 1273511u);

        // max < min (3 real classic-db rows): vmangos pays maxAmount, not min.
        Rig inverted = CreateRig(minGold: 20000, maxGold: 16194);
        (Player killer2, _) = inverted.Join(1);
        Creature wolf2 = inverted.KillWolf(killer2);
        Assert.Equal(16194u, inverted.Loot.FindLoot(wolf2.Guid)!.Gold);
    }

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
        // Rate.Corpse.Decay.Looted = 0 (retail default): a third of the 120 s respawn delay, which is longer than the 100 s corpse delay (Creature.cpp:3369-3380).
        Assert.Equal(40_000u, wolf.CorpseDecayMs);
        Assert.False(wolf.UnitFlags.HasFlag(UnitFlags.Skinnable));
    }

    [Fact]
    public void CreatureWithNothingToDrop_IsNeverLootable_AndImmediatelySkinnableWhenItHasSkinLoot()
    {
        Rig rig = CreateRig(rows: [(LootTableKind.Skinning, Row(SkinLoot, Hide, 100))], minGold: 0, maxGold: 0);
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
    public void FirstDamageTagsCreature_AndASecondPlayersKillingBlowDoesNotStealItsLoot()
    {
        // vmangos Unit.cpp:800-819 tags on the first landed damage; Object.cpp:779-799 filters the tapped flags.
        Rig rig = CreateRig();
        (Player tagger, _) = rig.Join(1);
        (Player finisher, _) = rig.Join(2, 1, 0);
        Creature wolf = rig.Wolf;
        rig.Map.Combat.DealDamage(tagger, wolf, 1, direct: false);
        rig.Map.Combat.DealDamage(finisher, wolf, 1, direct: false);
        Assert.Equal(LootService.UnitDynFlagTapped | LootService.UnitDynFlagTappedByPlayer,
            wolf.GetUInt32(UpdateFields.UnitDynamicFlags) & (LootService.UnitDynFlagTapped | LootService.UnitDynFlagTappedByPlayer));

        rig.Map.Combat.DealDamage(finisher, wolf, wolf.Health, direct: false);
        LootBag bag = rig.Loot.FindLoot(wolf.Guid)!;
        Assert.Equal([tagger.Guid], bag.Recipients);
        Assert.Equal(LootResult.NotAllowed, rig.Loot.Open(finisher, wolf.Guid));
        Assert.Equal(LootResult.Ok, rig.Loot.Open(tagger, wolf.Guid));
    }

    [Fact]
    public void FirstDamage_PreservesTheTaggersPartyLootRightsAtDeath()
    {
        Rig rig = CreateRig();
        (Player tagger, _) = rig.Join(1);
        (Player partyMember, _) = rig.Join(2, 1, 0);
        (Player finisher, _) = rig.Join(3, 2, 0);
        rig.Groups.Create(LootMethod.FreeForAll, tagger, partyMember);
        Creature wolf = rig.Wolf;
        rig.Map.Combat.DealDamage(tagger, wolf, 1, direct: false);
        rig.Map.Combat.DealDamage(finisher, wolf, wolf.Health, direct: false);

        LootBag bag = rig.Loot.FindLoot(wolf.Guid)!;
        Assert.Contains(tagger.Guid, bag.Recipients);
        Assert.Contains(partyMember.Guid, bag.Recipients);
        Assert.DoesNotContain(finisher.Guid, bag.Recipients);
        Assert.Equal(LootResult.Ok, rig.Loot.Open(partyMember, wolf.Guid));
    }

    [Fact]
    public void DisbandedTapGroup_FallsBackToTheOriginalTagger()
    {
        Rig rig = CreateRig();
        (Player tagger, _) = rig.Join(1);
        (Player formerMember, _) = rig.Join(2, 1, 0);
        (Player finisher, _) = rig.Join(3, 2, 0);
        rig.Groups.Create(LootMethod.FreeForAll, tagger, formerMember);
        Creature wolf = rig.Wolf;
        rig.Map.Combat.DealDamage(tagger, wolf, 1, direct: false);
        rig.Groups.ByMember.Clear(); // the group manager no longer owns the tapped group
        rig.Map.Combat.DealDamage(finisher, wolf, wolf.Health, direct: false);

        LootBag bag = rig.Loot.FindLoot(wolf.Guid)!;
        Assert.Equal([tagger.Guid], bag.Recipients);
        Assert.Equal(LootResult.NotAllowed, rig.Loot.Open(formerMember, wolf.Guid));
    }

    [Fact]
    public void EvadingCreature_ClearsItsLootTap()
    {
        Rig rig = CreateRig();
        (Player tagger, _) = rig.Join(1);
        Creature wolf = rig.Wolf;
        rig.Map.Combat.DealDamage(tagger, wolf, 1, direct: false);
        rig.Creatures.EnterEvadeMode(wolf);

        Assert.Equal(0u, wolf.GetUInt32(UpdateFields.UnitDynamicFlags)
            & (LootService.UnitDynFlagTapped | LootService.UnitDynFlagTappedByPlayer));
    }

    [Fact]
    public void MoneySplit_UsesTwoDimensionalDistanceFromTheLooter()
    {
        // vmangos LootHandler.cpp:303-330 uses the looter's IsWithinLootXPDist for each current group member.
        Rig rig = CreateRig(minGold: 10, maxGold: 10);
        (Player alice, _) = rig.Join(1);
        (Player bob, _) = rig.Join(2, 1, 0);
        rig.Groups.Create(LootMethod.FreeForAll, alice, bob);
        Creature wolf = rig.KillWolf(alice);
        bob.Relocate(1, 0, 200, 0, 0);

        uint before = bob.Money;
        Assert.Equal(LootResult.Ok, rig.Loot.Open(alice, wolf.Guid));
        Assert.True(rig.Loot.TakeMoney(alice));
        Assert.Equal(before + 5, bob.Money);
    }

    [Fact]
    public void ConditionedLoot_IsVisibleOnlyToTheRecipientWhoPassesTheCondition()
    {
        // vmangos LootMgr.cpp:370-377 evaluates a condition for each player viewing/taking an item.
        Rig rig = CreateRig(rows: [(LootTableKind.Creature, Row(WolfLoot, ItemTestData.ToughJerky, 100, condition: 7))], minGold: 0, maxGold: 0);
        (Player alice, _) = rig.Join(1);
        (Player bob, _) = rig.Join(2, 1, 0);
        rig.Groups.Create(LootMethod.FreeForAll, alice, bob);
        rig.Loot.Conditions = (player, condition) => condition == 7 && ReferenceEquals(player, alice);
        Creature wolf = rig.KillWolf(alice);
        LootBag bag = rig.Loot.FindLoot(wolf.Guid)!;
        LootItem item = Assert.Single(bag.Items);

        Assert.Equal(LootSlotType.AllowLoot, bag.SlotFor(alice, item));
        Assert.Null(bag.SlotFor(bob, item));
    }

    [Fact]
    public void ReferenceRowCondition_RestrictsItemsFromTheReferencedTemplate()
    {
        // vmangos LootMgr.cpp:1225-1243 checks a reference row's condition before expanding it.
        Rig rig = CreateRig(rows:
        [
            (LootTableKind.Creature, Row(WolfLoot, 0, 100, minOrRef: -88, max: 1, condition: 7)),
            (LootTableKind.Reference, Row(88, ItemTestData.ToughJerky, 100)),
        ], minGold: 0, maxGold: 0);
        (Player alice, _) = rig.Join(1);
        (Player bob, _) = rig.Join(2, 1, 0);
        rig.Groups.Create(LootMethod.FreeForAll, alice, bob);
        rig.Loot.Conditions = (player, condition) => condition == 7 && ReferenceEquals(player, alice);
        Creature wolf = rig.KillWolf(alice);
        LootItem item = Assert.Single(rig.Loot.FindLoot(wolf.Guid)!.Items);

        Assert.Equal(LootSlotType.AllowLoot, rig.Loot.FindLoot(wolf.Guid)!.SlotFor(alice, item));
        Assert.Null(rig.Loot.FindLoot(wolf.Guid)!.SlotFor(bob, item));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MoneySplit_DefersForAnEligibleHeldRecipient_WithoutRedistributing(bool inRange)
    {
        Rig rig = CreateRig();
        (Player alice, FakeSession aliceSession) = rig.Join(1);
        (Player bob, FakeSession bobSession) = rig.Join(2, 1, 0);
        rig.Groups.Create(LootMethod.FreeForAll, alice, bob);
        Creature wolf = rig.KillWolf(alice);
        Assert.Equal(LootResult.Ok, rig.Loot.Open(bob, wolf.Guid));
        LootBag bag = rig.Loot.FindLoot(wolf.Guid)!;
        Assert.Equal(2, bag.Recipients.Count);
        if (!inRange)
        {
            alice.Relocate(90, 0, 83.5f, 0, 0);
        }

        uint aliceBefore = alice.Money;
        uint bobBefore = bob.Money;
        Guid operation = Guid.NewGuid();
        Assert.True(alice.BeginQuestSettlement(operation));
        aliceSession.Clear();
        bobSession.Clear();

        Assert.Equal(!inRange, rig.Loot.TakeMoney(bob));
        Assert.Equal(aliceBefore, alice.Money);
        Assert.Equal(bobBefore + (inRange ? 0u : 10u), bob.Money);
        Assert.Equal(inRange ? 10u : 0u, bag.Gold);
        Assert.True(alice.IsQuestSettlementPending);
        Assert.Empty(Packets(aliceSession, WorldOpcode.SmsgLootMoneyNotify));
        if (inRange)
        {
            Assert.Empty(Packets(bobSession, WorldOpcode.SmsgLootClearMoney));
            Assert.Empty(rig.Quests.MoneyEvents);
        }

        Assert.True(alice.EndQuestSettlement(operation));
        if (inRange)
        {
            Assert.True(rig.Loot.TakeMoney(bob));
            Assert.Equal(aliceBefore + 5, alice.Money);
            Assert.Equal(bobBefore + 5, bob.Money);
            Assert.Equal(0u, bag.Gold);
            Assert.Equal(2, rig.Quests.MoneyEvents.Count);
        }
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
        Assert.Equal(alice.Guid, bag.Owner); // GroupManager sets the looter to the leader: he loots the first kill (Group.cpp:134, Unit.cpp:1037)
        Assert.Equal(bob.Guid, group.LooterGuid); // ... and the pointer advanced for the next kill (Unit.cpp:1078)
        Assert.Equal([group], rig.Groups.LooterUpdates);
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
    public void RoundRobin_AMemberOutOfReachLosesHisTurn()
    {
        Rig rig = CreateRig();
        (Player alice, _) = rig.Join(1);
        (Player bob, _) = rig.Join(2, 1, 0);
        (Player carol, _) = rig.Join(3, 500, 0);
        Group group = rig.Groups.Create(LootMethod.RoundRobin, alice, bob, carol);
        group.LooterGuid = carol.Guid; // the looter is far away from the corpse: he loses the turn (Group.cpp:2487-2493)
        Creature wolf = rig.KillWolf(alice);
        Assert.Equal(alice.Guid, rig.Loot.FindLoot(wolf.Guid)!.Owner); // next after carol wraps to the first member in reach
        Assert.Equal(bob.Guid, group.LooterGuid);
    }

    [Fact]
    public void MasterLoot_KeepsTheMasterAsLooter_AndDoesNotRotateIt()
    {
        Rig rig = CreateRig();
        (Player alice, _) = rig.Join(1);
        (Player bob, _) = rig.Join(2, 1, 0);
        Group group = rig.Groups.Create(LootMethod.MasterLoot, alice, bob);
        group.LooterGuid = bob.Guid; // the master looter chosen through CMSG_LOOT_METHOD
        Creature wolf = rig.KillWolf(alice);
        Assert.Equal(bob.Guid, group.LooterGuid); // vmangos Group.cpp:2476-2481 returns for MASTER_LOOT
        Assert.Equal(LootMethod.MasterLoot, group.LootMethod);
        Assert.Empty(rig.Groups.LooterUpdates);
        LootBag bag = rig.Loot.FindLoot(wolf.Guid)!;
        Assert.True(bag.Owner.IsEmpty); // master loot hands items out by master-give, not through an owner (GroupLootRollTests)
        Assert.Equal((LootPermission.Master, bob.Guid), (bag.Permission, bag.MasterLooter));
        rig.Creatures.ForceRespawn(wolf);
        rig.KillWolf(alice);
        Assert.Equal(bob.Guid, group.LooterGuid);
    }

    [Fact]
    public void MasterLoot_OfflineMaster_PassesToTheOnlineLeader_ElseTheGroupFallsBackToGroupLoot()
    {
        Rig rig = CreateRig();
        (Player alice, _) = rig.Join(1);
        (Player bob, _) = rig.Join(2, 1, 0);
        (Player carol, _) = rig.Join(3, 2, 0);
        Group group = rig.Groups.Create(LootMethod.MasterLoot, alice, bob, carol);
        group.LooterGuid = bob.Guid;
        rig.Groups.Offline.Add(bob.Guid);
        rig.KillWolf(carol);
        Assert.Equal(alice.Guid, group.LooterGuid); // Unit.cpp:1048-1055: the leader takes over
        Assert.Equal(LootMethod.MasterLoot, group.LootMethod);
        Assert.Equal([group], rig.Groups.LooterUpdates);

        rig.Groups.Offline.Add(alice.Guid); // nobody left to lead the loot (Unit.cpp:1057-1062)
        group.LootThreshold = 4;
        rig.Creatures.ForceRespawn(rig.Wolf);
        rig.KillWolf(carol);
        Assert.Equal(LootMethod.GroupLoot, group.LootMethod);
        Assert.Equal(Group.DefaultLootThreshold, group.LootThreshold);
        Assert.Equal(3, rig.Groups.LooterUpdates.Count); // the method switch, then the pointer moving off the vanished master
    }

    [Fact]
    public void Recipients_UseTheRetailRewardDistance_HorizontalStrictAndGhostsCountThroughTheirCorpse()
    {
        Rig rig = CreateRig();
        (Player alice, _) = rig.Join(1);
        (Player high, _) = rig.Join(2, 60, 0);
        high.Z = 183.5f; // 57 yd horizontal, 100 yd above the wolf: Object.cpp:1738-1752 is 2D
        (Player ghost, _) = rig.Join(3, 0, 0);
        ghost.Combat.Corpse = Corpse.CreateFor(ghost, pvpDeath: false); // died next to the wolf
        ghost.Combat.DeathState = DeathState.Dead;
        ghost.Relocate(400, 0, 83.5f, 0, 0); // Player::IsAtGroupRewardDistance (Player.cpp:20034-20050)
        (Player far, _) = rig.Join(4, 400, 0);
        rig.Groups.Create(LootMethod.FreeForAll, alice, high, ghost, far);
        Creature wolf = rig.KillWolf(alice);
        Assert.True(rig.Loot.FindLoot(wolf.Guid)!.Recipients.SetEquals([alice.Guid, high.Guid, ghost.Guid]));
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
        // vmangos Creature::SetDeathState (Creature.cpp:2274-2277): skinnable from death on; "loot first" is the spell's TARGET_NOT_LOOTED check.
        Assert.True(wolf.UnitFlags.HasFlag(UnitFlags.Skinnable));
        Assert.False(rig.Loot.IsCorpseLooted(wolf));

        rig.Loot.Open(player, wolf.Guid);
        rig.Loot.TakeItem(player, 0);
        rig.Loot.TakeMoney(player);
        rig.Loot.Release(player, wolf.Guid);
        session.Clear();
        Assert.Equal(LootResult.Ok, rig.Loot.OpenSkinning(player, wolf));
        Assert.False(wolf.UnitFlags.HasFlag(UnitFlags.Skinnable));
        ParsedLoot skin = LootResponse(session);
        // vmangos Player::SendLoot (Player.cpp:7980-7995): the client does not know LOOT_SKINNING, so it is sent as LOOT_PICKPOCKETING.
        Assert.Equal(LootType.Pickpocketing, skin.Type);
        Assert.Equal(Hide, Assert.Single(skin.Items).ItemId);
        Assert.Equal(InventoryResult.Ok, rig.Loot.TakeItem(player, 0));
        rig.Loot.Release(player, wolf.Guid);
        Assert.Null(rig.Loot.FindLoot(wolf.Guid));
        Assert.Equal(LootResult.NotLootable, rig.Loot.OpenSkinning(player, wolf));
    }

    [Fact]
    public void OpenItem_RefusesContainers_UntilTheContainerSliceOfDurableLootIsDelivered()
    {
        Rig rig = CreateRig(rows:
        [
            (LootTableKind.Item, Row(Lockbox, ItemTestData.ToughJerky, 100, minOrRef: 2, max: 2)),
            (LootTableKind.Item, Row(Lockbox, Hide, 100)),
        ]);
        (Player player, FakeSession session) = rig.Join(1);
        Item clam = ItemTestData.Give(player.Inventory, Lockbox);
        Item locked = ItemTestData.Give(player.Inventory, LockedBox);
        Item plain = ItemTestData.Give(player.Inventory, ItemTestData.WornShortsword);
        session.Clear();

        Assert.Equal(LootResult.NotLootable, rig.Loot.OpenItem(player, plain));
        Assert.Equal(LootResult.Locked, rig.Loot.OpenItem(player, locked));
        Assert.Contains(NonUpdatePackets(session), p => p.Opcode == WorldOpcode.SmsgInventoryChangeFailure);

        Assert.Equal(LootResult.NotAllowed, rig.Loot.OpenItem(player, clam));
        Assert.Empty(Packets(session, WorldOpcode.SmsgLootResponse));
        Assert.Null(rig.Loot.FindLoot(clam.Guid));
        Assert.Null(rig.Loot.OpenLootOf(player));
        Assert.Equal(InventoryResult.LootCantLootThatNow, rig.Loot.TakeItem(player, 0));
        rig.Loot.Release(player, clam.Guid);
        Assert.Same(clam, player.Inventory.GetItemByGuid(clam.Guid));
        Assert.Equal(0u, player.Inventory.GetItemCount(ItemTestData.ToughJerky));
        Assert.Equal(0u, player.Inventory.GetItemCount(Hide));
        Assert.Empty(rig.Quests.Looted);

        var persisted = player.Inventory.CreateSnapshot();
        rig.World.RemovePlayer(player);
        (Player replacement, FakeSession replacementSession) = rig.Join(1);
        replacement.Inventory.Load(persisted.Items);
        Item restored = replacement.Inventory.GetItemByGuid(clam.Guid)!;
        Assert.NotSame(clam, restored);
        replacementSession.Clear();
        Assert.Equal(LootResult.NotAllowed, rig.Loot.OpenItem(replacement, restored));
        Assert.Empty(Packets(replacementSession, WorldOpcode.SmsgLootResponse));
        Assert.Equal(1u, replacement.Inventory.GetItemCount(Lockbox));
        Assert.Equal(0u, replacement.Inventory.GetItemCount(ItemTestData.ToughJerky));
        Assert.Equal(0u, replacement.Inventory.GetItemCount(Hide));
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
    public void RefusedContainer_DoesNotReleaseTheCurrentCorpseWindow()
    {
        Rig rig = CreateRig(minGold: 5, maxGold: 5);
        (Player player, FakeSession session) = rig.Join(1);
        Creature wolf = rig.KillWolf(player);
        Item clam = ItemTestData.Give(player.Inventory, Lockbox);
        rig.Loot.Open(player, wolf.Guid);
        session.Clear();
        Assert.Equal(LootResult.NotAllowed, rig.Loot.OpenItem(player, clam));
        Assert.Same(rig.Loot.FindLoot(wolf.Guid), rig.Loot.OpenLootOf(player));
        Assert.Contains(player, rig.Loot.FindLoot(wolf.Guid)!.Viewers);
        Assert.Null(rig.Loot.FindLoot(clam.Guid));
        Assert.Empty(Packets(session, WorldOpcode.SmsgLootReleaseResponse));
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
