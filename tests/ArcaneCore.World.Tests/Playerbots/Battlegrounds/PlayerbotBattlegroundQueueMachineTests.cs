using ArcaneCore.World.Playerbots.Battlegrounds;
using Xunit;
using Act = ArcaneCore.World.Playerbots.Battlegrounds.PlayerbotBattlegroundAction;
using Phase = ArcaneCore.World.Playerbots.Battlegrounds.PlayerbotBattlegroundPhase;

namespace ArcaneCore.World.Tests.Playerbots.Battlegrounds;

/// <summary>
/// The queue-to-leave state machine of one bot, over plain facts. The flow is a client's: hello and join (vmangos
/// BattleGroundHandler.cpp:40-264), port in (361-507), leave (509-525).
/// </summary>
public sealed class PlayerbotBattlegroundQueueMachineTests
{
    private static (Phase Phase, Act Action) Next(Phase phase, PlayerbotBattlegroundFacts facts) => PlayerbotBattlegroundQueueMachine.Next(phase, facts);

    [Fact]
    public void Idle_EligibleAndChanceHits_StartsTravelToFriendlyBattlemaster()
        => Assert.Equal((Phase.Travel, Act.PlanToBattlemaster), Next(Phase.Idle, new() { Roll = true }));

    [Fact]
    public void Idle_ChanceMisses_StaysIdle()
    {
        Assert.Equal((Phase.Idle, Act.None), Next(Phase.Idle, new() { Roll = false }));

        // Nothing else the bot observes starts a queue on its own.
        Assert.Equal((Phase.Idle, Act.None), Next(Phase.Idle, new() { InReach = true, QueueSlot = true, InvitedInstance = 7, Bound = true }));
    }

    [Fact]
    public void Travel_InInteractRange_SendsHelloThenJoin()
    {
        Assert.Equal((Phase.Travel, Act.None), Next(Phase.Travel, new() { InReach = false, InPhaseMs = 1000 }));
        Assert.Equal((Phase.Join, Act.Hello), Next(Phase.Travel, new() { InReach = true }));

        // The join follows the hello on the next tick, until it has been sent.
        Assert.Equal((Phase.Join, Act.Join), Next(Phase.Join, new() { HelloSent = true, JoinSent = false }));
        Assert.Equal((Phase.Join, Act.None), Next(Phase.Join, new() { HelloSent = true, JoinSent = true, InPhaseMs = 100 }));
    }

    [Fact]
    public void Join_QueueSlotSeen_GoesQueued_AndReleasesBotToBrain()
    {
        Assert.Equal((Phase.Queued, Act.Release), Next(Phase.Join, new() { JoinSent = true, QueueSlot = true }));

        // The slot wins over a late timeout: the join did answer.
        Assert.Equal((Phase.Queued, Act.Release), Next(Phase.Join, new() { JoinSent = true, QueueSlot = true, InPhaseMs = PlayerbotBattlegroundQueueMachine.JoinReplyTimeoutMs + 1 }));
        Assert.False(PlayerbotBattlegroundQueueMachine.Drives(Phase.Queued));
    }

    [Fact]
    public void Join_NoQueueSlotAfterTimeout_SetsTypeAside()
    {
        uint timeout = PlayerbotBattlegroundQueueMachine.JoinReplyTimeoutMs;
        Assert.Equal((Phase.Join, Act.None), Next(Phase.Join, new() { JoinSent = true, InPhaseMs = timeout }));
        Assert.Equal((Phase.Idle, Act.SetAside), Next(Phase.Join, new() { JoinSent = true, InPhaseMs = timeout + 1 }));

        // A bot that was taken or cannot act is released, not set aside.
        Assert.Equal((Phase.Idle, Act.Release), Next(Phase.Join, new() { JoinSent = true, Taken = true }));
        Assert.Equal((Phase.Idle, Act.Release), Next(Phase.Join, new() { JoinSent = true, Unable = true }));
    }

