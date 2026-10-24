using ArcaneCore.Game;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots.BotSocial;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>The pure rules of helping a stranger and of answering a call for help (P2 sections 1 and 2).</summary>
public sealed class PlayerbotHelpRulesTests
{
    private static PlayerbotTroubleFacts Facts(bool alive = true, bool sameFaction = true, bool inCombat = true, int health = 100, int? mana = null, bool moving = false, bool duel = false)
        => new(alive, sameFaction, inCombat, health, mana, moving, duel);

    [Theory]
    [InlineData(100, null, false, false)]
    [InlineData(39, null, false, true)]
    [InlineData(40, null, false, false)]
    [InlineData(80, 19, true, true)]
    [InlineData(80, 19, false, false)] // low mana but standing still: not fleeing
    [InlineData(80, 20, true, false)]
    public void InTrouble_IsLowHealth_OrALowManaCasterOnTheRun(int health, int? mana, bool moving, bool expected)
        => Assert.Equal(expected, PlayerbotHelpRules.InTrouble(Facts(health: health, mana: mana, moving: moving)));

    [Fact]
    public void InTrouble_NeedsAnAliveSameFactionUnit_InAFight_NotADuel()
    {
        Assert.True(PlayerbotHelpRules.InTrouble(Facts(health: 10)));
        Assert.False(PlayerbotHelpRules.InTrouble(Facts(health: 10, alive: false)));
        Assert.False(PlayerbotHelpRules.InTrouble(Facts(health: 10, sameFaction: false)));
        Assert.False(PlayerbotHelpRules.InTrouble(Facts(health: 10, inCombat: false)));
        Assert.False(PlayerbotHelpRules.InTrouble(Facts(health: 10, duel: true)));
    }

    [Fact]
    public void Helps_IsDeterministic_PerBotVictimAndFight_AndFollowsHelpfulnessAndAffinity()
    {
        for (uint fight = 0; fight < 50; fight++)
            Assert.Equal(PlayerbotHelpRules.Helps(7, 9, fight, 0.5f, 0), PlayerbotHelpRules.Helps(7, 9, fight, 0.5f, 0));
        int never = 0, always = 0, middle = 0, liked = 0;
        for (uint fight = 0; fight < 2000; fight++)
        {
            if (PlayerbotHelpRules.Helps(7, 9, fight, 0f, 100)) never++;
            if (PlayerbotHelpRules.Helps(7, 9, fight, 0.5f, 100)) always++; // 0.5 x 2 = 1
            if (PlayerbotHelpRules.Helps(7, 9, fight, 0.5f, 0)) middle++;
            if (PlayerbotHelpRules.Helps(7, 9, fight, 0.5f, 50)) liked++;
        }

        Assert.Equal(0, never);
        Assert.Equal(2000, always);
        Assert.InRange(middle, 850, 1150);
        Assert.InRange(liked, 1350, 1650);
        Assert.Equal(0.25, PlayerbotHelpRules.HelpProbability(0.5f, -50), 6);
    }

    [Fact]
    public void IsFreeGoal_IsLowPriorityWork_NotFightingRestingOrRunning()
    {
        foreach (PlayerbotGoalKind free in new[] { PlayerbotGoalKind.Explore, PlayerbotGoalKind.Quest, PlayerbotGoalKind.Grind, PlayerbotGoalKind.Vendor, PlayerbotGoalKind.Train })
            Assert.True(PlayerbotHelpRules.IsFreeGoal(free), free.ToString());
        foreach (PlayerbotGoalKind busy in new[] { PlayerbotGoalKind.Combat, PlayerbotGoalKind.Recover, PlayerbotGoalKind.Rest, PlayerbotGoalKind.Retreat, PlayerbotGoalKind.Group,
                     PlayerbotGoalKind.Battleground, PlayerbotGoalKind.Follow, PlayerbotGoalKind.Assist, PlayerbotGoalKind.Escape, PlayerbotGoalKind.Loot })
            Assert.False(PlayerbotHelpRules.IsFreeGoal(busy), busy.ToString());
    }

