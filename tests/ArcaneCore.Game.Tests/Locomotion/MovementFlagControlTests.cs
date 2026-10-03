using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
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
