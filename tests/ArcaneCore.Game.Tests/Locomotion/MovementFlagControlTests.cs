using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Tests.CombatCore;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Locomotion;

/// <summary>
/// Water walk / hover / feather fall as server-ordered changes (vmangos Unit::SetWaterWalking, SetHover,
/// SetFeatherFall, Unit.cpp:7224-7296), MovementInfo::CorrectData (Object.cpp:153-189) and the root-flag rule of
/// HandleMoverRelocation (MovementHandler.cpp:1067-1071).
/// </summary>
public sealed class MovementFlagControlTests
{
    private static (Player Player, FakeSession Session, WorldRuntime World) InWorld()
    {
        (WorldRuntime world, _, _, _) = CombatTestKit.CreateWorld();
        var session = new FakeSession();
        Player player = CombatTestKit.AddPlayer(world, 1, 100, 100, session);
        return (player, session, world);
    }

    // --- the order / ack pair --------------------------------------------------------------

    [Theory]
    [InlineData(MovementChangeType.WaterWalk, true, WorldOpcode.SmsgMoveWaterWalk, MovementFlags.WaterWalking)]
    [InlineData(MovementChangeType.WaterWalk, false, WorldOpcode.SmsgMoveLandWalk, MovementFlags.WaterWalking)]
    [InlineData(MovementChangeType.Hover, true, WorldOpcode.SmsgMoveSetHover, MovementFlags.Hover)]
    [InlineData(MovementChangeType.Hover, false, WorldOpcode.SmsgMoveUnsetHover, MovementFlags.Hover)]
    [InlineData(MovementChangeType.FeatherFall, true, WorldOpcode.SmsgMoveFeatherFall, MovementFlags.SafeFall)]
    [InlineData(MovementChangeType.FeatherFall, false, WorldOpcode.SmsgMoveNormalFall, MovementFlags.SafeFall)]
    public void InWorld_TheOrderIsSent_AndTheFlagWaitsForTheAck(MovementChangeType type, bool apply, WorldOpcode opcode, MovementFlags flag)
    {
        (Player player, FakeSession session, _) = InWorld();
        if (!apply)
        {
            player.AddMovementFlags(flag); // the state to be cleared
        }

        MovementControl.Request(player, type, apply);

        (WorldOpcode sent, byte[] payload) = session.Next();
        Assert.Equal(opcode, sent);
        Assert.Equal(MovementChangePackets.BuildFlagChange(player.Guid.Value, 0), payload); // packed GUID + counter 0
        Assert.Equal(apply ? MovementFlags.None : flag, player.Movement.Flags & flag);       // unchanged until the ack
        Assert.True(player.Locomotion.Pending.HasPendingOfType(type));

        Assert.True(MovementControl.Acknowledge(player, type, 0, apply));
        MovementControl.ApplyReal(player, type, apply);
        Assert.Equal(apply ? flag : MovementFlags.None, player.Movement.Flags & flag);
    }

    [Fact]
    public void NotInTheWorld_TheFlagIsSetDirectly_AndNothingIsSent()
    {
        var session = new FakeSession();
        Player player = TestWorld.CreatePlayer(1, 0, 0, session); // never added to a map: a login restore

        MovementControl.Request(player, MovementChangeType.WaterWalk, true);
        MovementControl.Request(player, MovementChangeType.FeatherFall, true);

        Assert.True(player.Movement.HasFlag(MovementFlags.WaterWalking | MovementFlags.SafeFall));
        Assert.Empty(session.Sent);
        Assert.False(player.Locomotion.Pending.HasPending);
    }

    [Fact]
    public void ARequestThatChangesNothing_IsDropped()
    {
        (Player player, FakeSession session, _) = InWorld();
        MovementControl.Request(player, MovementChangeType.Hover, apply: false); // already off, nothing pending
        Assert.Empty(session.Sent);

        player.AddMovementFlags(MovementFlags.Hover);
        MovementControl.Request(player, MovementChangeType.Hover, apply: true);   // already on
        Assert.Empty(session.Sent);

        // While an order is pending the opposite request is not "no change": the client must be told.
        MovementControl.Request(player, MovementChangeType.Hover, apply: false);
        MovementControl.Request(player, MovementChangeType.Hover, apply: true);
        Assert.Equal(2, session.Sent.Count);
    }