    [Fact]
    public void Classes_HealersResurrect_AndFourClassesBuff()
    {
        Assert.True(PlayerbotHelpRules.IsHealer(Class.Priest));
        Assert.True(PlayerbotHelpRules.IsHealer(Class.Paladin));
        Assert.True(PlayerbotHelpRules.CanResurrect(Class.Shaman));
        Assert.False(PlayerbotHelpRules.IsHealer(Class.Warrior));
        Assert.False(PlayerbotHelpRules.CanResurrect(Class.Mage));
        Assert.Equal("Power Word: Fortitude", PlayerbotHelpRules.BuffOf(Class.Priest));
        Assert.Equal("Mark of the Wild", PlayerbotHelpRules.BuffOf(Class.Druid));
        Assert.Equal("Arcane Intellect", PlayerbotHelpRules.BuffOf(Class.Mage));
        Assert.Equal("Blessing of Might", PlayerbotHelpRules.BuffOf(Class.Paladin));
        Assert.Null(PlayerbotHelpRules.BuffOf(Class.Warrior));
    }

    [Theory]
    [InlineData("Help!", true)]
    [InlineData("I need help over here!", true)]
    [InlineData("someone HELP me", true)]
    [InlineData("A hand, please!", false)]
    [InlineData("thanks for the help", false)]
    [InlineData("Thank you for the help!", false)]
    [InlineData("ty for the help", false)]
    [InlineData("can someone help with this quest?", false)]
    [InlineData("help me find the quest item", false)]
    [InlineData("anyone know where to go? need help", false)]
    [InlineData("help with the quest, mobs everywhere pls", true)]
    [InlineData("help! adds! dying", true)]
    [InlineData("thanks! help, aggro", true)]
    [InlineData("helpful guy", false)]
    [InlineData("unhelp", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LooksLikeHelpCall_WantsTheWholeWord(string? text, bool expected) => Assert.Equal(expected, PlayerbotHelpRules.LooksLikeHelpCall(text));

    [Theory]
    [InlineData(100, 100, Class.Warrior, true)]
    [InlineData(70, null, Class.Warrior, true)]
    [InlineData(69, null, Class.Warrior, false)]
    [InlineData(100, 5, Class.Warrior, true)] // a warrior has no mana worth the name
    [InlineData(100, 30, Class.Priest, true)]
    [InlineData(100, 29, Class.Priest, false)]
    [InlineData(100, 29, Class.Mage, true)]
    [InlineData(100, 29, Class.Druid, false)]
    public void HelperFit_WantsHealth_AndManaForAHealer(int health, int? mana, Class cls, bool expected)
        => Assert.Equal(expected, PlayerbotHelpRules.HelperFit(health, mana, cls));

    [Fact]
    public void WinnableFight_IsWithinThreeLevels_AndElitesNeedAGroupOfThree()
    {
        Assert.True(PlayerbotHelpRules.Winnable(botLevel: 10, attackerLevel: 13, elite: false, groupSize: 1));
        Assert.False(PlayerbotHelpRules.Winnable(10, 14, false, 1));
        Assert.False(PlayerbotHelpRules.Winnable(10, 10, true, 1));
        Assert.False(PlayerbotHelpRules.Winnable(10, 10, true, 2));
        Assert.True(PlayerbotHelpRules.Winnable(10, 10, true, 3));
        Assert.False(PlayerbotHelpRules.Winnable(10, 14, true, 5)); // a group does not make a too-high level winnable
    }

    [Fact]
    public void SaysSorry_IsRare_AndDeterministic()
    {
        int sorry = 0;
        for (uint at = 0; at < 3000; at++)
        {
            bool says = PlayerbotCallForHelp.SaysSorry(5, 6, at);
            Assert.Equal(says, PlayerbotCallForHelp.SaysSorry(5, 6, at));
            if (says) sorry++;
        }

        Assert.InRange(sorry, 350, 650);
    }
}