    [Fact]
    public void Queued_InvitedInstanceSet_SendsPortEnter()
    {
        Assert.Equal((Phase.Queued, Act.None), Next(Phase.Queued, new() { QueueSlot = true, InvitedInstance = 0 }));
        Assert.Equal((Phase.Entering, Act.PortEnter), Next(Phase.Queued, new() { QueueSlot = true, InvitedInstance = 12 }));

        // Dead or between maps: the invitation waits (Invited), nothing is sent.
        Assert.Equal((Phase.Invited, Act.None), Next(Phase.Queued, new() { QueueSlot = true, InvitedInstance = 12, Unable = true }));
        Assert.Equal((Phase.Entering, Act.PortEnter), Next(Phase.Invited, new() { QueueSlot = true, InvitedInstance = 12 }));
        Assert.Equal((Phase.Invited, Act.None), Next(Phase.Invited, new() { QueueSlot = true, InvitedInstance = 12, Unable = true }));

        // The queue slot vanished (the server dropped it): back to Idle without a packet.
        Assert.Equal((Phase.Idle, Act.None), Next(Phase.Queued, new() { QueueSlot = false }));
        Assert.Equal((Phase.Idle, Act.None), Next(Phase.Invited, new() { QueueSlot = true, InvitedInstance = 0 }));

        // Already bound to a match (the server moved it): follow along.
        Assert.Equal((Phase.Entering, Act.None), Next(Phase.Queued, new() { Bound = true, QueueSlot = true }));
    }

    [Fact]
    public void Queued_PastMaxQueueWait_SendsPortLeave()
    {
        Assert.Equal((Phase.Queued, Act.None), Next(Phase.Queued, new() { QueueSlot = true, QueuedTooLong = false }));
        Assert.Equal((Phase.Idle, Act.PortLeave), Next(Phase.Queued, new() { QueueSlot = true, QueuedTooLong = true }));
    }

    [Fact]
    public void Queued_PartyOrGroupTakesBot_SendsPortLeave()
    {
        Assert.Equal((Phase.Idle, Act.PortLeave), Next(Phase.Queued, new() { QueueSlot = true, Taken = true }));
        Assert.Equal((Phase.Idle, Act.PortLeave), Next(Phase.Queued, new() { QueueSlot = true, Taken = true, InvitedInstance = 3 }));
        Assert.Equal((Phase.Idle, Act.PortLeave), Next(Phase.Invited, new() { QueueSlot = true, InvitedInstance = 3, Taken = true }));
    }

    [Fact]
    public void Invited_MatchHoldsCapBots_DeclinesInvite()
    {
        Assert.Equal((Phase.Idle, Act.PortLeave), Next(Phase.Queued, new() { QueueSlot = true, InvitedInstance = 5, MatchHoldsCap = true }));
        Assert.Equal((Phase.Idle, Act.PortLeave), Next(Phase.Invited, new() { QueueSlot = true, InvitedInstance = 5, MatchHoldsCap = true }));

        // A dead bot cannot answer yet; it declines once it can.
        Assert.Equal((Phase.Invited, Act.None), Next(Phase.Queued, new() { QueueSlot = true, InvitedInstance = 5, MatchHoldsCap = true, Unable = true }));
    }

    [Fact]
    public void Entering_TakenAfterThePort_LeavesTheMatchInsteadOfPlayingIt()
    {
        // A real player's party took the bot in the tick its port went out: out again, before or after the arrival.
        Assert.Equal((Phase.Leaving, Act.LeaveBattlefield), Next(Phase.Entering, new() { Bound = true, Taken = true }));
        Assert.Equal((Phase.Leaving, Act.LeaveBattlefield), Next(Phase.Entering, new() { Bound = true, Arrived = true, Taken = true }));
        // Not bound any more: nothing to leave.
        Assert.Equal((Phase.Idle, Act.Release), Next(Phase.Entering, new() { Bound = false, Taken = true }));
    }

