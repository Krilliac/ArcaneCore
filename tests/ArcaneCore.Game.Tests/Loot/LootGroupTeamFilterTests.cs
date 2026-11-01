using ArcaneCore.Game.Conditions;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Loot;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Loot;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.Loot;

/// <summary>
/// A loot group never picks a row nobody who shares the loot can take (vmangos LootMgr.cpp:403-425, 1056-1090): a faction-split group
/// (Horde-only and Alliance-only variants of one drop, told apart by condition_id) used to pick one row blind and then lose it to the
/// per-viewer condition, so an Alliance-only raid lost the item whenever the Horde row was picked.
/// </summary>
public sealed class LootGroupTeamFilterTests
{
    private const uint HordeCondition = 11;
    private const uint AllianceCondition = 12;
    private const int Rolls = 1000;

    // Condition ids the Alliance recipients satisfy.
    private static LootRowFilter AllianceRaid() => (row, inherited) =>
        (row.ConditionId == 0 || row.ConditionId == AllianceCondition) && inherited.All(id => id == AllianceCondition);

    private static LootRowFilter MixedRaid() => (row, inherited) => true;

    private static LootGenerator Generator(int seed, params (LootTableKind, LootStoreRow)[] rows)
        => new(new LootContent(rows, []), new Random(seed));

    [Fact]
    public void EqualChanceFactionGroup_AlwaysYieldsTheAllianceRow_ForAnAllianceRaid()
    {
        LootGenerator generator = Generator(1,
            (LootTableKind.Creature, Row(1, 100, 0, group: 1, condition: HordeCondition)),
            (LootTableKind.Creature, Row(1, 200, 0, group: 1, condition: AllianceCondition)));
        LootRowFilter filter = AllianceRaid();
        for (int i = 0; i < Rolls; i++)
        {
            RolledLoot roll = Assert.Single(generator.Roll(LootTableKind.Creature, 1, filter));
            Assert.Equal(200u, roll.ItemId);
        }
    }

    [Fact]
    public void WithoutAFilter_TheSameGroupStillDropsTheHordeRowSometimes()
    {
        LootGenerator generator = Generator(1,
            (LootTableKind.Creature, Row(1, 100, 0, group: 1, condition: HordeCondition)),
            (LootTableKind.Creature, Row(1, 200, 0, group: 1, condition: AllianceCondition)));
        int horde = Enumerable.Range(0, Rolls).Count(_ => generator.Roll(LootTableKind.Creature, 1).Single().ItemId == 100);
        Assert.InRange(horde, 1, Rolls - 1);
    }

    [Fact]
    public void ExplicitChanceFactionGroup_SkipsTheRefusedRowWithoutSpendingItsChance()
    {
        // Horde row 60%, Alliance row 40%: the Alliance raid must still get its 40%, and never the Horde item.
        LootGenerator generator = Generator(2,
            (LootTableKind.Creature, Row(1, 100, 60, group: 1, condition: HordeCondition)),
            (LootTableKind.Creature, Row(1, 200, 40, group: 1, condition: AllianceCondition)));
        LootRowFilter filter = AllianceRaid();
        int alliance = 0;
        for (int i = 0; i < Rolls; i++)
        {
            List<RolledLoot> rolled = generator.Roll(LootTableKind.Creature, 1, filter);
            Assert.DoesNotContain(rolled, r => r.ItemId == 100);
            alliance += rolled.Count;
        }

        Assert.InRange(alliance, 300, 500);
    }

    [Fact]
    public void ExplicitChanceFaction_FallsThroughToTheEqualChancePool()
    {
        LootGenerator generator = Generator(3,
            (LootTableKind.Creature, Row(1, 100, 100, group: 1, condition: HordeCondition)),
            (LootTableKind.Creature, Row(1, 300, 0, group: 1)));
        LootRowFilter filter = AllianceRaid();
        for (int i = 0; i < Rolls; i++)
        {
            Assert.Equal(300u, Assert.Single(generator.Roll(LootTableKind.Creature, 1, filter)).ItemId);
        }
    }

    [Fact]
    public void MixedRaid_AllowsEitherFactionRow()
    {
        LootGenerator generator = Generator(4,
            (LootTableKind.Creature, Row(1, 100, 0, group: 1, condition: HordeCondition)),
            (LootTableKind.Creature, Row(1, 200, 0, group: 1, condition: AllianceCondition)));
        LootRowFilter filter = MixedRaid();
        HashSet<uint> seen = [];
        for (int i = 0; i < Rolls; i++)
        {
            seen.Add(Assert.Single(generator.Roll(LootTableKind.Creature, 1, filter)).ItemId);
        }

        Assert.Equal(new uint[] { 100, 200 }, seen.Order());
    }

