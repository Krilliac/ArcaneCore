using ArcaneCore.Game.Crafting;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.Game.Tests.Crafting;

/// <summary>
/// The crafting pipeline over the numbers of three real classic-db 1.12.1 (z2815) <c>spell_template</c> rows, read only, ids and counts as fixtures:
/// 3275 Linen Bandage (1 x Linen Cloth 2589 -> Linen Bandage 1251), 17187 Transmute: Arcanite (Thorium Bar 12359 + Arcane Crystal 12363, tool Philosopher's
/// Stone 9149, spell category 310, category recovery 172800000 ms = 2 days, creates Arcanite Bar 12360) and 7421 Runed Copper Rod (three reagents, Runed
/// Copper Rod 6218). The row layout is what <c>SpellStoreFactory</c> maps (<see cref="SpellReagent"/>, <see cref="SpellInfo.Totems"/>).
/// </summary>
public sealed class RealCraftRowsTests
{
    // Linen Cloth 2589 and Linen Bandage 1251 are in the shared kit already.
    private static readonly ItemTemplate[] s_items =
    [
        new() { Entry = 12359, Class = 7, Name = "Thorium Bar", DisplayId = 3, Stackable = 20, Quality = 1 },
        new() { Entry = 12363, Class = 7, Name = "Arcane Crystal", DisplayId = 4, Stackable = 20, Quality = 1 },
        new() { Entry = 9149, Class = 7, Name = "Philosopher's Stone", DisplayId = 5, Stackable = 1, Quality = 1 },
        new() { Entry = 12360, Class = 7, Name = "Arcanite Bar", DisplayId = 6, Stackable = 20, Quality = 1 },
        new() { Entry = 6217, Class = 7, Name = "Copper Rod", DisplayId = 7, Stackable = 20, Quality = 1 },
        new() { Entry = 10940, Class = 7, Name = "Strange Dust", DisplayId = 8, Stackable = 20, Quality = 1 },
        new() { Entry = 10938, Class = 7, Name = "Lesser Magic Essence", DisplayId = 9, Stackable = 20, Quality = 1 },
        new() { Entry = 6218, Class = 7, Name = "Runed Copper Rod", DisplayId = 10, Stackable = 1, Quality = 1 },
    ];

    private static SpellInfo Row(uint id, uint creates, params SpellReagent[] reagents) =>
        CraftingTestKit.Craft(id, SpellTestKit.Effect(SpellEffectName.CreateItem, 1) with { ItemType = creates }) with
        {
            Attributes = SpellAttributes.IsTradeskill,
            Reagents = reagents,
        };

    private static CraftingTestKit Rig()
    {
        SpellInfo transmute = Row(17187, 12360, new SpellReagent(12359, 1), new SpellReagent(12363, 1)) with
        {
            Totems = [9149],
            Category = 310,
            CategoryRecoveryTime = 172_800_000,
        };
        var rig = new CraftingTestKit(
            [Row(3275, 1251, new SpellReagent(2589, 1)), transmute, Row(7421, 6218, new SpellReagent(6217, 1), new SpellReagent(10940, 1), new SpellReagent(10938, 1))],
            s_items);
        ReagentRules.Install(rig.System);
        CreateItemSpells.Install(rig.System);
        return rig;
    }

    [Fact]
    public void LinenBandage_3275_TurnsOneLinenClothIntoOneBandage()
    {
        using CraftingTestKit rig = Rig();
        rig.Give(2589, 3);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(3275));

        Assert.Equal(2u, rig.Inventory.GetItemCount(2589));
        Assert.Equal(1u, rig.Inventory.GetItemCount(1251));
    }

    [Fact]
    public void RunedCopperRod_7421_NeedsAllThreeReagents()
    {
        using CraftingTestKit rig = Rig();
        rig.Give(6217);
        rig.Give(10940);

        Assert.Equal(SpellCastResult.ItemNotReady, rig.Cast(7421));   // no Lesser Magic Essence
        Assert.Equal(1u, rig.Inventory.GetItemCount(6217));

        rig.Give(10938);
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(7421));
        Assert.Equal(1u, rig.Inventory.GetItemCount(6218));
        Assert.Equal(0u, rig.Inventory.GetItemCount(6217) + rig.Inventory.GetItemCount(10940) + rig.Inventory.GetItemCount(10938));
    }

    [Fact]
    public void TransmuteArcanite_17187_NeedsThePhilosophersStone_ConsumesNotTheStone_AndStartsATwoDayCategoryCooldown()
    {
        using CraftingTestKit rig = Rig();
        rig.Give(12359, 2);
        rig.Give(12363, 2);

        Assert.Equal(SpellCastResult.ItemGone, rig.Cast(17187));   // no tool

        rig.Give(9149);
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(17187));
        Assert.Equal(1u, rig.Inventory.GetItemCount(12360));
        Assert.Equal(1u, rig.Inventory.GetItemCount(9149));         // the tool stays
        Assert.Equal(1u, rig.Inventory.GetItemCount(12359));

        Assert.Equal(SpellCastResult.NotReady, rig.Cast(17187));    // category 310 is on cooldown for 2 days
        rig.Kit.Now += 172_800_000 - 1000;
        rig.Kit.System.Update(100);
        Assert.Equal(SpellCastResult.NotReady, rig.Cast(17187));
        rig.Kit.Now += 2000;
        rig.Kit.System.Update(100);
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(17187));
        Assert.Equal(2u, rig.Inventory.GetItemCount(12360));
    }
}
