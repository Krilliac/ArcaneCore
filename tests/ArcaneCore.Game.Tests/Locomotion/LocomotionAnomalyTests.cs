using ArcaneCore.Game.Combat;
using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Game.Tests.CombatCore;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Locomotion;

/// <summary>
/// The H1 locomotion anomaly keys, each proven both ways through the real movement observer pipeline:
/// <c>move/speed-violation</c> (FlagAuthorityObserver), <c>move/fall-without-landing</c> (FallObserver),
/// <c>move/non-finite</c> and <c>move/under-map</c> (UndermapObserver). The ledger is shared static state, so every test
/// captures only the notices whose subject carries its own unique player guid.
/// <para>
/// Clock note: the observers compare <c>incoming.Time</c> with the stored <c>previous.Time</c>. These tests keep both on the world clock
/// (the stored block is stamped with <c>NowMs</c> by ApplyClientMovement, and the helpers stamp the incoming block with <c>NowMs</c> too).
/// </para>
/// </summary>
public sealed class LocomotionAnomalyTests
{
    private const string SpeedKey = "move/speed-violation";
    private const string FallKey = "move/fall-without-landing";
    private const string NonFiniteKey = "move/non-finite";
    private const string UnderMapKey = "move/under-map";

    private static readonly MapContent Content = new(
        [new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", "")],
        [], [], [], []);

    private static AnomalyCapture CaptureFor(uint guid)
        => AnomalyLedger.Shared.Capture(n => n.Subject.Contains($"(guid {guid})", StringComparison.Ordinal));

    private static (WorldRuntime World, Player Player, FakeSession Session) Setup(uint guid, bool playerbot = false)
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        world.UseManualClock();
        var session = new FakeSession((int)guid) { IsPlayerbot = playerbot };
        Player player = TestWorld.CreatePlayer(guid, 100, 100, session);
        world.AddPlayer(player);
        return (world, player, session);
    }

    private static void Client(WorldRuntime world, Player player, MovementInfo incoming, WorldOpcode opcode = WorldOpcode.MsgMoveHeartbeat)
    {
        MovementInfo previous = player.Movement;
        var context = new MovementObserverContext(player, world, opcode);
        MovementObservers.Before(context, in previous, ref incoming);
        player.ApplyClientMovement(incoming, world.NowMs);
        MovementObservers.After(context, in previous);
    }

    private static MovementInfo At(WorldRuntime world, float x, float y = 100, float z = 83.5f, MovementFlags flags = MovementFlags.None)
        => new() { Flags = flags, Time = world.NowMs, X = x, Y = y, Z = z, Orientation = 0 };

    /// <summary>Stand at (100, 100) with a stored block stamped at world time 1000 ms.</summary>
    private static void Prime(WorldRuntime world, Player player)
    {
        world.RunTick(1000);
        Client(world, player, At(world, 100));
    }

    /// <summary>Wait <paramref name="dtMs"/>, then report a hop to x = 100 + <paramref name="dx"/>.</summary>
    private static void Hop(WorldRuntime world, Player player, float dx, uint dtMs)
    {
        world.RunTick(dtMs);
        Client(world, player, At(world, 100 + dx));
    }

    // --- move/speed-violation -------------------------------------------------------------------------------------------

    [Fact]
    public void SpeedViolation_AHundredYardHopInOneSecond_IsReportedOnce_AsAWarning()
    {
        (WorldRuntime world, Player player, _) = Setup(4_101_001);
        using AnomalyCapture capture = CaptureFor(4_101_001);
        Prime(world, player);

        Hop(world, player, 100, 1000);

        Assert.Equal(1, capture.Count(SpeedKey));
        Assert.Equal(AnomalySeverity.Warning, Assert.Single(capture.Notices, n => n.Key == SpeedKey).Severity);
    }

    [Fact]
    public void SpeedViolation_ForAServerManagedBot_IsReportedAtInfoSeverity()
    {
        (WorldRuntime world, Player player, _) = Setup(4_101_002, playerbot: true);
        using AnomalyCapture capture = CaptureFor(4_101_002);
        Prime(world, player);

        Hop(world, player, 100, 1000);

        Assert.Equal(AnomalySeverity.Info, Assert.Single(capture.Notices, n => n.Key == SpeedKey).Severity);
    }

