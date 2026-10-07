using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>
/// Need/greed rolls (group loot, need before greed) and master loot: start, vote, resolve order, timeout, all passed, give.
/// Behaviour pinned from vmangos Group::GroupLoot / NeedBeforeGreed / StartLootRoll / CountRollVote / CountTheRoll / EndRoll and
/// HandleLootMasterGiveOpcode; the packets are asserted as literal bytes where the layout is the point.
/// </summary>
public sealed class GroupLootRollTests
{
    private const uint WolfLoot = 399;
    private const uint BlueSword = 92001;
    private const uint GreenRing = 92002;
    private const uint NeedyHelm = 92003;
    private const uint UniqueGem = 92004;
    private const uint PartyGem = 92005;

    private static readonly ItemTemplateStore Items = new(
    [
        .. ItemTestData.Templates,
        new ItemTemplate { Entry = BlueSword, Class = 15, Name = "Test Blue", DisplayId = 301, Quality = 3 },
        new ItemTemplate { Entry = GreenRing, Class = 15, Name = "Test Green", DisplayId = 302, Quality = 2 },
        new ItemTemplate { Entry = NeedyHelm, Class = 15, Name = "Test Level Ten", DisplayId = 303, Quality = 3, RequiredLevel = 10 },
        new ItemTemplate { Entry = UniqueGem, Class = 15, Name = "Test Unique Gem", DisplayId = 304, Quality = 3, MaxCount = 1 },
        new ItemTemplate { Entry = PartyGem, Class = 15, Name = "Test Party Gem", DisplayId = 305, Quality = 3, Flags = LootService.ItemFlagPartyLoot },
    ], ItemTestData.StartingItems);

    private sealed class Rig
    {
        public required WorldRuntime World { get; init; }
        public required Map Map { get; init; }
        public required CreatureMapSystem Creatures { get; init; }
        public required LootService Loot { get; init; }
        public required FakeGroups Groups { get; init; }

        public (Player Player, FakeSession Session) Join(uint guid, float x = 0, float y = 0)
        {
            (Player player, FakeSession session) = Player(guid, x, y);
            player.Inventory.Templates = Items;
            World.AddPlayer(player);
            World.RunTick(50);
            session.Clear();
            return (player, session);
        }

        public Creature KillWolf(Player killer)
        {
            Creature wolf = Creatures.Creatures.Single(c => c.Entry == WolfEntry);
            Map.Combat.DealDamage(killer, wolf, wolf.Health, direct: false);
            Assert.Equal(CreatureDeathState.Corpse, wolf.DeathState);
            return wolf;
        }

        public void Elapse(uint ms) => Loot.Rolls.Update(Map, ms);
    }

    private static Rig CreateRig(params uint[] drops) => CreateRigWithGold(0, drops);

