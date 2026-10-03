using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Locomotion;

/// <summary>Unit::UpdateSpeed (vmangos Unit.cpp:6959-7100) over the aura ledger, and the speed packet layouts.</summary>
public sealed class SpeedRateFormulaTests
{
    private static readonly LocomotionOptions Retail = new();

    private static Player WithAuras(params (AuraType Type, int Amount)[] auras)
    {
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        foreach ((AuraType type, int amount) in auras)
        {
            player.Locomotion.Auras.Add(new SpellAura(0, type, amount, 0, 0));
        }

        return player;
    }

    private static float Speed(Player player, MoveType type, LocomotionOptions? options = null, bool battleground = false)
        => (UnitSpeed.ComputeRate(player, type, options ?? Retail, battleground) ?? throw new InvalidOperationException("no rate")) * UnitSpeed.BaseSpeed(type);

    [Fact]
    public void WithoutAuras_EveryRateIsOne_AndTheBaseSpeedsAreVanilla()
    {
        Player player = WithAuras();
        foreach (MoveType type in new[] { MoveType.Walk, MoveType.Run, MoveType.RunBack, MoveType.Swim })
        {
            Assert.Equal(1.0f, UnitSpeed.ComputeRate(player, type, Retail, false));
        }

        Assert.Equal((2.5f, 7.0f, 4.5f, 4.722222f, 2.5f), (UnitSpeed.BaseSpeed(MoveType.Walk), UnitSpeed.BaseSpeed(MoveType.Run), UnitSpeed.BaseSpeed(MoveType.RunBack), UnitSpeed.BaseSpeed(MoveType.Swim), UnitSpeed.BaseSpeed(MoveType.SwimBack)));
        Assert.Null(UnitSpeed.ComputeRate(player, MoveType.SwimBack, Retail, false)); // never updated (Unit.cpp:7005)
    }

    [Fact]
    public void GhostWolf_PlusForty_RunsAtNinePointEight()
    {
        Player player = WithAuras((AuraType.ModIncreaseSpeed, 40));
        Assert.Equal(7.0f * ((100.0f + 40) / 100.0f), Speed(player, MoveType.Run));
        Assert.Equal(9.8f, Speed(player, MoveType.Run), 4);
        Assert.Equal(2.5f, Speed(player, MoveType.Walk)); // only run is affected
        Assert.Equal(4.5f, Speed(player, MoveType.RunBack));
    }

    [Fact]
    public void TheStrongestIncrease_Counts_NotTheSum()
    {
        Player player = WithAuras((AuraType.ModIncreaseSpeed, 40), (AuraType.ModIncreaseSpeed, 60), (AuraType.ModIncreaseSpeed, -10));
        Assert.Equal(11.2f, Speed(player, MoveType.Run), 4);
    }

    [Fact]
    public void TheStrongestSlow_MultipliesRunRunBackAndSwim_Only()
    {
        Player player = WithAuras((AuraType.ModIncreaseSpeed, 40), (AuraType.ModDecreaseSpeed, -50), (AuraType.ModDecreaseSpeed, -30));

        Assert.Equal(4.9f, Speed(player, MoveType.Run), 4);          // 9.8 * 0.5, the strongest slow only
        Assert.Equal(2.25f, Speed(player, MoveType.RunBack), 4);
        Assert.Equal(4.722222f * 0.5f, Speed(player, MoveType.Swim), 4);
        Assert.Equal(2.5f, Speed(player, MoveType.Walk));            // walk is not slowed
    }

    [Fact]
    public void MountedRun_UsesTheMountedAuras_AndIgnoresTheOnFootOnes()
    {
        Player player = WithAuras((AuraType.ModIncreaseMountedSpeed, 60), (AuraType.ModIncreaseSpeed, 40));
        player.SetUInt32(UpdateFields.UnitFieldMountdisplayid, 14337);

        Assert.Equal(11.2f, Speed(player, MoveType.Run), 4); // 7 * 1.6; Ghost Wolf's +40 does not apply on a mount

        player.SetUInt32(UpdateFields.UnitFieldMountdisplayid, 0);
        Assert.Equal(9.8f, Speed(player, MoveType.Run), 4);  // dismounted: the mount aura does not apply on foot
    }

