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

    // --- push through: a straggler fight, a stalemate ---------------------------------------------------------------------

    /// <summary>In combat, no hostile near the leader for exactly <see cref="PlayerbotSiegeCommander.PushThroughQuietMs"/>.</summary>
    private static readonly PlayerbotSiegeFacts Straggler = Fight with { Hostiles = 0, NearestHostileYards = float.PositiveInfinity, MsQuietInCombat = PlayerbotSiegeCommander.PushThroughQuietMs };

    /// <summary>In combat at one step for exactly <see cref="PlayerbotSiegeCommander.StalemateMs"/>, healthy, nobody lost lately, hostiles all round.</summary>
    private static readonly PlayerbotSiegeFacts Stalemate = Fight with
    {
        Hostiles = 3, MsInCombatAtStep = PlayerbotSiegeCommander.StalemateMs, AverageHealthPct = PlayerbotSiegeCommander.StalemateHealthPct,
        MsSinceLoss = PlayerbotSiegeCommander.StalemateLossWindowMs,
    };

    [Fact]
    public void TheNamedPushThroughBounds_AreTenSeconds_ThreeMinutes_EightyPercent_AndAMinute()
    {
        Assert.Equal(10_000u, PlayerbotSiegeCommander.PushThroughQuietMs);
        Assert.Equal(180_000u, PlayerbotSiegeCommander.StalemateMs);
        Assert.Equal(80f, PlayerbotSiegeCommander.StalemateHealthPct);
        Assert.Equal(60_000u, PlayerbotSiegeCommander.StalemateLossWindowMs);
        Assert.Equal(5_000u, PlayerbotSiegeCommander.StepClockQuietMs);
    }

    [Fact]
    public void InCombatWithNoHostileNearTheLeaderForTenSeconds_TheOrderIsAdvance_NoRetreatNoLatch()
    {
        PlayerbotSiegeDecision pushed = PlayerbotSiegeCommander.Decide(Straggler);
        Assert.Equal((PlayerbotSiegeOrder.Advance, PlayerbotSiegePush.Quiet, false, false, false), (pushed.Order, pushed.Push, pushed.RetreatLatched, pushed.PullLatched, pushed.Committed));

        PlayerbotSiegeDecision waiting = PlayerbotSiegeCommander.Decide(Straggler with { MsQuietInCombat = PlayerbotSiegeCommander.PushThroughQuietMs - 1 });
        Assert.Equal((PlayerbotSiegeOrder.Assault, PlayerbotSiegePush.None), (waiting.Order, waiting.Push));

        // A hostile in sight ends the quiet at once, however long the clock ran.
        Assert.Equal(PlayerbotSiegeOrder.Assault, Order(Straggler with { Hostiles = 1, NearestHostileYards = 50f, MsQuietInCombat = 60_000 }));
        // Out of combat the push is not in play: an idle raid advances as ever, with no push named.
        PlayerbotSiegeDecision idle = PlayerbotSiegeCommander.Decide(Calm with { MsQuietInCombat = 60_000 });
        Assert.Equal((PlayerbotSiegeOrder.Advance, PlayerbotSiegePush.None), (idle.Order, idle.Push));
    }

    [Fact]
    public void AStalemate_IsPushedThrough_AtEightyPercentHealth_WithNoLossInTheLastMinute_WhateverTheHostiles()
    {
        PlayerbotSiegeDecision pushed = PlayerbotSiegeCommander.Decide(Stalemate);
        Assert.Equal((PlayerbotSiegeOrder.Advance, PlayerbotSiegePush.Stalemate, false), (pushed.Order, pushed.Push, pushed.RetreatLatched));

        // Two hostiles and no cross is an AssignTank for a raid that fights on; a stalemate goes on.
        Assert.Equal(PlayerbotSiegeOrder.Advance, Order(Stalemate with { Hostiles = 2, CrossValid = false }));

        Assert.Equal(PlayerbotSiegeOrder.Assault, Order(Stalemate with { MsInCombatAtStep = PlayerbotSiegeCommander.StalemateMs - 1, Hostiles = 1 }));
        Assert.Equal(PlayerbotSiegeOrder.Assault, Order(Stalemate with { AverageHealthPct = 79.9f, Hostiles = 1 }));
        Assert.Equal(PlayerbotSiegeOrder.Assault, Order(Stalemate with { MsSinceLoss = PlayerbotSiegeCommander.StalemateLossWindowMs - 1, Hostiles = 1 }));
        Assert.Equal(PlayerbotSiegePush.Stalemate, PlayerbotSiegeCommander.Decide(Stalemate with { MsSinceLoss = uint.MaxValue }).Push);
    }

    [Fact]
    public void NeitherPushHappensOnTheObjective_OnTheObjectiveLeg_OrWithARetreatLatchOn()
    {
        foreach (PlayerbotSiegeFacts pushing in new[] { Straggler, Stalemate })
        {
            Assert.Equal(PlayerbotSiegeOrder.Assault, Order(pushing with { ObjectiveEngaged = true, Hostiles = 1 }));
            Assert.Equal(PlayerbotSiegeOrder.Assault, Order(pushing with { ObjectiveLeg = true, Hostiles = 1 }));
            Assert.Equal(PlayerbotSiegePush.None, PlayerbotSiegeCommander.PushOf(pushing with { ObjectiveEngaged = true }));
            Assert.Equal(PlayerbotSiegePush.None, PlayerbotSiegeCommander.PushOf(pushing with { ObjectiveLeg = true }));

            // A latch that runs keeps its order (Retreat on the way, Rally at the point); it does not become an advance.
            Assert.Equal(PlayerbotSiegeOrder.Retreat, Order(pushing with { RetreatLatched = true }));
            Assert.Equal(PlayerbotSiegeOrder.Rally, Order(pushing with { RetreatLatched = true, AtRally = true }));
            Assert.Equal(PlayerbotSiegePush.None, PlayerbotSiegeCommander.PushOf(pushing with { RetreatLatched = true }));
        }
    }

    [Fact]
    public void AFightTheRetreatRulesCallLost_IsNotPushedThrough_AndStillRetreats_OnceAndCounted()
    {
        foreach (PlayerbotSiegeFacts pushing in new[] { Straggler, Stalemate })
        {
            // Low health, the tank dead, a third gone: the retreat rules decide, the push does not (the stalemate also needs 80%).
            PlayerbotSiegeDecision lowHealth = PlayerbotSiegeCommander.Decide(pushing with { AverageHealthPct = 34f });
            Assert.Equal((PlayerbotSiegeOrder.Retreat, true, PlayerbotSiegePush.None), (lowHealth.Order, lowHealth.RetreatLatched, lowHealth.Push));
            Assert.Equal(PlayerbotSiegeOrder.Retreat, Order(pushing with { TankAlive = false, Alive = 5 }));
            Assert.Equal(PlayerbotSiegeOrder.Retreat, Order(pushing with { Alive = 4, DeadSinceEngagement = 2 }));
        }

        // A raid that has retreated MaxRetreats times, or already did in this engagement, presses on as Assault, not as a push.
        PlayerbotSiegeFacts lost = Straggler with { TankAlive = false };
        Assert.Equal((PlayerbotSiegeOrder.Assault, PlayerbotSiegePush.None, true),
            (PlayerbotSiegeCommander.Decide(lost with { RetreatsUsed = PlayerbotSiegeCommander.MaxRetreats }).Order,
                PlayerbotSiegeCommander.Decide(lost with { RetreatsUsed = PlayerbotSiegeCommander.MaxRetreats }).Push,
                PlayerbotSiegeCommander.Decide(lost with { RetreatsUsed = PlayerbotSiegeCommander.MaxRetreats }).Committed));
        Assert.Equal(PlayerbotSiegeOrder.Assault, Order(lost with { RetreatedThisEngagement = true }));
    }

    [Fact]
    public void ARaidWithNoLivingMember_IsNeverPushed()
        => Assert.Equal(PlayerbotSiegePush.None, PlayerbotSiegeCommander.PushOf(Straggler with { Alive = 0 }));

    [Fact]
    public void ThePushChangesNothingForTheRulesAround_RetreatAndPullAndRegroupAreAsBefore()
    {
        // The same facts without the push clocks decide as they did.
        Assert.Equal(PlayerbotSiegeOrder.Assault, Order(Fight with { Hostiles = 0 }));
        Assert.Equal(PlayerbotSiegeOrder.Pull, Order(Calm with { Hostiles = 1, NearestHostileYards = 30f, MsQuietInCombat = 60_000, MsInCombatAtStep = 600_000 }));
        Assert.Equal(PlayerbotSiegeOrder.Regroup, Order(Calm with { AnyResting = true, MsQuietInCombat = 60_000 }));
        Assert.Equal(PlayerbotSiegeOrder.Retreat, Order(Fight with { TankAlive = false, MsQuietInCombat = 60_000, MsInCombatAtStep = 600_000 }));
    }

    [Fact]
    public void TheStepClock_StartsInCombat_ResetsOnAStepChange_AndAfterFiveSecondsOutOfCombat_NotBefore()
    {
        var siege = new PlayerbotSiege(1, 0, 1, [new System.Numerics.Vector3(10f, 0f, 0f), new System.Numerics.Vector3(20f, 0f, 0f)], default, 0);
        var raid = new PlayerbotSiegeRaid(siege, 1);

        // Out of combat before any fight: the clock is not running.
        Assert.Equal(0u, PlayerbotGroupCoordinator.ReadClocks(siege, raid, inCombat: false, sinceCombat: uint.MaxValue, hostiles: 0, now: 1_000).StepCombatMs);
        // It starts at the first read in combat, and counts from there.
        Assert.Equal(0u, PlayerbotGroupCoordinator.ReadClocks(siege, raid, true, 0, 1, 2_000).StepCombatMs);
        Assert.Equal(60_000u, PlayerbotGroupCoordinator.ReadClocks(siege, raid, true, 0, 1, 62_000).StepCombatMs);
        // A gap shorter than five seconds does not break it.
        Assert.Equal(63_000u, PlayerbotGroupCoordinator.ReadClocks(siege, raid, false, 4_999, 1, 65_000).StepCombatMs);
        // Five seconds out of combat resets it; the next fight starts a new count.
        Assert.Equal(0u, PlayerbotGroupCoordinator.ReadClocks(siege, raid, false, 5_000, 1, 66_000).StepCombatMs);
        Assert.Equal(0u, PlayerbotGroupCoordinator.ReadClocks(siege, raid, true, 0, 1, 70_000).StepCombatMs);
        Assert.Equal(100_000u, PlayerbotGroupCoordinator.ReadClocks(siege, raid, true, 0, 1, 170_000).StepCombatMs);
        // A step change resets it, and a raid still in combat starts again at once.
        siege.Step = 1;
        Assert.Equal(0u, PlayerbotGroupCoordinator.ReadClocks(siege, raid, true, 0, 1, 171_000).StepCombatMs);
        Assert.Equal(9_000u, PlayerbotGroupCoordinator.ReadClocks(siege, raid, true, 0, 1, 180_000).StepCombatMs);
    }

    [Fact]
    public void TheQuietClock_RunsOnlyInCombatWithNoHostile_AndStopsAtTheFirstReadThatIsNot()
    {
        var siege = new PlayerbotSiege(1, 0, 1, [new System.Numerics.Vector3(10f, 0f, 0f)], default, 0);
        var raid = new PlayerbotSiegeRaid(siege, 1);

        Assert.Equal(0u, PlayerbotGroupCoordinator.ReadClocks(siege, raid, true, 0, hostiles: 0, now: 1_000).QuietCombatMs);
        Assert.Equal(12_000u, PlayerbotGroupCoordinator.ReadClocks(siege, raid, true, 0, 0, 13_000).QuietCombatMs);
        // One read with a hostile in sight, or out of combat, and the count starts over.
        Assert.Equal(0u, PlayerbotGroupCoordinator.ReadClocks(siege, raid, true, 0, 2, 14_000).QuietCombatMs);
        Assert.Equal(0u, PlayerbotGroupCoordinator.ReadClocks(siege, raid, true, 0, 0, 15_000).QuietCombatMs);
        Assert.Equal(0u, PlayerbotGroupCoordinator.ReadClocks(siege, raid, false, 1_000, 0, 16_000).QuietCombatMs);
        Assert.Equal(0u, PlayerbotGroupCoordinator.ReadClocks(siege, raid, true, 0, 0, 17_000).QuietCombatMs);
        Assert.Equal(10_000u, PlayerbotGroupCoordinator.ReadClocks(siege, raid, true, 0, 0, 27_000).QuietCombatMs);
    }

    [Fact]
    public void ALiftRideIsKeptThroughCombat_OnlyByARaidThatPushesThrough()
    {
        var siege = new PlayerbotSiege(1, 0, 1, [new System.Numerics.Vector3(10f, 0f, 0f)], default, 0);
        var raid = new PlayerbotSiegeRaid(siege, 1);
        Assert.True(PlayerbotGroupAI.CombatEndsRide(null));
        Assert.True(PlayerbotGroupAI.CombatEndsRide(raid));
        raid.Push = PlayerbotSiegePush.Quiet;
        Assert.False(PlayerbotGroupAI.CombatEndsRide(raid));
        raid.Push = PlayerbotSiegePush.Stalemate;
        Assert.False(PlayerbotGroupAI.CombatEndsRide(raid));
        raid.Push = PlayerbotSiegePush.None;
        Assert.True(PlayerbotGroupAI.CombatEndsRide(raid));
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