    private static Rig CreateRigWithGold(uint gold, params uint[] drops)
    {
        CreatureContent creatures = Content([Template()], [Spawn(1, WolfEntry, 3, 0)]);
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

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FakeSession, List<(WorldOpcode Opcode, byte[] Payload)>> Seen = new();

    /// <summary>Every packet of one opcode the session received since <see cref="Forget"/> (the kit's drain consumes the queue, so keep what was seen).</summary>
    private static List<(WorldOpcode Opcode, byte[] Payload)> Packets(FakeSession session, WorldOpcode opcode)
    {
        List<(WorldOpcode Opcode, byte[] Payload)> all = Seen.GetOrCreateValue(session);
        all.AddRange(NonUpdatePackets(session));
        return [.. all.Where(p => p.Opcode == opcode)];
    }

    private static void Forget(FakeSession session)
    {
        Packets(session, WorldOpcode.SmsgLootResponse); // drain the queue into the list, then drop both
        Seen.GetOrCreateValue(session).Clear();
    }

    private static byte SlotOf(Rig rig, Creature wolf, uint itemId) => rig.Loot.FindLoot(wolf.Guid)!.Items.Single(i => i.ItemId == itemId).Slot;

    private static ParsedLoot Window(FakeSession session)
        => ParsedLoot.Parse(Assert.Single(Packets(session, WorldOpcode.SmsgLootResponse)).Payload);

    private static LootSlotType SlotTypeOf(ParsedLoot window, uint itemId) => window.Items.Single(i => i.ItemId == itemId).SlotType;

    /// <summary>(roller guid, roll number, vote byte) of every SMSG_LOOT_ROLL a session received.</summary>
    private static List<(ulong Roller, byte Number, byte Vote)> RollPackets(FakeSession session)
        => [.. Packets(session, WorldOpcode.SmsgLootRoll).Select(p =>
        {
            Assert.Equal(34, p.Payload.Length);
            return (BitConverter.ToUInt64(p.Payload, 12), p.Payload[32], p.Payload[33]);
        })];

    private static void Vote(Rig rig, Player player, Creature wolf, uint slot, RollVote vote)
        => Assert.True(rig.Loot.Rolls.Vote(player, wolf.Guid, slot, vote));

    [Fact]
    public void GroupLoot_FirstOpenStartsARollForTheItemAboveTheThreshold_AndHoldsItViewOnly()
    {
        Rig rig = CreateRig(ItemTestData.ToughJerky, BlueSword);
        (Player alice, FakeSession aliceSession) = rig.Join(1);
        (Player bob, FakeSession bobSession) = rig.Join(2, 1, 0);
        rig.Groups.Create(LootMethod.GroupLoot, alice, bob);
        Creature wolf = rig.KillWolf(alice);

        LootBag bag = rig.Loot.FindLoot(wolf.Guid)!;
        Assert.True(bag.Owner.IsEmpty); // the round robin fallback is gone for group loot
        Assert.Equal(LootPermission.Roll, bag.Permission);
        Assert.Equal(0, rig.Loot.Rolls.ActiveRollCount); // rolls start when the loot is opened, not at the kill

        Assert.Equal(LootResult.Ok, rig.Loot.Open(alice, wolf.Guid));
        Assert.Equal(1, rig.Loot.Rolls.ActiveRollCount);
        byte blue = SlotOf(rig, wolf, BlueSword);
        byte[] start = Assert.Single(Packets(bobSession, WorldOpcode.SmsgLootStartRoll)).Payload; // bob never opened it and still gets the roll
        Assert.Equal(GroupLootPackets.StartRoll(wolf.Guid, blue, BlueSword, 60000), start);
        Assert.Single(Packets(aliceSession, WorldOpcode.SmsgLootStartRoll));

        ParsedLoot window = Window(aliceSession);
        Assert.Equal(LootSlotType.AllowLoot, SlotTypeOf(window, ItemTestData.ToughJerky)); // under the threshold: free for all
        Assert.Equal(LootSlotType.RollOngoing, SlotTypeOf(window, BlueSword));
        Assert.Equal(InventoryResult.LootCantLootThatNow, rig.Loot.TakeItem(alice, blue)); // nobody takes a rolled item

        Forget(bobSession);
        rig.Loot.Open(bob, wolf.Guid);
        Assert.Equal(1, rig.Loot.Rolls.ActiveRollCount); // the second opener does not start it again
        Assert.Empty(Packets(bobSession, WorldOpcode.SmsgLootStartRoll));
    }

    [Fact]
    public void Resolve_NeedBeatsGreedBeatsPass_AndTheWinnerGetsTheItem()
    {
        Rig rig = CreateRig(BlueSword);
        (Player alice, FakeSession aliceSession) = rig.Join(1);
        (Player bob, FakeSession bobSession) = rig.Join(2, 1, 0);
        (Player carol, FakeSession carolSession) = rig.Join(3, 2, 0);
        rig.Groups.Create(LootMethod.GroupLoot, alice, bob, carol);
        Creature wolf = rig.KillWolf(alice);
        rig.Loot.Open(alice, wolf.Guid);
        Forget(aliceSession);
        Forget(bobSession);

        Vote(rig, alice, wolf, 0, RollVote.Greed);
        Assert.Equal(1, rig.Loot.Rolls.ActiveRollCount); // two still to vote
        Vote(rig, carol, wolf, 0, RollVote.Pass);
        Assert.Equal(1, rig.Loot.Rolls.ActiveRollCount);
        Vote(rig, bob, wolf, 0, RollVote.Need);
        Assert.Equal(0, rig.Loot.Rolls.ActiveRollCount); // the last vote resolved it

        // Announcements first (vmangos CountRollVote: greed 128/2, pass 128/128, need 0/0), then bob's own 1..100 need roll.
        List<(ulong Roller, byte Number, byte Vote)> rolls = RollPackets(bobSession);
        Assert.Equal(4, rolls.Count);
        Assert.Equal((alice.Guid.Value, (byte)128, (byte)2), rolls[0]);
        Assert.Equal((carol.Guid.Value, (byte)128, (byte)128), rolls[1]);
        Assert.Equal((bob.Guid.Value, (byte)0, (byte)0), rolls[2]);
        Assert.Equal(bob.Guid.Value, rolls[3].Roller);
        Assert.InRange(rolls[3].Number, 1, 100);
        Assert.Equal((byte)1, rolls[3].Vote); // need

        byte[] won = Assert.Single(Packets(aliceSession, WorldOpcode.SmsgLootRollWon)).Payload;
        Assert.Equal(GroupLootPackets.RollWon(wolf.Guid, 0, BlueSword, bob.Guid, rolls[3].Number, RollVote.Need), won);
        Assert.Equal(1u, bob.Inventory.GetItemCount(BlueSword));
        Assert.Equal(0u, alice.Inventory.GetItemCount(BlueSword));
        Assert.Contains(Packets(aliceSession, WorldOpcode.SmsgLootRemoved), p => p.Payload.SequenceEqual(new byte[] { 0 }));
        Assert.Empty(Packets(aliceSession, WorldOpcode.SmsgLootAllPassed));
    }

    [Fact]
    public void Resolve_GreedAmongSeveral_TheHighestNumberWins_ATieGoesToTheEarlierMember()
    {
        Rig rig = CreateRig(BlueSword);
        (Player alice, _) = rig.Join(1);
        (Player bob, FakeSession bobSession) = rig.Join(2, 1, 0);
        (Player carol, _) = rig.Join(3, 2, 0);
        rig.Groups.Create(LootMethod.GroupLoot, alice, bob, carol);
        Creature wolf = rig.KillWolf(alice);
        rig.Loot.Open(alice, wolf.Guid);
        Forget(bobSession);

        Vote(rig, alice, wolf, 0, RollVote.Greed);
        Vote(rig, bob, wolf, 0, RollVote.Greed);
        Vote(rig, carol, wolf, 0, RollVote.Greed);

        List<(ulong Roller, byte Number, byte Vote)> numbers = [.. RollPackets(bobSession).Where(r => r.Number is >= 1 and <= 100)];
        Assert.Equal(3, numbers.Count);
        Assert.All(numbers, n => Assert.Equal((byte)2, n.Vote));
        byte top = numbers.Max(n => n.Number);
        ulong expected = numbers.First(n => n.Number == top).Roller; // first in group order among equal numbers
        Player winner = new[] { alice, bob, carol }.Single(p => p.Guid.Value == expected);
        Assert.Equal(1u, winner.Inventory.GetItemCount(BlueSword));
        Assert.Equal(1u, new[] { alice, bob, carol }.Sum(p => p.Inventory.GetItemCount(BlueSword)));
        byte[] won = Assert.Single(Packets(bobSession, WorldOpcode.SmsgLootRollWon)).Payload;
        Assert.Equal(GroupLootPackets.RollWon(wolf.Guid, 0, BlueSword, winner.Guid, top, RollVote.Greed), won);
    }

    [Fact]
    public void Resolve_EveryonePassed_IsReportedAndTheItemIsFreeToTake()
    {
        Rig rig = CreateRig(BlueSword);
        (Player alice, FakeSession aliceSession) = rig.Join(1);
        (Player bob, FakeSession bobSession) = rig.Join(2, 1, 0);
        rig.Groups.Create(LootMethod.GroupLoot, alice, bob);
        Creature wolf = rig.KillWolf(alice);
        rig.Loot.Open(alice, wolf.Guid);
        Forget(aliceSession);
        Forget(bobSession);

        Vote(rig, alice, wolf, 0, RollVote.Pass);
        Vote(rig, bob, wolf, 0, RollVote.Pass);

        Assert.Equal(GroupLootPackets.AllPassed(wolf.Guid, 0, BlueSword), Assert.Single(Packets(aliceSession, WorldOpcode.SmsgLootAllPassed)).Payload);
        Assert.Single(Packets(bobSession, WorldOpcode.SmsgLootAllPassed));
        Assert.Empty(Packets(bobSession, WorldOpcode.SmsgLootRollWon));
        Assert.False(rig.Loot.FindLoot(wolf.Guid)!.FindSlot(0)!.RollActive);
        Assert.Equal(InventoryResult.Ok, rig.Loot.TakeItem(alice, 0)); // back to a plain, shared item
    }

    [Fact]
    public void Timer_NobodyVoted_ExpiresIntoPass_AndALoneVoteDecidesAtTheTimeout()
    {
        Rig rig = CreateRig(BlueSword, GreenRing);
        (Player alice, FakeSession aliceSession) = rig.Join(1);
        (Player bob, FakeSession bobSession) = rig.Join(2, 1, 0);
        rig.Groups.Create(LootMethod.GroupLoot, alice, bob);
        Creature wolf = rig.KillWolf(alice);
        rig.Loot.Open(alice, wolf.Guid);
        Assert.Equal(2, rig.Loot.Rolls.ActiveRollCount);
        Forget(aliceSession);
        Forget(bobSession);

        Vote(rig, bob, wolf, SlotOf(rig, wolf, GreenRing), RollVote.Greed); // only bob answers the green ring
        rig.World.RunTick(50);
        rig.Elapse(59900 - 50 - 1); // still inside the countdown
        Assert.Equal(2, rig.Loot.Rolls.ActiveRollCount);
        Assert.Empty(Packets(aliceSession, WorldOpcode.SmsgLootAllPassed));

        rig.Elapse(60000); // the countdown is over: alice, who never voted, counts as passed
        Assert.Equal(0, rig.Loot.Rolls.ActiveRollCount);
        Assert.Equal(1u, bob.Inventory.GetItemCount(GreenRing));
        byte[] passed = Assert.Single(Packets(aliceSession, WorldOpcode.SmsgLootAllPassed)).Payload; // the blue sword nobody answered
        Assert.Equal(GroupLootPackets.AllPassed(wolf.Guid, SlotOf(rig, wolf, BlueSword), BlueSword), passed);
        Assert.Equal(0u, bob.Inventory.GetItemCount(BlueSword));
    }

    [Fact]
    public void Timer_FollowsTheConfiguredTimeout_AndTheMapUpdaterRunsIt()
    {
        Rig rig = CreateRig(BlueSword);
        rig.Loot.Options.RollTimeoutMs = 1000;
        (Player alice, FakeSession aliceSession) = rig.Join(1);
        (Player bob, _) = rig.Join(2, 1, 0);
        rig.Groups.Create(LootMethod.GroupLoot, alice, bob);
        Creature wolf = rig.KillWolf(alice);
        rig.Loot.Open(alice, wolf.Guid);
        Assert.Equal(GroupLootPackets.StartRoll(wolf.Guid, 0, BlueSword, 1000), Assert.Single(Packets(aliceSession, WorldOpcode.SmsgLootStartRoll)).Payload);

        for (int i = 0; i < 25; i++)
        {
            rig.World.RunTick(50); // 1250 ms through the attached map updater
        }

        Assert.Equal(0, rig.Loot.Rolls.ActiveRollCount);
        Assert.Single(Packets(aliceSession, WorldOpcode.SmsgLootAllPassed));
    }

    [Fact]
    public void Vote_RepeatNonParticipantAndUnknownRollAreIgnored()
    {
        Rig rig = CreateRig(BlueSword);
        (Player alice, _) = rig.Join(1);
        (Player bob, _) = rig.Join(2, 1, 0);
        (Player carol, _) = rig.Join(3, 2, 0);
        (Player outsider, _) = rig.Join(4, 3, 0);
        rig.Groups.Create(LootMethod.GroupLoot, alice, bob, carol);
        Creature wolf = rig.KillWolf(alice);
        rig.Loot.Open(alice, wolf.Guid);

        Vote(rig, alice, wolf, 0, RollVote.Need);
        Assert.False(rig.Loot.Rolls.Vote(alice, wolf.Guid, 0, RollVote.Pass)); // a second vote does not count (vmangos would count it again)
        Assert.False(rig.Loot.Rolls.Vote(outsider, wolf.Guid, 0, RollVote.Need));
        Assert.False(rig.Loot.Rolls.Vote(bob, wolf.Guid, 7, RollVote.Need)); // no roll for slot 7
        Assert.False(rig.Loot.Rolls.Vote(bob, ObjectGuid.Player(99), 0, RollVote.Need));
        Assert.Equal(1, rig.Loot.Rolls.ActiveRollCount);
        Vote(rig, bob, wolf, 0, RollVote.Pass);
        Vote(rig, carol, wolf, 0, RollVote.Pass);
        Assert.Equal(1u, alice.Inventory.GetItemCount(BlueSword)); // the only need vote won
    }

    [Fact]
    public void Winner_WhoCannotStoreTheItem_KeepsTheOnlyClaim_AndIsToldWhy()
    {
        Rig rig = CreateRig(UniqueGem);
        (Player alice, FakeSession aliceSession) = rig.Join(1);
        (Player bob, FakeSession bobSession) = rig.Join(2, 1, 0);
        rig.Groups.Create(LootMethod.GroupLoot, alice, bob);
        Assert.Equal(InventoryResult.Ok, bob.Inventory.AddItem(UniqueGem, 1, out _)); // bob already carries the unique gem
        Forget(bobSession);
        Creature wolf = rig.KillWolf(alice);
        rig.Loot.Open(alice, wolf.Guid);
        Forget(aliceSession);

        Vote(rig, bob, wolf, 0, RollVote.Need);
        Vote(rig, alice, wolf, 0, RollVote.Pass);

        LootItem gem = rig.Loot.FindLoot(wolf.Guid)!.FindSlot(0)!;
        Assert.False(gem.IsLooted);
        Assert.Equal(bob.Guid, gem.Winner);
        Assert.Single(Packets(bobSession, WorldOpcode.SmsgInventoryChangeFailure));
        Assert.Contains(Packets(aliceSession, WorldOpcode.SmsgLootRemoved), p => p.Payload.SequenceEqual(new byte[] { 0 })); // gone for the others
        Assert.Equal(InventoryResult.LootCantLootThatNow, rig.Loot.TakeItem(alice, 0));
        Assert.Equal(1u, bob.Inventory.GetItemCount(UniqueGem));
        rig.Loot.Open(bob, wolf.Guid);
        Assert.Equal(LootSlotType.AllowLoot, SlotTypeOf(Window(bobSession), UniqueGem)); // his claim stays visible to him
    }

    [Fact]
    public void NeedBeforeGreed_OnlyMembersWhoCanUseTheItemRoll_AndALoneUserAutoNeedsIt()
    {
        Rig rig = CreateRig(NeedyHelm, BlueSword);
        (Player alice, FakeSession aliceSession) = rig.Join(1);
        (Player bob, FakeSession bobSession) = rig.Join(2, 1, 0);
        (Player carol, FakeSession carolSession) = rig.Join(3, 2, 0);
        bob.Level = 10; // alice and carol (level 1) cannot use the level 10 helm
        rig.Groups.Create(LootMethod.NeedBeforeGreed, alice, bob, carol);
        Creature wolf = rig.KillWolf(alice);
        byte helm = SlotOf(rig, wolf, NeedyHelm);
        rig.Loot.Open(alice, wolf.Guid);

        // The helm: only bob can use it, so it is not rolled: he needs it with 100 and it goes straight into his bags
        // (vmangos Group::StartLootRoll single looter → CountSingleLooterRoll, Group.cpp:1023-1027, 1090-1121).
        // The sword is usable by all three and is rolled.
        Assert.Equal(1, rig.Loot.Rolls.ActiveRollCount);
        byte sword = SlotOf(rig, wolf, BlueSword);
        Assert.Equal(GroupLootPackets.StartRoll(wolf.Guid, sword, BlueSword, 60000), Assert.Single(Packets(carolSession, WorldOpcode.SmsgLootStartRoll)).Payload);
        Assert.True(rig.Loot.FindLoot(wolf.Guid)!.FindSlot(helm)!.IsLooted);
        Assert.True(rig.Loot.FindLoot(wolf.Guid)!.FindSlot(sword)!.RollActive);
        Assert.Equal(1u, bob.Inventory.GetItemCount(NeedyHelm));
        Assert.Equal(GroupLootPackets.RollWon(wolf.Guid, helm, NeedyHelm, bob.Guid, 100, RollVote.Need),
            Assert.Single(Packets(bobSession, WorldOpcode.SmsgLootRollWon)).Payload);
        Assert.Empty(Packets(carolSession, WorldOpcode.SmsgLootRollWon)); // only the lone roller hears of it
        Assert.DoesNotContain(Window(aliceSession).Items, i => i.ItemId == NeedyHelm);
        Assert.Equal(InventoryResult.AlreadyLooted, rig.Loot.TakeItem(alice, helm));
    }

    [Fact]
    public void ALoneEligibleMember_WhoCannotStoreTheItem_KeepsTheOnlyClaim()
    {
        Rig rig = CreateRig(UniqueGem);
        (Player alice, FakeSession aliceSession) = rig.Join(1);
        (Player bob, FakeSession bobSession) = rig.Join(2, 1, 0);
        rig.Groups.Create(LootMethod.GroupLoot, alice, bob);
        Creature wolf = rig.KillWolf(alice);
        LootBag bag = rig.Loot.FindLoot(wolf.Guid)!;
        Assert.Equal(LootPermission.Roll, bag.Permission);
        bob.Relocate(400, 0, 83.5f, 0, 0); // bob walks off: alice is the only member left within reward distance
        Assert.Equal(InventoryResult.Ok, alice.Inventory.AddItem(UniqueGem, 1, out _)); // and she already carries the unique gem
        Forget(aliceSession);

        rig.Loot.Open(alice, wolf.Guid);

        // vmangos CountSingleLooterRoll: the store fails, the item is unblocked with the lone roller as its owner and he is told why.
        LootItem gem = bag.FindSlot(0)!;
        Assert.Equal(0, rig.Loot.Rolls.ActiveRollCount);
        Assert.False(gem.IsLooted);
        Assert.Equal(alice.Guid, gem.Winner);
        Assert.Single(Packets(aliceSession, WorldOpcode.SmsgInventoryChangeFailure));
        Assert.Single(Packets(aliceSession, WorldOpcode.SmsgLootRollWon));
        Assert.Empty(Packets(bobSession, WorldOpcode.SmsgLootRollWon));
        Assert.Null(bag.SlotFor(bob, gem)); // bob may not walk back and take it
        Assert.Equal(LootSlotType.AllowLoot, bag.SlotFor(alice, gem)); // she can, once she makes room
    }

    // --- the roster: rolls belong to the group that earned the loot ---------------------------------

    [Fact]
    public void AFirstOpenerWhoLeftTheGroup_DoesNotSpendTheRolls_TheGroupStillRolls()
    {
        Rig rig = CreateRig(BlueSword);
        (Player alice, _) = rig.Join(1);
        (Player bob, FakeSession bobSession) = rig.Join(2, 1, 0);
        (Player carol, FakeSession carolSession) = rig.Join(3, 2, 0);
        Group group = rig.Groups.Create(LootMethod.GroupLoot, alice, bob, carol);
        Creature wolf = rig.KillWolf(alice);
        Leave(rig, group, alice); // alice was a recipient at the kill and left the group afterwards

        Assert.Equal(LootResult.Ok, rig.Loot.Open(alice, wolf.Guid));
        Assert.Equal(1, rig.Loot.Rolls.ActiveRollCount); // the roll is among the group, without her
        Assert.Equal(InventoryResult.LootCantLootThatNow, rig.Loot.TakeItem(alice, 0));
        Assert.Single(Packets(bobSession, WorldOpcode.SmsgLootStartRoll));
        Assert.Single(Packets(carolSession, WorldOpcode.SmsgLootStartRoll));
        Assert.False(rig.Loot.Rolls.Vote(alice, wolf.Guid, 0, RollVote.Need));
    }

    [Fact]
    public void AnOpenWhileTheMethodIsNoLongerGroupLoot_DoesNotSpendTheRolls()
    {
        Rig rig = CreateRig(BlueSword);
        (Player alice, _) = rig.Join(1);
        (Player bob, _) = rig.Join(2, 1, 0);
        Group group = rig.Groups.Create(LootMethod.GroupLoot, alice, bob);
        Creature wolf = rig.KillWolf(alice);

        group.LootMethod = LootMethod.FreeForAll;
        rig.Loot.Open(alice, wolf.Guid);
        rig.Loot.Release(alice, wolf.Guid);
        Assert.Equal(0, rig.Loot.Rolls.ActiveRollCount);

        group.LootMethod = LootMethod.GroupLoot; // the leader switched back before anyone took the sword
        rig.Loot.Open(bob, wolf.Guid);
        Assert.Equal(1, rig.Loot.Rolls.ActiveRollCount);
        Assert.True(rig.Loot.FindLoot(wolf.Guid)!.FindSlot(0)!.RollActive);
    }

    [Fact]
    public void AMemberWhoLeavesTheGroup_DropsOutOfTheRoll_AndHisNeedNoLongerCounts()
    {
        Rig rig = CreateRig(BlueSword);
        (Player alice, _) = rig.Join(1);
        (Player bob, FakeSession bobSession) = rig.Join(2, 1, 0);
        (Player carol, _) = rig.Join(3, 2, 0);
        Group group = rig.Groups.Create(LootMethod.GroupLoot, alice, bob, carol);
        Creature wolf = rig.KillWolf(alice);
        rig.Loot.Open(alice, wolf.Guid);

        Vote(rig, bob, wolf, 0, RollVote.Need);
        Leave(rig, group, bob); // vmangos Group::_removeMember → _removeRolls (Group.cpp:1627, 1780-1805): his vote is erased
        Forget(bobSession);
        Vote(rig, alice, wolf, 0, RollVote.Greed);
        Vote(rig, carol, wolf, 0, RollVote.Pass);

        Assert.Equal(0, rig.Loot.Rolls.ActiveRollCount);
        Assert.Equal(0u, bob.Inventory.GetItemCount(BlueSword));
        Assert.Equal(1u, alice.Inventory.GetItemCount(BlueSword));
        Assert.Empty(Packets(bobSession, WorldOpcode.SmsgLootRollWon)); // he is no longer told about the roll
    }

    [Fact]
    public void ALeaverWhoHadNotVoted_NoLongerHoldsTheRollUp_AndCannotVoteAnyMore()
    {
        Rig rig = CreateRig(BlueSword);
        (Player alice, _) = rig.Join(1);
        (Player bob, _) = rig.Join(2, 1, 0);
        (Player carol, _) = rig.Join(3, 2, 0);
        Group group = rig.Groups.Create(LootMethod.GroupLoot, alice, bob, carol);
        Creature wolf = rig.KillWolf(alice);
        rig.Loot.Open(alice, wolf.Guid);

        Vote(rig, alice, wolf, 0, RollVote.Greed);
        Vote(rig, carol, wolf, 0, RollVote.Pass);
        Leave(rig, group, bob);
        Assert.False(rig.Loot.Rolls.Vote(bob, wolf.Guid, 0, RollVote.Need));

        rig.Elapse(1); // the next map update notices that everyone left in the roll has voted
        Assert.Equal(0, rig.Loot.Rolls.ActiveRollCount);
        Assert.Equal(1u, alice.Inventory.GetItemCount(BlueSword));
        Assert.Equal(0u, bob.Inventory.GetItemCount(BlueSword));
    }

    [Fact]
    public void ADisbandedGroup_ResolvesItsRollsAtOnce_WithTheVotesCast()
    {
        Rig rig = CreateRig(BlueSword);
        (Player alice, _) = rig.Join(1);
        (Player bob, _) = rig.Join(2, 1, 0);
        (Player carol, _) = rig.Join(3, 2, 0);
        Group group = rig.Groups.Create(LootMethod.GroupLoot, alice, bob, carol);
        Creature wolf = rig.KillWolf(alice);
        rig.Loot.Open(alice, wolf.Guid);

        Vote(rig, carol, wolf, 0, RollVote.Need);
        group.Clear(); // vmangos Group::Disband counts every roll before it drops the members (Group.cpp:605-606)
        rig.Groups.ByMember.Clear();

        rig.Elapse(1);
        Assert.Equal(0, rig.Loot.Rolls.ActiveRollCount);
        Assert.Equal(1u, carol.Inventory.GetItemCount(BlueSword));
    }

    private static void Leave(Rig rig, Group group, Player member)
    {
        group.RemoveMemberSlot(group.Find(member.Guid)!);
        rig.Groups.ByMember.Remove(member.Guid);
    }

    [Fact]
    public void NeedBeforeGreed_TheRollIsAmongTheMembersWhoCanUseTheItem_Only()
    {
        Rig rig = CreateRig(NeedyHelm);
        (Player alice, FakeSession aliceSession) = rig.Join(1);
        (Player bob, FakeSession bobSession) = rig.Join(2, 1, 0);
        (Player carol, FakeSession carolSession) = rig.Join(3, 2, 0);
        bob.Level = 10;
        carol.Level = 10;
        rig.Groups.Create(LootMethod.NeedBeforeGreed, alice, bob, carol);
        Creature wolf = rig.KillWolf(alice);
        rig.Loot.Open(alice, wolf.Guid);

        Assert.Single(Packets(bobSession, WorldOpcode.SmsgLootStartRoll));
        Assert.Single(Packets(carolSession, WorldOpcode.SmsgLootStartRoll));
        Assert.Empty(Packets(aliceSession, WorldOpcode.SmsgLootStartRoll)); // alice cannot use it and takes no part
        Assert.False(rig.Loot.Rolls.Vote(alice, wolf.Guid, 0, RollVote.Need));
        Vote(rig, bob, wolf, 0, RollVote.Greed);
        Vote(rig, carol, wolf, 0, RollVote.Pass);
        Assert.Equal(1u, bob.Inventory.GetItemCount(NeedyHelm));
    }

    [Fact]
    public void SingleRecipient_NeverRolls()
    {
        Rig rig = CreateRig(BlueSword);
        (Player alice, _) = rig.Join(1);
        (Player bob, _) = rig.Join(2, 500, 0); // out of reward distance: not a recipient
        rig.Groups.Create(LootMethod.GroupLoot, alice, bob);
        Creature wolf = rig.KillWolf(alice);
        Assert.Equal(LootResult.Ok, rig.Loot.Open(alice, wolf.Guid));
        Assert.Equal(0, rig.Loot.Rolls.ActiveRollCount);
        Assert.Equal(InventoryResult.Ok, rig.Loot.TakeItem(alice, 0));
    }

    [Fact]
    public void ThresholdDecides_AndPerPlayerItemsAreNeverRolled()
    {
        Rig rig = CreateRig(GreenRing, PartyGem);
        (Player alice, FakeSession aliceSession) = rig.Join(1);
        (Player bob, _) = rig.Join(2, 1, 0);
        Group group = rig.Groups.Create(LootMethod.GroupLoot, alice, bob);
        group.LootThreshold = 3; // rare and up
        Creature wolf = rig.KillWolf(alice);
        rig.Loot.Open(alice, wolf.Guid);
        Assert.Equal(0, rig.Loot.Rolls.ActiveRollCount); // green is under the raised threshold, the party-loot gem is a copy for everyone
        ParsedLoot window = Window(aliceSession);
        Assert.Equal(LootSlotType.AllowLoot, SlotTypeOf(window, GreenRing));
        Assert.Equal(LootSlotType.AllowLoot, SlotTypeOf(window, PartyGem));

        // At the default threshold (uncommon) the green ring is rolled but the party-loot gem still is not.
        group.LootThreshold = Group.DefaultLootThreshold;
        rig.Creatures.ForceRespawn(wolf);
        wolf = rig.KillWolf(alice);
        rig.Loot.Open(alice, wolf.Guid);
        Assert.Equal(1, rig.Loot.Rolls.ActiveRollCount);
        Assert.True(rig.Loot.FindLoot(wolf.Guid)!.FindSlot(SlotOf(rig, wolf, GreenRing))!.RollActive);
        Assert.False(rig.Loot.FindLoot(wolf.Guid)!.FindSlot(SlotOf(rig, wolf, PartyGem))!.RollActive);
    }

    [Fact]
    public void MemberWhoLeavesTheMap_PassesOnTheRoll_AndTheOthersAreNotKeptWaiting()
    {
        Rig rig = CreateRig(BlueSword);
        (Player alice, _) = rig.Join(1);
        (Player bob, _) = rig.Join(2, 1, 0);
        rig.Groups.Create(LootMethod.GroupLoot, alice, bob);
        Creature wolf = rig.KillWolf(alice);
        rig.Loot.Open(alice, wolf.Guid);

        Vote(rig, alice, wolf, 0, RollVote.Greed);
        rig.World.RemovePlayer(bob);
        Assert.Equal(0, rig.Loot.Rolls.ActiveRollCount);
        Assert.Equal(1u, alice.Inventory.GetItemCount(BlueSword));
    }

    [Fact]
    public void GoneLoot_CancelsItsRollsWithoutAnyAnnouncement()
    {
        Rig rig = CreateRig(BlueSword);
        (Player alice, FakeSession aliceSession) = rig.Join(1);
        (Player bob, _) = rig.Join(2, 1, 0);
        rig.Groups.Create(LootMethod.GroupLoot, alice, bob);
        Creature wolf = rig.KillWolf(alice);
        rig.Loot.Open(alice, wolf.Guid);
        Forget(aliceSession);

        rig.Loot.ForgetLoot(wolf); // the corpse decayed
        rig.Elapse(60000);
        Assert.Equal(0, rig.Loot.Rolls.ActiveRollCount);
        Assert.Empty(Packets(aliceSession, WorldOpcode.SmsgLootAllPassed));
        Assert.Empty(Packets(aliceSession, WorldOpcode.SmsgLootRollWon));
    }

    [Fact]
    public void RollingTheLastItemAway_WhileNobodyHasTheWindowOpen_LootsTheCorpseOut()
    {
        Rig rig = CreateRig(BlueSword);
        (Player alice, _) = rig.Join(1);
        (Player bob, _) = rig.Join(2, 1, 0);
        rig.Groups.Create(LootMethod.GroupLoot, alice, bob);
        Creature wolf = rig.KillWolf(alice);
        rig.Loot.Open(alice, wolf.Guid);
        rig.Loot.Release(alice, wolf.Guid); // the roll keeps running without a window

        Vote(rig, alice, wolf, 0, RollVote.Need);
        Vote(rig, bob, wolf, 0, RollVote.Pass);
        Assert.True(rig.Loot.FindLoot(wolf.Guid)!.IsClosed);
        Assert.Equal(0u, wolf.GetUInt32(UpdateFields.UnitDynamicFlags) & LootService.UnitDynFlagLootable);
    }

    // --- master loot -------------------------------------------------------------------------

    [Fact]
    public void MasterLoot_ShowsTheMasterAMasterSlot_AndEveryoneElseOnlyWhatIsUnderTheThreshold()
    {
        Rig rig = CreateRigWithGold(10, ItemTestData.ToughJerky, BlueSword);
        (Player alice, FakeSession aliceSession) = rig.Join(1);
        (Player bob, FakeSession bobSession) = rig.Join(2, 1, 0);
        Group group = rig.Groups.Create(LootMethod.MasterLoot, alice, bob);
        group.LooterGuid = bob.Guid;
        Creature wolf = rig.KillWolf(alice);
        LootBag bag = rig.Loot.FindLoot(wolf.Guid)!;
        Assert.True(bag.Owner.IsEmpty);
        Assert.Equal(LootPermission.Master, bag.Permission);

        rig.Loot.Open(alice, wolf.Guid);
        ParsedLoot others = Window(aliceSession);
        Assert.Equal(10u, others.Gold); // money is not held back
        Assert.Equal(LootSlotType.AllowLoot, SlotTypeOf(others, ItemTestData.ToughJerky));
        Assert.DoesNotContain(others.Items, i => i.ItemId == BlueSword);
        Assert.Empty(Packets(aliceSession, WorldOpcode.SmsgLootMasterList));

        rig.Loot.Open(bob, wolf.Guid);
        ParsedLoot master = Window(bobSession);
        Assert.Equal(LootSlotType.AllowLoot, SlotTypeOf(master, ItemTestData.ToughJerky));
        Assert.Equal(LootSlotType.Master, SlotTypeOf(master, BlueSword));
        // SMSG_LOOT_MASTER_LIST: u8 count, then the guids of the members in range, group order.
        byte[] list = Assert.Single(Packets(bobSession, WorldOpcode.SmsgLootMasterList)).Payload;
        Assert.Equal(GroupLootPackets.MasterList([alice.Guid, bob.Guid]), list);
        byte sword = SlotOf(rig, wolf, BlueSword);
        Assert.Equal(InventoryResult.LootCantLootThatNow, rig.Loot.TakeItem(alice, sword));
        Assert.Equal(InventoryResult.LootCantLootThatNow, rig.Loot.TakeItem(bob, sword)); // the master gives, he does not auto-store
    }

    [Fact]
    public void MasterGive_PutsTheItemIntoTheRecipientsBags_AndRemovesTheSlotForEveryViewer()
    {
        Rig rig = CreateRig(ItemTestData.ToughJerky, BlueSword); // bob only sees the jerky, but that is enough for his window to open
        (Player alice, FakeSession aliceSession) = rig.Join(1);
        (Player bob, FakeSession bobSession) = rig.Join(2, 1, 0);
        Group group = rig.Groups.Create(LootMethod.MasterLoot, alice, bob);
        group.LooterGuid = alice.Guid;
        Creature wolf = rig.KillWolf(bob);
        rig.Loot.Open(alice, wolf.Guid);
        rig.Loot.Open(bob, wolf.Guid);
        Forget(aliceSession);
        Forget(bobSession);

        byte sword = SlotOf(rig, wolf, BlueSword);
        Assert.Equal(MasterGiveResult.Given, rig.Loot.GiveMasterLoot(alice, wolf.Guid, sword, bob.Guid));
        Assert.Equal(1u, bob.Inventory.GetItemCount(BlueSword));
        Assert.Equal(0u, alice.Inventory.GetItemCount(BlueSword));
        Assert.Single(Packets(aliceSession, WorldOpcode.SmsgLootRemoved));
        Assert.Single(Packets(bobSession, WorldOpcode.SmsgLootRemoved));
        Assert.True(rig.Loot.FindLoot(wolf.Guid)!.FindSlot(sword)!.IsLooted);
        Assert.Equal(MasterGiveResult.NotApplicable, rig.Loot.GiveMasterLoot(alice, wolf.Guid, sword, bob.Guid)); // already given
    }

    [Fact]
    public void MasterGive_ByAnyoneButTheMaster_ClosesTheWindowAndGivesNothing()
    {
        Rig rig = CreateRig(BlueSword);
        (Player alice, _) = rig.Join(1);
        (Player bob, FakeSession bobSession) = rig.Join(2, 1, 0);
        Group group = rig.Groups.Create(LootMethod.MasterLoot, alice, bob);
        group.LooterGuid = alice.Guid;
        Creature wolf = rig.KillWolf(bob);
        rig.Loot.Open(bob, wolf.Guid);
        Forget(bobSession);

        Assert.Equal(MasterGiveResult.NotMaster, rig.Loot.GiveMasterLoot(bob, wolf.Guid, 0, bob.Guid));
        Assert.Equal(0u, bob.Inventory.GetItemCount(BlueSword));
        Assert.Single(Packets(bobSession, WorldOpcode.SmsgLootReleaseResponse));
        Assert.False(rig.Loot.FindLoot(wolf.Guid)!.FindSlot(0)!.IsLooted);

        // A master who has not opened the loot gives nothing either.
        Assert.Equal(MasterGiveResult.NotApplicable, rig.Loot.GiveMasterLoot(alice, wolf.Guid, 0, bob.Guid));
    }

    [Fact]
    public void MasterGive_ToSomeoneNotOnTheMasterList_ReportsPlayerNotFound()
    {
        Rig rig = CreateRig(BlueSword);
        (Player alice, FakeSession aliceSession) = rig.Join(1);
        (Player bob, _) = rig.Join(2, 1, 0);
        (Player stranger, _) = rig.Join(3, 2, 0);
        (Player far, _) = rig.Join(4, 500, 0);
        Group group = rig.Groups.Create(LootMethod.MasterLoot, alice, bob, far);
        group.LooterGuid = alice.Guid;
        Creature wolf = rig.KillWolf(alice);
        rig.Loot.Open(alice, wolf.Guid);
        Forget(aliceSession);

        Assert.Equal(MasterGiveResult.TargetNotEligible, rig.Loot.GiveMasterLoot(alice, wolf.Guid, 0, stranger.Guid)); // not in the group
        Assert.Equal(MasterGiveResult.TargetNotEligible, rig.Loot.GiveMasterLoot(alice, wolf.Guid, 0, far.Guid)); // out of reward distance
        Assert.Equal(MasterGiveResult.TargetNotEligible, rig.Loot.GiveMasterLoot(alice, wolf.Guid, 0, ObjectGuid.Player(77))); // not online
        List<(WorldOpcode Opcode, byte[] Payload)> errors = Packets(aliceSession, WorldOpcode.SmsgLootResponse);
        Assert.Equal(3, errors.Count);
        Assert.All(errors, e => Assert.Equal(GroupLootPackets.LootErrorResponse(wolf.Guid, LootError.PlayerNotFound), e.Payload));
        Assert.Equal(0u, stranger.Inventory.GetItemCount(BlueSword));
        Assert.False(rig.Loot.FindLoot(wolf.Guid)!.FindSlot(0)!.IsLooted);
    }

    [Fact]
    public void MasterGive_ARefusedStore_TellsTheMasterAndKeepsTheItem()
    {
        Rig rig = CreateRig(UniqueGem);
        (Player alice, FakeSession aliceSession) = rig.Join(1);
        (Player bob, _) = rig.Join(2, 1, 0);
        Group group = rig.Groups.Create(LootMethod.MasterLoot, alice, bob);
        group.LooterGuid = alice.Guid;
        Assert.Equal(InventoryResult.Ok, bob.Inventory.AddItem(UniqueGem, 1, out _));
        Creature wolf = rig.KillWolf(alice);
        rig.Loot.Open(alice, wolf.Guid);
        Forget(aliceSession);

        Assert.Equal(MasterGiveResult.TargetUnique, rig.Loot.GiveMasterLoot(alice, wolf.Guid, 0, bob.Guid));
        Assert.Equal(GroupLootPackets.LootErrorResponse(wolf.Guid, LootError.MasterUniqueItem), Assert.Single(Packets(aliceSession, WorldOpcode.SmsgLootResponse)).Payload);
        Assert.False(rig.Loot.FindLoot(wolf.Guid)!.FindSlot(0)!.IsLooted);
        Assert.Equal(MasterGiveResult.Given, rig.Loot.GiveMasterLoot(alice, wolf.Guid, 0, alice.Guid)); // he can still give it to someone who can take it
        Assert.Equal(1u, alice.Inventory.GetItemCount(UniqueGem));
    }

    [Fact]
    public void RoundRobin_StillHoldsTheLootForTheLooter_AndNeverRolls()
    {
        Rig rig = CreateRig(BlueSword);
        (Player alice, _) = rig.Join(1);
        (Player bob, _) = rig.Join(2, 1, 0);
        rig.Groups.Create(LootMethod.RoundRobin, alice, bob);
        Creature wolf = rig.KillWolf(alice);
        LootBag bag = rig.Loot.FindLoot(wolf.Guid)!;
        Assert.Equal(alice.Guid, bag.Owner);
        Assert.Equal(LootPermission.Open, bag.Permission);
        rig.Loot.Open(alice, wolf.Guid);
        Assert.Equal(0, rig.Loot.Rolls.ActiveRollCount);
    }
}