    [Fact]
    public void TheLargerOfTheStackingAndTheNonStackingBonus_IsUsed()
    {
        // 25% always-on speed with a +40% main speed: 1.25 * 140 / 100 = 1.75.
        Player stacked = WithAuras((AuraType.ModIncreaseSpeed, 40), (AuraType.ModSpeedAlways, 25));
        Assert.Equal(12.25f, Speed(stacked, MoveType.Run), 4);

        // two always-on auras multiply: 1.25 * 1.25.
        Player twice = WithAuras((AuraType.ModSpeedAlways, 25), (AuraType.ModSpeedAlways, 25));
        Assert.Equal(7.0f * 1.5625f, Speed(twice, MoveType.Run), 4);

        // a non-stacking +15 beats the empty stacking bonus (1.0), a +30 beats +25.
        Assert.Equal(7.0f * 1.15f, Speed(WithAuras((AuraType.ModSpeedNotStack, 15)), MoveType.Run), 4);
        Assert.Equal(7.0f * 1.30f, Speed(WithAuras((AuraType.ModSpeedNotStack, 30), (AuraType.ModSpeedAlways, 25)), MoveType.Run), 4);

        // mounted: the mounted always/not-stack auras.
        Player mounted = WithAuras((AuraType.ModIncreaseMountedSpeed, 60), (AuraType.ModMountedSpeedAlways, 25));
        mounted.SetUInt32(UpdateFields.UnitFieldMountdisplayid, 1);
        Assert.Equal(14.0f, Speed(mounted, MoveType.Run), 4); // 1.25 * 160 / 100 = 2.0
    }

    [Fact]
    public void SwimIncrease_ScalesTheSwimSpeedOnly()
    {
        Player player = WithAuras((AuraType.ModIncreaseSwimSpeed, 50));
        Assert.Equal(4.722222f * 1.5f, Speed(player, MoveType.Swim), 4);
        Assert.Equal(7.083333f, Speed(player, MoveType.Swim), 4);
        Assert.Equal(7.0f, Speed(player, MoveType.Run));
    }

    [Fact]
    public void UseNormalMovementSpeed_CapsRunAndSwimAtTheAuraAmount()
    {
        Player player = WithAuras((AuraType.ModIncreaseSpeed, 100), (AuraType.ModIncreaseSwimSpeed, 100), (AuraType.UseNormalMovementSpeed, 7));

        Assert.Equal(1.0f, UnitSpeed.ComputeRate(player, MoveType.Run, Retail, false)); // 7 / 7
        Assert.Equal(7.0f / 4.722222f, UnitSpeed.ComputeRate(player, MoveType.Swim, Retail, false));
    }

    [Fact]
    public void TheGhostRate_AppliesOnlyWhileTheDeathStateIsCorpse_PerWorldOrBattleground()
    {
        Player player = WithAuras();
        var options = new LocomotionOptions { GhostRunSpeedWorld = 1.5f, GhostRunSpeedBattleground = 2.0f };

        Assert.Equal(1.0f, UnitSpeed.ComputeRate(player, MoveType.Run, options, false)); // alive

        player.Combat.DeathState = DeathState.Corpse;
        Assert.Equal(1.5f, UnitSpeed.ComputeRate(player, MoveType.Run, options, false));
        Assert.Equal(2.0f, UnitSpeed.ComputeRate(player, MoveType.Run, options, true));
        Assert.Equal(1.0f, UnitSpeed.ComputeRate(player, MoveType.Run, Retail, false));    // the default is inert

        player.Combat.DeathState = DeathState.Dead;
        Assert.Equal(1.0f, UnitSpeed.ComputeRate(player, MoveType.Run, options, false));
    }

    [Fact]
    public void GhostRates_AreClampedToTheVmangosRange()
    {
        var options = new LocomotionOptions { GhostRunSpeedWorld = 0.0f, GhostRunSpeedBattleground = 50.0f };
        Assert.Equal([nameof(LocomotionOptions.GhostRunSpeedWorld), nameof(LocomotionOptions.GhostRunSpeedBattleground)], options.Normalize());
        Assert.Equal((0.1f, 10.0f), (options.GhostRunSpeedWorld, options.GhostRunSpeedBattleground));
    }

