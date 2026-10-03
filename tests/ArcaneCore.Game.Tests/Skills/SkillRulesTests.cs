using ArcaneCore.Game.Skills;
using ArcaneCore.Kernel.Skills;
using Xunit;

namespace ArcaneCore.Game.Tests.Skills;

/// <summary>The pure skill formulas against vmangos Objects/Player.cpp, World.h and SpellCaster.h.</summary>
public sealed class SkillRulesTests
{
    private static readonly SkillOptions Defaults = new();

    [Theory]
    [InlineData(1u, 5)]
    [InlineData(10u, 50)]
    [InlineData(60u, 300)]
    public void MaxForLevel_IsFiveTimesTheLevel(uint level, int expected)
        => Assert.Equal((ushort)expected, SkillRules.MaxForLevel(level));

    [Theory]
    [InlineData(1u, 300)]      // lvl = max(60, MaxPlayerLevel)
    [InlineData(60u, 300)]
    [InlineData(70u, 375)]     // 300 + (10 * 75) / 10
    [InlineData(80u, 450)]
    public void ConfigMaxSkillValue_IsAWorldConstant_NotAFunctionOfThePlayer(uint maxPlayerLevel, int expected)
        => Assert.Equal((ushort)expected, SkillRules.ConfigMaxSkillValue(maxPlayerLevel));

    [Fact]
    public void GainChance_BandsAndBoundaries()
    {
        Assert.Equal(0, SkillRules.GainChance(100, 100, 50, 25, Defaults));
        Assert.Equal(250, SkillRules.GainChance(99, 100, 50, 25, Defaults));
        Assert.Equal(250, SkillRules.GainChance(50, 100, 50, 25, Defaults));
        Assert.Equal(750, SkillRules.GainChance(49, 100, 50, 25, Defaults));
        Assert.Equal(750, SkillRules.GainChance(25, 100, 50, 25, Defaults));
        Assert.Equal(1000, SkillRules.GainChance(24, 100, 50, 25, Defaults));

        var custom = new SkillOptions { ChanceOrange = 90, ChanceYellow = 60, ChanceGreen = 10, ChanceGrey = 5 };
        Assert.Equal(50, SkillRules.GainChance(100, 100, 50, 25, custom));
        Assert.Equal(900, SkillRules.GainChance(0, 100, 50, 25, custom));
    }

    [Fact]
    public void CraftChance_TrivialHighIsGrey_MeanIsGreen_LowIsYellow()
    {
        // Recipe orange until 25, yellow to 62, green to 99, grey from 100 (Player.cpp:5233-5238).
        Assert.Equal(1000, SkillRules.CraftChance(24, 100, 25, Defaults));
        Assert.Equal(750, SkillRules.CraftChance(25, 100, 25, Defaults));
        Assert.Equal(750, SkillRules.CraftChance(61, 100, 25, Defaults));
        Assert.Equal(250, SkillRules.CraftChance(62, 100, 25, Defaults));
        Assert.Equal(0, SkillRules.CraftChance(100, 100, 25, Defaults));
    }

    [Fact]
    public void GatherChance_GoldenValues()
    {
        // Herbalism req 1, skill 1: red level 1 -> orange -> 1000 (no steps).
        Assert.Equal(1000, SkillRules.GatherChance(SkillIds.Herbalism, 1, 1, 1, Defaults));
        // Mining req 65, skill 80, steps 75: orange 1000 >> (80 / 75 = 1) = 500.
        Assert.Equal(500, SkillRules.GatherChance(SkillIds.Mining, 80, 65, 1, Defaults));
        // Copper vein (req 1) at skill 150: grey -> 0.
        Assert.Equal(0, SkillRules.GatherChance(SkillIds.Mining, 150, 1, 1, Defaults));
        // Mining at skill 1..74 never decays; 150 gives >> 2.
        Assert.Equal(1000, SkillRules.GatherChance(SkillIds.Mining, 74, 65, 1, Defaults));
        Assert.Equal(250, SkillRules.GatherChance(SkillIds.Mining, 150, 150, 1, Defaults));
        // Skinning uses its own step option; the multiplicator scales before the shift.
        Assert.Equal(2000, SkillRules.GatherChance(SkillIds.Skinning, 1, 1, 2, Defaults));
        Assert.Equal(1000, SkillRules.GatherChance(SkillIds.Skinning, 75, 75, 2, Defaults));
        // Lockpicking never decays.
        Assert.Equal(1000, SkillRules.GatherChance(SkillIds.Lockpicking, 200, 200, 1, Defaults));
        // Steps 0 switch the decay off (the shipped mangosd.conf.dist.in sample does this).
        var noSteps = new SkillOptions { MiningSteps = 0, SkinningSteps = 0 };
        Assert.Equal(1000, SkillRules.GatherChance(SkillIds.Mining, 200, 200, 1, noSteps));
        Assert.Equal(1000, SkillRules.GatherChance(SkillIds.Skinning, 200, 200, 1, noSteps));
        // Not a gathering skill.
        Assert.Null(SkillRules.GatherChance(SkillIds.Swords, 1, 1, 1, Defaults));
        Assert.Null(SkillRules.GatherChance(SkillIds.Fishing, 1, 1, 1, Defaults));
    }

