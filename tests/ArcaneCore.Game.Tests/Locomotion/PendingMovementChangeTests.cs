using ArcaneCore.Game.Locomotion;
using Xunit;

namespace ArcaneCore.Game.Tests.Locomotion;

/// <summary>The pending-change ledger (vmangos Unit.cpp:6619-6930): matching, supersession, timeout.</summary>
public sealed class PendingMovementChangeTests
{
    private const uint Ack = 4000;

    [Fact]
    public void Acknowledge_NeedsTheSameCounterApplyFlagAndType()
    {
        var ledger = new PendingMovementChanges();
        ledger.Push(7, MovementChangeType.WaterWalk, apply: true);

        Assert.False(ledger.TryAcknowledge(8, MovementChangeType.WaterWalk, true));   // counter
        Assert.False(ledger.TryAcknowledge(7, MovementChangeType.WaterWalk, false));  // apply
        Assert.False(ledger.TryAcknowledge(7, MovementChangeType.Hover, true));       // type
        Assert.Equal(1, ledger.Count);                                                // a wrong ack changes nothing

        Assert.True(ledger.TryAcknowledge(7, MovementChangeType.WaterWalk, true));
        Assert.False(ledger.TryAcknowledge(7, MovementChangeType.WaterWalk, true));   // a replay
        Assert.False(ledger.HasPending);
    }

    [Fact]
    public void AcknowledgingTheNewerChangeFirst_LeavesTheOlderOneUntilItTimesOut()
    {
        var ledger = new PendingMovementChanges();
        ledger.Push(1, MovementChangeType.Root, apply: true);
        ledger.Push(2, MovementChangeType.Root, apply: false);

        Assert.True(ledger.TryAcknowledge(2, MovementChangeType.Root, false));
        Assert.Equal(1, ledger.Count);

        // The older change was superseded by counter 2: when it times out it is dropped, not enforced.
        ledger.Age(Ack + 1);
        Assert.Null(ledger.CheckTimeout(Ack, teleporting: false, out bool dropped));
        Assert.True(dropped);
        Assert.False(ledger.HasPending);
    }

    [Fact]
    public void LastCounter_TracksThePerTypeCounter()
    {
        var ledger = new PendingMovementChanges();
        Assert.Equal(0u, ledger.LastCounterOf(MovementChangeType.Hover));
        ledger.Push(3, MovementChangeType.Hover, true);
        ledger.Push(5, MovementChangeType.Root, true);
        ledger.Push(9, MovementChangeType.Hover, false);
        Assert.Equal(9u, ledger.LastCounterOf(MovementChangeType.Hover));
        Assert.Equal(5u, ledger.LastCounterOf(MovementChangeType.Root));
    }

    [Fact]
    public void Timeout_IsStrictlyAfterTheAckTime_AndFiveTimesLongerWhileTeleporting()
    {
        var ledger = new PendingMovementChanges();
        PendingMovementChange change = ledger.Push(0, MovementChangeType.Root, apply: true);

        ledger.Age(Ack);
        Assert.Null(ledger.CheckTimeout(Ack, teleporting: false, out _));       // 4000 ms is not past 4000 ms
        Assert.True(ledger.HasPending);

        ledger.Age(1);
        Assert.Null(ledger.CheckTimeout(Ack, teleporting: true, out _));        // teleporting: limit is 20000 ms
        Assert.True(ledger.HasPending);

        Assert.Same(change, ledger.CheckTimeout(Ack, teleporting: false, out bool dropped));
        Assert.False(dropped);
        Assert.False(ledger.HasPending);
    }

    [Fact]
    public void ResolveAll_ReturnsOnlyTheLatestChangePerType_AndEmptiesTheLedger()
    {
        var ledger = new PendingMovementChanges();
        ledger.Push(1, MovementChangeType.Root, true);
        ledger.Push(2, MovementChangeType.WaterWalk, true);
        ledger.Push(3, MovementChangeType.Root, false);

        IReadOnlyList<PendingMovementChange> resolved = ledger.ResolveAll();

        Assert.Equal([(2u, MovementChangeType.WaterWalk), (3u, MovementChangeType.Root)], resolved.Select(c => (c.Counter, c.Type)).ToArray());
        Assert.False(ledger.HasPending);
    }

    [Fact]
    public void ChangeInfo_MapsEachTypeToItsFlagAndOpcodes()
    {
        Assert.Equal(Protocol.MovementFlags.WaterWalking, MovementChangeInfo.FlagOf(MovementChangeType.WaterWalk));
        Assert.Equal(Protocol.WorldOpcode.SmsgMoveLandWalk, MovementChangeInfo.ControllerOpcode(MovementChangeType.WaterWalk, false));
        Assert.Equal(Protocol.WorldOpcode.SmsgMoveSetHover, MovementChangeInfo.ControllerOpcode(MovementChangeType.Hover, true));
        Assert.Equal(Protocol.WorldOpcode.SmsgMoveNormalFall, MovementChangeInfo.ControllerOpcode(MovementChangeType.FeatherFall, false));
        Assert.Equal(Protocol.WorldOpcode.MsgMoveUnroot, MovementChangeInfo.ObserverOpcode(MovementChangeType.Root, false));
        Assert.Equal(Protocol.WorldOpcode.MsgMoveWaterWalk, MovementChangeInfo.ObserverOpcode(MovementChangeType.WaterWalk, false)); // one opcode, the block carries the flag
        Assert.Equal(Protocol.WorldOpcode.SmsgSplineMoveRoot, MovementChangeInfo.EnforcedOpcode(MovementChangeType.Root, true));
    }
}