    [Theory]
    [InlineData(4_101_003u, 25.0f, false)] // run speed 7 * 1 s * 1.5 + 15 yd slack = 25.5 yd allowed
    [InlineData(4_101_004u, 26.0f, true)]
    public void SpeedViolation_TheToleranceBoundary_IsAtSevenAndAHalfYardsPlusSlack(uint guid, float dx, bool reported)
    {
        (WorldRuntime world, Player player, _) = Setup(guid);
        using AnomalyCapture capture = CaptureFor(guid);
        Prime(world, player);

        Hop(world, player, dx, 1000);

        Assert.Equal(reported, capture.Any(SpeedKey));
    }

    [Fact]
    public void SpeedViolation_OrdinaryRunning_ReportsNothing()
    {
        (WorldRuntime world, Player player, _) = Setup(4_101_005);
        using AnomalyCapture capture = CaptureFor(4_101_005);
        Prime(world, player);

        for (int i = 1; i <= 20; i++)
        {
            world.RunTick(500);
            Client(world, player, At(world, 100 + (3.5f * i), flags: MovementFlags.Forward)); // 7 yd/s, one heartbeat per half second
        }

        Assert.False(capture.Any(SpeedKey), capture.Describe());
    }

    [Fact]
    public void SpeedViolation_ASustainedViolation_IsReportedOncePerEpisode()
    {
        // An episode ends after EpisodeEndsAfterLegitPackets legitimate packets in a row, or once EpisodeQuietMs have passed since the last
        // violation; a cheater who keeps teleporting inside one episode is one report, not one per packet or per second.
        (WorldRuntime world, Player player, _) = Setup(4_101_006);
        using AnomalyCapture capture = CaptureFor(4_101_006);
        Prime(world, player);

        float x = 0;
        void Violate(uint dtMs = 100)
        {
            x += 100;
            Hop(world, player, x, dtMs);
        }

        void Behave(int packets)
        {
            for (int i = 0; i < packets; i++)
            {
                world.RunTick(100);
                Client(world, player, At(world, 100 + x));
            }
        }

        for (int i = 0; i < 5; i++)
        {
            Violate();
        }

        world.RunTick(1500); // longer than the old one-second cooldown, still the same episode
        Violate(100);
        Assert.Equal(1, capture.Count(SpeedKey));

        Behave(FlagAuthorityObserver.EpisodeEndsAfterLegitPackets - 1);
        Violate(); // a violation resets the run of legitimate packets: still the same episode
        Assert.Equal(1, capture.Count(SpeedKey));

        Behave(FlagAuthorityObserver.EpisodeEndsAfterLegitPackets);
        Violate();
        Assert.Equal(2, capture.Count(SpeedKey)); // a full run of legitimate packets ended the episode

        Violate(FlagAuthorityObserver.EpisodeQuietMs); // no legitimate packet at all, but the quiet period passed
        Assert.Equal(3, capture.Count(SpeedKey));
    }

    [Fact]
    public void SpeedViolation_AGameMaster_IsNeverReported()
    {
        (WorldRuntime world, Player player, _) = Setup(4_101_007);
        using AnomalyCapture capture = CaptureFor(4_101_007);
        Prime(world, player);
        player.SetGameMaster(true);

        Hop(world, player, 500, 100);

        Assert.False(capture.Any(SpeedKey), capture.Describe());
    }

    [Fact]
    public void SpeedViolation_DuringAChaosProbe_IsSuppressed()
    {
        (WorldRuntime world, Player player, _) = Setup(4_101_008);
        using AnomalyCapture capture = CaptureFor(4_101_008);
        Prime(world, player);
        player.Locomotion.SuppressAnomalies = true;

        Hop(world, player, 500, 100);

        Assert.False(capture.Any(SpeedKey), capture.Describe());
    }