    [Fact]
    public void Entering_ParticipantOnMatchMap_GoesInMatch()
    {
        Assert.Equal((Phase.InMatch, Act.None), Next(Phase.Entering, new() { Bound = true, Arrived = true }));
        Assert.Equal((Phase.Entering, Act.None), Next(Phase.Entering, new() { Bound = true, InPhaseMs = PlayerbotBattlegroundQueueMachine.EnteringTimeoutMs }));

        // Fails closed: unbound, a vanished match or a stuck port release the bot.
        Assert.Equal((Phase.Idle, Act.Release), Next(Phase.Entering, new() { Bound = false }));
        Assert.Equal((Phase.Idle, Act.Release), Next(Phase.Entering, new() { Bound = true, MatchGone = true }));
        Assert.Equal((Phase.Idle, Act.Release), Next(Phase.Entering, new() { Bound = true, InPhaseMs = PlayerbotBattlegroundQueueMachine.EnteringTimeoutMs + 1 }));
    }

    [Fact]
    public void InMatch_StatusWaitLeave_SendsLeaveBattlefield()
    {
        Assert.Equal((Phase.InMatch, Act.None), Next(Phase.InMatch, new() { Bound = true }));
        Assert.Equal((Phase.Leaving, Act.LeaveBattlefield), Next(Phase.InMatch, new() { Bound = true, MatchOver = true }));

        // Already pulled out of the match by the server: wait for the world to take it back, no packet.
        Assert.Equal((Phase.Leaving, Act.None), Next(Phase.InMatch, new() { Bound = false }));
        Assert.Equal((Phase.Idle, Act.Release), Next(Phase.InMatch, new() { Bound = false, BackInWorld = true }));
        Assert.Equal((Phase.Leaving, Act.None), Next(Phase.InMatch, new() { Bound = true, MatchGone = true }));
    }

    [Fact]
    public void Leaving_BackInWorldOutOfMatch_ReturnsToBrain()
    {
        Assert.Equal((Phase.Idle, Act.Release), Next(Phase.Leaving, new() { BackInWorld = true }));
        Assert.Equal((Phase.Leaving, Act.None), Next(Phase.Leaving, new() { InPhaseMs = PlayerbotBattlegroundQueueMachine.LeavingTimeoutMs }));
        Assert.Equal((Phase.Idle, Act.Release), Next(Phase.Leaving, new() { InPhaseMs = PlayerbotBattlegroundQueueMachine.LeavingTimeoutMs + 1 }));
    }

    [Fact]
    public void Travel_PastTimeout_GivesUp()
    {
        uint timeout = PlayerbotBattlegroundQueueMachine.TravelTimeoutMs;
        Assert.Equal((Phase.Travel, Act.None), Next(Phase.Travel, new() { InPhaseMs = timeout }));
        Assert.Equal((Phase.Idle, Act.Release), Next(Phase.Travel, new() { InPhaseMs = timeout + 1 }));
        Assert.Equal((Phase.Idle, Act.Release), Next(Phase.Travel, new() { Taken = true }));
        Assert.Equal((Phase.Idle, Act.Release), Next(Phase.Travel, new() { Unable = true, InReach = true }));
        Assert.Equal((Phase.Idle, Act.SetAside), Next(Phase.Travel, new() { TravelRefused = true }));
        Assert.Equal((Phase.Join, Act.Hello), Next(Phase.Travel, new() { InReach = true, TravelRefused = true }));
    }

    [Fact]
    public void Drives_IsTrueExactlyWhileTheCoordinatorOwnsTheBot()
    {
        foreach (Phase phase in Enum.GetValues<Phase>())
        {
            bool expected = phase is Phase.Travel or Phase.Join or Phase.Entering or Phase.InMatch or Phase.Leaving;
            Assert.Equal(expected, PlayerbotBattlegroundQueueMachine.Drives(phase));
        }
    }

    [Fact]
    public void UnknownPhase_FailsClosedToIdleRelease()
        => Assert.Equal((Phase.Idle, Act.Release), Next((Phase)200, new()));
}
