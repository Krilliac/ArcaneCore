using System.Numerics;
using ArcaneCore.World.Playerbots.Groups;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// <see cref="PlayerbotGroupGoal.Anchors"/>: whether a bot waiting for a goal can anchor a group for it. Every instance plan needs an anchoring
/// waiter; a bot whose only reasons are derived from other waiting bots (Companion, FormingGroup) must never be one, or bots seeded by one
/// another would form a group of out-of-band bots.
/// </summary>
public sealed class PlayerbotGroupGoalAnchorTests
{
    private const uint Deadmines = 36, EntranceTrigger = 78;

    private const byte Quest = (byte)PlayerbotDungeonReasons.Quest;
    private const byte Attunement = (byte)PlayerbotDungeonReasons.Attunement;
    private const byte Gear = (byte)PlayerbotDungeonReasons.GearUpgrade;
    private const byte Companion = (byte)PlayerbotDungeonReasons.Companion;
    private const byte Forming = (byte)PlayerbotDungeonReasons.FormingGroup;
    private const byte Farming = (byte)PlayerbotDungeonReasons.Farming;

    private static PlayerbotGroupGoal Instance(string? fit, PlayerbotDungeonReasons reasons, PlayerbotGroupGoalKind kind = PlayerbotGroupGoalKind.Dungeon)
        => new(kind, 990703, 990740, Deadmines, 0, Vector3.Zero, EntranceTrigger, Vector3.One, 3, 10, "instance",
            Fit: fit, Reasons: reasons, FitBand: fit is null ? default : new PlayerbotDungeonBand(15, 28));

    private static PlayerbotGroupGoal OpenWorld(string? fit, PlayerbotDungeonReasons reasons)
        => new(PlayerbotGroupGoalKind.Quest, 990701, 990720, 0, 0, Vector3.Zero, 0, Vector3.One, 3, 1, "quest", Fit: fit, Reasons: reasons);

    [Fact]
    public void DirectReasons_AreExactlyQuestAttunementAndGearUpgrade()
        => Assert.Equal(PlayerbotDungeonReasons.Quest | PlayerbotDungeonReasons.Attunement | PlayerbotDungeonReasons.GearUpgrade, PlayerbotDungeonFit.DirectReasons);

    [Fact]
    public void AnOpenWorldGoal_Anchors_WhateverItsFitAndReasonsSay()
    {
        Assert.False(OpenWorld(null, PlayerbotDungeonReasons.None).InInstance);
        Assert.True(OpenWorld(null, PlayerbotDungeonReasons.None).Anchors);
        Assert.True(OpenWorld("out-of-band 60 vs 15-28: companion", PlayerbotDungeonReasons.Companion).Anchors); // not an instance: the rule does not apply
    }

    [Theory]
    [InlineData((byte)PlayerbotGroupGoalKind.Dungeon)]
    [InlineData((byte)PlayerbotGroupGoalKind.Raid)]
    public void AnInstanceGoalWithNoFit_TheBotIsInTheBand_Anchors(byte kind) // byte: the enum is internal, a public test cannot take it
    {
        PlayerbotGroupGoal goal = Instance(null, PlayerbotDungeonReasons.None, (PlayerbotGroupGoalKind)kind);
        Assert.True(goal.InInstance);
        Assert.True(goal.Anchors);
    }

    [Fact]
    public void AnInstanceGoalWithNoFit_Anchors_EvenIfReasonsWereLeftOnlyDerived()
        => Assert.True(Instance(null, PlayerbotDungeonReasons.Companion).Anchors); // Fit null wins: the bot is in the band

    [Theory]
    [InlineData(Quest)]
    [InlineData(Attunement)]
    [InlineData(Gear)]
    [InlineData(Quest | Attunement)]
    [InlineData(Quest | Gear)]
    [InlineData(Attunement | Gear)]
    [InlineData(Quest | Attunement | Gear)]
    [InlineData(Quest | Companion)] // a direct reason next to a derived one still anchors
    [InlineData(Gear | Forming)]
    [InlineData(Attunement | Companion | Forming)]
    [InlineData(Quest | Farming)]
    public void AnOutOfBandInstanceGoal_WithADirectReason_Anchors(byte reasons)
    {
        var flags = (PlayerbotDungeonReasons)reasons;
        Assert.True(Instance("out-of-band 60 vs 15-28: " + PlayerbotDungeonFit.Describe(flags), flags).Anchors);
    }

    [Theory]
    [InlineData(Companion)]
    [InlineData(Forming)]
    [InlineData(Companion | Forming)]
    public void AnOutOfBandInstanceGoal_WithOnlyCompanionAndOrFormingGroup_DoesNotAnchor(byte reasons)
    {
        var flags = (PlayerbotDungeonReasons)reasons;
        Assert.False(Instance("out-of-band 60 vs 15-28: " + PlayerbotDungeonFit.Describe(flags), flags).Anchors);
    }

    [Theory]
    [InlineData(Farming)] // not a direct reason either (and never produced by the coordinator)
    [InlineData(Farming | Companion)]
    [InlineData(0)] // no reason at all: by default (DungeonOutOfBandNeedsReason) such a goal is not built; with the key off it is built with Fit = null (in band), so this is a defensive case
    public void AnOutOfBandInstanceGoal_WithNoDirectReason_DoesNotAnchor(byte reasons)
    {
        var flags = (PlayerbotDungeonReasons)reasons;
        Assert.False(Instance("out-of-band 60 vs 15-28: " + PlayerbotDungeonFit.Describe(flags), flags).Anchors);
    }

    [Fact]
    public void ARaidGoal_FollowsTheSameRule()
    {
        Assert.False(Instance("out-of-band 60 vs 15-28: companion", PlayerbotDungeonReasons.Companion, PlayerbotGroupGoalKind.Raid).Anchors);
        Assert.True(Instance("out-of-band 60 vs 15-28: gearupgrade", PlayerbotDungeonReasons.GearUpgrade, PlayerbotGroupGoalKind.Raid).Anchors);
    }

    [Fact]
    public void TheDerivedReasons_DescribeAsCompanionPlusForminggroup_TheTextTheWorldTestsMatch()
        => Assert.Equal("companion+forminggroup", PlayerbotDungeonFit.Describe(PlayerbotDungeonReasons.Companion | PlayerbotDungeonReasons.FormingGroup));

    [Fact]
    public void TheSeedRecheckInterval_IsSixSeconds()
        => Assert.Equal(6_000u, PlayerbotGroupCoordinator.SeedRecheckMs);
}
