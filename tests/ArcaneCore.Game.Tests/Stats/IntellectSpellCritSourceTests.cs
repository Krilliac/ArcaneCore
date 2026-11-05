using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Stats;
using ArcaneCore.Game.Tests.SpellRules;
using ArcaneCore.Game.Tests.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.Stats;

/// <summary>The intellect-driven base spell crit (vmangos Unit::GetSpellCritFromIntellect, Unit.cpp:2597-2641) feeds the spell crit roll.</summary>
public sealed class IntellectSpellCritSourceTests
{
    private const uint Bolt = 900_201;
    private const uint SchoolCrit = 900_210;
    private const uint GenericCrit = 900_211;
    private const uint AttackerCrit = 900_212;
    private const float Tol = 0.001f;

    private static SpellTestKit Kit() => new(
        RuleTestKit.Magic(Bolt, SpellSchool.Fire),
        RuleTestKit.Grant(SchoolCrit, AuraType.ModSpellCritChanceSchool, 7, 1 << (int)SpellSchool.Fire),
        RuleTestKit.Grant(GenericCrit, AuraType.ModSpellCritChance, 3),
        RuleTestKit.Grant(AttackerCrit, AuraType.ModAttackerSpellCritChance, 12, 1 << (int)SpellSchool.Fire));

    private static Player MakePlayer(SpellTestKit kit, uint guid, Class cls, byte level, uint intellect)
    {
        (Player player, _) = kit.AddPlayer(guid);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)cls);
        player.Level = level;
        player.SetUInt32(UpdateFields.UnitFieldStat0 + 3, intellect);
        return player;
    }

    [Fact]
    public void Player_GetsTheClassTableValue_FromItsLevelAndTotalIntellect()
    {
        using SpellTestKit kit = Kit();
        Player mage = MakePlayer(kit, 1, Class.Mage, 60, 100);

        Assert.Equal(5.5597f, new IntellectSpellCritSource().SpellCritPercent(mage, SpellSchool.Fire), Tol);
    }

    [Fact]
    public void Priest_WithLowIntellect_FallsBelowFivePercent()
    {
        using SpellTestKit kit = Kit();
        Player priest = MakePlayer(kit, 1, Class.Priest, 60, 100);

        Assert.Equal(4.6584f, new IntellectSpellCritSource().SpellCritPercent(priest, SpellSchool.Holy), Tol); // 2.97 + 100 / (10.03 + 0.82 * 60)
    }

    [Theory]
    [InlineData(Class.Warrior)]
    [InlineData(Class.Rogue)]
    [InlineData(Class.Hunter)]
    public void FillerRowClasses_FallToNearZero(Class cls)
    {
        using SpellTestKit kit = Kit();
        Player player = MakePlayer(kit, 1, cls, 60, 40);

        float percent = new IntellectSpellCritSource().SpellCritPercent(player, SpellSchool.Nature);

        Assert.Equal(40f / 600f, percent, Tol); // 0 + 40 / (0 + 10 * 60)
        Assert.True(percent < 0.1f);
    }

    [Fact]
    public void NegativeIntellectField_IsTreatedAsZeroIntellect()
    {
        using SpellTestKit kit = Kit();
        Player mage = MakePlayer(kit, 1, Class.Mage, 60, 0);
        float zeroIntellect = new IntellectSpellCritSource().SpellCritPercent(mage, SpellSchool.Fire);
        mage.SetInt32(UpdateFields.UnitFieldStat0 + 3, -25);

        float percent = new IntellectSpellCritSource().SpellCritPercent(mage, SpellSchool.Fire);

        Assert.Equal(zeroIntellect, percent, Tol);
        Assert.True(percent < 50f);
    }

    [Fact]
    public void NonPlayer_GetsFlatFivePercent()
    {
        Creature creature = FoundationTests.MakeCreature(0, 20);

        Assert.Equal(5f, new IntellectSpellCritSource().SpellCritPercent(creature, SpellSchool.Fire), Tol);
    }

    [Fact]
    public void CritChance_UsesTheSource_ForAPlayerCaster()
    {
        using SpellTestKit kit = Kit();
        Player mage = MakePlayer(kit, 1, Class.Mage, 60, 100);
        Player warrior = MakePlayer(kit, 2, Class.Warrior, 60, 100);
        SpellInfo bolt = kit.Store.Get(Bolt)!;
        var rules = new VanillaSpellCombatRules { CritSource = new IntellectSpellCritSource() };

        Assert.Equal(5.5597f, rules.CritChance(kit.System, mage, bolt), Tol);
        Assert.Equal(100f / 600f, rules.CritChance(kit.System, warrior, bolt), Tol);
    }

    [Fact]
    public void CritChance_AddsTheCasterAndVictimAuraBonuses_OnTopOfTheIntellectBase()
    {
        // SpellCombatRules.CritChance: source + MOD_SPELL_CRIT_CHANCE + MOD_SPELL_CRIT_CHANCE_SCHOOL (caster) + MOD_ATTACKER_SPELL_CRIT_CHANCE (hostile target).
        using SpellTestKit kit = Kit();
        Player mage = MakePlayer(kit, 1, Class.Mage, 60, 100);
        (Player target, _) = kit.AddPlayer(2, 2);
        SpellInfo bolt = kit.Store.Get(Bolt)!;
        var rules = new VanillaSpellCombatRules { CritSource = new IntellectSpellCritSource() };
        float baseChance = rules.CritChance(kit.System, mage, target, bolt);
        Assert.Equal(5.5597f, baseChance, Tol);

        RuleTestKit.Apply(kit, mage, SchoolCrit);
        RuleTestKit.Apply(kit, mage, GenericCrit);
        Assert.Equal(5.5597f + 7f + 3f, rules.CritChance(kit.System, mage, target, bolt), Tol);

        RuleTestKit.Apply(kit, target, AttackerCrit);
        Assert.Equal(5.5597f + 7f + 3f + 12f, rules.CritChance(kit.System, mage, target, bolt), Tol);
    }
}