    [Fact]
    public void GroupWhereNoRowIsAllowed_YieldsNothing()
    {
        LootGenerator generator = Generator(5,
            (LootTableKind.Creature, Row(1, 100, 0, group: 1, condition: HordeCondition)));
        LootRowFilter filter = AllianceRaid();
        for (int i = 0; i < 100; i++)
        {
            Assert.Empty(generator.Roll(LootTableKind.Creature, 1, filter));
        }
    }

    [Fact]
    public void ReferenceGroup_IsFilteredToo_AndInheritsTheReferenceRowsCondition()
    {
        // Creature 1 -> reference 9 group 1 (faction-split rows) and, through a reference row that carries a condition nobody holds,
        // reference 8 group 1 (item 700, no condition of its own: the filter sees only the inherited condition).
        LootGenerator generator = Generator(6,
            (LootTableKind.Creature, Row(1, 0, 100, group: 1, minOrRef: -9, max: 1)),
            (LootTableKind.Creature, Row(1, 0, 100, group: 1, minOrRef: -8, max: 1, condition: HordeCondition)),
            (LootTableKind.Reference, Row(9, 100, 0, group: 1, condition: HordeCondition)),
            (LootTableKind.Reference, Row(9, 200, 0, group: 1, condition: AllianceCondition)),
            (LootTableKind.Reference, Row(8, 700, 0, group: 1)));

        // Known to be reachable: without a filter item 700 drops every time (its group has one member).
        for (int i = 0; i < 20; i++)
        {
            Assert.Contains(generator.Roll(LootTableKind.Creature, 1), r => r.ItemId == 700);
        }

        LootRowFilter filter = AllianceRaid();
        for (int i = 0; i < Rolls; i++)
        {
            List<RolledLoot> rolled = generator.Roll(LootTableKind.Creature, 1, filter);
            Assert.Contains(rolled, r => r.ItemId == 200);
            Assert.DoesNotContain(rolled, r => r.ItemId == 100);
            Assert.DoesNotContain(rolled, r => r.ItemId == 700);
        }
    }

    [Fact]
    public void UngroupedRows_AreNotPreFiltered()
    {
        LootGenerator generator = Generator(7,
            (LootTableKind.Creature, Row(1, 100, 100, group: 0, condition: HordeCondition)));
        RolledLoot roll = Assert.Single(generator.Roll(LootTableKind.Creature, 1, AllianceRaid()));
        Assert.Equal(HordeCondition, roll.ConditionId);
    }
}

/// <summary>
/// <see cref="LootService.Generate"/> over real recipients and a real condition table: the pre-filter is vmangos AllowedForTeam (team
/// conditions against the loot team, cross-faction allows all, plus conditions decidable without a player), nothing more.
/// </summary>
public sealed class LootServiceTeamPrefilterTests
{
    private const uint HordeCondition = 11;
    private const uint AllianceCondition = 12;
    private const uint LevelCondition = 13;
    private const uint GameEventCondition = 14;
    private const uint HordeItem = ItemTestData.RecruitsShirt;
    private const uint AllianceItem = ItemTestData.WornWoodenShield;
    private const int Rolls = 400;

    private static readonly ConditionRecord[] Conditions =
    [
        new(HordeCondition, 6, 67, 0, 0, 0, 0),
        new(AllianceCondition, 6, 469, 0, 0, 0, 0),
        new(LevelCondition, 15, 60, 0, 0, 0, 0),
        new(GameEventCondition, 12, 5, 0, 0, 0, 0),
    ];

    private static LootService Service(int seed, Func<Player, uint, bool>? perViewer, params (LootTableKind, LootStoreRow)[] rows)
    {
        var evaluator = new ConditionEvaluator(ConditionTable.Build(Conditions), new ConditionContext { IsGameEventActive = _ => false });
        return new LootService(new LootContent(rows, []), random: new Random(seed))
        {
            Items = ItemStore,
            Conditions = (player, _, id) => perViewer is null ? evaluator.IsSatisfied(id, player, null) : perViewer(player, id),
            ConditionRules = () => evaluator,
        };
    }

    private static Player Recipient(uint guid, Race race)
        => TestWorld.CreatePlayer(guid, 0, 0, Player(guid).Session, race: race);

    private static LootBag Generate(LootService service, uint entry, params Player[] recipients)
        => service.Generate(default, LootSourceKind.Creature, LootType.Corpse, LootTableKind.Creature, entry, recipients);