    [Fact]
    public void AnUnackedOrder_IsEnforcedByTheTimeout_WithTheSplinePacketToEveryone()
    {
        (Player player, FakeSession session, WorldRuntime world) = InWorld();
        MovementControl.Request(player, MovementChangeType.WaterWalk, true);
        session.Clear();

        world.RunTick(4001);

        Assert.True(player.Movement.HasFlag(MovementFlags.WaterWalking));
        (WorldOpcode opcode, byte[] payload) = session.Next();
        Assert.Equal(WorldOpcode.SmsgSplineMoveWaterWalk, opcode);
        Assert.Equal(MovementChangePackets.BuildEnforced(player.Guid.Value), payload);
    }

    [Fact]
    public void ATimedOutTurnRateOrder_TellsEveryone_WithTheSplinePacket_ThroughMovementControlEnforce()
    {
        (Player player, FakeSession session, _) = InWorld();
        UnitSpeed.SetTurnRate(player, 2.0f); // taken at once, recorded in the ledger as TurnRate
        float turn = player.TurnRate;
        session.Clear();

        player.Locomotion.Pending.Age(4001);
        PendingMovementChange? timedOut = player.Locomotion.Pending.CheckTimeout(4000, teleporting: false, out _);
        Assert.NotNull(timedOut);
        Assert.Equal(MovementChangeType.TurnRate, timedOut.Type);

        // Without the TurnRate guard in Enforce this reaches ApplyReal -> MovementChangeInfo.FlagOf, which throws.
        MovementControl.Enforce(player, timedOut, sendToClient: true);

        // vmangos resolves a timed-out turn rate with SetSpeedRateReal + SendSpeedChangeToAll (Unit.cpp:6772-6774): the rate is already taken, everyone is told.
        Assert.Equal(turn, player.TurnRate);
        (WorldOpcode opcode, byte[] payload) = session.Next();
        Assert.Equal(WorldOpcode.SmsgSplineSetTurnRate, opcode);
        Assert.Equal(SpeedPackets.BuildSpline(player.Guid.Value, turn), payload);
        Assert.Empty(session.Sent);
        Assert.False(player.Locomotion.Pending.HasPending);
    }

    [Fact]
    public void ALateTurnRateAck_AfterTheTimeout_IsIgnored_AndNotCountedAsWrong()
    {
        (Player player, _, _) = InWorld();
        UnitSpeed.SetTurnRate(player, 2.0f); // counter 0
        float first = player.TurnRate;
        UnitSpeed.SetTurnRate(player, 3.0f); // counter 1
        float second = player.TurnRate;
        player.Locomotion.Pending.Age(4001);
        while (player.Locomotion.Pending.HasPending)
        {
            player.Locomotion.Pending.CheckTimeout(4000, teleporting: false, out _); // the older order is dropped as superseded, the newer one times out
        }

        // vmangos counts a wrong ack only for counter 0 or one above the current (next) counter (MovementHandler.cpp:468-469, :481-482).
        Assert.False(MovementControl.AcknowledgeTurnRate(player, 1, second));
        Assert.Equal(0, player.Locomotion.WrongAckCount);
        Assert.False(MovementControl.AcknowledgeTurnRate(player, 2, second));  // the next counter: not above the current one, so not counted
        Assert.Equal(0, player.Locomotion.WrongAckCount);
        Assert.False(MovementControl.AcknowledgeTurnRate(player, 3, second));  // above the current counter: counted
        Assert.Equal(1, player.Locomotion.WrongAckCount);
        Assert.False(MovementControl.AcknowledgeTurnRate(player, 0, first));  // counter 0 is always counted
        Assert.Equal(2, player.Locomotion.WrongAckCount);
    }

    [Fact]
    public void ALateSpeedAck_AfterTheTimeout_IsIgnored_AndNotCountedAsWrong_LikeTheTurnRateAck()
    {
        (Player player, _, _) = InWorld();
        UnitSpeed.SetTurnRate(player, 2.0f); // counter 0 (any order moves the counter; the ledger is then emptied)
        UnitSpeed.SetTurnRate(player, 3.0f); // counter 1
        player.Locomotion.Pending.Age(4001);
        while (player.Locomotion.Pending.HasPending)
        {
            player.Locomotion.Pending.CheckTimeout(4000, teleporting: false, out _);
        }

        // vmangos counts a wrong speed ack only for counter 0 or above the current counter (MovementHandler.cpp:463-483).
        Assert.False(MovementControl.AcknowledgeSpeed(player, MoveType.Run, 1, 9.8f));
        Assert.Equal(0, player.Locomotion.WrongAckCount);
        Assert.False(MovementControl.AcknowledgeSpeed(player, MoveType.Run, 2, 9.8f));
        Assert.Equal(0, player.Locomotion.WrongAckCount);
        Assert.False(MovementControl.AcknowledgeSpeed(player, MoveType.Run, 3, 9.8f));
        Assert.Equal(1, player.Locomotion.WrongAckCount);
        Assert.False(MovementControl.AcknowledgeSpeed(player, MoveType.Run, 0, 9.8f));
        Assert.Equal(2, player.Locomotion.WrongAckCount);
    }

