using ArcaneCore.Game.Spells.Druid;
using Xunit;

namespace ArcaneCore.Game.Tests.Druid;

/// <summary>
/// Pure druid form tables. Spell ids and Stances masks are cited from classic-db spell_template (read outside
/// the repo); display ids and boost ids from D:\refs\vmangos\src\game\Spells\SpellAuras.cpp.
/// </summary>
public class FormTablesTests
{
    [Fact]
    public void StanceMask_IsOneShiftedByFormMinusOne()
    {
        Assert.Equal(1u, DruidForms.StanceMask(DruidForms.Cat));
        Assert.Equal(0x08u, DruidForms.StanceMask(DruidForms.Aquatic));
        Assert.Equal(0x10u, DruidForms.StanceMask(DruidForms.Bear));
        Assert.Equal(0x80u, DruidForms.StanceMask(DruidForms.DireBear));
        Assert.Equal(0u, DruidForms.StanceMask(DruidForms.None));
    }

    [Fact]
    public void StanceMask_Moonkin_EqualsTheStancesNotBitClassicDbUsesOnCatDireBearTravelAquatic()
    {
        // classic-db: 768 Cat Form, 9634 Dire Bear Form have StancesNot = 1073741824; Leader of the Pack
        // 24932 has Stances 145 = 0x91 = cat | bear | dire bear.
        Assert.Equal(1073741824u, DruidForms.StanceMask(DruidForms.Moonkin));
        Assert.Equal(145u, DruidForms.StanceMask(DruidForms.Cat) | DruidForms.StanceMask(DruidForms.Bear) | DruidForms.StanceMask(DruidForms.DireBear));
    }

    [Theory]
    [InlineData(DruidForms.Cat, true, 892u, 0.8f)]
    [InlineData(DruidForms.Cat, false, 8571u, 0.8f)]
    [InlineData(DruidForms.Bear, true, 2281u, 1.0f)]
    [InlineData(DruidForms.Bear, false, 2289u, 1.0f)]
    [InlineData(DruidForms.DireBear, true, 2281u, 1.0f)]
    [InlineData(DruidForms.DireBear, false, 2289u, 1.0f)]
    [InlineData(DruidForms.Travel, true, 632u, 0.8f)]
    [InlineData(DruidForms.Travel, false, 632u, 0.8f)]
    [InlineData(DruidForms.Aquatic, true, 2428u, 0.8f)]
    [InlineData(DruidForms.Moonkin, true, 15374u, 1.0f)]
    [InlineData(DruidForms.Moonkin, false, 15375u, 1.0f)]
    [InlineData(DruidForms.Tree, true, 864u, 1.0f)]
    public void FormDisplayTable_MatchesVmangosGetShapeshiftDisplayInfo(byte form, bool alliance, uint display, float scale)
    {
        FormDisplay? result = FormDisplayTable.Get(form, alliance);

        Assert.NotNull(result);
        Assert.Equal(display, result.Value.DisplayId);
        Assert.Equal(scale, result.Value.Scale);
    }

    [Fact]
    public void FormDisplayTable_FormNone_HasNoDisplay() => Assert.Null(FormDisplayTable.Get(DruidForms.None, true));

    [Theory]
    [InlineData(DruidForms.Cat, 3025u, 0u, 24900u)]
    [InlineData(DruidForms.Bear, 1178u, 21178u, 24899u)]
    [InlineData(DruidForms.DireBear, 9635u, 21178u, 24899u)]
    [InlineData(DruidForms.Travel, 5419u, 0u, 0u)]
    [InlineData(DruidForms.Aquatic, 5421u, 0u, 0u)]
    [InlineData(DruidForms.Moonkin, 24905u, 0u, 0u)]
    [InlineData(DruidForms.Tree, 5420u, 0u, 0u)]
    public void FormBoostTable_MatchesHandleShapeshiftBoosts(byte form, uint spell1, uint spell2, uint hotw)
    {
        FormBoosts boosts = FormBoostTable.Get(form);

        Assert.Equal(spell1, boosts.Spell1);
        Assert.Equal(spell2, boosts.Spell2);
        Assert.Equal(hotw, boosts.HeartOfTheWildSpell);
    }