    [Fact]
    public void SpeedViolation_WhileAKnockbackIsPending_IsSilent_AndTheAckOpensATwoSecondWindow()
    {
        (WorldRuntime world, Player player, _) = Setup(4_101_009);
        using AnomalyCapture capture = CaptureFor(4_101_009);
        Prime(world, player);
        var info = new KnockbackInfo(1f, 0f, 25f, -8f);
        uint counter = player.NextMovementCounter();
        player.Locomotion.Pending.Push(counter, MovementChangeType.KnockBack, apply: true, knockback: info);

        Hop(world, player, 80, 100); // pending order: the client is flung by the server
        Assert.False(capture.Any(SpeedKey), capture.Describe());

        // The ack (KnockbackService.Acknowledge, Knockback.cs:122) stamps LastTeleportOrKnockbackMs and the window is [0, 2000) ms.
        var ack = new MovementInfo { JumpCosAngle = info.VCos, JumpSinAngle = info.VSin, JumpXySpeed = info.SpeedXY, JumpZSpeed = info.SpeedZ };
        Assert.NotNull(KnockbackService.Acknowledge(player, counter, in ack));
        world.RunTick(500);
        Client(world, player, At(world, 180 + 40)); // 40 yd in 500 ms is above the 20.25 yd allowance, but the ack opened the window
        Assert.False(capture.Any(SpeedKey), "500 ms after the ack is inside the window: " + capture.Describe());
    }

    [Theory]
    [InlineData(4_101_010u, 1999u, false)]
    [InlineData(4_101_011u, 2000u, true)]
    public void SpeedViolation_TheKnockbackWindow_ClosesAtTwoThousandMilliseconds(uint guid, uint elapsedMs, bool reported)
    {
        (WorldRuntime world, Player player, _) = Setup(guid);
        using AnomalyCapture capture = CaptureFor(guid);
        Prime(world, player);
        player.Locomotion.LastTeleportOrKnockbackMs = world.NowMs;

        Hop(world, player, 80, elapsedMs); // 80 yd over ~2 s is above 7 * 2 * 1.5 + 15 = 36 yd

        Assert.Equal(reported, capture.Any(SpeedKey));
    }

    [Fact]
    public void SpeedViolation_OnATaxiFlightOrAShip_IsNotJudged()
    {
        (WorldRuntime world, Player player, _) = Setup(4_101_012);
        using AnomalyCapture capture = CaptureFor(4_101_012);
        Prime(world, player);

        player.UnitFlags |= UnitFlags.TaxiFlight;
        Hop(world, player, 500, 100);
        player.UnitFlags &= ~UnitFlags.TaxiFlight;
        Assert.False(capture.Any(SpeedKey), capture.Describe());

        world.RunTick(100);
        Client(world, player, At(world, 1000, flags: MovementFlags.OnTransport));
        Assert.False(capture.Any(SpeedKey), capture.Describe());
    }

    // --- Blink / Charge / leap: the exemptions the speed check relies on -------------------------------------------------

    private sealed class TeleportFixture : IDisposable
    {
        public TeleportFixture(uint guid)
        {
            World = TestWorld.CreateRuntime();
            World.UseManualClock();
            WorldMaps.Of(World).Load(Content);
            Teleports = new TeleportService(World, _ => { }, _ => { });
            // The daemon's LocomotionFeature registers exactly this probe (src/ArcaneCore.World/Locomotion/LocomotionFeature.cs:34).
            LocomotionEnvironment.Register(World, new LocomotionEnvironment(new LocomotionOptions(), p => Teleports.IsBeingTeleported(p)));
            Player = TestWorld.CreatePlayer(guid, 100, 100, new FakeSession((int)guid));
            World.AddPlayer(Player);
        }

        public WorldRuntime World { get; }

        public TeleportService Teleports { get; }

        public Player Player { get; }

        public void Dispose() => World.Dispose();
    }

