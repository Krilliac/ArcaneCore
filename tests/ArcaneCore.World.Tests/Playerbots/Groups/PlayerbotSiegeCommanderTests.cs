using ArcaneCore.World.Playerbots.Groups;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// The siege commander's rules (<see cref="PlayerbotSiegeCommander.Decide"/>) over plain facts, in the order they are stated:
/// Retreat, Regroup or Hold, Pull with hysteresis, AssignTank, Advance, Assault. And the leader's raid-warning gap.
/// </summary>
public sealed class PlayerbotSiegeCommanderTests
{
    private static readonly PlayerbotSiegeFacts Calm = new(
        InCombat: false, MsSinceCombat: 60_000, Total: 6, Alive: 6, HasTank: true, TankAlive: true, AverageHealthPct: 100f, MaxLagYards: 5f,
        AnyResting: false, AnyHurt: false, AtWaypoint: false, PeersAtWaypoint: true, Hostiles: 0, NearestHostileYards: float.PositiveInfinity,
        CrossValid: false, RetreatLatched: false, PullLatched: false);

    private static readonly PlayerbotSiegeFacts Fight = Calm with { InCombat = true, MsSinceCombat = 0, Hostiles = 1, NearestHostileYards = 8f };

    private static PlayerbotSiegeOrder Order(PlayerbotSiegeFacts facts) => PlayerbotSiegeCommander.Decide(facts).Order;

    [Fact]
    public void AnIdleRaid_Advances_AndARaidInCombat_Assaults()
    {
        Assert.Equal(PlayerbotSiegeOrder.Advance, Order(Calm));
        Assert.Equal(PlayerbotSiegeOrder.Assault, Order(Fight));
    }

    [Fact]
    public void ACombatLostByTheTank_ByTheAverageHealth_OrByAThirdOfTheRaid_IsARetreat()
    {
        Assert.Equal(PlayerbotSiegeOrder.Retreat, Order(Fight with { TankAlive = false, Alive = 5 }));
        Assert.Equal(PlayerbotSiegeOrder.Retreat, Order(Fight with { AverageHealthPct = 34f }));
        Assert.Equal(PlayerbotSiegeOrder.Retreat, Order(Fight with { Alive = 4, DeadSinceEngagement = 2 }));
        Assert.Equal(PlayerbotSiegeOrder.Assault, Order(Fight with { AverageHealthPct = 36f }));
        Assert.Equal(PlayerbotSiegeOrder.Assault, Order(Fight with { Alive = 5, DeadSinceEngagement = 1 }));
        Assert.Equal(PlayerbotSiegeOrder.Assault, Order(Fight with { HasTank = false, TankAlive = false }));
    }

    [Fact]
    public void DeathsOfEarlierFights_DoNotMakeAThirdLost_OnlyThoseOfThisEngagementDo()
    {
        // Four of six are dead, all of them before this fight began: the raid fights on. Two dying in this fight is a third.
        Assert.Equal(PlayerbotSiegeOrder.Assault, Order(Fight with { Alive = 2, DeadSinceEngagement = 0 }));
        Assert.Equal(PlayerbotSiegeOrder.Assault, Order(Fight with { Alive = 2, DeadSinceEngagement = 1 }));
        Assert.Equal(PlayerbotSiegeOrder.Retreat, Order(Fight with { Alive = 2, DeadSinceEngagement = 2 }));
    }

    [Fact]
    public void AfterTwoRetreats_TheRaidCommits_AndSaysSoOnce_ButARetreatInProgressRunsOut()
    {
        Assert.Equal(2, PlayerbotSiegeCommander.MaxRetreats);
        PlayerbotSiegeFacts lost = Fight with { TankAlive = false };

        PlayerbotSiegeDecision second = PlayerbotSiegeCommander.Decide(lost with { RetreatsUsed = 1 });
        Assert.Equal((PlayerbotSiegeOrder.Retreat, false), (second.Order, second.Committed));

        PlayerbotSiegeDecision committed = PlayerbotSiegeCommander.Decide(lost with { RetreatsUsed = 2 });
        Assert.Equal((PlayerbotSiegeOrder.Assault, true, false), (committed.Order, committed.Committed, committed.RetreatLatched));
        Assert.Equal(PlayerbotSiegeOrder.Assault, Order(Fight with { AverageHealthPct = 5f, DeadSinceEngagement = 4, Alive = 2, RetreatsUsed = 3 }));

        // The second retreat itself (latched, RetreatsUsed already 2) is not cut short.
        Assert.Equal(PlayerbotSiegeOrder.Retreat, Order(lost with { RetreatsUsed = 2, RetreatLatched = true }));
        // A fight that is going well says nothing of retreats.
        Assert.False(PlayerbotSiegeCommander.Decide(Fight with { RetreatsUsed = 2 }).Committed);
    }