    [Fact]
    public void FormBoostTable_FormNone_LinksNothing() => Assert.Equal(default, FormBoostTable.Get(DruidForms.None));

    [Theory]
    [InlineData(DruidForms.Cat, true)]
    [InlineData(DruidForms.Bear, true)]
    [InlineData(DruidForms.DireBear, true)]
    [InlineData(DruidForms.Moonkin, false)]
    [InlineData(DruidForms.Travel, false)]
    public void LeaderOfThePack_NeedsTheTalentAndTheFormInTheEffectSpellStances(byte form, bool expected)
    {
        Assert.Equal(expected, FormBoostTable.LeaderOfThePackApplies(true, 0x91, form));
        Assert.False(FormBoostTable.LeaderOfThePackApplies(false, 0x91, form));
    }

    [Theory]
    [InlineData(DruidForms.None, false)]
    [InlineData(DruidForms.BattleStance, false)]
    [InlineData(DruidForms.DefensiveStance, false)]
    [InlineData(DruidForms.BerserkerStance, false)]
    [InlineData(DruidForms.Shadow, false)]
    [InlineData(DruidForms.Stealth, false)]
    [InlineData(DruidForms.Cat, true)]
    [InlineData(DruidForms.Tree, true)]
    [InlineData(DruidForms.Travel, true)]
    [InlineData(DruidForms.Aquatic, true)]
    [InlineData(DruidForms.Bear, true)]
    [InlineData(DruidForms.DireBear, true)]
    [InlineData(DruidForms.Moonkin, true)]
    [InlineData(0x10, true)]
    public void IsDisallowedMountForm_FormIdHalf(byte form, bool expected) =>
        Assert.Equal(expected, DruidForms.IsDisallowedMountForm(form));

    [Fact]
    public void TankingAndAttackSpeedOverriddenForms()
    {
        Assert.True(DruidForms.IsTankingForm(DruidForms.Bear));
        Assert.True(DruidForms.IsTankingForm(DruidForms.DireBear));
        Assert.True(DruidForms.IsTankingForm(DruidForms.DefensiveStance));
        Assert.False(DruidForms.IsTankingForm(DruidForms.Cat));
        Assert.True(DruidForms.IsAttackSpeedOverridden(DruidForms.Cat));
        Assert.False(DruidForms.IsAttackSpeedOverridden(DruidForms.Moonkin));
    }

    [Fact]
    public void Furor_RanksAreTheFiveTalentSpellsAndNotTheProcSpells()
    {
        // classic-db: 17056,17058,17059,17060,17061 are Furor ranks 1-5 (bp 19..99); 17057/17099 are the procs.
        Assert.Equal([20, 40, 60, 80, 100], [FurorRules.RankAmounts[17056], FurorRules.RankAmounts[17058], FurorRules.RankAmounts[17059], FurorRules.RankAmounts[17060], FurorRules.RankAmounts[17061]]);
        Assert.False(FurorRules.RankAmounts.ContainsKey(17057));
        Assert.Equal(17099u, FurorRules.ProcSpell(DruidForms.Cat));
        Assert.Equal(17057u, FurorRules.ProcSpell(DruidForms.Bear));
        Assert.Equal(17057u, FurorRules.ProcSpell(DruidForms.DireBear));
        Assert.Equal(0u, FurorRules.ProcSpell(DruidForms.Travel));
    }

    [Fact]
    public void Furor_RollIsIrand1To100AtMostChance()
    {
        Assert.True(FurorRules.Procs(100, 100));
        Assert.True(FurorRules.Procs(40, 40));
        Assert.False(FurorRules.Procs(40, 41));
        Assert.False(FurorRules.Procs(0, 1));
    }
}
