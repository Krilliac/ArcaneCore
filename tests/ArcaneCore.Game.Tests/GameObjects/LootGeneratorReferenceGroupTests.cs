using ArcaneCore.Game.Loot;
using ArcaneCore.Kernel.WorldData.Loot;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>
/// vmangos LootTemplate::AddEntry/Process (D:\refs\vmangos\src\game\LootMgr.cpp:1193-1244): reference
/// rows are never group members, roll by their own chance, and a reference row's groupid selects only that
/// group of the referenced template; maxcount 0 processes it zero times. Shapes mirror classic-db
/// fishing entries 443/445 (reference 11103 group 1) and creature entry 639 (several reference rows in one group id).
/// </summary>
public sealed class LootGeneratorReferenceGroupTests
{
    private static LootGenerator Generator(int seed, params (LootTableKind, LootStoreRow)[] rows)
        => new(new LootContent(rows, []), new Random(seed));

    [Fact]
    public void ReferenceRowWithGroup_ProcessesOnlyThatGroupOfTheReference()
    {
        // fishing 445 -> reference 11103 group 1; 11103 also has an ungrouped entry (item 9999) that must never drop.
        LootGenerator generator = Generator(1,
            (LootTableKind.Creature, Row(445, 0, 100, group: 1, minOrRef: -11103, max: 1)),
            (LootTableKind.Reference, Row(11103, 9999, 100, group: 0)),
            (LootTableKind.Reference, Row(11103, 101, 0, group: 1)),
            (LootTableKind.Reference, Row(11103, 102, 0, group: 1)),
            (LootTableKind.Reference, Row(11103, 201, 100, group: 2)));
        for (int i = 0; i < 300; i++)
        {
            RolledLoot roll = Assert.Single(generator.Roll(LootTableKind.Creature, 445));
            Assert.Contains(roll.ItemId, new uint[] { 101, 102 });
        }
    }

    [Fact]
    public void TwoReferenceRowsSharingAGroupId_RollIndependently()
    {
        LootGenerator generator = Generator(3,
            (LootTableKind.Creature, Row(639, 0, 50, group: 1, minOrRef: -1, max: 1)),
            (LootTableKind.Creature, Row(639, 0, 100, group: 1, minOrRef: -2, max: 1)),
            (LootTableKind.Reference, Row(1, 11, 100, group: 1)),
            (LootTableKind.Reference, Row(2, 22, 100, group: 1)));
        int both = 0;
        int firstOnly = 0;
        for (int i = 0; i < 1000; i++)
        {
            List<RolledLoot> rolled = generator.Roll(LootTableKind.Creature, 639);
            Assert.Contains(rolled, r => r.ItemId == 22);
            both += rolled.Any(r => r.ItemId == 11) ? 1 : 0;
            firstOnly += rolled.Count == 1 ? 1 : 0;
        }

        Assert.InRange(both, 430, 570);
        Assert.Equal(1000 - both, firstOnly);
    }

    [Fact]
    public void ReferenceWithMaxcountZero_ProcessesZeroTimes()
    {
        LootGenerator generator = Generator(4,
            (LootTableKind.Creature, Row(5, 0, 100, minOrRef: -7, max: 0)),
            (LootTableKind.Reference, Row(7, 70, 100)));
        Assert.Empty(generator.Roll(LootTableKind.Creature, 5));
    }

    [Fact]
    public void ReferenceMaxcountRepeatsTheReference()
    {
        LootGenerator generator = Generator(4,
            (LootTableKind.Creature, Row(5, 0, 100, minOrRef: -7, max: 3)),
            (LootTableKind.Reference, Row(7, 70, 100)));
        Assert.Equal(3, generator.Roll(LootTableKind.Creature, 5).Count);
    }

    [Fact]
    public void ReferenceRowIsNotCountedAsGroupMember_PlainGroupMembersStillExclusive()
    {
        LootGenerator generator = Generator(8,
            (LootTableKind.Creature, Row(9, 0, 100, group: 1, minOrRef: -7, max: 1)),
            (LootTableKind.Creature, Row(9, 31, 100, group: 1)),
            (LootTableKind.Creature, Row(9, 32, 0, group: 1)),
            (LootTableKind.Reference, Row(7, 70, 100, group: 1)));
        for (int i = 0; i < 200; i++)
        {
            List<RolledLoot> rolled = generator.Roll(LootTableKind.Creature, 9);
            Assert.Contains(rolled, r => r.ItemId == 70);   // the reference always rolls
            Assert.Equal(1, rolled.Count(r => r.ItemId is 31 or 32)); // the plain group yields exactly one (100% row)
        }
    }

    [Fact]
    public void GroupsAreBuiltByGroupId_NotByRowOrder()
    {
        // Rows of one group interleaved with another: LootContent sorts, but the generator must not depend on it.
        LootGenerator generator = Generator(2,
            (LootTableKind.Creature, Row(3, 1, 0, group: 2)),
            (LootTableKind.Creature, Row(3, 2, 0, group: 1)),
            (LootTableKind.Creature, Row(3, 3, 0, group: 2)),
            (LootTableKind.Creature, Row(3, 4, 0, group: 1)));
        for (int i = 0; i < 100; i++)
        {
            List<RolledLoot> rolled = generator.Roll(LootTableKind.Creature, 3);
            Assert.Equal(2, rolled.Count);
            Assert.Single(rolled, r => r.ItemId is 1 or 3);
            Assert.Single(rolled, r => r.ItemId is 2 or 4);
        }
    }
}


