using ArcaneCore.Game.Skills;
using ArcaneCore.Kernel.Skills;
using Xunit;
using static ArcaneCore.Game.Tests.Skills.SkillTestKit;

namespace ArcaneCore.Game.Tests.Skills;

public sealed class StartingSkillsTests
{
    [Theory]
    [InlineData(1, 75)]
    [InlineData(2, 150)]
    [InlineData(16, 1200)]
    public void RankedStarterUsesOneBasedSourceStep(int step, int maximum)
    {
        (_, PlayerSkills skills, _, _) = CreateSkills(level: 10);

        Assert.Equal(1, skills.ApplyStartingSkills([new StartingSkill(SkillIds.Mining, (ushort)step, "rank") ]));

        Assert.Equal((ushort)1, skills.GetValuePure(SkillIds.Mining));
        Assert.Equal((ushort)maximum, skills.GetMaxPure(SkillIds.Mining));
    }

    [Fact]
    public void MaximizedStarterUsesTheCatalogMaximum()
    {
        (_, PlayerSkills skills, _, _) = CreateSkills(level: 10);

        Assert.Equal(1, skills.ApplyStartingSkills([new StartingSkill(SkillIds.Unarmed, 1, "maximized") ]));

        Assert.Equal((ushort)50, skills.GetValuePure(SkillIds.Unarmed));
        Assert.Equal((ushort)50, skills.GetMaxPure(SkillIds.Unarmed));
    }

    [Fact]
    public void AppliesMissingSkills_UsesCatalogRange_AndDoesNotResetPersistedOrForgotten()
    {
        (var player, PlayerSkills skills, _, _) = CreateSkills(level: 10);
        skills.Load([new CharacterSkillRow((ushort)SkillIds.Swords, 42, 50)],
            [new ForgottenSkillRow((ushort)SkillIds.Axes, 33)]);

        int added = skills.ApplyStartingSkills([
            new StartingSkill(SkillIds.Swords, 0, "existing"),
            new StartingSkill(SkillIds.Axes, 0, "forgotten weapon"),
            new StartingSkill(SkillIds.LanguageCommon, 0, "language"),
            new StartingSkill(SkillIds.PlateMail, 0, "mono"),
        ]);

        Assert.Equal(2, added);
        Assert.Equal((ushort)42, skills.GetValuePure(SkillIds.Swords));
        Assert.False(skills.Has(SkillIds.Axes));
        Assert.Equal(((ushort)300, (ushort)300), (skills.GetValuePure(SkillIds.LanguageCommon), skills.GetMaxPure(SkillIds.LanguageCommon)));
        Assert.Equal(((ushort)1, (ushort)1), (skills.GetValuePure(SkillIds.PlateMail), skills.GetMaxPure(SkillIds.PlateMail)));
        Assert.Equal(0u, player.GetUInt32(SlotIndex(0)) >> 16); // persisted rows keep their recorded step
    }
}
