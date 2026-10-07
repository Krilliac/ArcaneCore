using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Spells;
using Xunit;

namespace ArcaneCore.World.Tests.Crafting;

/// <summary>
/// Crafting lane, slice spellinfo-item-fields: <see cref="SpellStoreFactory"/> carries Reagent1-8, ReagentCount1-8 and
/// Totem1-2 of <c>spell_template</c> onto <see cref="SpellInfo"/> (vmangos SpellEntry.h:635-637: Totem 40-41, Reagent 42-49,
/// ReagentCount 50-57). Item ids are the classic-db 1.12.1 values of the named crafts.
/// </summary>
public sealed class SpellStoreFactoryItemFieldsTests
{
    private static SpellInfo Convert(SpellTemplateRow row) => SpellStoreFactory.ToSpellInfo(
        row,
        new Dictionary<uint, SpellCastTimeRow>(),
        new Dictionary<uint, SpellDurationRow>(),
        new Dictionary<uint, SpellRangeRow>(),
        new Dictionary<uint, SpellRadiusRow>());

    [Fact]
    public void RunedCopperRod_Row_CarriesReagentsAndTotemTool()
    {
        // classic-db spell 7421 (Runed Copper Rod): reagents Copper Bar 2840 x6 ... Totem1 Blacksmith Hammer is not used here; the
        // numbers below exercise the mapping, not the exact recipe.
        SpellInfo spell = Convert(new SpellTemplateRow { Id = 7421, Reagent1 = 2840, ReagentCount1 = 6, Reagent2 = 4470, ReagentCount2 = 1, Totem1 = 5956 });

        Assert.Equal(new[] { new SpellReagent(2840, 6), new SpellReagent(4470, 1) }, spell.Reagents.Where(r => r.IsPresent));
        Assert.Equal(new uint[] { 5956 }, spell.Totems);
    }

    [Fact]
    public void ReagentSlotsWithZeroOrNegativeItem_AreSkipped()
    {
        // vmangos Spell.cpp:7254 "if (m_spellInfo->Reagent[i] <= 0) continue".
        SpellInfo spell = Convert(new SpellTemplateRow { Id = 1, Reagent1 = 0, ReagentCount1 = 5, Reagent2 = -1, ReagentCount2 = 5, Reagent3 = 2589, ReagentCount3 = 2 });

        // The eight slots keep their positions (vmangos Reagent[i]); only a positive item is a reagent.
        Assert.Equal(new[] { new SpellReagent(2589, 2) }, spell.Reagents.Where(r => r.IsPresent));
        Assert.Equal(new SpellReagent(2589, 2), spell.Reagents[2]);
    }

    [Fact]
    public void AllEightReagentSlotsAndBothTotemSlots_RoundTrip()
    {
        SpellInfo spell = Convert(new SpellTemplateRow
        {
            Id = 2,
            Reagent1 = 11, ReagentCount1 = 1, Reagent2 = 12, ReagentCount2 = 2, Reagent3 = 13, ReagentCount3 = 3, Reagent4 = 14, ReagentCount4 = 4,
            Reagent5 = 15, ReagentCount5 = 5, Reagent6 = 16, ReagentCount6 = 6, Reagent7 = 17, ReagentCount7 = 7, Reagent8 = 18, ReagentCount8 = 8,
            Totem1 = 21, Totem2 = 22,
        });

        Assert.Equal(8, spell.Reagents.Count);
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(new SpellReagent((uint)(11 + i), (uint)(1 + i)), spell.Reagents[i]);
        }

        Assert.Equal(new uint[] { 21, 22 }, spell.Totems);
    }

    [Fact]
    public void RowWithoutReagents_HasEmptyLists()
    {
        SpellInfo spell = Convert(new SpellTemplateRow { Id = 3 });

        Assert.All(spell.Reagents, r => Assert.False(r.IsPresent));
        Assert.Empty(spell.Totems);
    }
}