    [Fact]
    public void ARaidFightingTheObjective_NeverRetreats()
    {
        PlayerbotSiegeFacts atTheObjective = Fight with { TankAlive = false, AverageHealthPct = 10f, Alive = 2, DeadSinceEngagement = 4, ObjectiveEngaged = true };
        PlayerbotSiegeDecision commit = PlayerbotSiegeCommander.Decide(atTheObjective);
        Assert.Equal((PlayerbotSiegeOrder.Assault, false), (commit.Order, commit.Committed));

        // Not even a retreat latched before the raid got to the objective goes on.
        Assert.Equal(PlayerbotSiegeOrder.Assault, Order(atTheObjective with { RetreatLatched = true }));
    }

    [Fact]
    public void ARetreat_NeedsACombat_ToBegin_AndIsALatch_UntilTheRaidIsQuietForFiveSeconds()
    {
        Assert.Equal(PlayerbotSiegeOrder.Advance, Order(Calm with { TankAlive = false, AverageHealthPct = 10f, Alive = 3 }));

        PlayerbotSiegeDecision begun = PlayerbotSiegeCommander.Decide(Fight with { TankAlive = false });
        Assert.Equal((PlayerbotSiegeOrder.Retreat, true), (begun.Order, begun.RetreatLatched));

        PlayerbotSiegeDecision stillRunning = PlayerbotSiegeCommander.Decide(Fight with { RetreatLatched = true });
        Assert.Equal((PlayerbotSiegeOrder.Retreat, true), (stillRunning.Order, stillRunning.RetreatLatched));

        PlayerbotSiegeDecision quiet = PlayerbotSiegeCommander.Decide(Calm with { RetreatLatched = true, MsSinceCombat = 4_999 });
        Assert.Equal((PlayerbotSiegeOrder.Retreat, true), (quiet.Order, quiet.RetreatLatched));

        PlayerbotSiegeDecision over = PlayerbotSiegeCommander.Decide(Calm with { RetreatLatched = true, MsSinceCombat = 5_000 });
        Assert.Equal((PlayerbotSiegeOrder.Advance, false), (over.Order, over.RetreatLatched));
    }

    [Fact]
    public void ARetreatBecomesARally_AtItsPoint_AndEndsAfterRallyHoldMs_StillInCombat()
    {
        Assert.Equal(30_000u, PlayerbotSiegeCommander.RallyHoldMs);
        PlayerbotSiegeFacts latched = Fight with { RetreatLatched = true, AtRally = true };

        PlayerbotSiegeDecision rally = PlayerbotSiegeCommander.Decide(latched with { MsAtRally = 29_999 });
        Assert.Equal((PlayerbotSiegeOrder.Rally, true), (rally.Order, rally.RetreatLatched));

        PlayerbotSiegeDecision chasedOut = PlayerbotSiegeCommander.Decide(latched with { MsAtRally = 30_000 });
        Assert.Equal((PlayerbotSiegeOrder.Assault, false), (chasedOut.Order, chasedOut.RetreatLatched));

        // Not at the point, the bound does not apply: the raid is still walking.
        PlayerbotSiegeDecision walking = PlayerbotSiegeCommander.Decide(Fight with { RetreatLatched = true, AtRally = false, MsAtRally = 30_000 });
        Assert.Equal((PlayerbotSiegeOrder.Retreat, true), (walking.Order, walking.RetreatLatched));
    }

    [Fact]
    public void ARetreatThatBeginsAtItsPoint_IsARallyAtOnce()
    {
        PlayerbotSiegeDecision begun = PlayerbotSiegeCommander.Decide(Fight with { TankAlive = false, AtRally = true });
        Assert.Equal((PlayerbotSiegeOrder.Rally, true), (begun.Order, begun.RetreatLatched));
    }

    [Fact]
    public void OneRetreatPerEngagement()
    {
        PlayerbotSiegeFacts lost = Fight with { TankAlive = false, RetreatedThisEngagement = true };

        PlayerbotSiegeDecision refused = PlayerbotSiegeCommander.Decide(lost);
        Assert.Equal((PlayerbotSiegeOrder.Assault, false, false), (refused.Order, refused.RetreatLatched, refused.Committed));

        // A retreat already running is not cut short by it.
        Assert.Equal(PlayerbotSiegeOrder.Retreat, Order(lost with { RetreatLatched = true }));
    }

    [Fact]
    public void ARallyEndsWhenQuiet_LikeARetreat()
    {
        PlayerbotSiegeFacts quiet = Calm with { RetreatLatched = true, AtRally = true };

        PlayerbotSiegeDecision over = PlayerbotSiegeCommander.Decide(quiet with { MsSinceCombat = 5_000 });
        Assert.Equal((PlayerbotSiegeOrder.Advance, false), (over.Order, over.RetreatLatched));

        PlayerbotSiegeDecision still = PlayerbotSiegeCommander.Decide(quiet with { MsSinceCombat = 4_999 });
        Assert.Equal((PlayerbotSiegeOrder.Rally, true), (still.Order, still.RetreatLatched));
    }

