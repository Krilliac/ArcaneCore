using ArcaneCore.World.Playerbots.BotSocial;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>The pure rules of the low-health window: bystanders, the call for help and the answer.</summary>
public sealed class PlayerbotCallForHelpTests
{
    private static readonly PlayerbotSocialMemoryOptions Options = new();

    private static PlayerbotBystanderFacts Good() => new(Alive: true, SameFaction: true, InCombat: false, HealthPercent: 100f, Level: 20, Distance: 10f, InDuel: false);

    [Fact]
    public void ABystander_Qualifies_WhenEveryConditionHolds()
        => Assert.True(PlayerbotCallForHelp.Qualifies(Options, 20, Good()));

    [Theory]
    [InlineData("dead")]
    [InlineData("otherFaction")]
    [InlineData("inCombat")]
    [InlineData("lowHealth")]
    [InlineData("lowLevel")]
    [InlineData("tooFar")]
    [InlineData("duel")]
    public void AnyOneConditionFailing_DisqualifiesTheBystander(string failure)
    {
        PlayerbotBystanderFacts facts = failure switch
        {
            "dead" => Good() with { Alive = false },
            "otherFaction" => Good() with { SameFaction = false },
            "inCombat" => Good() with { InCombat = true },
            "lowHealth" => Good() with { HealthPercent = 49f },
            "lowLevel" => Good() with { Level = 14 },
            "tooFar" => Good() with { Distance = 30.5f },
            _ => Good() with { InDuel = true },
        };
        Assert.False(PlayerbotCallForHelp.Qualifies(Options, 20, facts));
    }

    [Fact]
    public void TheEdges_AreInclusive()
    {
        Assert.True(PlayerbotCallForHelp.Qualifies(Options, 20, Good() with { HealthPercent = 50f, Level = 15, Distance = 30f }));
        Assert.True(PlayerbotCallForHelp.WindowLongEnough(Options, 5000));
        Assert.False(PlayerbotCallForHelp.WindowLongEnough(Options, 4999));
    }

    [Fact]
    public void TheLossRule_IsTheStrongerAttackerFraction()
    {
        Assert.True(PlayerbotCallForHelp.LooksLost(0.3f, 0.8f));
        Assert.False(PlayerbotCallForHelp.LooksLost(0.3f, 0.2f));
        Assert.False(PlayerbotCallForHelp.LooksLost(0.3f, 0f));
    }

    [Theory]
    [InlineData(false, 0, false, true)] // a stranger
    [InlineData(true, 0, false, true)]
    [InlineData(true, 40, false, true)]
    [InlineData(true, -1, false, false)] // disliked
    [InlineData(true, 10, true, false)] // a rival
    public void OnlyUnitsNotDislikedOrRivals_AreAsked(bool known, int affinity, bool rival, bool askable)
        => Assert.Equal(askable, PlayerbotCallForHelp.Askable(known, affinity, rival));

    [Fact]
    public void AskProbability_IsBasePlusSociabilityPlusAffinity_Clamped()
    {
        Assert.Equal(0.25 + 0.6 * 0.5 + 0.002 * 50, PlayerbotCallForHelp.AskProbability(Options, 0.5f, 50), 6);
        Assert.Equal(1.0, PlayerbotCallForHelp.AskProbability(Options, 1f, 100));
        var shy = new PlayerbotSocialMemoryOptions { CallForHelpBaseChance = 0f, CallForHelpSociabilityWeight = 0f };
        Assert.Equal(0.0, PlayerbotCallForHelp.AskProbability(shy, 1f, 0));
    }

    [Fact]
    public void TheAskDecision_IsDeterministicPerBotAndFight_AndFollowsTheProbability()
    {
        for (uint fight = 1; fight < 50; fight++)
        {
            Assert.Equal(PlayerbotCallForHelp.Asks(77, fight * 1000, 0.5), PlayerbotCallForHelp.Asks(77, fight * 1000, 0.5));
            Assert.False(PlayerbotCallForHelp.Asks(77, fight * 1000, 0.0));
            Assert.True(PlayerbotCallForHelp.Asks(77, fight * 1000, 1.0));
        }

        int asked = Enumerable.Range(1, 2000).Count(fight => PlayerbotCallForHelp.Asks(77, (uint)fight * 1000, 0.5));
        Assert.InRange(asked, 850, 1150); // a hash, roughly a coin flip at 0.5
        Assert.Contains(Enumerable.Range(1, 200), fight => PlayerbotCallForHelp.Asks(77, (uint)fight, 0.5));
        Assert.Contains(Enumerable.Range(1, 200), fight => !PlayerbotCallForHelp.Asks(77, (uint)fight, 0.5));
    }

    [Theory]
    [InlineData(0.5f, 0, 0.5)]
    [InlineData(0.5f, 100, 1.0)]
    [InlineData(0.5f, -100, 0.0)]
    [InlineData(0.8f, 50, 1.0)]
    [InlineData(0.4f, -50, 0.2)]
    public void AnswerProbability_ScalesHelpfulnessByAffinity(float helpfulness, int affinity, double expected)
        => Assert.Equal(expected, PlayerbotCallForHelp.AnswerProbability(helpfulness, affinity), 6);

    [Fact]
    public void TheAnswerDecision_IsDeterministic_AndCertainAtTheExtremes()
    {
        Assert.Equal(PlayerbotCallForHelp.Answers(5, 9, 1234, 0.5f, 10), PlayerbotCallForHelp.Answers(5, 9, 1234, 0.5f, 10));
        for (uint at = 1; at < 100; at++)
        {
            Assert.True(PlayerbotCallForHelp.Answers(5, 9, at, 1f, 100));
            Assert.False(PlayerbotCallForHelp.Answers(5, 9, at, 1f, -100));
            Assert.False(PlayerbotCallForHelp.Answers(5, 9, at, 0f, 0));
        }
    }
}