    [Fact]
    public void Blink_ANearTeleportOfBlinkLength_IsSilent_BeforeAndAfterTheAck_ButTheSameHopWithoutOneIsReported()
    {
        // Production path of Blink / Leap: SpellSystem Leap/TeleportUnits -> WorldSpellTeleportSink -> TeleportService.TeleportTo (near).
        // The teleport is NOT stamped in LastTeleportOrKnockbackMs (only Knockback.cs does that); what keeps the check quiet is
        // IsBeingTeleported while the order is pending and ArriveNear relocating the stored block to the destination at the ack.
        using var f = new TeleportFixture(4_102_001);
        using AnomalyCapture capture = CaptureFor(4_102_001);
        Prime(f.World, f.Player);

        Assert.True(f.Teleports.TeleportTo(f.Player, 0, 125, 100, 83.5f, 0f)); // 25 yd, beyond the 15.5 yd a 50 ms packet may cover
        f.World.RunTick(50);
        Client(f.World, f.Player, At(f.World, 125)); // a packet that races the order
        Assert.False(capture.Any(SpeedKey), "while the teleport is pending: " + capture.Describe());

        Assert.True(f.Teleports.HandleTeleportAck(f.Player, f.Player.Guid.Value));
        f.World.RunTick(100);
        Client(f.World, f.Player, At(f.World, 126));
        Assert.False(capture.Any(SpeedKey), "after the ack: " + capture.Describe());
        Assert.Equal(126f, f.Player.X);

        // Control: the identical 25 yd in 100 ms with no teleport behind it is reported, so the silence above is the exemption's doing.
        f.World.RunTick(100);
        Client(f.World, f.Player, At(f.World, 126 + 25));
        Assert.Equal(1, capture.Count(SpeedKey));
    }

    [Fact]
    public void Charge_AServerSpline_ExemptsTheClientPackets_UntilItEnds()
    {
        using var f = new TeleportFixture(4_102_002);
        using AnomalyCapture capture = CaptureFor(4_102_002);
        Prime(f.World, f.Player);

        Assert.True(ForcedMovement.LaunchSpline(f.Player, [new Vector3(125, 100, 83.5f)], ForcedMovement.ChargeSpeed));
        Assert.NotNull(f.Player.Locomotion.ActiveSpline);
        Assert.Equal(125f, f.Player.X); // the server moved the player to the end of the charge at once

        f.World.RunTick(100);
        Client(f.World, f.Player, At(f.World, 100)); // a stale client position 25 yd behind the stored one, during the charge
        Assert.False(capture.Any(SpeedKey), "during the spline: " + capture.Describe());

        f.World.RunTick(2000);
        Assert.Null(f.Player.Locomotion.ActiveSpline);
        Client(f.World, f.Player, At(f.World, 125));
        f.World.RunTick(100);
        Client(f.World, f.Player, At(f.World, 100)); // the same stale hop once the spline is gone
        Assert.Equal(1, capture.Count(SpeedKey));
    }

    // --- move/fall-without-landing ---------------------------------------------------------------------------------------

    private static MovementInfo Falling(WorldRuntime world, uint fallTimeMs, MovementFlags extra = MovementFlags.None)
    {
        MovementInfo info = At(world, 100, flags: MovementFlags.Jumping | MovementFlags.FallingFar | extra);
        info.FallTime = fallTimeMs;
        return info;
    }

    private static void Advance(WorldRuntime world, uint ms)
    {
        for (uint done = 0; done < ms; done += 1000)
        {
            world.RunTick(Math.Min(1000, ms - done));
        }
    }

    [Fact]
    public void FallWithoutLanding_ThirtySecondsInTheAir_IsReportedOnce()
    {
        (WorldRuntime world, Player player, _) = Setup(4_103_001);
        using AnomalyCapture capture = CaptureFor(4_103_001);
        Prime(world, player);
        Client(world, player, Falling(world, 0));

        Advance(world, FallObserver.MaxUnresolvedFallMs);
        Client(world, player, Falling(world, FallObserver.MaxUnresolvedFallMs));
        Client(world, player, Falling(world, FallObserver.MaxUnresolvedFallMs + 100));
        Advance(world, 5000);
        Client(world, player, Falling(world, FallObserver.MaxUnresolvedFallMs + 5000));

        Assert.Equal(1, capture.Count(FallKey));
    }