    [Fact]
    public void GatherChance_ADecayShiftOf32OrMoreIsZero_NotAWrappedShift()
    {
        var steps1 = new SkillOptions { MiningSteps = 1 };
        Assert.Equal(0, SkillRules.GatherChance(SkillIds.Mining, 40, 40, 1, steps1));
    }

    [Theory]
    [InlineData(1u, 1000)]
    [InlineData(74u, 1000)]
    [InlineData(75u, 1000)]     // 2500 / 25 = 100 percent
    [InlineData(100u, 500)]     // 2500 / 50
    [InlineData(150u, 250)]     // 2500 / 100
    [InlineData(300u, 100)]     // 2500 / 250
    public void FishingChance_IsAHundredPercentUntil75_ThenDecays(uint skill, int expected)
        => Assert.Equal(expected, SkillRules.FishingChance(skill));

    [Fact]
    public void CombatGainChance_WeaponGoldenValues()
    {
        // Level 10 (maximum 50), no intellect.
        Assert.Equal(100f, SkillRules.CombatGainChance(false, 10, 10, 20, 0f)!.Value);          // 45 * 50 / 20 = 112 -> capped at 100
        Assert.Equal(50.0f, SkillRules.CombatGainChance(false, 10, 10, 45, 0f)!.Value, 0.01f);   // the 90% knee: 50
        Assert.Equal(4.9f, SkillRules.CombatGainChance(false, 10, 10, 48, 0f)!.Value, 0.05f);    // within 3 of the maximum: scaled by 0.5 / (4 - diff)
        Assert.Null(SkillRules.CombatGainChance(false, 10, 10, 50, 0f));                          // at the maximum
        Assert.Null(SkillRules.CombatGainChance(false, 10, 10, 60, 0f));                          // above it (after a level loss)
    }

    [Fact]
    public void CombatGainChance_IntellectAddsAtMostTenPercent_ThenCapsAtOneHundred()
    {
        float without = SkillRules.CombatGainChance(false, 60, 60, 270, 0f)!.Value;
        Assert.Equal(without + 1.0f, SkillRules.CombatGainChance(false, 60, 60, 270, 50f)!.Value, 0.001f);   // 0.02 * 50
        Assert.Equal(without + 10.0f, SkillRules.CombatGainChance(false, 60, 60, 270, 5000f)!.Value, 0.001f);
        Assert.Equal(100f, SkillRules.CombatGainChance(false, 10, 10, 20, 5000f)!.Value);
    }

    [Fact]
    public void CombatGainChance_DefenceUsesTheOldFormulaWithTheVictimLevelCap()
    {
        // Level 10, defence 20: skillDiff 30; gray level of 10 is 4; mob 12 -> lvldif 8 -> 3 * 8 * 30 / 10 = 72.
        Assert.Equal(72f, SkillRules.CombatGainChance(true, 10, 12, 20, 0f)!.Value, 0.001f);
        // A mob far above is treated as level + 5 (15): lvldif 11 -> 99.
        Assert.Equal(99f, SkillRules.CombatGainChance(true, 10, 40, 20, 0f)!.Value, 0.001f);
        // A low mob keeps a minimum level difference of 3: 3 * 3 * 30 / 10 = 27.
        Assert.Equal(27f, SkillRules.CombatGainChance(true, 10, 1, 20, 0f)!.Value, 0.001f);
        // Intellect plays no part in defence.
        Assert.Equal(72f, SkillRules.CombatGainChance(true, 10, 12, 20, 5000f)!.Value, 0.001f);
    }

    [Fact]
    public void Options_Validate_RejectsNonsense()
    {
        Assert.Same(Defaults, Defaults.Validate());
        Assert.Throws<ArgumentException>(() => (Defaults with { MaxPrimaryTradeSkill = 11 }).Validate());
        Assert.Throws<ArgumentException>(() => (Defaults with { ChanceOrange = 101 }).Validate());
        Assert.Throws<ArgumentException>(() => (Defaults with { GainCrafting = 0 }).Validate());
        Assert.Throws<ArgumentException>(() => (Defaults with { MaxPlayerLevel = 0 }).Validate());
    }

    [Fact]
    public void Options_Defaults_AreTheVmangosCodeDefaults()
    {
        // World.cpp:705-720; the sample conf's MiningSteps/SkinningSteps = 0 is the documented disagreement (docs/areas/skills.md).
        Assert.Equal((100u, 75u, 25u, 0u), (Defaults.ChanceOrange, Defaults.ChanceYellow, Defaults.ChanceGreen, Defaults.ChanceGrey));
        Assert.Equal((75u, 75u), (Defaults.MiningSteps, Defaults.SkinningSteps));
        Assert.Equal((1u, 1u, 1u, 1u), (Defaults.GainCrafting, Defaults.GainDefense, Defaults.GainGathering, Defaults.GainWeapon));
        Assert.Equal(2, Defaults.MaxPrimaryTradeSkill);
        Assert.False(Defaults.AlwaysMaxSkillForLevel);
        Assert.Equal(60u, Defaults.MaxPlayerLevel);
    }
}
