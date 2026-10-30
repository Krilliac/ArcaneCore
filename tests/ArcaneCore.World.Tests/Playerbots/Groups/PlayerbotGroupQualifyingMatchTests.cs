using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Playerbots.Groups;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// <see cref="PlayerbotGroupContent.Match(IReadOnlyList{PlayerbotGroupCandidate}, int, PlayerbotGroupOptions, bool)"/> with <c>needQualifying</c>
/// (an instance plan must hold a member that is in band or has a direct reason), <see cref="PlayerbotGroupContent.QualifyingAnchor"/> and
/// <see cref="PlayerbotGroupContent.QualifiesAtLevel"/>. All mages and no role quotas, so only levels, waiting order and Qualifying decide.
/// </summary>
public sealed class PlayerbotGroupQualifyingMatchTests
{
    private static readonly PlayerbotGroupOptions Options = new(); // LevelRange 5

    private static PlayerbotGroupCandidate Bot(string name, byte level, uint since, bool qualifying, Guid? id = null)
        => new(id ?? Guid.NewGuid(), (ulong)name.GetHashCode(StringComparison.Ordinal) & 0xFFFF, name, Class.Mage, level, 1, 0, Vector3.Zero, PlayerbotRole.MeleeDps, 0, since,
            Qualifying: qualifying);

    private static PlayerbotGroupPlan? Match(PlayerbotGroupCandidate[] candidates, int size, bool needQualifying)
        => PlayerbotGroupContent.Match(candidates, size, Options, (0, 0), needQualifying);

    private static string[] Names(PlayerbotGroupPlan plan) => [.. plan.Members.Select(m => m.Bot.Name).OrderBy(n => n, StringComparer.Ordinal)];

    // --- Match: the qualifying member ---------------------------------------------------------------------------------------

    [Fact]
    public void ThreeNonQualifyingLevel60s_AndAQualifyingLevel20_AtSizeThree_GiveNoPlan_WhenAQualifyingMemberIsNeeded()
    {
        PlayerbotGroupCandidate[] candidates =
        [
            Bot("Sixtya", 60, 1, false), Bot("Sixtyb", 60, 2, false), Bot("Sixtyc", 60, 3, false), Bot("Twenty", 20, 4, true),
        ];

        Assert.Null(Match(candidates, 3, needQualifying: true)); // the 60s are a window of their own; the 20 is out of their range
    }

    [Fact]
    public void WhenOneLevel60Qualifies_TheThreeSixtiesFormAPlan_ThatHoldsIt()
    {
        PlayerbotGroupCandidate[] candidates =
        [
            Bot("Sixtya", 60, 1, false), Bot("Sixtyb", 60, 2, true), Bot("Sixtyc", 60, 3, false), Bot("Twenty", 20, 4, true),
        ];

        PlayerbotGroupPlan plan = Match(candidates, 3, needQualifying: true)!;
        Assert.Equal(["Sixtya", "Sixtyb", "Sixtyc"], Names(plan));
        Assert.Contains(plan.Members, m => m.Bot.Name == "Sixtyb" && m.Bot.Qualifying);
    }

    [Fact]
    public void WithoutNeedQualifying_TheThreeNonQualifyingSixtiesFormAPlan_TheOldBehaviour()
    {
        PlayerbotGroupCandidate[] candidates =
        [
            Bot("Sixtya", 60, 1, false), Bot("Sixtyb", 60, 2, false), Bot("Sixtyc", 60, 3, false), Bot("Twenty", 20, 4, true),
        ];

        Assert.Equal(["Sixtya", "Sixtyb", "Sixtyc"], Names(Match(candidates, 3, needQualifying: false)!));
    }

    [Fact]
    public void TheThreeArgumentOverload_DefaultsToNoQualifyingRule_WithTheRoleQuotasOfAParty()
    {
        // The default role needs apply here (a tank and a healer), so the level 60s are a warrior, a priest and a mage.
        PlayerbotGroupCandidate Of(string name, Class @class, uint since) => Bot(name, 60, since, false) with { Class = @class };
        PlayerbotGroupCandidate[] sixties = [Of("Tankk", Class.Warrior, 1), Of("Healr", Class.Priest, 2), Of("Magee", Class.Mage, 3)];
        Assert.Equal(["Healr", "Magee", "Tankk"], Names(PlayerbotGroupContent.Match(sixties, 3, Options)!));
    }

