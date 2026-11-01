using ArcaneCore.Game.Loot;
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
        // Creature 1 -> reference 9 group 1 (faction-split rows); a second reference row carries a condition nobody holds.
        LootGenerator generator = Generator(6,
            (LootTableKind.Creature, Row(1, 0, 100, group: 1, minOrRef: -9, max: 1)),
            (LootTableKind.Creature, Row(1, 0, 100, group: 2, minOrRef: -8, max: 1, condition: HordeCondition)),
            (LootTableKind.Reference, Row(9, 100, 0, group: 1, condition: HordeCondition)),
            (LootTableKind.Reference, Row(9, 200, 0, group: 1, condition: AllianceCondition)),
            (LootTableKind.Reference, Row(8, 700, 0, group: 1)));
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
