using ArcaneCore.Game.Loot;
using ArcaneCore.Kernel.WorldData.Loot;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.GameObjects;

public sealed class LootGeneratorTests
{
    private static LootGenerator Generator(int seed, params (LootTableKind, LootStoreRow)[] rows)
        => new(new LootContent(rows, []), new Random(seed));

    [Fact]
    public void Ungrouped_HundredPercentAlwaysDrops_ZeroNeverDrops_CountWithinRange()
    {
        LootGenerator generator = Generator(1,
            (LootTableKind.Creature, Row(1, 10, 100, minOrRef: 2, max: 5)),
            (LootTableKind.Creature, Row(1, 11, 0)));
        for (int i = 0; i < 200; i++)
        {
            RolledLoot roll = Assert.Single(generator.Roll(LootTableKind.Creature, 1));
            Assert.Equal(10u, roll.ItemId);
            Assert.InRange(roll.Count, 2u, 5u);
            Assert.False(roll.IsQuestItem);
        }
    }

    [Fact]
    public void SameSeed_SameLoot_AndChanceIsRespectedStatistically()
    {
        (LootTableKind, LootStoreRow)[] rows = [(LootTableKind.Creature, Row(1, 10, 25)), (LootTableKind.Creature, Row(1, 11, 75, minOrRef: 1, max: 3))];
        LootGenerator a = Generator(42, rows);
        LootGenerator b = Generator(42, rows);
        int drops = 0;
        for (int i = 0; i < 4000; i++)
        {
            List<RolledLoot> ra = a.Roll(LootTableKind.Creature, 1);
            Assert.Equal(ra, b.Roll(LootTableKind.Creature, 1));
            drops += ra.Count(r => r.ItemId == 10);
        }

        Assert.InRange(drops, 850, 1150); // 25% of 4000
    }

    [Fact]
    public void Group_YieldsAtMostOne_ExplicitChancesFirst_ThenEqualChancePool()
    {
        LootGenerator generator = Generator(7,
            (LootTableKind.Creature, Row(1, 20, 30, group: 1)),
            (LootTableKind.Creature, Row(1, 21, 0, group: 1)),
            (LootTableKind.Creature, Row(1, 22, 0, group: 1)));
        var counts = new Dictionary<uint, int>();
        for (int i = 0; i < 3000; i++)
        {
            RolledLoot roll = Assert.Single(generator.Roll(LootTableKind.Creature, 1));
            counts[roll.ItemId] = counts.GetValueOrDefault(roll.ItemId) + 1;
        }

        Assert.InRange(counts[20], 750, 1050);  // 30%
        Assert.InRange(counts[21], 900, 1200);  // 35% each for the equal pool
        Assert.InRange(counts[22], 900, 1200);
    }

    [Fact]
    public void Group_WithOnlyExplicitChances_CanYieldNothing_AndHundredIsCertain()
    {
        LootGenerator sometimes = Generator(3, (LootTableKind.Creature, Row(1, 20, 10, group: 2)));
        int empty = Enumerable.Range(0, 1000).Count(_ => sometimes.Roll(LootTableKind.Creature, 1).Count == 0);
        Assert.InRange(empty, 850, 950);

        LootGenerator certain = Generator(3, (LootTableKind.Creature, Row(1, 20, 100, group: 2)), (LootTableKind.Creature, Row(1, 21, 50, group: 2)));
        Assert.All(Enumerable.Range(0, 100), _ => Assert.Equal(20u, Assert.Single(certain.Roll(LootTableKind.Creature, 1)).ItemId));
    }

    [Fact]
    public void Reference_IsProcessedMaxCountTimes_AndCyclesTerminate()
    {
        LootGenerator generator = Generator(5,
            (LootTableKind.Creature, Row(1, 0, 100, minOrRef: -900, max: 3)),
            (LootTableKind.Reference, Row(900, 30, 100)));
        Assert.Equal(3, generator.Roll(LootTableKind.Creature, 1).Count(r => r.ItemId == 30));

        LootGenerator cyclic = Generator(5,
            (LootTableKind.Reference, Row(1, 0, 100, minOrRef: -1, max: 1)),
            (LootTableKind.Reference, Row(1, 31, 100)));
        List<RolledLoot> rolled = cyclic.Roll(LootTableKind.Reference, 1);
        Assert.Equal(LootGenerator.MaxReferenceDepth + 1, rolled.Count);
    }

    [Fact]
    public void QuestChance_IsAbsolute_AndMarksTheItem_ConditionCarried()
    {
        LootGenerator generator = Generator(1,
            (LootTableKind.GameObject, Row(1, 40, -100)),
            (LootTableKind.GameObject, Row(1, 41, 100, condition: 9)));
        List<RolledLoot> rolled = generator.Roll(LootTableKind.GameObject, 1);
        Assert.Contains(rolled, r => r is { ItemId: 40, IsQuestItem: true });
        Assert.Contains(rolled, r => r is { ItemId: 41, ConditionId: 9 });
        Assert.Empty(generator.Roll(LootTableKind.Creature, 1));
        Assert.Empty(generator.Roll(LootTableKind.GameObject, 2));
    }

    [Fact]
    public void LootContent_OrdersRowsByGroupThenItem_AndIndexesCreatureInfo()
    {
        var content = new LootContent(
            [(LootTableKind.Item, Row(1, 9, 1, group: 2)), (LootTableKind.Item, Row(1, 5, 1, group: 0)), (LootTableKind.Item, Row(1, 3, 1, group: 2))],
            [new CreatureLootInfo(7, 70, 71, 1, 2)]);
        Assert.Equal([5u, 3u, 9u], content.GetRows(LootTableKind.Item, 1).Select(r => r.Item));
        Assert.True(content.HasEntry(LootTableKind.Item, 1));
        Assert.False(content.HasEntry(LootTableKind.Skinning, 1));
        Assert.Equal(3, content.RowCount);
        Assert.Equal(71u, content.FindCreature(7)!.SkinningLootId);
        Assert.Null(content.FindCreature(8));
    }
}
