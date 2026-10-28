using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>
/// Creature school resistances (world schema 57): the template's six resistances are written to <c>UnitFieldResistances + 1..6</c>
/// (vmangos Creature.cpp:606-611) next to the armor at +0, and the spell resist roll reads them.
/// </summary>
public sealed class CreatureSchoolResistanceTests
{
    private static Creature MakeCreature(Action<CreatureTemplateBuilder> configure)
    {
        CreatureTemplate template = Template(WolfEntry, b =>
        {
            b.MinLevel = 63;
            b.MaxLevel = 63;
            configure(b);
        });
        var creature = new Creature(1, template, null, Content([template], []), new Random(1));
        creature.Level = 63;
        return creature;
    }

    [Fact]
    public void TheSixTemplateResistances_AreWrittenToTheUpdateFieldsAfterTheArmor()
    {
        Creature creature = MakeCreature(b =>
        {
            b.ResistanceHoly = 10;
            b.ResistanceFire = 100;
            b.ResistanceNature = 20;
            b.ResistanceFrost = 30;
            b.ResistanceShadow = 40;
            b.ResistanceArcane = 50;
        });

        Assert.Equal(0, creature.GetInt32(UpdateFields.UnitFieldResistances));
        Assert.Equal(10, creature.GetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Holy));
        Assert.Equal(100, creature.GetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Fire));
        Assert.Equal(20, creature.GetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Nature));
        Assert.Equal(30, creature.GetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Frost));
        Assert.Equal(40, creature.GetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Shadow));
        Assert.Equal(50, creature.GetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Arcane));
    }

    [Fact]
    public void ANegativeResistance_IsStoredAsAVulnerability()
    {
        Creature creature = MakeCreature(b => b.ResistanceNature = -25);

        Assert.Equal(-25, creature.GetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Nature));
    }

    [Fact]
    public void ATemplateWithoutResistances_LeavesAllSixAtZero()
    {
        Creature creature = MakeCreature(_ => { });

        for (int school = 1; school <= 6; school++)
        {
            Assert.Equal(0, creature.GetInt32(UpdateFields.UnitFieldResistances + school));
        }
    }

    [Fact]
    public void FireResistance_ShiftsOnlyTheFireResistRoll()
    {
        using var kit = new SpellTestKit();
        (Player caster, _) = kit.AddPlayer(1);
        caster.Level = 60;
        Creature plain = MakeCreature(_ => { });
        Creature fireProof = MakeCreature(b => b.ResistanceFire = 100);

        Assert.True(
            VanillaSpellCombatRules.AverageResistFraction(caster, fireProof, SpellSchool.Fire)
            > VanillaSpellCombatRules.AverageResistFraction(caster, plain, SpellSchool.Fire));
        Assert.Equal(
            VanillaSpellCombatRules.AverageResistFraction(caster, plain, SpellSchool.Frost),
            VanillaSpellCombatRules.AverageResistFraction(caster, fireProof, SpellSchool.Frost));
    }
}
