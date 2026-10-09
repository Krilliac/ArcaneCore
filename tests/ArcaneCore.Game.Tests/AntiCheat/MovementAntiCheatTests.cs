using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.AntiCheat;

/// <summary>
/// The movement checks driven by synthetic MovementInfo sequences (docs/areas/anticheat.md): every detector has a positive
/// case and the legitimate look-alikes it must not score (lag bunching, knockback, speed change acks, teleports, transports).
/// </summary>
public sealed class MovementAntiCheatTests
{
    private const float Run = 7.0f;

    /// <summary>A scripted client: tracks its own clock, the server's receive clock and its position.</summary>
    private sealed class Client
    {
        public readonly MovementAntiCheat Checks;
        public readonly List<AntiCheatFinding> All = [];
        public uint ClientTime = 1_000_000;
        public uint Received = 50_000;
        public float X = 100, Y = 100, Z = 10, O;
        public MovementFlags Flags = MovementFlags.Forward;
        public float Allowed = Run;
        public bool Rooted;
        public bool Alive = true;
        public MovementFlags Granted;
        public TransportClaim Transport = TransportClaim.None;
        public IAntiCheatTerrain? Terrain;
        public int Latency = 100;

        public Client(AntiCheatOptions? options = null)
        {
            Checks = new MovementAntiCheat(options ?? new AntiCheatOptions());
        }

        public List<AntiCheatFinding> Send(WorldOpcode opcode = WorldOpcode.MsgMoveHeartbeat, float jumpXy = 0)
        {
            var findings = new List<AntiCheatFinding>();
            var movement = new MovementInfo { Flags = Flags, Time = ClientTime, X = X, Y = Y, Z = Z, Orientation = O, JumpXySpeed = jumpXy };
            Checks.CheckServerPosition(StoredX, StoredY, StoredZ);
            Checks.Observe(new MovementSample
            {
                Opcode = opcode, Movement = movement, ReceivedMs = Received, LatencyMs = Latency, AllowedSpeed = Allowed, Alive = Alive,
                Rooted = Rooted, GrantedFlags = Granted, Transport = Transport, Terrain = Terrain,
            }, findings);
            StoredX = X;
            StoredY = Y;
            StoredZ = Z;
            Checks.NotifyStored(X, Y, Z);
            All.AddRange(findings);
            return findings;
        }

        public float StoredX = 100, StoredY = 100, StoredZ = 10;

        /// <summary>Advance both clocks by <paramref name="ms"/> and move <paramref name="yards"/> along +X.</summary>
        public List<AntiCheatFinding> Step(uint ms, float yards, WorldOpcode opcode = WorldOpcode.MsgMoveHeartbeat, uint? receivedMs = null)
        {
            ClientTime += ms;
            Received += receivedMs ?? ms;
            X += yards;
            return Send(opcode);
        }

        /// <summary>Run at <paramref name="speed"/> with heartbeats every 500 ms for <paramref name="beats"/> beats.</summary>
        public void RunFor(int beats, float speed)
        {
            for (int i = 0; i < beats; i++)
            {
                Step(500, speed * 0.5f);
            }
        }
    }

    private sealed class FakeTerrain : IAntiCheatTerrain
    {
        public bool? Liquid;
        public float? Floor;
        public bool? Sight;

        public bool? IsInLiquid(float x, float y, float z) => Liquid;

        public float? FloorHeight(float x, float y, float z) => Floor;

        public bool? IsInLineOfSight(float x1, float y1, float z1, float x2, float y2, float z2) => Sight;
    }

    private static bool Has(IEnumerable<AntiCheatFinding> findings, AntiCheatViolation type) => findings.Any(f => f.Type == type);

    // --- legitimate movement --------------------------------------------------------------------------------------

    [Fact]
    public void NormalRunning_WithReceiveJitter_ScoresNothing()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        int[] jitter = [+60, -40, +80, -20, +30, -70, +50, -10];
        for (int i = 0; i < 200; i++)
        {
            client.Step(500, Run * 0.5f, receivedMs: (uint)(500 + jitter[i % 8]));
        }

