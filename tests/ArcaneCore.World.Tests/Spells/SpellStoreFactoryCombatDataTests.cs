using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Spells;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

/// <summary>
/// S02a: <see cref="SpellStoreFactory"/> carries the combat columns of <c>spell_template</c> (Stances,
/// StancesNot, CasterAuraState, TargetAuraState, ProcFlags, ProcChance, ProcCharges, EquippedItem*) into
/// <see cref="SpellInfo"/>. The numbers are the classic-db 1.12.1 values of the named spells (read only;
/// vmangos reads the same columns in SpellEntry).
/// </summary>
public sealed class SpellStoreFactoryCombatDataTests
{
    [Fact]
    public void ScriptTargetRows_ReachTheSpellStore()
    {
        var content = new SpellContent([new SpellTemplateRow { Id = 8283 }], [], [], [], [], [], [])
        {
            ScriptTargets = [new SpellScriptTargetRow { SpellId = 8283, Type = 1, TargetEntry = 4781 }],
        };
        SpellStore store = SpellStoreFactory.Build(content);
        Assert.Equal((8283u, 1u, 4781u), store.GetScriptTargets(8283)
            .Select(t => (t.SpellId, t.Type, t.TargetEntry)).Single());
    }

    private static SpellInfo Convert(SpellTemplateRow row) => SpellStoreFactory.ToSpellInfo(
        row,
        new Dictionary<uint, SpellCastTimeRow>(),
        new Dictionary<uint, SpellDurationRow>(),
        new Dictionary<uint, SpellRangeRow>(),
        new Dictionary<uint, SpellRadiusRow>());

    [Fact]
    public void HeroicStrike_Row_IsANextSwingSpell_ThroughBit0x4()
    {
        // classic-db spell 78: Attributes 327700 (0x50014), EquippedItemClass 2 (weapon), SubClassMask 173555.
        SpellInfo spell = Convert(new SpellTemplateRow
        {
            Id = 78, Attributes = 327700, AttributesEx = 134217728, AttributesEx3 = 1024, ProcChance = 101,
            EquippedItemClass = 2, EquippedItemSubClassMask = 173555, SpellFamilyName = 4,
        });

        Assert.True(spell.IsNextMeleeSwing);
        Assert.False(spell.HasAttribute(SpellAttributes.OnNextSwing));   // the legacy 0x400 bit is NOT what Heroic Strike uses
        Assert.True(spell.HasAttribute(SpellAttributesCombat.OnNextSwingNoDamage));
        Assert.True(spell.HasAttribute(SpellAttributesEx3Combat.RequiresMainHandWeapon));
        Assert.Equal(2, spell.EquippedItemClass);
        Assert.Equal(173555, spell.EquippedItemSubClassMask);
        Assert.Equal(101u, spell.ProcChance);
    }

    [Fact]
    public void Execute_Row_CarriesStanceMask_AndTargetAuraState()
    {
        // classic-db spell 5308 (Execute rank 1): Stances 327680 (Battle | Berserker), TargetAuraState 2 (below 20% health).
        SpellInfo spell = Convert(new SpellTemplateRow { Id = 5308, Stances = 327680, TargetAuraState = 2, EquippedItemClass = 2, EquippedItemSubClassMask = 173555 });

        Assert.Equal(327680u, spell.Stances);
        Assert.Equal((1u << 16) | (1u << 18), spell.Stances);
        Assert.Equal(AuraState.Healthless20Percent, spell.TargetAuraState);
        Assert.Equal(AuraState.None, spell.CasterAuraState);
    }

    [Fact]
    public void Revenge_Row_CarriesCasterAuraState_Defense()
    {
        // classic-db spell 6572: Stances 131072 (Defensive), CasterAuraState 1 (AURA_STATE_DEFENSE).
        SpellInfo spell = Convert(new SpellTemplateRow { Id = 6572, Stances = 131072, CasterAuraState = 1, ProcChance = 101 });

        Assert.Equal(AuraState.Defense, spell.CasterAuraState);
        Assert.Equal(1u << 17, spell.Stances);
    }

    [Fact]
    public void Overpower_Row_CarriesTheCombatAttributeBits()
    {
        // classic-db spell 7384: Attributes 2424848, AttributesEx 1209008640, Stances 65536.
        SpellInfo spell = Convert(new SpellTemplateRow { Id = 7384, Attributes = 2424848, AttributesEx = 1209008640, Stances = 65536 });

        Assert.True(spell.HasAttribute(SpellAttributesCombat.NoActiveDefense));
        Assert.True(spell.HasAttribute(SpellAttributesExCombat.ComboOnBlock));
        Assert.True(spell.NeedsComboPoints);
        Assert.False(spell.IsNextMeleeSwing);
        Assert.Equal(1u << 16, spell.Stances);
    }

    [Fact]
    public void ShieldBlock_Row_CarriesProcFlagsChanceChargesAndShieldRequirement()
    {
        // classic-db spell 2565: ProcFlags 680, ProcChance 100, ProcCharges 1, EquippedItemClass 4 (armor), SubClassMask 64 (shield).
        SpellInfo spell = Convert(new SpellTemplateRow
        {
            Id = 2565, Stances = 131072, ProcFlags = 680, ProcChance = 100, ProcCharges = 1, EquippedItemClass = 4, EquippedItemSubClassMask = 64,
        });

        Assert.Equal((ProcFlags)680, spell.ProcFlags);
        Assert.Equal(100u, spell.ProcChance);
        Assert.Equal(1u, spell.ProcCharges);
        Assert.Equal(4, spell.EquippedItemClass);
        Assert.Equal(64, spell.EquippedItemSubClassMask);
    }

    [Fact]
    public void StancesNot_AndInventoryTypeMask_AreCopied()
    {
        SpellInfo spell = Convert(new SpellTemplateRow { Id = 9, StancesNot = 6, EquippedItemInventoryTypeMask = 0x100, EquippedItemClass = -1 });

        Assert.Equal(6u, spell.StancesNot);
        Assert.Equal(0x100, spell.EquippedItemInventoryTypeMask);
        Assert.Equal(-1, spell.EquippedItemClass);
    }

    [Fact]
    public void ReagentSlotsPreserveSignedIdsCountsAndOriginalOrdering()
    {
        SpellInfo spell = Convert(new SpellTemplateRow
        {
            Id = 21169, Reagent1 = 17030, ReagentCount1 = 1, Reagent2 = -1, ReagentCount2 = 2,
            Reagent3 = 3, ReagentCount3 = 30, Reagent4 = 4, ReagentCount4 = 40,
            Reagent5 = 5, ReagentCount5 = 50, Reagent6 = 6, ReagentCount6 = 60,
            Reagent7 = 7, ReagentCount7 = 70, Reagent8 = 8, ReagentCount8 = 80,
        });
        Assert.Equal(new SpellReagent[]
        {
            new(17030, 1), new(-1, 2), new(3, 30), new(4, 40),
            new(5, 50), new(6, 60), new(7, 70), new(8, 80),
        }, spell.Reagents);
    }
}