    [Fact]
    public void FallWithoutLanding_JustUnderThirtySeconds_IsSilent_AndALandingForgetsTheFall()
    {
        (WorldRuntime world, Player player, _) = Setup(4_103_002);
        using AnomalyCapture capture = CaptureFor(4_103_002);
        Prime(world, player);
        Client(world, player, Falling(world, 0));

        Advance(world, FallObserver.MaxUnresolvedFallMs - 1);
        Client(world, player, Falling(world, FallObserver.MaxUnresolvedFallMs - 1));
        Assert.False(capture.Any(FallKey), capture.Describe());

        Client(world, player, At(world, 100), WorldOpcode.MsgMoveFallLand); // lands
        Assert.False(player.Locomotion.IsFalling);
        Advance(world, 60_000);
        Client(world, player, At(world, 100, flags: MovementFlags.Forward));
        Assert.False(capture.Any(FallKey), capture.Describe());
    }

    [Fact]
    public void FallWithoutLanding_ALongFlightOverWaterOrAsAGameMaster_IsNotAFall()
    {
        (WorldRuntime world, Player player, _) = Setup(4_103_003);
        using AnomalyCapture capture = CaptureFor(4_103_003);
        Prime(world, player);

        Client(world, player, Falling(world, 0, MovementFlags.Swimming));
        Advance(world, 40_000);
        Client(world, player, Falling(world, 40_000, MovementFlags.Swimming));
        Assert.False(capture.Any(FallKey), "swimming: " + capture.Describe());

        player.SetGameMaster(true);
        Client(world, player, Falling(world, 0));
        Advance(world, 40_000);
        Client(world, player, Falling(world, 40_000));
        Assert.False(capture.Any(FallKey), "game master: " + capture.Describe());
    }

    // --- move/non-finite (UndermapObserver.BeforeApply) ------------------------------------------------------------------

    private static void OfferToObservers(WorldRuntime world, Player player, MovementInfo incoming)
    {
        MovementInfo previous = player.Movement;
        var context = new MovementObserverContext(player, world, WorldOpcode.MsgMoveHeartbeat);
        MovementObservers.Before(context, in previous, ref incoming); // never stored: the handler would have dropped it
    }

    [Fact]
    public void NonFinite_ANanCoordinate_IsReported_AtMostOncePerServerMillisecond()
    {
        (WorldRuntime world, Player player, _) = Setup(4_104_001);
        using AnomalyCapture capture = CaptureFor(4_104_001);
        Prime(world, player);

        OfferToObservers(world, player, At(world, float.NaN));
        OfferToObservers(world, player, At(world, 100, float.PositiveInfinity));
        Assert.Equal(1, capture.Count(NonFiniteKey)); // same millisecond: one report

        world.RunTick(1);
        OfferToObservers(world, player, new MovementInfo { Time = world.NowMs, X = 100, Y = 100, Z = 83.5f, Orientation = float.NaN });
        Assert.Equal(2, capture.Count(NonFiniteKey));
        Assert.All(capture.Notices.Where(n => n.Key == NonFiniteKey), n => Assert.Equal(AnomalySeverity.Error, n.Severity));
    }

    [Fact]
    public void NonFinite_FiniteBlocks_AndAChaosProbe_ReportNothing()
    {
        (WorldRuntime world, Player player, _) = Setup(4_104_002);
        using AnomalyCapture capture = CaptureFor(4_104_002);
        Prime(world, player);

        OfferToObservers(world, player, At(world, 101));
        player.Locomotion.SuppressAnomalies = true;
        OfferToObservers(world, player, At(world, float.NaN));

        Assert.False(capture.Any(NonFiniteKey), capture.Describe());
    }

    // --- move/under-map --------------------------------------------------------------------------------------------------

    private static (WorldRuntime World, Player Player) VoidSetup(uint guid)
    {
        (WorldRuntime world, _, _, TestCombatHooks hooks) = CombatTestKit.CreateWorld();
        WorldMaps.Of(world).Load(Content);
        world.GetMap(0).Combat.Hooks = hooks;
        Player player = CombatTestKit.AddPlayer(world, guid, 100, 100, new FakeSession((int)guid));
        player.MaxHealth = 2000;
        player.Health = 2000;
        return (world, player);
    }