        client.Flags = MovementFlags.None;
        client.Step(200, Run * 0.2f, WorldOpcode.MsgMoveStop);
        Assert.Empty(client.All);
    }

    [Fact]
    public void LagBunching_ManyPacketsArrivingTogetherAfterAStall_ScoresNothing()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(10, Run);

        // The network stalls for 2.5 s; five heartbeats the client sent meanwhile arrive in the same millisecond.
        client.Step(500, Run * 0.5f, receivedMs: 2500);
        for (int i = 0; i < 4; i++)
        {
            client.Step(500, Run * 0.5f, receivedMs: 0);
        }

        client.RunFor(20, Run);
        Assert.Empty(client.All);
    }

    [Fact]
    public void Jumping_AndLanding_AndRunningOffACliff_WithFallLand_ScoresNothing()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.Flags = MovementFlags.Forward | MovementFlags.Jumping;
        client.Step(100, Run * 0.1f, WorldOpcode.MsgMoveJump);
        client.Z += 1.5f;
        client.Step(400, Run * 0.4f);
        client.Flags = MovementFlags.Forward;
        client.Z -= 1.5f;
        client.Step(300, Run * 0.3f, WorldOpcode.MsgMoveFallLand);
        client.Flags = MovementFlags.Forward | MovementFlags.Jumping;
        client.Step(100, Run * 0.1f, WorldOpcode.MsgMoveJump);
        client.Flags = MovementFlags.Forward | MovementFlags.FallingFar;
        for (int i = 0; i < 6; i++)
        {
            client.Z -= 8;
            client.Step(500, Run * 0.5f);
        }

        client.Flags = MovementFlags.Forward;
        client.Step(100, Run * 0.1f, WorldOpcode.MsgMoveFallLand);
        client.RunFor(5, Run);
        Assert.Empty(client.All);
    }

    // --- speed and teleport -----------------------------------------------------------------------------------------

    [Fact]
    public void ASpeedHack_TwiceTheRunSpeedWithAnHonestClock_ScoresSpeed()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(10, Run * 2);
        Assert.True(Has(client.All, AntiCheatViolation.Speed));
        Assert.All(client.All.Where(f => f.Type == AntiCheatViolation.Speed), f => Assert.InRange(f.Weight, 5f, 25f));
    }

    [Fact]
    public void AFastClock_OneAndAHalfTimesReal_IsCaughtByTheIndependentClock()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);

        // The whole client runs 1.5x: its clock says 750 ms passed and it moved 750 ms at run speed, but only 500 ms did.
        for (int i = 0; i < 100; i++)
        {
            client.ClientTime += 750;
            client.Received += 500;
            client.X += Run * 0.75f;
            client.Send();
        }

        Assert.True(Has(client.All, AntiCheatViolation.TimeSync));
    }

    [Fact]
    public void ABlink_EightyYardsInOnePacket_ScoresTeleport_NotSpeed()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(4, Run);
        List<AntiCheatFinding> findings = client.Step(500, 80);
        Assert.True(Has(findings, AntiCheatViolation.Teleport));
        Assert.False(Has(findings, AntiCheatViolation.Speed));
    }

    [Fact]
    public void APacketAfterTheBaselineGap_IsANewBaseline_SoTheSameBlinkIsNotScored()
    {
        // The default 3 s gap (AFK, a loading screen): the packet after it is trusted.
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(4, Run);
        Assert.Empty(client.Step(500, 80, receivedMs: 3001));

        // The same pause under a larger gap is an ordinary step, and the blink is scored.
        var patient = new Client(new AntiCheatOptions { BaselineGapMs = 600_000 });
        patient.Send(WorldOpcode.MsgMoveStartForward);
        patient.RunFor(4, Run);
        Assert.True(Has(patient.Step(500, 80, receivedMs: 3001), AntiCheatViolation.Teleport));
    }

    [Fact]
    public void AServerRelocation_TrustsTheNextPacket_WhetherNotifiedOrSeenInThePosition()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(4, Run);

        // A teleport the server announced.
        client.Checks.NotifyServerRelocation();
        client.X += 500;
        client.StoredX = client.X;
        client.Step(500, 0);

        // A relocation the server did on its own (a charge spline, a graveyard): the stored position moved.
        client.X += 300;
        client.StoredX = client.X;
        client.Step(500, 0);
        client.RunFor(4, Run);
        Assert.Empty(client.All);
    }

    [Fact]
    public void Knockback_FlyingFastUntilTheLanding_ScoresNothing_ButSpeedingAfterTheLandingDoes()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(4, Run);
        client.Checks.NotifyKnockback();
        client.Flags = MovementFlags.FallingFar;
        for (int i = 0; i < 4; i++)
        {
            client.Step(250, 30 * 0.25f); // 30 yards per second through the air
        }

        client.Flags = MovementFlags.None;
        client.Step(100, 1, WorldOpcode.MsgMoveFallLand);
        client.Flags = MovementFlags.Forward;
        client.RunFor(4, Run);
        Assert.Empty(client.All);

        client.RunFor(6, Run * 2);
        Assert.True(Has(client.All, AntiCheatViolation.Speed));
    }

    [Fact]
    public void ASpeedDecrease_PacketsStillAtTheOldSpeedWithinTheGrace_ScoreNothing_ButLaterOnesDo()
    {
        var client = new Client { Allowed = Run * 2 }; // mounted
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(6, Run * 2);
        client.Allowed = Run; // dismounted: the ack arrived, these packets were sent before it
        client.RunFor(3, Run * 2);
        Assert.Empty(client.All);

        client.RunFor(12, Run * 2);
        Assert.True(Has(client.All, AntiCheatViolation.Speed));
    }

    [Fact]
    public void ASpeedIncreasePendingItsAck_IsInTheAllowedSpeed_AndScoresNothing()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(4, Run);
        client.Allowed = Run * 1.6f; // the caller includes the pending change's new speed
        client.RunFor(10, Run * 1.6f);
        Assert.Empty(client.All);
    }

    // --- transports ---------------------------------------------------------------------------------------------------

    [Fact]
    public void OnAKnownTransport_FastWorldMovementScoresNothing_AndLeavingItStartsANewBaseline()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(2, Run);
        client.Flags = MovementFlags.Forward | MovementFlags.OnTransport;
        client.Transport = TransportClaim.Known;
        client.RunFor(10, 25); // the ship carries the player at 25 yards per second
        client.Flags = MovementFlags.Forward;
        client.Transport = TransportClaim.None;
        client.Step(500, 30); // stepping off the moving ship
        client.RunFor(4, Run);
        Assert.Empty(client.All);
    }

    [Fact]
    public void AFakeTransport_ScoresFlag_AndDoesNotHideTheSpeed()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(2, Run);
        client.Flags = MovementFlags.Forward | MovementFlags.OnTransport;
        client.Transport = TransportClaim.Fake;
        client.RunFor(6, Run * 3);
        Assert.Contains(client.All, f => f.Type == AntiCheatViolation.Flag);
        Assert.True(Has(client.All, AntiCheatViolation.Speed) || Has(client.All, AntiCheatViolation.Teleport));
    }

    [Fact]
    public void AnUnknownTransport_IsNeverScored()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.Flags = MovementFlags.Forward | MovementFlags.OnTransport;
        client.Transport = TransportClaim.Unknown;
        client.RunFor(6, 20);
        Assert.Empty(client.All);
    }

    // --- capability flags -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(MovementFlags.WaterWalking)]
    [InlineData(MovementFlags.Hover)]
    [InlineData(MovementFlags.SafeFall)]
    public void ACapabilityFlagWithoutAGrant_ScoresFlag_WithAGrantOrWhenDead_Nothing(MovementFlags flag)
    {
        var cheat = new Client();
        cheat.Flags = MovementFlags.Forward | flag;
        cheat.Send(WorldOpcode.MsgMoveStartForward);
        cheat.RunFor(2, Run);
        Assert.True(Has(cheat.All, AntiCheatViolation.Flag));

        var granted = new Client { Granted = flag, Flags = MovementFlags.Forward | flag };
        granted.Send(WorldOpcode.MsgMoveStartForward);
        granted.RunFor(4, Run);
        Assert.Empty(granted.All);

        var ghost = new Client { Alive = false, Flags = MovementFlags.Forward | flag };
        ghost.Send(WorldOpcode.MsgMoveStartForward);
        ghost.RunFor(4, Run);
        Assert.Empty(ghost.All);
    }

    [Fact]
    public void Levitating_OrFlyingWhileSwimming_ScoresFlag_FlyingAloneDoesNot()
    {
        var levitate = new Client { Flags = MovementFlags.Forward | MovementFlags.Levitating };
        levitate.Send(WorldOpcode.MsgMoveStartForward);
        Assert.True(Has(levitate.All, AntiCheatViolation.Flag));

        var swimFly = new Client { Flags = MovementFlags.Forward | MovementFlags.Swimming | MovementFlags.Flying };
        swimFly.Send(WorldOpcode.MsgMoveStartForward);
        Assert.True(Has(swimFly.All, AntiCheatViolation.Flag));

        var grantedFly = new Client { Flags = MovementFlags.Forward | MovementFlags.Levitating, Granted = MovementFlags.Levitating | MovementFlags.Flying };
        grantedFly.Send(WorldOpcode.MsgMoveStartForward);
        Assert.Empty(grantedFly.All);

        var flying = new Client { Flags = MovementFlags.Forward | MovementFlags.Flying };
        flying.Send(WorldOpcode.MsgMoveStartForward);
        flying.RunFor(3, Run);
        Assert.Empty(flying.All);
    }

    // --- root -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void WhileRooted_MovingOrStartingToMove_ScoresPhysics_TurningInPlaceDoesNot()
    {
        var client = new Client { Flags = MovementFlags.None };
        client.Send(WorldOpcode.MsgMoveStop);
        client.Rooted = true;
        client.Flags = MovementFlags.TurnLeft | MovementFlags.Root;
        client.O = 1;
        client.Step(500, 0, WorldOpcode.MsgMoveSetFacing);
        Assert.Empty(client.All);

        client.Flags = MovementFlags.Forward | MovementFlags.Root;
        List<AntiCheatFinding> start = client.Step(100, 0, WorldOpcode.MsgMoveStartForward);
        Assert.True(Has(start, AntiCheatViolation.Physics));
        List<AntiCheatFinding> moved = client.Step(500, 3.5f);
        Assert.True(Has(moved, AntiCheatViolation.Physics));
    }

    [Fact]
    public void NotRooted_TheSameStartScoresNothing()
    {
        var client = new Client { Flags = MovementFlags.None };
        client.Send(WorldOpcode.MsgMoveStop);
        client.Flags = MovementFlags.Forward;
        client.Step(100, 0, WorldOpcode.MsgMoveStartForward);
        client.RunFor(3, Run);
        Assert.Empty(client.All);
    }

    // --- jumps and falls --------------------------------------------------------------------------------------------

    [Fact]
    public void AJumpWhileStillInTheAir_ScoresJump()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.Flags = MovementFlags.Forward | MovementFlags.Jumping;
        client.Step(100, 0.7f, WorldOpcode.MsgMoveJump);
        client.Z += 2;
        client.Step(300, 2.1f);
        List<AntiCheatFinding> again = client.Step(100, 0.7f, WorldOpcode.MsgMoveJump);
        Assert.True(Has(again, AntiCheatViolation.Jump));
    }

    [Fact]
    public void ALongDropEndingWithoutFallLand_ScoresFall_IntoWaterItDoesNot()
    {
        var cheat = new Client();
        cheat.Send(WorldOpcode.MsgMoveStartForward);
        cheat.Flags = MovementFlags.Forward | MovementFlags.FallingFar;
        for (int i = 0; i < 5; i++)
        {
            cheat.Z -= 6;
            cheat.Step(400, 2.8f);
        }

        cheat.Flags = MovementFlags.Forward;
        List<AntiCheatFinding> landed = cheat.Step(100, 0.7f);
        Assert.True(Has(landed, AntiCheatViolation.Fall));

        var swimmer = new Client();
        swimmer.Send(WorldOpcode.MsgMoveStartForward);
        swimmer.Flags = MovementFlags.Forward | MovementFlags.FallingFar;
        for (int i = 0; i < 5; i++)
        {
            swimmer.Z -= 6;
            swimmer.Step(400, 2.8f);
        }

        swimmer.Flags = MovementFlags.Forward | MovementFlags.Swimming;
        swimmer.Step(100, 0.7f, WorldOpcode.MsgMoveStartSwim);
        swimmer.RunFor(3, Run);
        Assert.Empty(swimmer.All);
    }

    // --- timing -------------------------------------------------------------------------------------------------------

    [Fact]
    public void AClientTimestampGoingBack_ScoresPacketTiming_SmallJitterDoesNot()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(3, Run);
        client.ClientTime -= 100;
        client.Received += 100;
        client.Send();
        Assert.Empty(client.All);

        client.ClientTime -= 5000;
        client.Received += 100;
        List<AntiCheatFinding> back = client.Send();
        Assert.True(Has(back, AntiCheatViolation.PacketTiming));
    }

    [Fact]
    public void ABurst_SixtyPacketsInHalfASecondByBothClocks_ScoresBurst_LagBunchedOnesDoNot()
    {
        var flood = new Client { Flags = MovementFlags.None };
        flood.Send(WorldOpcode.MsgMoveStop);
        for (int i = 0; i < 60; i++)
        {
            flood.Step(8, 0, WorldOpcode.MsgMoveSetFacing);
        }

        Assert.True(Has(flood.All, AntiCheatViolation.Burst));

        var lagged = new Client { Flags = MovementFlags.None };
        lagged.Send(WorldOpcode.MsgMoveStop);
        for (int i = 0; i < 60; i++)
        {
            lagged.Step(100, 0, WorldOpcode.MsgMoveSetFacing, receivedMs: i == 0 ? 2000u : 0u); // sent over 6 s, received at once
        }

        Assert.Empty(lagged.All);
    }

    [Fact]
    public void TimeSkips_AnOversizedOrSpammedSkipScores_AnOrdinaryOneDoesNot_AndItsClockJumpIsTrusted()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(4, Run);
        var findings = new List<AntiCheatFinding>();
        client.Checks.NotifyTimeSkip(2000, client.Received, findings);
        Assert.Empty(findings);

        // The client's movement clock jumped by the skip; the next packet is trusted.
        client.ClientTime += 2000;
        client.RunFor(4, Run);
        Assert.Empty(client.All);

        client.Checks.NotifyTimeSkip(60_000, client.Received, findings);
        Assert.True(Has(findings, AntiCheatViolation.TimeSync));

        findings.Clear();
        for (int i = 0; i < 12; i++)
        {
            client.Checks.NotifyTimeSkip(100, client.Received + (uint)(i * 200), findings);
        }

        Assert.True(Has(findings, AntiCheatViolation.PacketTiming));
    }

    [Fact]
    public void AnAcknowledgementWhoseTimestampGoesBack_ScoresPacketTiming_ExceptRightAfterATimeSkip()
    {
        var checks = new MovementAntiCheat(new AntiCheatOptions());
        var findings = new List<AntiCheatFinding>();
        checks.NotifyAcknowledgement(100_000, 10_000, findings);
        checks.NotifyAcknowledgement(101_000, 11_000, findings);
        Assert.Empty(findings);
        checks.NotifyAcknowledgement(90_000, 12_000, findings);
        Assert.True(Has(findings, AntiCheatViolation.PacketTiming));

        var skipped = new MovementAntiCheat(new AntiCheatOptions());
        var none = new List<AntiCheatFinding>();
        skipped.NotifyAcknowledgement(100_000, 10_000, none);
        skipped.NotifyTimeSkip(1500, 10_100, none);
        skipped.NotifyAcknowledgement(99_000, 10_200, none);
        Assert.Empty(none);
    }

    // --- terrain-dependent ----------------------------------------------------------------------------------------

    [Fact]
    public void SwimmingWhereTheTerrainHasNoWater_ScoresPhysics_OnlyWhenTheDataIsLoaded()
    {
        var withData = new Client { Terrain = new FakeTerrain { Liquid = false }, Flags = MovementFlags.Forward | MovementFlags.Swimming };
        withData.Send(WorldOpcode.MsgMoveStartSwim);
        withData.RunFor(6, Run);
        Assert.True(Has(withData.All, AntiCheatViolation.Physics));

        var noData = new Client { Terrain = new FakeTerrain { Liquid = null }, Flags = MovementFlags.Forward | MovementFlags.Swimming };
        noData.Send(WorldOpcode.MsgMoveStartSwim);
        noData.RunFor(6, Run);
        Assert.Empty(noData.All);

        var water = new Client { Terrain = new FakeTerrain { Liquid = true }, Flags = MovementFlags.Forward | MovementFlags.Swimming };
        water.Send(WorldOpcode.MsgMoveStartSwim);
        water.RunFor(6, Run);
        Assert.Empty(water.All);
    }

    [Fact]
    public void ClimbingIntoTheAirOnTheGround_ScoresVertical_OnlyWhenTheFloorIsKnown()
    {
        var withData = new Client { Terrain = new FakeTerrain { Floor = 10 } };
        withData.Send(WorldOpcode.MsgMoveStartForward);
        withData.Z += 6;
        List<AntiCheatFinding> climbed = withData.Step(500, 1);
        Assert.True(Has(climbed, AntiCheatViolation.Vertical));

        var noData = new Client { Terrain = new FakeTerrain { Floor = null } };
        noData.Send(WorldOpcode.MsgMoveStartForward);
        noData.Z += 6;
        Assert.Empty(noData.Step(500, 1));

        var stairs = new Client { Terrain = new FakeTerrain { Floor = 16 } }; // the floor rose with the player
        stairs.Send(WorldOpcode.MsgMoveStartForward);
        stairs.Z += 6;
        Assert.Empty(stairs.Step(500, 1));
    }

    [Fact]
    public void WalkingThroughWalls_ScoresPhysicsOnTheSecondBlockedStep_OnlyWithModelData()
    {
        var mounted = new Client { Allowed = Run * 2, Terrain = new FakeTerrain { Sight = false } };
        mounted.Send(WorldOpcode.MsgMoveStartForward);
        Assert.Empty(mounted.Step(500, 6.5f)); // one blocked step: a corner can do that
        mounted.RunFor(4, Run * 2);
        Assert.True(Has(mounted.All, AntiCheatViolation.Physics));

        var noData = new Client { Allowed = Run * 2, Terrain = new FakeTerrain { Sight = null } };
        noData.Send(WorldOpcode.MsgMoveStartForward);
        noData.RunFor(6, Run * 2);
        Assert.Empty(noData.All);
    }

    // --- rubberband target ------------------------------------------------------------------------------------------

    [Fact]
    public void TheLastValidPosition_IsTheLastPacketWithoutAPositionFinding()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(4, Run);
        float cleanX = client.X;
        client.Step(500, 80); // teleport
        Assert.True(client.Checks.TryGetLastValid(out float x, out _, out _, out _));
        Assert.Equal(cleanX, x);
    }
}