    [Fact]
    public void ALaggingResting_OrHurtMember_RegroupsTheRaid_WhenItIsNotFighting()
    {
        Assert.Equal(PlayerbotSiegeOrder.Regroup, Order(Calm with { MaxLagYards = 30.5f }));
        Assert.Equal(PlayerbotSiegeOrder.Advance, Order(Calm with { MaxLagYards = 30f }));
        Assert.Equal(PlayerbotSiegeOrder.Regroup, Order(Calm with { AnyResting = true }));
        Assert.Equal(PlayerbotSiegeOrder.Regroup, Order(Calm with { AnyHurt = true }));
        Assert.Equal(PlayerbotSiegeOrder.Assault, Order(Fight with { MaxLagYards = 80f, AnyResting = true, AnyHurt = true }));
    }

    [Fact]
    public void AtItsWaypointWhileThePeersAreNot_TheRaidHolds_UnlessThereIsSomethingToPull()
    {
        PlayerbotSiegeFacts waiting = Calm with { AtWaypoint = true, PeersAtWaypoint = false };
        Assert.Equal(PlayerbotSiegeOrder.Hold, Order(waiting));
        Assert.Equal(PlayerbotSiegeOrder.Advance, Order(waiting with { PeersAtWaypoint = true }));
        Assert.Equal(PlayerbotSiegeOrder.Pull, Order(waiting with { Hostiles = 1, NearestHostileYards = 30f }));
        Assert.Equal(PlayerbotSiegeOrder.Hold, Order(waiting with { Hostiles = 1, NearestHostileYards = 45f }));
    }

    [Fact]
    public void RegroupBeatsHoldAndPull_AndRetreatBeatsEverything()
    {
        PlayerbotSiegeFacts lagging = Calm with { MaxLagYards = 50f, AtWaypoint = true, PeersAtWaypoint = false, Hostiles = 2, NearestHostileYards = 20f };
        Assert.Equal(PlayerbotSiegeOrder.Regroup, Order(lagging));
        Assert.Equal(PlayerbotSiegeOrder.Retreat, Order(lagging with { InCombat = true, MsSinceCombat = 0, TankAlive = false }));
    }

    [Theory]
    [InlineData(45f, false, (int)PlayerbotSiegeOrder.Advance, false)]
    [InlineData(40f, false, (int)PlayerbotSiegeOrder.Pull, true)]
    [InlineData(25f, false, (int)PlayerbotSiegeOrder.Pull, true)]
    [InlineData(50f, true, (int)PlayerbotSiegeOrder.Pull, true)]
    [InlineData(55f, true, (int)PlayerbotSiegeOrder.Pull, true)]
    [InlineData(56f, true, (int)PlayerbotSiegeOrder.Advance, false)]
    [InlineData(float.PositiveInfinity, true, (int)PlayerbotSiegeOrder.Advance, false)]
    public void APullStartsWithin40Yards_AndEndsBeyond55(float nearest, bool latched, int order, bool latchAfter)
    {
        PlayerbotSiegeDecision decision = PlayerbotSiegeCommander.Decide(Calm with
        {
            Hostiles = float.IsFinite(nearest) ? 1 : 0, NearestHostileYards = nearest, PullLatched = latched,
        });
        Assert.Equal(((PlayerbotSiegeOrder)order, latchAfter), (decision.Order, decision.PullLatched));
    }

    [Fact]
    public void TwoHostilesWithoutACross_AreAnAssignTank_AndAPullComesFirst()
    {
        Assert.Equal(PlayerbotSiegeOrder.AssignTank, Order(Fight with { Hostiles = 2 }));
        Assert.Equal(PlayerbotSiegeOrder.Assault, Order(Fight with { Hostiles = 2, CrossValid = true }));
        Assert.Equal(PlayerbotSiegeOrder.Assault, Order(Fight with { Hostiles = 1 }));
        Assert.Equal(PlayerbotSiegeOrder.AssignTank, Order(Calm with { Hostiles = 2, NearestHostileYards = 50f }));
        Assert.Equal(PlayerbotSiegeOrder.Pull, Order(Calm with { Hostiles = 2, NearestHostileYards = 30f }));
        Assert.Equal(PlayerbotSiegeOrder.Advance, Order(Calm with { Hostiles = 2, NearestHostileYards = 50f, CrossValid = true }));
    }

    [Fact]
    public void TheRaidWarningsAreOnePerThreeSeconds_AndOnlyOnAChange()
    {
        var announcer = new PlayerbotSiegeAnnouncer();
        Assert.True(announcer.Offer("Advancing to waypoint 1", 10_000));
        Assert.False(announcer.Offer("Advancing to waypoint 1", 20_000));
        Assert.False(announcer.Offer("Pulling", 11_000));
        Assert.False(announcer.Offer("Pulling", 12_999));
        Assert.True(announcer.Offer("Pulling", 13_000));
        Assert.False(announcer.Offer("Pulling", 30_000));
        Assert.True(announcer.Offer("Advancing to waypoint 1", 33_000));
        Assert.Equal(["Advancing to waypoint 1", "Pulling", "Advancing to waypoint 1"], announcer.Sent);
    }
}