    private static (LootTableKind, LootStoreRow)[] FactionSplitGroup() =>
    [
        (LootTableKind.Creature, Row(1, HordeItem, 0, group: 1, condition: HordeCondition)),
        (LootTableKind.Creature, Row(1, AllianceItem, 0, group: 1, condition: AllianceCondition)),
    ];

    [Fact]
    public void AllianceOnlyRaid_AlwaysGetsTheAllianceRowOfAFactionSplitGroup()
    {
        LootService service = Service(1, null, FactionSplitGroup());
        Player human = Recipient(1, Race.Human);
        Player dwarf = Recipient(2, Race.Dwarf);
        for (int i = 0; i < Rolls; i++)
        {
            LootItem item = Assert.Single(Generate(service, 1, human, dwarf).Items);
            Assert.Equal(AllianceItem, item.ItemId);
        }
    }

    [Fact]
    public void MixedRaid_CanGetEitherRow_EachRestrictedToItsFaction()
    {
        LootService service = Service(2, null, FactionSplitGroup());
        Player human = Recipient(1, Race.Human);
        Player orc = Recipient(2, Race.Orc);
        HashSet<uint> seen = [];
        for (int i = 0; i < Rolls; i++)
        {
            LootItem item = Assert.Single(Generate(service, 1, human, orc).Items);
            seen.Add(item.ItemId);
            Player holder = item.ItemId == HordeItem ? orc : human;
            Player other = item.ItemId == HordeItem ? human : orc;
            Assert.Contains(holder.Guid, item.AllowedLooters);
            Assert.DoesNotContain(other.Guid, item.AllowedLooters);
        }

        Assert.Equal(new[] { HordeItem, AllianceItem }.Order(), seen.Order());
    }

    [Fact]
    public void ReferenceRowCondition_IsStillAppliedPerViewer_ThroughTheInheritedConditions()
    {
        // Creature 1 -> reference 9 group 1 (one unconditioned item) through a reference row conditioned on the Horde.
        (LootTableKind, LootStoreRow)[] rows =
        [
            (LootTableKind.Creature, Row(1, 0, 100, minOrRef: -9, max: 1, condition: HordeCondition)),
            (LootTableKind.Reference, Row(9, AllianceItem, 0, group: 1)),
        ];
        LootService service = Service(3, null, rows);
        Player human = Recipient(1, Race.Human);
        Player orc = Recipient(2, Race.Orc);

        Assert.Empty(Generate(service, 1, human).Items);

        LootItem item = Assert.Single(Generate(service, 1, human, orc).Items);
        Assert.Equal(AllianceItem, item.ItemId);
        Assert.Contains(orc.Guid, item.AllowedLooters);
        Assert.DoesNotContain(human.Guid, item.AllowedLooters);
    }

    [Fact]
    public void PlayerDependentCondition_IsNotPreFiltered_AndStillSpendsItsShareOfTheRoll()
    {
        // vmangos AllowedForTeam does not decide level/quest/skill rows: the 50% row is picked half the time and then hidden per viewer.
        LootService service = Service(4, (_, id) => id != LevelCondition,
            (LootTableKind.Creature, Row(1, HordeItem, 50, group: 1, condition: LevelCondition)),
            (LootTableKind.Creature, Row(1, AllianceItem, 0, group: 1)));
        Player human = Recipient(1, Race.Human);
        int alliance = 0;
        for (int i = 0; i < Rolls; i++)
        {
            LootBag bag = Generate(service, 1, human);
            Assert.DoesNotContain(bag.Items, item => item.ItemId == HordeItem);
            alliance += bag.Items.Count;
        }

        Assert.InRange(alliance, 120, 280);
    }

    [Fact]
    public void NonPlayerCondition_NotMet_IsSkippedBeforeThePick()
    {
        // The game-event row can be decided without a player and its event is off: the group never picks it, though the viewer check would pass.
        LootService service = Service(5, (_, _) => true,
            (LootTableKind.Creature, Row(1, HordeItem, 0, group: 1, condition: GameEventCondition)),
            (LootTableKind.Creature, Row(1, AllianceItem, 0, group: 1)));
        Player human = Recipient(1, Race.Human);
        for (int i = 0; i < Rolls; i++)
        {
            Assert.Equal(AllianceItem, Assert.Single(Generate(service, 1, human).Items).ItemId);
        }
    }

    [Fact]
    public void WithoutConditionRules_GroupsPickBlind()
    {
        LootService service = Service(6, null, FactionSplitGroup());
        service.ConditionRules = null;
        Player human = Recipient(1, Race.Human);
        int empty = Enumerable.Range(0, Rolls).Count(_ => Generate(service, 1, human).Items.Count == 0);
        Assert.InRange(empty, 1, Rolls - 1);
    }
}
