using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Stats;
using ArcaneCore.Game.Tests.SpellRules;
using ArcaneCore.Game.Tests.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.Stats;

/// <summary>The intellect-driven base spell crit (vmangos Unit::GetSpellCritFromIntellect, Unit.cpp:2597-2641) feeds the spell crit roll.</summary>
public sealed class IntellectSpellCritSourceTests
{
    private const uint Bolt = 900_201;
    private const float Tol = 0.001f;

    private static SpellTestKit Kit() => new(RuleTestKit.Magic(Bolt, SpellSchool.Fire));

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
    public void NonPlayer_GetsFlatFivePercent()
    {
        Creature creature = FoundationTests.MakeCreature(0, 20);

        Assert.Equal(5f, new IntellectSpellCritSource().SpellCritPercent(creature, SpellSchool.Fire), Tol);
    }

    [Fact]
    public void CritChance_UsesTheSource_ForAPlayerCaster_AndAddsAuraBonuses()
    {
        using SpellTestKit kit = Kit();
        Player mage = MakePlayer(kit, 1, Class.Mage, 60, 100);
        Player warrior = MakePlayer(kit, 2, Class.Warrior, 60, 100);
        SpellInfo bolt = kit.Store.Get(Bolt)!;
        var rules = new VanillaSpellCombatRules { CritSource = new IntellectSpellCritSource() };

        Assert.Equal(5.5597f, rules.CritChance(kit.System, mage, bolt), Tol);
        Assert.Equal(100f / 600f, rules.CritChance(kit.System, warrior, bolt), Tol);
    }
}
