using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Tests.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.SpellRules;

/// <summary>
/// The signed resist roll: damage over time resists a tenth as often (vmangos Unit.cpp:2406-2425), a vulnerability adds
/// damage (Unit.cpp:1948-1953, 2229-2230), and an operator-supplied outcome table replaces the quarter-step approximation.
/// The tables here are synthetic test data, not the retail rows.
/// </summary>
public sealed class ResistRollTests
{
    private static (SpellTestKit Kit, Player Caster, Player Target) Setup(int fireResistance)
    {
        var kit = new SpellTestKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        caster.Level = 60;
        target.Level = 60;
        target.SetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Fire, fireResistance);
        return (kit, caster, target);
    }

    private static SpellInfo Fire(uint id = 1) => RuleTestSupport.Magic(id, SpellSchool.Fire);

    private static ResistOutcomeTable Table(Func<int, string> row) =>
        ResistOutcomeTable.Parse(Enumerable.Range(0, ResistOutcomeTable.RowCount).Select(row));

    [Fact]
    public void ADirectHit_IsResistedAtTheFullChance_ADotAtATenth()
    {
        (SpellTestKit kit, Player caster, Player target) = Setup(200); // 0.5 average resist
        using (kit)
        {
            var rules = new VanillaSpellCombatRules();
            SpellInfo fire = Fire();

            Assert.Equal(50, rules.RollResist(kit.System, caster, target, fire, 100, periodic: false));
            kit.System.Random = new ScriptedRandom(1900, 0);
            Assert.Equal(25, rules.RollResist(kit.System, caster, target, fire, 100, periodic: true)); // 0.05 average: 20% to take the 25% step
            kit.System.Random = new ScriptedRandom(2100, 0);
            Assert.Equal(0, rules.RollResist(kit.System, caster, target, fire, 100, periodic: true));
        }
    }

    [Theory]
    [InlineData(23461u)]
    [InlineData(24818u)]
    [InlineData(25812u)]
    [InlineData(28531u)]
    public void TheFourExemptDots_FollowTheNormalResistRules(uint spellId)
    {
        (SpellTestKit kit, Player caster, Player target) = Setup(200);
        using (kit)
        {
            Assert.Equal(50, new VanillaSpellCombatRules().RollResist(kit.System, caster, target, Fire(spellId), 100, periodic: true));
        }
    }

    [Fact]
    public void ANegativeResistance_AddsDamage_EvenForSpellsThatCannotBeResisted()
    {
        (SpellTestKit kit, Player caster, Player target) = Setup(-150); // -150 / 300 skill = -0.5
        using (kit)
        {
            var rules = new VanillaSpellCombatRules();
            SpellInfo fire = Fire();

            Assert.Equal(-50, rules.RollResist(kit.System, caster, target, fire, 100, periodic: false));
            Assert.Equal(-50, rules.RollResist(kit.System, caster, target, fire with { AttributesEx4 = SpellRuleFlags.Ex4IgnoreResistances }, 100, periodic: false));
            Assert.Equal(0u, rules.RollPartialResist(kit.System, caster, target, fire, 100)); // the unsigned interface never reports a bonus
        }
    }

    [Fact]
    public void DealDirectDamage_AddsTheVulnerabilityBonusBeforeAbsorbs()
    {
        (SpellTestKit kit, Player caster, Player target) = Setup(-150);
        using (kit)
        {
            kit.System.CombatRules = new VanillaSpellCombatRules();
            target.Health = target.MaxHealth = 1000;

            SpellDamageResult result = kit.System.DealDirectDamage(caster, target, Fire(), 100, allowCrit: false);

            Assert.Equal((150u, 0u), (result.Dealt, result.Resisted));
        }
    }

    [Fact]
    public void ATableOutcome_ReplacesTheApproximation_AndRoundsAFullResistDownTo75()
    {
        (SpellTestKit kit, Player caster, Player target) = Setup(200);
        using (kit)
        {
            ResistOutcomeTable half = Table(i => i == 0 ? "0,0,0,0,100,0" : $"0,0,100,0,0,{i * 2.5f}");
            ResistOutcomeTable full = Table(i => i == 0 ? "0,0,0,0,100,0" : $"100,0,0,0,0,{i * 2.5f}");

            Assert.Equal(50, new VanillaSpellCombatRules { ResistTable = half }.RollResist(kit.System, caster, target, Fire(), 100, periodic: false));
            Assert.Equal(75, new VanillaSpellCombatRules { ResistTable = full }.RollResist(kit.System, caster, target, Fire(), 100, periodic: false));
        }
    }

    [Fact]
    public void ATableInterpolatesBetweenRows_AndScalesDotsByATenth()
    {
        // Row i: chance 2.5i; resist25 = 3i percent (rest unresisted). At chance 10 (row 4) the chance of 25% is 12%.
        ResistOutcomeTable table = Table(i => $"0,0,0,{3 * i},{100 - (3 * i)},{i * 2.5f}");

        Assert.Equal(0.25f, table.Multiplier(10f, 11));
        Assert.Equal(0f, table.Multiplier(10f, 12));
        Assert.Equal(0.25f, table.Multiplier(11.25f, 13)); // halfway to row 5: 13.5%
        Assert.Equal(0f, table.Multiplier(11.25f, 14));
    }

    [Theory]
    [InlineData("1,2,3")]
    [InlineData("not,a,number,0,0,0")]
    public void AMalformedTable_IsRejected(string row)
    {
        Assert.Throws<InvalidDataException>(() => ResistOutcomeTable.Parse([row]));
        Assert.Throws<InvalidDataException>(() => ResistOutcomeTable.Parse(Enumerable.Repeat("0,0,0,0,100,0", ResistOutcomeTable.RowCount)));
    }

    [Fact]
    public void ATableWhoseOutcomesDoNotSumTo100_IsRejected()
    {
        Assert.Throws<InvalidDataException>(() => ResistOutcomeTable.Parse(Enumerable.Range(0, ResistOutcomeTable.RowCount).Select(i => $"0,0,0,0,50,{i}")));
    }
}