    private static void FallTo(WorldRuntime world, Player player, float z)
        => Client(world, player, new MovementInfo { Flags = MovementFlags.Jumping | MovementFlags.FallingFar, Time = 1, X = 100, Y = 100, Z = z });

    [Fact]
    public void UnderMap_BelowTheVoidHeight_IsReportedOncePerEpisode_AndAgainAfterRecovering()
    {
        (WorldRuntime world, Player player) = VoidSetup(4_105_001);
        using AnomalyCapture capture = CaptureFor(4_105_001);

        FallTo(world, player, -501f);
        FallTo(world, player, -600f);
        FallTo(world, player, -700f);
        Assert.Equal(1, capture.Count(UnderMapKey)); // the packets keep coming while the client falls: still one episode
        Assert.Contains("below-void-height", Assert.Single(capture.Notices, n => n.Key == UnderMapKey).Detail);

        // Back on the map (no terrain is loaded in this world, so only the void rule can fire): the episode ends.
        Client(world, player, new MovementInfo { Time = 2, X = 100, Y = 100, Z = 83.5f });
        FallTo(world, player, -501f);
        Assert.Equal(2, capture.Count(UnderMapKey));
    }

    [Fact]
    public void UnderMap_Report_CarriesTheWorldItWasMadeIn()
    {
        (WorldRuntime world, Player player) = VoidSetup(4_109_001);
        using AnomalyCapture capture = CaptureFor(4_109_001);

        FallTo(world, player, -501f);

        AnomalyNotice notice = Assert.Single(capture.Notices, n => n.Key == UnderMapKey);
        Assert.Same(world.Identity, notice.Origin);
    }

    [Fact]
    public void UnderMap_AtAndAboveTheVoidHeight_AndForAGameMaster_IsSilent()
    {
        (WorldRuntime world, Player player) = VoidSetup(4_105_002);
        using AnomalyCapture capture = CaptureFor(4_105_002);

        FallTo(world, player, -499f);
        FallTo(world, player, UndermapObserver.VoidHeight); // the rule is "below": exactly -500 is still the map
        Assert.False(capture.Any(UnderMapKey), capture.Describe());

        player.SetGameMaster(true);
        FallTo(world, player, -900f);
        Assert.False(capture.Any(UnderMapKey), "game master: " + capture.Describe());
    }

    [Fact]
    public void UnderMap_BelowTheTerrainFloor_IsReported_AndOnlyBeyondFiftyYards()
    {
        using var kit = new HazardTestKit(liquid: null);
        Player deep = kit.AddPlayer(4_105_003, 20f, out _);
        Player shallow = kit.AddPlayer(4_105_004, 20f, out _);
        Player swimmer = kit.AddPlayer(4_105_005, 20f, out _);
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == UnderMapKey && n.Subject.Contains("(guid 410500", StringComparison.Ordinal));

        void Move(Player p, float z, MovementFlags flags = MovementFlags.None)
        {
            MovementInfo previous = p.Movement;
            var incoming = new MovementInfo { Flags = flags, Time = 1, X = HazardTestKit.X, Y = HazardTestKit.Y, Z = z };
            var context = new MovementObserverContext(p, kit.Kit.World, WorldOpcode.MsgMoveHeartbeat);
            MovementObservers.Before(context, in previous, ref incoming);
            p.ApplyClientMovement(incoming, 1);
            MovementObservers.After(context, in previous);
        }

        Move(shallow, HazardTestKit.Ground - 49f);                      // 49 yd under the floor: inside the tolerance
        Move(swimmer, HazardTestKit.Ground - 60f, MovementFlags.Swimming); // swimming under a floor is not under the map
        Move(deep, HazardTestKit.Ground - 60f);

        Assert.Equal(1, capture.Count(UnderMapKey));
        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Contains("(guid 4105003)", notice.Subject);
        Assert.Contains("below-terrain", notice.Detail);
    }
}