    // --- packets -------------------------------------------------------------------------

    [Fact]
    public void ForceSpeedChange_MatchesTheGtkerVector()
    {
        // smsg_force_run_speed_change.wowm: guid 6, move_event (counter) 0, speed 7 -> 01 06 | 00 00 00 00 | 00 00 e0 40
        Assert.Equal(new byte[] { 0x01, 0x06, 0, 0, 0, 0, 0x00, 0x00, 0xe0, 0x40 }, SpeedPackets.BuildForceChange(6, 0, 7.0f));
        Assert.Equal(WorldOpcode.SmsgForceRunSpeedChange, SpeedPackets.ForceOpcode(MoveType.Run));
        Assert.Equal(WorldOpcode.CmsgForceSwimBackSpeedChangeAck, SpeedPackets.AckOpcode(MoveType.SwimBack));
        Assert.Equal(WorldOpcode.MsgMoveSetRunBackSpeed, SpeedPackets.ObserverOpcode(MoveType.RunBack));
        Assert.Equal(WorldOpcode.SmsgSplineSetWalkSpeed, SpeedPackets.SplineOpcode(MoveType.Walk));
    }

    [Fact]
    public void TheAck_ParsesTheGtkerVector()
    {
        // cmsg_force_run_speed_change_ack.wowm test vector (without the 6-byte header).
        byte[] bytes =
        [
            0x06, 0, 0, 0, 0, 0, 0, 0,                         // guid
            0, 0, 0, 0,                                        // counter
            0, 0, 0, 0,                                        // flags
            0x40, 0x17, 0xf6, 0x01,                            // timestamp 32905024
            0xcb, 0xab, 0x0b, 0xc6, 0x07, 0x86, 0xf8, 0xc2,    // x, y
            0x8e, 0xd1, 0xa5, 0x42, 0xed, 0x99, 0x7f, 0x40,    // z, orientation
            0x39, 0x03, 0x00, 0x00,                            // fall time 0x339
            0x00, 0x00, 0xe0, 0x40,                            // new speed 7
        ];

        SpeedAck ack = SpeedPackets.ReadAck(bytes);

        Assert.Equal((6ul, 0u, 7.0f), (ack.Guid, ack.Counter, ack.Speed));
        Assert.Equal((MovementFlags.None, 32905024u, 0x339u), (ack.Movement.Flags, ack.Movement.Time, ack.Movement.FallTime));
        Assert.Equal(-8938.948f, ack.Movement.X, 2);
    }

    [Fact]
    public void ObserverAndSplinePackets_AreGuidThenBlockThenSpeed()
    {
        var info = new MovementInfo { Time = 5, X = 1, Y = 2, Z = 3, Orientation = 4 };
        byte[] observer = SpeedPackets.BuildObserver(6, info, 9.8f);
        var reader = new PacketReader(observer);
        Assert.Equal(6ul, reader.ReadPackedGuid());
        Assert.Equal(5u, MovementInfo.Read(ref reader).Time);
        Assert.Equal(9.8f, reader.ReadSingle());
        Assert.Equal(0, reader.Remaining);

        Assert.Equal(new byte[] { 0x01, 0x06, 0x00, 0x00, 0xe0, 0x40 }, SpeedPackets.BuildSpline(6, 7.0f));
    }

    [Fact]
    public void SpeedAcks_MatchByCounterTypeAndSpeedWithinOneHundredth()
    {
        var ledger = new PendingMovementChanges();
        ledger.Push(4, MovementChangeType.SpeedRun, true, 9.8f);

        Assert.False(ledger.TryAcknowledgeSpeed(5, MovementChangeType.SpeedRun, 9.8f));    // counter
        Assert.False(ledger.TryAcknowledgeSpeed(4, MovementChangeType.SpeedSwim, 9.8f));   // type
        Assert.False(ledger.TryAcknowledgeSpeed(4, MovementChangeType.SpeedRun, 9.82f));   // 0.02 off
        Assert.True(ledger.TryAcknowledgeSpeed(4, MovementChangeType.SpeedRun, 9.805f));   // 0.005 off
        Assert.False(ledger.HasPending);
    }
}
