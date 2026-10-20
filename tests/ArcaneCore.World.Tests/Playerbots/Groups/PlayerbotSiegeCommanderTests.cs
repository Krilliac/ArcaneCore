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
    public void APushTheSiegeBlocks_IsNoPush_TheRaidFightsOn()
    {
        Assert.Equal(PlayerbotSiegePush.None, PlayerbotSiegeCommander.PushOf(Straggler with { PushBlocked = true }));
        PlayerbotSiegeDecision blocked = PlayerbotSiegeCommander.Decide(Straggler with { PushBlocked = true });
        Assert.Equal((PlayerbotSiegeOrder.Assault, PlayerbotSiegePush.None), (blocked.Order, blocked.Push));
        Assert.Equal(PlayerbotSiegeOrder.Assault, Order(Stalemate with { PushBlocked = true, Hostiles = 1 }));
        Assert.Equal(PlayerbotSiegeOrder.AssignTank, Order(Stalemate with { PushBlocked = true }));
    }

    [Fact]
    public void AStalemateWhoseHostileCountFell_IsASlowWin_NotAStalemate()
    {
        // The clock started with 4 hostiles near the leader and 3 are left: the raid is winning, slowly.
        Assert.Equal(PlayerbotSiegePush.None, PlayerbotSiegeCommander.PushOf(Stalemate with { Hostiles = 3, HostilesAtStepStart = 4 }));
        // As many as at the start, or more (a trickle that refills): a stalemate.
        Assert.Equal(PlayerbotSiegePush.Stalemate, PlayerbotSiegeCommander.PushOf(Stalemate with { Hostiles = 3, HostilesAtStepStart = 3 }));
        Assert.Equal(PlayerbotSiegePush.Stalemate, PlayerbotSiegeCommander.PushOf(Stalemate with { Hostiles = 5, HostilesAtStepStart = 3 }));
        // A push that has begun goes on when the count falls (the leader walked out of the scan range, or the raid killed what attacked it).
        Assert.Equal(PlayerbotSiegePush.Stalemate, PlayerbotSiegeCommander.PushOf(Stalemate with { Hostiles = 1, HostilesAtStepStart = 4, Pushing = PlayerbotSiegePush.Stalemate }));
        // ... and a dip under 80% (it needs only the retreat rules to stay away: the fight is not lost at 70%).
        Assert.Equal(PlayerbotSiegePush.Stalemate, PlayerbotSiegeCommander.PushOf(Stalemate with { Hostiles = 1, HostilesAtStepStart = 4, Pushing = PlayerbotSiegePush.Stalemate, AverageHealthPct = 70f }));
        // ... until the fight is lost by the retreat rules.
        Assert.Equal(PlayerbotSiegePush.None, PlayerbotSiegeCommander.PushOf(Stalemate with { Hostiles = 1, HostilesAtStepStart = 4, Pushing = PlayerbotSiegePush.Stalemate, AverageHealthPct = 34f }));
        // The quiet rule is no slow win: no hostile is no hostile.
        Assert.Equal(PlayerbotSiegePush.Quiet, PlayerbotSiegeCommander.PushOf(Straggler with { HostilesAtStepStart = 4 }));
    }


    [Fact]
    public void ACarriedPush_SurvivesTheStepBump_AndAWaypointHold_ButNotTheExclusions()
    {
        // The step clock and the quiet clock are zero after a step bump and a hold shows hostiles: a push in force goes on all the same.
        PlayerbotSiegeFacts bumped = Fight with { Hostiles = 0, MsInCombatAtStep = 0, MsQuietInCombat = 0, Pushing = PlayerbotSiegePush.Stalemate };
        Assert.Equal(PlayerbotSiegePush.Stalemate, PlayerbotSiegeCommander.PushOf(bumped));
        Assert.Equal(PlayerbotSiegePush.Quiet, PlayerbotSiegeCommander.PushOf(bumped with { Hostiles = 3, Pushing = PlayerbotSiegePush.Quiet }));
        // The exclusions end it.
        Assert.Equal(PlayerbotSiegePush.None, PlayerbotSiegeCommander.PushOf(bumped with { PushBlocked = true }));
        Assert.Equal(PlayerbotSiegePush.None, PlayerbotSiegeCommander.PushOf(bumped with { ObjectiveLeg = true }));
        Assert.Equal(PlayerbotSiegePush.None, PlayerbotSiegeCommander.PushOf(bumped with { ObjectiveEngaged = true }));
        Assert.Equal(PlayerbotSiegePush.None, PlayerbotSiegeCommander.PushOf(bumped with { RetreatLatched = true }));
        Assert.Equal(PlayerbotSiegePush.None, PlayerbotSiegeCommander.PushOf(bumped with { InCombat = false }));
        Assert.Equal(PlayerbotSiegePush.None, PlayerbotSiegeCommander.PushOf(bumped with { TankAlive = false }));
        // The decision carries it as an Advance in combat.
        Assert.Equal(PlayerbotSiegePush.Stalemate, PlayerbotSiegeCommander.Decide(bumped with { Hostiles = 3 }).Push);
    }

    [Fact]
    public void TheNoProgressBound_WithdrawsAPushAfterThirtySeconds_NotAtTheWaypoint_AndDeniesTheStep()
    {
        var siege = new PlayerbotSiege(1, 0, 1, [new System.Numerics.Vector3(100f, 0f, 0f), new System.Numerics.Vector3(200f, 0f, 0f)], default, 0);
        var raid = new PlayerbotSiegeRaid(siege, 1) { Push = PlayerbotSiegePush.Stalemate, PushInForce = PlayerbotSiegePush.Stalemate };

        Assert.False(PlayerbotGroupCoordinator.NotePushProgress(raid, 0, 50f, false, false, 1_000));
        Assert.False(PlayerbotGroupCoordinator.NotePushProgress(raid, 0, 47f, false, false, 5_000)); // 3 yd nearer: progress
        Assert.False(PlayerbotGroupCoordinator.NotePushProgress(raid, 0, 46f, false, false, 20_000)); // 1 yd: not progress (needs more than 2)
        Assert.False(PlayerbotGroupCoordinator.NotePushProgress(raid, 0, 46f, false, false, 34_999)); // 29.999 s after the last progress
        Assert.Equal(-1, raid.PushDeniedStep);
        Assert.True(PlayerbotGroupCoordinator.NotePushProgress(raid, 0, 46f, false, false, 35_000));
        Assert.Equal(0, raid.PushDeniedStep);
        Assert.Equal(PlayerbotSiegePush.None, raid.PushInForce);

        // Standing at the waypoint is progress, however long the lockstep keeps the leader there.
        raid.PushInForce = PlayerbotSiegePush.Stalemate;
        raid.PushDeniedStep = -1;
        Assert.False(PlayerbotGroupCoordinator.NotePushProgress(raid, 0, 60f, false, false, 100_000));
        Assert.False(PlayerbotGroupCoordinator.NotePushProgress(raid, 0, 0f, true, false, 120_000));
        Assert.False(PlayerbotGroupCoordinator.NotePushProgress(raid, 0, 0f, true, false, 149_000));
        Assert.Equal(-1, raid.PushDeniedStep);

        // A step change starts the best distance over: 150 yd at the next waypoint is no stall against the 0 of the last.
        Assert.False(PlayerbotGroupCoordinator.NotePushProgress(raid, 1, 150f, false, false, 150_000));
        Assert.Equal(150f, raid.PushBestYards);
        Assert.Equal(1, raid.PushProgressStep);

        // A raid that does not push tracks nothing.
        raid.Push = PlayerbotSiegePush.None;
        Assert.False(PlayerbotGroupCoordinator.NotePushProgress(raid, 1, 150f, false, false, 500_000));
        Assert.Equal(-1f, raid.PushBestYards);
        Assert.Equal(-1, raid.PushProgressStep);
        Assert.Equal(-1, raid.PushDeniedStep);
    }


    [Fact]
    public void ALiftWaitAndRide_GainsNo2DYards_AndIsNoStall_ButTheLiftsOwnBoundApplies()
    {
        var siege = new PlayerbotSiege(1, 0, 1, [new System.Numerics.Vector3(100f, 0f, 0f), new System.Numerics.Vector3(200f, 0f, 0f)], default, 0);
        var raid = new PlayerbotSiegeRaid(siege, 1) { Push = PlayerbotSiegePush.Stalemate, PushInForce = PlayerbotSiegePush.Stalemate };

        Assert.False(PlayerbotGroupCoordinator.NotePushProgress(raid, 0, 50f, false, false, 1_000));
        // 40 s with no 2D gain at all while the leader has an elevator trip (the wait for the car and the ride share their XY): not withdrawn.
        for (uint t = 5_000; t <= 41_000; t += 4_000) Assert.False(PlayerbotGroupCoordinator.NotePushProgress(raid, 0, 50f, false, true, t));
        Assert.Equal(-1, raid.PushDeniedStep);
        // Off the lift the clock runs from the last riding read: 29 s later nothing, 30 s later the push is withdrawn.
        Assert.False(PlayerbotGroupCoordinator.NotePushProgress(raid, 0, 50f, false, false, 69_999));
        Assert.True(PlayerbotGroupCoordinator.NotePushProgress(raid, 0, 50f, false, false, 71_000));
        Assert.Equal(0, raid.PushDeniedStep);
    }

    [Fact]
    public void ABlockedQualification_StartsNoPush_ButAGrantedPushSurvivesAHold_AndTheDenialEndsIt()
    {
        PlayerbotSiegeFacts qualifies = Stalemate;
        Assert.Equal(PlayerbotSiegePush.Stalemate, PlayerbotSiegeCommander.PushOf(qualifies));
        // The siege refuses the push (PushBlocked): the decision grants none, and nothing starts.
        PlayerbotSiegeFacts refused = qualifies with { PushBlocked = true };
        PlayerbotSiegePush granted = PlayerbotSiegeCommander.Decide(refused).Push;
        Assert.Equal(PlayerbotSiegePush.None, granted);
        PlayerbotSiegePush inForce = PlayerbotGroupCoordinator.NextPushInForce(PlayerbotSiegePush.None, granted, refused, denied: false);
        Assert.Equal(PlayerbotSiegePush.None, inForce);

        // The raid then drops under 80 % (or loses a member), and the peer's fight ends: the refusal is gone, the raid no longer qualifies, and no push starts.
        PlayerbotSiegeFacts later = qualifies with { AverageHealthPct = 70f };
        granted = PlayerbotSiegeCommander.Decide(later with { Pushing = inForce }).Push;
        inForce = PlayerbotGroupCoordinator.NextPushInForce(inForce, granted, later with { Pushing = inForce }, denied: false);
        Assert.Equal(PlayerbotSiegePush.None, inForce);
        later = qualifies with { MsSinceLoss = 1_000 };
        granted = PlayerbotSiegeCommander.Decide(later).Push;
        Assert.Equal(PlayerbotSiegePush.None, PlayerbotGroupCoordinator.NextPushInForce(inForce, granted, later, denied: false));

        // A push that was granted survives a hold: the siege blocks it for the peer (the order is Assault, the decision grants none) and it stays in force, even at 70 % health.
        inForce = PlayerbotGroupCoordinator.NextPushInForce(PlayerbotSiegePush.None, PlayerbotSiegeCommander.Decide(qualifies).Push, qualifies, denied: false);
        Assert.Equal(PlayerbotSiegePush.Stalemate, inForce);
        PlayerbotSiegeFacts held = qualifies with { AverageHealthPct = 70f, MsInCombatAtStep = 0, Pushing = inForce, PushBlocked = true };
        Assert.Equal(PlayerbotSiegePush.None, PlayerbotSiegeCommander.Decide(held).Push);
        Assert.Equal(PlayerbotSiegePush.Stalemate, PlayerbotGroupCoordinator.NextPushInForce(inForce, PlayerbotSiegePush.None, held, denied: false));
        // ... and resumes when the block ends.
        Assert.Equal(PlayerbotSiegePush.Stalemate, PlayerbotSiegeCommander.Decide(held with { PushBlocked = false }).Push);
        // The denial of the step ends it; so does an exclusion (out of combat).
        Assert.Equal(PlayerbotSiegePush.None, PlayerbotGroupCoordinator.NextPushInForce(inForce, PlayerbotSiegePush.None, held, denied: true));
        Assert.Equal(PlayerbotSiegePush.None, PlayerbotGroupCoordinator.NextPushInForce(inForce, PlayerbotSiegePush.None, held with { InCombat = false }, denied: false));
    }

    [Fact]
    public void TheStallWatch_CountsAnObjectiveFightAndAGettingNearerPush_ButNotAHeldOrStalledPush()
    {
        var siege = new PlayerbotSiege(1, 0, 1, [new System.Numerics.Vector3(100f, 0f, 0f)], default, 0);
        var raid = new PlayerbotSiegeRaid(siege, 1);
        PlayerbotSiegeFacts longFight = Fight with { MsInCombatAtStep = PlayerbotSiegeCommander.StalemateMs + 60_000 };

        // A long fight of the ordinary kind is no progress; the objective's fight, and the one on the leg to it, are.
        Assert.False(PlayerbotGroupCoordinator.ShowsProgress(longFight, raid, 500_000));
        Assert.True(PlayerbotGroupCoordinator.ShowsProgress(longFight with { ObjectiveEngaged = true }, raid, 500_000));
        Assert.True(PlayerbotGroupCoordinator.ShowsProgress(longFight with { ObjectiveLeg = true }, raid, 500_000));
        Assert.True(PlayerbotGroupCoordinator.ShowsProgress(Fight with { MsInCombatAtStep = 10_000 }, raid, 500_000));

        // A push held for the peer (in force, the order is Assault, no push executes) is no progress, even in a young fight.
        raid.PushInForce = PlayerbotSiegePush.Stalemate;
        Assert.False(PlayerbotGroupCoordinator.ShowsProgress(Fight with { MsInCombatAtStep = 10_000 }, raid, 500_000));
        // A push that is executing counts while its leader is getting nearer, and stops counting after the stall window.
        raid.Push = PlayerbotSiegePush.Stalemate;
        raid.PushProgressMs = 495_000;
        Assert.True(PlayerbotGroupCoordinator.ShowsProgress(longFight, raid, 500_000));
        Assert.False(PlayerbotGroupCoordinator.ShowsProgress(longFight, raid, 495_000 + PlayerbotSiegeCommander.PushStallMs));
    }

    [Fact]
    public void AtAWaypoint_MeansOnItsLevel_ALeaderUnderALiftTopIsNotAtIt()
    {
        var waypoint = new System.Numerics.Vector3(100f, 50f, 40f);
        Assert.True(PlayerbotGroupCoordinator.IsAtWaypoint(new System.Numerics.Vector3(103f, 50f, 40f), waypoint));
        Assert.True(PlayerbotGroupCoordinator.IsAtWaypoint(new System.Numerics.Vector3(100f, 50f, 40f + PlayerbotSiegeCommander.WaypointLevelYards), waypoint));
        // The same XY on the floor of the Undercity lift shaft, about 100 yards below: not at it. Beyond the plane bound is not at it either.
        Assert.False(PlayerbotGroupCoordinator.IsAtWaypoint(new System.Numerics.Vector3(100f, 50f, -60f), waypoint));
        Assert.False(PlayerbotGroupCoordinator.IsAtWaypoint(new System.Numerics.Vector3(100f, 50f, 40f + PlayerbotSiegeCommander.WaypointLevelYards + 1f), waypoint));
        Assert.False(PlayerbotGroupCoordinator.IsAtWaypoint(new System.Numerics.Vector3(100f + PlayerbotSiegeCommander.WaypointYards + 1f, 50f, 40f), waypoint));
        Assert.True(PlayerbotSiegeCommander.WaypointLevelYards < 100f / 2);
    }
    [Fact]
    public void TheNoProgressAndLagHoldBounds_AreThirtySeconds_TwoYards_AndNinetySeconds()
    {
        Assert.Equal(30_000u, PlayerbotSiegeCommander.PushStallMs);
        Assert.Equal(2f, PlayerbotSiegeCommander.PushProgressYards);
        Assert.Equal(90_000u, PlayerbotGroupCoordinator.LagHoldMs);
    }
    [Fact]
    public void TheStepClock_TakesTheHostileCountOfItsFirstRead_AsTheBaseline_AndAStepChangeRestartsTheQuietClock()
    {
        var siege = new PlayerbotSiege(1, 0, 1, [new System.Numerics.Vector3(10f, 0f, 0f), new System.Numerics.Vector3(20f, 0f, 0f)], default, 0);
        var raid = new PlayerbotSiegeRaid(siege, 1);

        PlayerbotGroupCoordinator.ReadClocks(siege, raid, true, 0, hostiles: 4, now: 1_000);
        PlayerbotGroupCoordinator.ReadClocks(siege, raid, true, 0, hostiles: 2, now: 2_000);
        Assert.Equal(4, raid.HostilesAtStepStart);
        // Five seconds out of combat ends the clock; the next fight takes its own baseline.
        PlayerbotGroupCoordinator.ReadClocks(siege, raid, false, 5_000, 0, 8_000);
        PlayerbotGroupCoordinator.ReadClocks(siege, raid, true, 0, hostiles: 1, now: 9_000);
        Assert.Equal(1, raid.HostilesAtStepStart);

        // No hostile for 12 s, then the step changes: the quiet clock starts over (10 s more are needed at the new waypoint).
        PlayerbotGroupCoordinator.ReadClocks(siege, raid, true, 0, 0, 10_000);
        Assert.Equal(12_000u, PlayerbotGroupCoordinator.ReadClocks(siege, raid, true, 0, 0, 22_000).QuietCombatMs);
        siege.Step = 1;
        Assert.Equal(0u, PlayerbotGroupCoordinator.ReadClocks(siege, raid, true, 0, 0, 23_000).QuietCombatMs);
        Assert.Equal(10_000u, PlayerbotGroupCoordinator.ReadClocks(siege, raid, true, 0, 0, 33_000).QuietCombatMs);
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