    [Fact]
    public void AWindowWhoseCandidatesIncludeAQualifyingMember_FormsAPlan_OnTheFirstFloorThatHoldsIt()
    {
        // Floor 40 holds all four (40..45); Fill reserves one slot for the qualifying 43, so floor 40 succeeds without skipping to floor 41.
        // Floor 41 is not needed because floor 40's plan includes the qualifying member.
        PlayerbotGroupCandidate[] candidates =
        [
            Bot("Forty", 40, 1, false), Bot("Fortyone", 41, 2, false), Bot("Fortytwo", 42, 3, false), Bot("Fortythree", 43, 4, true),
        ];

        Assert.Equal(["Forty", "Fortyone", "Fortytwo"], Names(Match(candidates, 3, needQualifying: false)!)); // the first window wins without the rule
        PlayerbotGroupPlan plan = Match(candidates, 3, needQualifying: true)!;
        Assert.Equal(["Forty", "Fortyone", "Fortythree"], Names(plan));
    }

    [Fact]
    public void AWindowWhereOlderNonQualifyingBotsWouldFillEverySlot_IncludesTheQualifyingBot()
    {
        // All candidates at level 40: only one floor exists. Without qualifying-aware Fill, the three older non-qualifying bots
        // fill every slot and Match would return null. With qualifying-aware Fill, the qualifying bot is included.
        PlayerbotGroupCandidate[] candidates =
        [
            Bot("Old1", 40, 1, false), Bot("Old2", 40, 2, false), Bot("Old3", 40, 3, false), Bot("Qual", 40, 4, true),
        ];

        Assert.Equal(["Old1", "Old2", "Old3"], Names(Match(candidates, 3, needQualifying: false)!));
        PlayerbotGroupPlan plan = Match(candidates, 3, needQualifying: true)!;
        Assert.NotNull(plan);
        Assert.Equal(["Old1", "Old2", "Qual"], Names(plan));
        Assert.Contains(plan.Members, m => m.Bot.Name == "Qual" && m.Bot.Qualifying);
    }

    [Fact]
    public void AWindowWhereNoQualifyingBotExists_ReturnsNull_WhenQualifyingIsNeeded()
    {
        PlayerbotGroupCandidate[] candidates =
        [
            Bot("Old1", 40, 1, false), Bot("Old2", 40, 2, false), Bot("Old3", 40, 3, false), Bot("Old4", 40, 4, false),
        ];

        Assert.NotNull(Match(candidates, 3, needQualifying: false));
        Assert.Null(Match(candidates, 3, needQualifying: true));
    }

    [Fact]
    public void WhenNoFloorHoldsTheQualifyingMember_TheResultIsNull()
    {
        PlayerbotGroupCandidate[] candidates =
        [
            Bot("Forty", 40, 1, false), Bot("Fortyone", 41, 2, false), Bot("Fortytwo", 42, 3, false), Bot("Sixty", 60, 4, true),
        ];

        Assert.NotNull(Match(candidates, 3, needQualifying: false));
        Assert.Null(Match(candidates, 3, needQualifying: true));
    }

    [Fact]
    public void NoCandidateQualifying_GivesNull_WhateverTheWindows()
        => Assert.Null(Match([Bot("Aa", 30, 1, false), Bot("Bb", 31, 2, false), Bot("Cc", 32, 3, false)], 3, needQualifying: true));

    [Fact]
    public void TheRoleNeedsOverload_AppliesTheQualifyingRuleToo()
    {
        PlayerbotGroupCandidate[] sixties = [Bot("Aa", 60, 1, false), Bot("Bb", 60, 2, false)];
        Assert.Null(PlayerbotGroupContent.Match(sixties, 2, Options, needQualifying: true));
        Assert.NotNull(PlayerbotGroupContent.Match(sixties, 2, Options, needQualifying: false));
    }

    // --- QualifyingAnchor -------------------------------------------------------------------------------------------------

    [Fact]
    public void TheQualifyingAnchor_IsTheOldestQualifyingWaiter_NotAnOlderSocialOnlyOne()
    {
        PlayerbotGroupCandidate[] candidates = [Bot("Social", 31, 1, false), Bot("Late", 27, 9, true), Bot("Early", 27, 5, true), Bot("Later", 27, 12, true)];
        Assert.Equal("Early", PlayerbotGroupContent.QualifyingAnchor(candidates)!.Name);
    }

    [Fact]
    public void TheQualifyingAnchor_IsNull_WhenNoneQualifies_OrTheListIsEmpty()
    {
        Assert.Null(PlayerbotGroupContent.QualifyingAnchor([Bot("Aa", 31, 1, false), Bot("Bb", 32, 2, false)]));
        Assert.Null(PlayerbotGroupContent.QualifyingAnchor([]));
        Assert.Throws<ArgumentNullException>(() => PlayerbotGroupContent.QualifyingAnchor(null!));
    }