    [Fact]
    public void ATimedOutTurnRateOrder_SendsNothing_WhenTheCallerDoesNotSendToClient()
    {
        (Player player, FakeSession session, _) = InWorld();
        UnitSpeed.SetTurnRate(player, 2.0f);
        float turn = player.TurnRate;
        session.Clear();
        player.Locomotion.Pending.Age(4001);
        PendingMovementChange? timedOut = player.Locomotion.Pending.CheckTimeout(4000, teleporting: false, out _);
        Assert.NotNull(timedOut);

        MovementControl.Enforce(player, timedOut, sendToClient: false);

        Assert.Equal(turn, player.TurnRate);
        Assert.Empty(session.Sent);
    }

    // --- CorrectData ---------------------------------------------------------------------

    [Theory]
    [InlineData(MovementFlags.Root | MovementFlags.Forward, MovementFlags.Forward)]
    [InlineData(MovementFlags.Root | MovementFlags.Jumping, MovementFlags.Jumping)]
    [InlineData(MovementFlags.TurnLeft | MovementFlags.TurnRight, MovementFlags.None)]
    [InlineData(MovementFlags.StrafeLeft | MovementFlags.StrafeRight | MovementFlags.Forward, MovementFlags.Forward)]
    [InlineData(MovementFlags.PitchUp | MovementFlags.PitchDown, MovementFlags.None)]
    [InlineData(MovementFlags.Forward | MovementFlags.Backward | MovementFlags.WalkMode, MovementFlags.WalkMode)]
    [InlineData(MovementFlags.Root | MovementFlags.TurnLeft, MovementFlags.Root | MovementFlags.TurnLeft)] // turning is not in MASK_MOVING
    [InlineData(MovementFlags.Forward | MovementFlags.StrafeLeft, MovementFlags.Forward | MovementFlags.StrafeLeft)]
    public void CorrectData_RemovesContradictoryFlags(MovementFlags given, MovementFlags expected)
    {
        var info = new MovementInfo { Flags = given };
        info.CorrectData();
        Assert.Equal(expected, info.Flags);
    }

    [Fact]
    public void CorrectData_LeavesHoverAlone()
    {
        // vmangos has the "cannot hover without the aura" rule commented out (Object.cpp:168-170).
        var info = new MovementInfo { Flags = MovementFlags.Hover | MovementFlags.WaterWalking | MovementFlags.SafeFall };
        info.CorrectData();
        Assert.Equal(MovementFlags.Hover | MovementFlags.WaterWalking | MovementFlags.SafeFall, info.Flags);
    }

    // --- the flag authority observer ------------------------------------------------------

    [Fact]
    public void ARootedPlayersClient_CannotClearTheRootFlag()
    {
        (Player player, _, WorldRuntime world) = InWorld();
        player.AddMovementFlags(MovementFlags.Root);
        var context = new MovementObserverContext(player, world, WorldOpcode.MsgMoveHeartbeat);
        MovementInfo previous = player.Movement;
        var incoming = new MovementInfo { Flags = MovementFlags.None, X = 1 };

        MovementObservers.Before(context, in previous, ref incoming);

        Assert.True(incoming.HasFlag(MovementFlags.Root));
    }

    [Fact]
    public void AnUnrootedPlayersClient_KeepsItsOwnFlags_AndContradictionsAreCorrected()
    {
        (Player player, _, WorldRuntime world) = InWorld();
        var context = new MovementObserverContext(player, world, WorldOpcode.MsgMoveHeartbeat);
        MovementInfo previous = player.Movement;
        var incoming = new MovementInfo { Flags = MovementFlags.Forward | MovementFlags.Backward | MovementFlags.WaterWalking };

        MovementObservers.Before(context, in previous, ref incoming);

        Assert.Equal(MovementFlags.WaterWalking, incoming.Flags); // the server does not grant or strip WaterWalking here
    }
}
