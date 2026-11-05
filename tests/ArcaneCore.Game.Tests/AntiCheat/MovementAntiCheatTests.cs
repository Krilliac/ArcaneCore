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
        public bool ServerControlled;
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
                Rooted = Rooted, GrantedFlags = Granted, ServerControlled = ServerControlled, Transport = Transport, Terrain = Terrain,
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

    [Theory]
    [InlineData(1u)]
    [InlineData(200u)]
    [InlineData(500u)]
    public void ABlink_StampedBehindThePreviousBlock_ScoresTeleport(uint behindMs)
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(4, Run);
        client.Received += 500;
        client.ClientTime -= behindMs; // behind the last block, within ClientTimeRegressionMs
        client.X += 80;
        List<AntiCheatFinding> findings = client.Send();
        Assert.True(Has(findings, AntiCheatViolation.Teleport), "an 80 yard step behind the last stamp is a blink");
        Assert.False(Has(findings, AntiCheatViolation.PacketTiming), "the stamp is inside the regression threshold, so the blink score is the only finding that can catch it");
    }

    [Theory]
    [InlineData(3f)]
    [InlineData(10f)]
    [InlineData(30f)]
    [InlineData(49f)]
    public void AStep_StampedBehindThePreviousBlock_BeyondTheSlackYards_ScoresSpeed_NotTeleport(float yards)
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(4, Run);
        client.Received += 500;
        client.ClientTime -= 100;
        client.X += yards;
        List<AntiCheatFinding> findings = client.Send();
        Assert.True(Has(findings, AntiCheatViolation.Speed), "a stamp behind the last carries no client time: the step gets only SpeedSlackYards");
        Assert.False(Has(findings, AntiCheatViolation.Teleport));
    }

    [Fact]
    public void ABlink_StampedJustOverTheRegressionThreshold_ScoresBothTheStampAndTheTeleport_OncePerCheck()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(4, Run);
        client.Received += 500;
        client.ClientTime -= 501;
        client.X += 80;
        List<AntiCheatFinding> findings = client.Send();
        Assert.Equal(1, findings.Count(f => f.Type == AntiCheatViolation.Teleport));
        Assert.Equal(1, findings.Count(f => f.Type == AntiCheatViolation.PacketTiming && f.Detail == "client timestamp went back"));
    }

    [Fact]
    public void AStreamOfSmallSteps_EachStampedBehindTheLast_ScoresSpeedAndBurst_NotTheOneFreePassPerPacket()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(4, Run);

        // 2 yd (SpeedSlackYards) per packet, 2 ms apart (500 packets/s), every stamp 1 ms behind the last: ~1000 yd/s, each step alone unscored.
        var findings = new List<AntiCheatFinding>();
        for (int i = 0; i < 300; i++)
        {
            client.ClientTime -= 1;
            client.Received += 2;
            client.X += 2f;
            findings.AddRange(client.Send());
        }

        Assert.Contains(findings, f => f.Type == AntiCheatViolation.Speed);
        Assert.Contains(findings, f => f.Type == AntiCheatViolation.Burst);
        Assert.DoesNotContain(findings, f => f.Type == AntiCheatViolation.Teleport);
    }

    [Fact]
    public void AStreamOfBehindStampedSteps_ScoresWithinAWindowOfOneSecond()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(4, Run);
        int firstSpeed = -1;
        for (int i = 0; i < 100; i++)
        {
            client.ClientTime -= 1;
            client.Received += 10;
            client.X += 1.9f; // 190 yd/s against 7: the bucket (slack yards + the latency slack's run) is empty after a few packets
            if (Has(client.Send(), AntiCheatViolation.Speed) && firstSpeed < 0)
            {
                firstSpeed = i;
            }
        }

        Assert.InRange(firstSpeed, 0, 20);
    }

    [Fact]
    public void LagBunchedHonestSteps_AfterAStall_AreNotScoredByTheDistanceBudget()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(4, Run);

        // 12 heartbeats sent 500 ms apart by the client but delivered 10 ms apart: the client's clock vouches for the distance.
        for (int i = 0; i < 12; i++)
        {
            client.Step(500, Run * 0.5f, receivedMs: 10);
        }

        Assert.DoesNotContain(client.All, f => f.Type is AntiCheatViolation.Speed or AntiCheatViolation.Teleport);
    }

    [Fact]
    public void AStamp_MoreThanMaxGapAheadOfRealTime_ScoresTimeSync_AndGetsNoLatencySlack()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(4, Run);

        // 100 ms real, the stamp 3.5 s ahead (the independent clock discards the pair): 5 yd in 100 ms is 50 yd/s. With the
        // latency slack (1.2 s) a step would have been allowed 12 yd.
        client.Received += 100;
        client.ClientTime += 3500;
        client.X += 5f;
        List<AntiCheatFinding> findings = client.Send();
        Assert.Contains(findings, f => f.Type == AntiCheatViolation.TimeSync && f.Detail == "client timestamp far ahead of real time");
        Assert.Contains(findings, f => f.Type == AntiCheatViolation.Speed);
    }

    [Fact]
    public void AnHonestStep_AfterASkippedHeartbeat_IsNotScoredAsAStampAhead()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(4, Run);
        client.Step(1500, Run * 1.5f, receivedMs: 1500);
        Assert.Empty(client.All);
    }

    [Fact]
    public void AnAcknowledgementStampedBack_ScoresOnce_NotAgainByItsMovementBlock()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(4, Run);
        var ack = new List<AntiCheatFinding>();
        client.Checks.NotifyAcknowledgement(client.ClientTime, client.Received, ack);
        Assert.Empty(ack);

        // The next ack, and the block it carries, are stamped 1 s back: one PacketTiming finding for the stamp, not two.
        client.Received += 100;
        client.ClientTime -= 1000;
        client.Checks.NotifyAcknowledgement(client.ClientTime, client.Received, ack);
        List<AntiCheatFinding> block = client.Send(WorldOpcode.CmsgForceRunSpeedChangeAck);
        int timing = ack.Count(f => f.Type == AntiCheatViolation.PacketTiming) + block.Count(f => f.Type == AntiCheatViolation.PacketTiming);
        Assert.Equal(1, timing);
    }

    [Fact]
    public void ABlockStampedBack_WithNoAckScoredFirst_StillScoresItsOwnRegression()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(4, Run);
        client.Received += 100;
        client.ClientTime -= 1000;
        Assert.Contains(client.Send(), f => f.Type == AntiCheatViolation.PacketTiming);
    }

    [Fact]
    public void AnOrdinaryStep_StampedBehindThePreviousBlock_IsNotScored()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(4, Run);
        client.Received += 500;
        client.ClientTime -= 100;
        client.X += 1;
        Assert.DoesNotContain(client.Send(), f => f.Type is AntiCheatViolation.Teleport or AntiCheatViolation.Speed);
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
    public void ASilenceOverTheBaselineGap_IsNotAFreeBaseline_TheBlinkAfterItIsScored()
    {
        // 3.001 s of silence on the receive clock with only 500 ms on the client's clock: the step is judged, not trusted.
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(4, Run);
        Assert.True(Has(client.Step(500, 80, receivedMs: 3001), AntiCheatViolation.Teleport));

        // Packets held 3.1 s with the client's clock running through the silence: the budget is those 3.1 s (about 22 yards at run speed
        // plus the teleport distance), so a blink of 80 yards after the hold is still scored.
        var holder = new Client();
        holder.Send(WorldOpcode.MsgMoveStartForward);
        holder.RunFor(4, Run);
        Assert.True(Has(holder.Step(3100, 80), AntiCheatViolation.Teleport));
    }

    [Fact]
    public void ASilenceOverTheBaselineGap_OfAnHonestClient_ScoresNothing()
    {
        // AFK for a minute, then walking on from where it stood: the clock covers the silence, the step is short.
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(4, Run);
        client.Flags = MovementFlags.None;
        client.Step(100, 0.2f, WorldOpcode.MsgMoveStop);
        client.Step(60_000, 0, WorldOpcode.MsgMoveHeartbeat);
        client.Flags = MovementFlags.Forward;
        client.Step(100, 0.5f, WorldOpcode.MsgMoveStartForward);
        client.RunFor(4, Run);
        Assert.Empty(client.All);
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
    public void TimeSkips_AnOversizedOrSpammedSkipScores_AnOrdinaryOneDoesNot_AndAnHonestFreezeAfterItIsClean()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(4, Run);

        // An honest 2 s freeze: the client sent nothing (the receive clock moved too), then reported the skip.
        client.Received += 2000;
        var findings = new List<AntiCheatFinding>();
        client.Checks.NotifyTimeSkip(2000, client.Received, findings);
        Assert.Empty(findings);

        // Its movement clock jumped by the skip; the step that spans it is judged against real time and passes.
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
    public void AnAcknowledgementWhoseTimestampGoesBack_ScoresPacketTiming_EvenRightAfterATimeSkip()
    {
        var checks = new MovementAntiCheat(new AntiCheatOptions());
        var findings = new List<AntiCheatFinding>();
        checks.NotifyAcknowledgement(100_000, 10_000, findings);
        checks.NotifyAcknowledgement(101_000, 11_000, findings);
        Assert.Empty(findings);
        checks.NotifyAcknowledgement(90_000, 12_000, findings);
        Assert.True(Has(findings, AntiCheatViolation.PacketTiming));

        // A stalled client's clock only runs forward: a skip is no licence for an acknowledgement to go back.
        var skipped = new MovementAntiCheat(new AntiCheatOptions());
        var after = new List<AntiCheatFinding>();
        skipped.NotifyAcknowledgement(100_000, 10_000, after);
        skipped.NotifyTimeSkip(1500, 10_100, after);
        Assert.Empty(after);
        skipped.NotifyAcknowledgement(99_000, 10_200, after);
        Assert.True(Has(after, AntiCheatViolation.PacketTiming));

        // An acknowledgement after an honest freeze, its clock ahead by the skip, is clean.
        var honest = new MovementAntiCheat(new AntiCheatOptions());
        var clean = new List<AntiCheatFinding>();
        honest.NotifyAcknowledgement(100_000, 10_000, clean);
        honest.NotifyTimeSkip(1500, 11_600, clean);
        honest.NotifyAcknowledgement(101_700, 11_700, clean);
        Assert.Empty(clean);
    }

    // --- time skips buy no baseline -------------------------------------------------------------------------------

    [Fact]
    public void TimeSkip_ThenABlink_IsScoredAsTeleport()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(4, Run);
        float cleanX = client.X;
        var skip = new List<AntiCheatFinding>();
        client.Checks.NotifyTimeSkip(1, client.Received, skip);
        Assert.Empty(skip);

        List<AntiCheatFinding> blink = client.Step(500, 100);
        Assert.True(Has(blink, AntiCheatViolation.Teleport));
        Assert.True(client.Checks.TryGetLastValid(out float x, out _, out _, out _));
        Assert.Equal(cleanX, x); // the blink is not the rubberband target
    }

    [Fact]
    public void TimeSkip_LagZero_GrantsNothing()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(4, Run);
        var skip = new List<AntiCheatFinding>();
        client.Checks.NotifyTimeSkip(0, client.Received, skip);
        Assert.Empty(skip);
        Assert.True(Has(client.Step(500, 100), AntiCheatViolation.Teleport));
    }

    [Fact]
    public void TenCheapSkipsInTenSeconds_EachFollowedByABlink_AllScore()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(4, Run);
        var skips = new List<AntiCheatFinding>();
        int teleports = 0;
        for (int i = 0; i < 10; i++)
        {
            client.Checks.NotifyTimeSkip(1, client.Received, skips);
            if (Has(client.Step(900, 100), AntiCheatViolation.Teleport))
            {
                teleports++;
            }
        }

        Assert.Empty(skips); // ten in ten seconds is under the spam threshold: only the blinks score
        Assert.Equal(10, teleports);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TimeSkip_ThenAnHonestRun_IsClean(bool clockJumped)
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(4, Run);

        // A 2000 ms freeze, then the next heartbeat 2100 ms after the last one by the receive clock. Whether or not the
        // client's clock included the skip, and whether or not it simulated movement meanwhile, wall time bounds the step.
        client.Received += 2000;
        var skip = new List<AntiCheatFinding>();
        client.Checks.NotifyTimeSkip(2000, client.Received, skip);
        Assert.Empty(skip);
        client.Step(clockJumped ? 2100u : 100u, Run * 2.1f, receivedMs: 100);
        client.RunFor(30, Run);
        Assert.Empty(client.All);
    }

    [Fact]
    public void SpeedHack_WithACheapSkipEvery4s_StillFires()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);

        // The whole client runs 1.5x for 60 s, and reports a 1 ms skip every eight heartbeats (4 s real) to keep the
        // independent clock from ever filling its window.
        var skips = new List<AntiCheatFinding>();
        for (int i = 0; i < 120; i++)
        {
            if (i % 8 == 7)
            {
                client.Checks.NotifyTimeSkip(1, client.Received, skips);
            }

            client.ClientTime += 750;
            client.Received += 500;
            client.X += Run * 0.75f;
            client.Send();
        }

        Assert.Empty(skips);
        Assert.Contains(client.All, f => f.Type == AntiCheatViolation.TimeSync && f.Detail == "client clock runs fast (independent clock)");
    }

    [Fact]
    public void ForgedSkip_WithoutASilence_IsCreditedOnlyTheJitter_SoItsClockJumpStillFires()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);

        // An honest-speed clock that jumps 3 s ahead every 4 s, each jump covered by a 3000 ms skip that no silence backs.
        var skips = new List<AntiCheatFinding>();
        for (int i = 0; i < 120; i++)
        {
            if (i % 8 == 7)
            {
                client.Checks.NotifyTimeSkip(3000, client.Received, skips);
                client.ClientTime += 3000;
            }

            client.ClientTime += 500;
            client.Received += 500;
            client.X += Run * 0.5f;
            client.Send();
        }

        Assert.Empty(skips);
        Assert.Contains(client.All, f => f.Type == AntiCheatViolation.TimeSync);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TimeSkip_TheSessionsPreviousPacket_NarrowsTheSilenceTheSkipIsSupportedBy(bool sessionStamp)
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(4, Run);

        // No movement for 2 s, but the session kept receiving other packets (chat, a cast) until 10 ms before the skip. A
        // 3000 ms skip is then followed by a step that moved 2 s of running with only 100 ms on the client's own clock.
        client.Received += 2000;
        var skip = new List<AntiCheatFinding>();
        if (sessionStamp)
        {
            client.Checks.NotifyTimeSkip(3000, client.Received, client.Received - 10, skip);
        }
        else
        {
            client.Checks.NotifyTimeSkip(3000, client.Received, skip);
        }

        Assert.Empty(skip);
        client.Step(100, Run * 2f, receivedMs: 100);

        // With the session's stamp the silence was 10 ms, so the skip supports 10 ms and the step reads as too fast; measured
        // from the last movement alone, the 2 s silence supports it.
        Assert.Equal(sessionStamp, Has(client.All, AntiCheatViolation.Speed));
        Assert.False(Has(client.All, AntiCheatViolation.Teleport));
        Assert.False(Has(client.All, AntiCheatViolation.TimeSync));
    }

    [Fact]
    public void ACheapSkipAtTheSpamThresholdCadence_BuysAboutItsOwnLength_SoAThreeTimesRunSpanningStepStillScores()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(4, Run);

        // An honest clock, a 1 ms skip every 1010 ms (nine in ten seconds: under the spam threshold), the spanning step
        // moving at 3x run speed (10.6 yd in 505 ms: under the teleport limit, and inside what the unbounded skip budget
        // allowed). The step after it is an ordinary one.
        var skips = new List<AntiCheatFinding>();
        int speedFindings = 0;
        for (int i = 0; i < 9; i++)
        {
            client.Step(505, Run * 0.505f);
            client.Received += 505;
            client.Checks.NotifyTimeSkip(1, client.Received, skips);
            client.ClientTime += 505;
            client.X += Run * 3f * 0.505f;
            if (Has(client.Send(), AntiCheatViolation.Speed))
            {
                speedFindings++;
            }
        }

        Assert.Empty(skips);
        Assert.Equal(9, speedFindings);
        Assert.False(Has(client.All, AntiCheatViolation.Teleport));
    }

    [Fact]
    public void AHeartbeatSizedSkipAfterRealSilence_OnAnHonestClock_BuysNothingBeyondRealTime_SoAStepJustOverTheNoSkipLimitStillScores()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(4, Run);

        // Ordinary heartbeat cadence: both clocks advance 505 ms, and the client sends skip(505) that the 505 ms of silence
        // supports. Run=7, so no skip allows 7 * 1.1 * 0.505 + 2 = 5.89 yd; the old budget (real time plus the 1000 ms slack)
        // allowed 9.78 yd. A 9.7 yd step (2.7x run) must score.
        var skips = new List<AntiCheatFinding>();
        client.Received += 505;
        client.Checks.NotifyTimeSkip(505, client.Received, skips);
        client.ClientTime += 505;
        client.X += 9.7f;
        Assert.True(Has(client.Send(), AntiCheatViolation.Speed));
        Assert.Empty(skips);
        Assert.False(Has(client.All, AntiCheatViolation.Teleport));
    }

    [Fact]
    public void AHeartbeatSizedSkip_OnAClockThatLeftTheSilenceOut_StillMakesTheClientWhole()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(4, Run);

        // 505 ms of real silence, only 50 ms on the client's clock, a skip of 505: budget = min(50 + 505, 505) = 505 ms, so a
        // 4.5 yd step (under 5.89 yd) is clean, while without the skip the 50 ms budget (2.4 yd) would score it.
        var skips = new List<AntiCheatFinding>();
        client.Received += 505;
        client.Checks.NotifyTimeSkip(505, client.Received, skips);
        client.ClientTime += 50;
        client.X += 4.5f;
        Assert.False(Has(client.Send(), AntiCheatViolation.Speed));
        Assert.Empty(skips);
    }

    [Fact]
    public void AFastClockWithSkipsOfAtLeast250msJustUnderTheSpamCadence_StillFires()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);

        // The whole client runs 1.5x. Every second heartbeat (1010 ms real: ten skips fit one window, so the spam finding
        // never fires) is preceded by a 250 ms skip that 505 ms of silence supports. Credited without a cap, every other
        // pair would read as honest and the ratio would stay at 1.25, below the trip.
        var skips = new List<AntiCheatFinding>();
        for (int i = 0; i < 120; i++)
        {
            client.Received += 505;
            if (i % 2 == 1)
            {
                client.Checks.NotifyTimeSkip(250, client.Received, skips);
            }

            client.ClientTime += 758;
            client.X += Run * 0.758f;
            client.Send();
        }

        Assert.Empty(skips);
        Assert.Contains(client.All, f => f.Type == AntiCheatViolation.TimeSync && f.Detail == "client clock runs fast (independent clock)");
    }

    [Fact]
    public void MaxUintSkip_GrantsNoGrace()
    {
        var client = new Client();
        client.Send(WorldOpcode.MsgMoveStartForward);
        client.RunFor(4, Run);
        var findings = new List<AntiCheatFinding>();
        client.Checks.NotifyAcknowledgement(500_000, client.Received, findings);
        client.Checks.NotifyTimeSkip(uint.MaxValue, client.Received, findings);
        Assert.Contains(findings, f => f.Type == AntiCheatViolation.PacketTiming && f.Detail == "impossible move time skip");
        Assert.Contains(findings, f => f.Type == AntiCheatViolation.TimeSync); // the oversized finding stays

        // Not the 999 ms grace a wrapped (lag + 1000) gave: an acknowledgement going back is scored...
        findings.Clear();
        client.Checks.NotifyAcknowledgement(498_000, client.Received + 100, findings);
        Assert.True(Has(findings, AntiCheatViolation.PacketTiming));

        // ...and the next step is judged, not trusted.
        Assert.True(Has(client.Step(500, 100), AntiCheatViolation.Teleport));
    }

    [Fact]
    public void SkipAcrossReceiveClockWrap_WindowsCorrect()
    {
        var honest = new Client { Received = 0xFFFF_F400 };
        honest.Send(WorldOpcode.MsgMoveStartForward);
        honest.RunFor(4, Run); // the receive clock is now 0xFFFF_FBD0
        honest.Received += 1500; // a freeze across the wrap
        var skip = new List<AntiCheatFinding>();
        honest.Checks.NotifyTimeSkip(1500, honest.Received, skip);
        honest.ClientTime += 1500;
        honest.RunFor(6, Run);
        Assert.Empty(skip);
        Assert.Empty(honest.All);

        var cheat = new Client { Received = 0xFFFF_FF00 };
        cheat.Send(WorldOpcode.MsgMoveStartForward);
        cheat.Checks.NotifyTimeSkip(1, cheat.Received, skip);
        Assert.True(Has(cheat.Step(500, 100), AntiCheatViolation.Teleport)); // received at 0x000001F4
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
    public void SwimFlyingInTheAir_IsNotSwimmingOutOfWater_WhenTheServerGrantedFlight_AndIsWhenItDidNot()
    {
        // .cheat fly: the client swims in the air (vmangos MovementAnticheat.cpp:796-806 exempts the swim-fly flags for PLAYER_CHEAT_FLY only).
        MovementFlags flying = MovementFlags.Forward | MovementFlags.Levitating | MovementFlags.Swimming | MovementFlags.Moved | MovementFlags.Flying;
        var granted = new Client { Terrain = new FakeTerrain { Liquid = false }, Flags = flying, Granted = MovementFlags.Levitating | MovementFlags.Flying };
        granted.Send(WorldOpcode.MsgMoveStartSwim);
        granted.RunFor(6, Run);
        Assert.Empty(granted.All);

        var notGranted = new Client { Terrain = new FakeTerrain { Liquid = false }, Flags = flying };
        notGranted.Send(WorldOpcode.MsgMoveStartSwim);
        notGranted.RunFor(6, Run);
        Assert.True(Has(notGranted.All, AntiCheatViolation.Physics));
        Assert.True(Has(notGranted.All, AntiCheatViolation.Flag));
    }

    [Fact]
    public void ClimbingIntoTheAir_WithServerControlled_IsNotScored_AndIsWithoutIt()
    {
        // .cheat wallclimb sets UNIT_FLAG_SERVER_CONTROLLED, which vmangos lists in NO_WALL_CLIMB_CHECK_UNIT_FLAGS (MovementAnticheat.cpp:1170).
        var climber = new Client { Terrain = new FakeTerrain { Floor = 10 }, ServerControlled = true };
        climber.Send(WorldOpcode.MsgMoveStartForward);
        climber.Z += 6;
        Assert.Empty(climber.Step(500, 1));

        var plain = new Client { Terrain = new FakeTerrain { Floor = 10 } };
        plain.Send(WorldOpcode.MsgMoveStartForward);
        plain.Z += 6;
        Assert.True(Has(plain.Step(500, 1), AntiCheatViolation.Vertical));
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