    [Fact]
    public void TheQualifyingAnchor_ReadsTheWaitingClockAcrossAWrap_AndBreaksTiesByBotId()
    {
        // uint.MaxValue - 5 was taken before 2 (the 32-bit game clock wrapped in between).
        Assert.Equal("Beforewrap", PlayerbotGroupContent.QualifyingAnchor([Bot("Afterwrap", 27, 2, true), Bot("Beforewrap", 27, uint.MaxValue - 5, true)])!.Name);
        var low = new Guid(1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var high = new Guid(2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        Assert.Equal("Low", PlayerbotGroupContent.QualifyingAnchor([Bot("High", 27, 5, true, high), Bot("Low", 27, 5, true, low)])!.Name);
    }

    // --- The uint.MaxValue "sorts last" sentinel of a real player candidate (Invites, Volunteers) ----------------------------

    private static PlayerbotGroupCandidate Player(string name, byte level, bool qualifying)
        => Bot(name, level, uint.MaxValue, qualifying, Guid.Empty);

    [Fact]
    public void ThePlayerSentinel_NeverTakesASlotFromOlderBots_WhenTheBotsFillThePlan()
    {
        // Uptime ticks are far below 2^31, so a plain wrap-safe difference would read uint.MaxValue as the longest waiter.
        PlayerbotGroupCandidate[] candidates = [Player("Human", 30, false), Bot("Botone", 30, 1000, false), Bot("Bottwo", 30, 2000, false)];

        PlayerbotGroupPlan plan = Match(candidates, 2, needQualifying: false)!;
        Assert.Equal(["Botone", "Bottwo"], Names(plan));
        Assert.DoesNotContain(plan.Members, m => m.Bot.BotId == Guid.Empty);
    }

    [Fact]
    public void ThePlayerSentinel_IsIncludedWhenBotsAreShort_AndTheLeaderIsTheBot()
    {
        PlayerbotGroupCandidate[] candidates = [Player("Human", 30, false), Bot("Botone", 30, 5000, false)];

        PlayerbotGroupPlan plan = Match(candidates, 2, needQualifying: false)!;
        Assert.Equal(["Botone", "Human"], Names(plan));
        Assert.Equal("Botone", plan.Leader.Name);
        Assert.NotEqual(Guid.Empty, plan.Leader.BotId);
    }

    [Fact]
    public void ThePlayerSentinel_NeverBecomesTheQualifyingAnchor_OverAQualifyingBot()
    {
        PlayerbotGroupCandidate[] candidates = [Player("Human", 30, true), Bot("Botone", 30, 5000, true)];
        Assert.Equal("Botone", PlayerbotGroupContent.QualifyingAnchor(candidates)!.Name);
        Assert.Equal("Botone", PlayerbotGroupContent.QualifyingAnchor([.. candidates.Reverse()])!.Name);
        // Alone, the sentinel is still the anchor (it is the only qualifier).
        Assert.Equal("Human", PlayerbotGroupContent.QualifyingAnchor([Player("Human", 30, true), Bot("Other", 30, 1, false)])!.Name);
    }

    [Fact]
    public void ThePlayerSentinel_QualifyingPlayerDoesNotDisplaceOlderBots_WhenQualifyingIsNeeded()
    {
        PlayerbotGroupCandidate[] candidates = [Player("Human", 30, true), Bot("Botone", 30, 1000, false), Bot("Bottwo", 30, 2000, true)];

        PlayerbotGroupPlan plan = Match(candidates, 2, needQualifying: true)!;
        Assert.Equal(["Botone", "Bottwo"], Names(plan));
        Assert.Contains(plan.Members, m => m.Bot.Name == "Bottwo" && m.Bot.Qualifying);
        Assert.NotEqual(Guid.Empty, plan.Leader.BotId);
    }

    // --- QualifiesAtLevel -------------------------------------------------------------------------------------------------

    private static PlayerbotGroupGoal Goal(uint entranceTrigger, PlayerbotDungeonBand band)
        => new(PlayerbotGroupGoalKind.Dungeon, 1, 2, 36, 0, Vector3.Zero, entranceTrigger, Vector3.One, 3, 10, "instance", FitBand: band);

    [Theory]
    [InlineData(14, false)]
    [InlineData(15, true)]
    [InlineData(28, true)]
    [InlineData(29, false)]
    [InlineData(60, false)]
    public void QualifiesAtLevel_IsTheBandOfAnInstanceGoal_Inclusive(int level, bool expected)
        => Assert.Equal(expected, PlayerbotGroupContent.QualifiesAtLevel(Goal(78, new PlayerbotDungeonBand(15, 28)), level));

    [Theory]
    [InlineData(1)]
    [InlineData(60)]
    public void QualifiesAtLevel_IsTrue_ForAnInstanceGoalWithNoBand_AndForAnOpenWorldGoal(int level)
    {
        Assert.True(PlayerbotGroupContent.QualifiesAtLevel(Goal(78, default), level));
        Assert.True(PlayerbotGroupContent.QualifiesAtLevel(Goal(0, new PlayerbotDungeonBand(15, 28)), level)); // not in an instance: the band is ignored
    }
}
