using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Code = ArcaneCore.World.Playerbots.PlayerbotMotionAnomalies.Code;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>Helpers for the H1 outcome-check tests of the motion, brain and recovery lanes.</summary>
internal static class H1AnomalyKit
{
    /// <summary>
    /// The reports of one bot in this host's world (<see cref="PlayerbotAnomalies.CaptureFor"/>: other tests' bots share the name and guid of
    /// <see cref="PlayerbotMovementControlTests.EnterAsync"/>'s, but their reports name another world).
    /// </summary>
    internal static AnomalyCapture CaptureFor(Player player)
    {
        Assert.NotNull(player.Map);
        return PlayerbotAnomalies.CaptureFor(player);
    }

    internal sealed class ManualClock : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }
}

/// <summary>
/// The outcome checks of <see cref="PlayerbotMotion"/> (<see cref="PlayerbotMotionAnomalies"/>): each is proven to fire on its failing
/// scenario and to stay silent on the matching correct one. Time moves only on the manual world clock.
/// </summary>
public sealed class PlayerbotMotionAnomalyTests
{
    private const float Floor = 83.53f;

    private static string Key(Code code) => PlayerbotMotionAnomalies.Key(code);

    // ---- the pure ground verdict -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(100f, 90f, UnderMapKind.Unknown, 0f, PlayerbotMotionAnomalies.GroundVerdict.Airwalk)] // 10 yd above its floor
    [InlineData(90.5f, 90f, UnderMapKind.Unknown, 0f, PlayerbotMotionAnomalies.GroundVerdict.Ok)] // standing
    [InlineData(95.5f, 90f, UnderMapKind.Unknown, 0f, PlayerbotMotionAnomalies.GroundVerdict.Ok)] // a mesh leg floating inside the tolerance
    [InlineData(80f, -200000f, UnderMapKind.UnderMap, 10f, PlayerbotMotionAnomalies.GroundVerdict.UnderMap)] // 10 yd under the terrain, no floor, no model explains it
    [InlineData(40f, -200000f, UnderMapKind.UnderMap, 50f, PlayerbotMotionAnomalies.GroundVerdict.FarUnderMap)] // 50 yd under
    [InlineData(80f, -200000f, UnderMapKind.Ok, 90f, PlayerbotMotionAnomalies.GroundVerdict.Ok)] // under the terrain inside a WMO or just below its floor (a cave, a city)
    [InlineData(80f, -200000f, UnderMapKind.Unknown, 90f, PlayerbotMotionAnomalies.GroundVerdict.Ok)] // no data, or the model tile is not loaded
    [InlineData(80f, 79f, UnderMapKind.UnderMap, 90f, PlayerbotMotionAnomalies.GroundVerdict.Ok)] // on a floor: the under-map verdict is not consulted
    [InlineData(float.NaN, 79f, UnderMapKind.UnderMap, 90f, PlayerbotMotionAnomalies.GroundVerdict.Ok)]
    internal void Classify_JudgesTheGroundUnderTheBot(float z, float floor, UnderMapKind kind, float depth, PlayerbotMotionAnomalies.GroundVerdict expected)
        => Assert.Equal(expected, PlayerbotMotionAnomalies.Classify(z, floor, new UnderMapVerdict(kind, depth)));

    /// <summary>A model-aware query with a fixed terrain depth that honours the margin it is asked with, as the real one does.</summary>
    private sealed class DepthClassifier(float depth) : IUnderMapClassifier
    {
        public List<float> Margins { get; } = [];

        public UnderMapVerdict ClassifyUnderMap(float x, float y, float z, float minDepthYards = 0f)
        {
            Margins.Add(minDepthYards);
            return depth > minDepthYards ? UnderMapVerdict.Under(depth) : UnderMapVerdict.Ok;
        }
    }

    [Theory]
    [InlineData(6f, PlayerbotMotionAnomalies.GroundVerdict.Ok)]        // navmesh-versus-terrain drift: inside the margin
    [InlineData(10f, PlayerbotMotionAnomalies.GroundVerdict.UnderMap)] // beyond it
    [InlineData(50f, PlayerbotMotionAnomalies.GroundVerdict.FarUnderMap)]
    internal void JudgeGround_PassesTheEightYardMargin_ToTheModelAwareQuery(float depth, PlayerbotMotionAnomalies.GroundVerdict expected)
    {
        var classifier = new DepthClassifier(depth);
        var at = new Vector3(0, 0, 80);

        PlayerbotMotionAnomalies.GroundVerdict verdict = PlayerbotMotionAnomalies.JudgeGround(at, -200000f, modelsEnabled: true, classifier, out _);

        Assert.Equal(expected, verdict);
        Assert.Equal(8f, Assert.Single(classifier.Margins));
    }

    [Fact]
    internal void JudgeGround_DoesNotAskTheQuery_WithAFloorUnderTheBot_OrWithoutModelData()
    {
        var classifier = new DepthClassifier(90f);
        var at = new Vector3(0, 0, 80);

        Assert.Equal(PlayerbotMotionAnomalies.GroundVerdict.Ok, PlayerbotMotionAnomalies.JudgeGround(at, 79f, modelsEnabled: true, classifier, out _));
        Assert.Equal(PlayerbotMotionAnomalies.GroundVerdict.Ok, PlayerbotMotionAnomalies.JudgeGround(at, -200000f, modelsEnabled: false, classifier, out _));
        Assert.Empty(classifier.Margins);
    }

    // ---- reports through the shared front door ---------------------------------------------------------------------------------

    [Fact]
    public async Task GroundVerdicts_AreReported_WithTheirSeverity_AndOkIsSilent()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            using AnomalyCapture cap = H1AnomalyKit.CaptureFor(session.Player!);
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                var at = new Vector3(player.X, player.Y, player.Z);
                var watch = new PlayerbotMotionAnomalies.MotionWatch();
                PlayerbotMotionAnomalies.ReportGround(player, player.Map!, watch, PlayerbotMotionAnomalies.GroundVerdict.Ok, at, 0, 0, 1000);
                Assert.Empty(cap.Notices);

                PlayerbotMotionAnomalies.ReportGround(player, player.Map!, watch, PlayerbotMotionAnomalies.GroundVerdict.Airwalk, at, 0, at.Z - 10, 1000);
                PlayerbotMotionAnomalies.ReportGround(player, player.Map!, watch, PlayerbotMotionAnomalies.GroundVerdict.UnderMap, at, at.Z + 10, -200000, 1000);
                PlayerbotMotionAnomalies.ReportGround(player, player.Map!, watch, PlayerbotMotionAnomalies.GroundVerdict.FarUnderMap, at, at.Z + 60, -200000, 1000);
                return true;
            });
            Assert.Equal(AnomalySeverity.Warning, cap.Notices.Single(n => n.Key == Key(Code.Airwalk)).Severity);
            // under-map is reported once per episode (the second, worse verdict inside the quiet period is held back), as a Warning here.
            Assert.Equal(AnomalySeverity.Warning, cap.Notices.Single(n => n.Key == Key(Code.UnderMap)).Severity);
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task FarUnderTheMap_IsAnError()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            using AnomalyCapture cap = H1AnomalyKit.CaptureFor(session.Player!);
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                var at = new Vector3(player.X, player.Y, player.Z);
                PlayerbotMotionAnomalies.ReportGround(player, player.Map!, new PlayerbotMotionAnomalies.MotionWatch(),
                    PlayerbotMotionAnomalies.GroundVerdict.FarUnderMap, at, at.Z + 60, -200000, 1000);
                return true;
            });
            Assert.Equal(AnomalySeverity.Error, cap.Notices.Single(n => n.Key == Key(Code.UnderMap)).Severity);
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task ANonFinitePosition_IsAnError_AndAFinitePositionIsSilent()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            using AnomalyCapture cap = H1AnomalyKit.CaptureFor(session.Player!);
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                PlayerbotMotionAnomalies.CheckStoredPosition(session, player, 1000); // a real, finite position
                Assert.Empty(cap.Notices);
                PlayerbotMotionAnomalies.ReportNonFinite(session, player, "the position the route walk computed", 1000);
                return true;
            });
            Assert.Equal(AnomalySeverity.Error, cap.Notices.Single(n => n.Key == Key(Code.NonFinitePosition)).Severity);
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task AMovePacketTheServerRefused_IsReported_OncePerEpisode()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            using AnomalyCapture cap = H1AnomalyKit.CaptureFor(session.Player!);
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                var to = new Vector3(player.X + 5, player.Y, player.Z);
                PlayerbotMotionAnomalies.ReportMoveRejected(session, player, "a heartbeat", to, 1000);
                PlayerbotMotionAnomalies.ReportMoveRejected(session, player, "a heartbeat", to, 2000); // same episode
                PlayerbotMotionAnomalies.ReportMoveRejected(session, player, "a heartbeat", to, 1000 + PlayerbotMotionAnomalies.EpisodeQuietMs + 1);
                return true;
            });
            Assert.Equal(2, cap.Count(Key(Code.MoveRejected)));
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task AStartWithNoActionBudget_IsTheCallersBackOff_NotARejectedMove()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            using AnomalyCapture cap = H1AnomalyKit.CaptureFor(session.Player!);
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new ScriptedGround());
                Player player = session.Player!;
                PlayerbotRoute route = LineRoute(player, 30, navigated: true);
                session.ManagedBudget = new ManagedActionBudget(0);
                Assert.False(PlayerbotMotion.Follow(session, player, route, new PlayerbotOptions { Enabled = true }, host.World.NowMs));
                return true;
            });
            Assert.False(cap.Any(Key(Code.MoveRejected)), cap.Describe());
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// Bot QA #792: a bot that stalled teleports home (a near teleport, waiting for its acknowledgement), and the same think walked on.
    /// The server ignores movement while a teleport is under way (MovementHandlers), so the MSG_MOVE_START_FORWARD went nowhere and was
    /// reported as rejected at the spot the bot was leaving. The walk waits for the teleport instead: no packet, no report.
    /// </summary>
    [Fact]
    public async Task AStartWhileATeleportIsUnderWay_WaitsForIt_NotARejectedMove()
    {
        await using Scenario scenario = await Scenario.StartAsync(blocked: static (_, _, _) => false, start: false);
        (bool started, bool teleporting) = await scenario.Host.OnWorldAsync(() =>
        {
            Player player = scenario.Session.Player!;
            TeleportService teleports = scenario.Session.Services.GetRequiredService<TeleportFeature>().Teleports;
            Assert.True(teleports.TeleportTo(player, player.MapId, player.X - 40f, player.Y, Floor, player.Orientation));
            scenario.Session.ManagedBudget = new ManagedActionBudget(4);
            bool follow = PlayerbotMotion.Follow(scenario.Session, player, scenario.Route, new PlayerbotOptions { Enabled = true },
                scenario.Host.World.NowMs);
            return (follow, teleports.IsBeingTeleported(player));
        });

        Assert.True(teleporting, "the near teleport was acknowledged before the walk began: the test proves nothing");
        Assert.False(started);
        Assert.False(scenario.Capture.Any(Key(Code.MoveRejected)), scenario.Capture.Describe());
        Assert.False(scenario.Capture.Any(Key(Code.StartBlocked)), scenario.Capture.Describe());
    }

    // ---- stuck -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task HeartbeatsThatGoNowhere_AreStuck_AndRealProgressIsNot()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            using AnomalyCapture cap = H1AnomalyKit.CaptureFor(session.Player!);
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                PlayerbotRoute route = LineRoute(player, 30, navigated: true);
                var origin = new Vector3(player.X, player.Y, player.Z);

                // Walking: 40 yards in the window.
                var walking = new PlayerbotMotionAnomalies.MotionWatch();
                PlayerbotMotionAnomalies.CheckStuck(session, player, walking, route, origin, 1000);
                PlayerbotMotionAnomalies.CheckStuck(session, player, walking, route, origin + new Vector3(40, 0, 0), 1000 + PlayerbotMotionAnomalies.StuckWindowMs + 100);
                Assert.Empty(cap.Notices);

                // Too early to judge: the window is not over.
                var early = new PlayerbotMotionAnomalies.MotionWatch();
                PlayerbotMotionAnomalies.CheckStuck(session, player, early, route, origin, 1000);
                PlayerbotMotionAnomalies.CheckStuck(session, player, early, route, origin, 1000 + (PlayerbotMotionAnomalies.StuckWindowMs / 2));
                Assert.Empty(cap.Notices);

                // A different route restarts the window.
                var other = new PlayerbotMotionAnomalies.MotionWatch();
                PlayerbotMotionAnomalies.CheckStuck(session, player, other, route, origin, 1000);
                PlayerbotMotionAnomalies.CheckStuck(session, player, other, LineRoute(player, 30, navigated: true), origin, 1000 + PlayerbotMotionAnomalies.StuckWindowMs + 100);
                Assert.Empty(cap.Notices);

                // Stuck: a whole window of heartbeats and the bot ended 0.2 yards from where it began.
                var stuck = new PlayerbotMotionAnomalies.MotionWatch();
                PlayerbotMotionAnomalies.CheckStuck(session, player, stuck, route, origin, 1000);
                PlayerbotMotionAnomalies.CheckStuck(session, player, stuck, route, origin + new Vector3(0.2f, 0, 0), 1000 + PlayerbotMotionAnomalies.StuckWindowMs + 100);
                return true;
            });
            Assert.Equal(1, cap.Count(Key(Code.Stuck)));
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    // ---- walls and doors -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AMeshLegThroughAWall_IsReportedAsWallCrossed()
    {
        await using Scenario scenario = await Scenario.StartAsync(blocked: static (_, _, _) => false);
        scenario.Ground.Blocked = (a, b, _) => Math.Min(a.X, b.X) < scenario.WallX && Math.Max(a.X, b.X) > scenario.WallX;
        await scenario.WalkAsync(600);
        Assert.True(scenario.Capture.Any(Key(Code.WallCrossed)), scenario.Capture.Describe());
        Assert.False(scenario.Capture.Any(Key(Code.DoorCrossed)), scenario.Capture.Describe());
        Assert.Equal(AnomalySeverity.Warning, scenario.Capture.Notices.First(n => n.Key == Key(Code.WallCrossed)).Severity);
    }

    [Fact]
    public async Task AMeshLegThroughADoorway_IsReportedAsDoorCrossed_WhenOnlyAnM2Blocks()
    {
        await using Scenario scenario = await Scenario.StartAsync(blocked: static (_, _, _) => false);
        scenario.Ground.Blocked = (a, b, ignoreM2) => !ignoreM2 && Math.Min(a.X, b.X) < scenario.WallX && Math.Max(a.X, b.X) > scenario.WallX;
        await scenario.WalkAsync(600);
        Assert.True(scenario.Capture.Any(Key(Code.DoorCrossed)), scenario.Capture.Describe());
        Assert.False(scenario.Capture.Any(Key(Code.WallCrossed)), scenario.Capture.Describe());
        Assert.Equal(AnomalySeverity.Info, scenario.Capture.Notices.First(n => n.Key == Key(Code.DoorCrossed)).Severity);
    }

    [Fact]
    public async Task AClearMeshLeg_IsSilent_AndSoIsAWallThatIsNotOnTheLeg()
    {
        await using Scenario scenario = await Scenario.StartAsync(blocked: static (_, _, _) => false);
        await scenario.WalkAsync(600);
        await scenario.WalkAsync(1000);
        scenario.Ground.Blocked = (a, b, _) => Math.Min(a.X, b.X) < scenario.WallX + 500 && Math.Max(a.X, b.X) > scenario.WallX + 400; // far off the leg
        await scenario.WalkAsync(1000);
        Assert.Empty(scenario.Capture.Notices);
    }

    [Fact]
    public async Task ACrossingIsReportedOncePerEpisode_NotOncePerSample()
    {
        await using Scenario scenario = await Scenario.StartAsync(blocked: static (_, _, _) => false);
        scenario.Ground.Blocked = (a, b, _) => Math.Min(a.X, b.X) < scenario.WallX + 1 && Math.Max(a.X, b.X) > scenario.WallX - 1;
        for (int i = 0; i < 4; i++) await scenario.WalkAsync(600);
        Assert.Equal(1, scenario.Capture.Count(Key(Code.WallCrossed)));
    }

    [Fact]
    public async Task AMeshLegThroughAModel_ThatStaysOnTheWalkableMesh_IsNotACrossing()
    {
        // Live: 147 of 422 wall-crossed reports had both ends on the mesh, 1-7 yd apart (a porch step, a stair slab, a ramp): the chest-high
        // line clips a model the mesh, built from the same models, walks over.
        var mesh = new ScriptedMesh();
        await using Scenario scenario = await Scenario.StartAsync(blocked: static (_, _, _) => false, mesh: mesh);
        scenario.Ground.Blocked = (a, b, _) => Math.Min(a.X, b.X) < scenario.WallX && Math.Max(a.X, b.X) > scenario.WallX;
        await scenario.WalkAsync(600);
        Assert.True(mesh.Walks > 0, "the mesh was never asked");
        Assert.False(scenario.Capture.Any(Key(Code.WallCrossed)), scenario.Capture.Describe());
        Assert.False(scenario.Capture.Any(Key(Code.DoorCrossed)), scenario.Capture.Describe());
    }

    [Fact]
    public async Task AMeshLegThroughAWall_ThatLeavesTheMesh_IsStillACrossing()
    {
        var mesh = new ScriptedMesh();
        await using Scenario scenario = await Scenario.StartAsync(blocked: static (_, _, _) => false, mesh: mesh);
        mesh.Edge = (a, b) => Math.Min(a.X, b.X) < scenario.WallX && Math.Max(a.X, b.X) > scenario.WallX;
        scenario.Ground.Blocked = (a, b, _) => Math.Min(a.X, b.X) < scenario.WallX && Math.Max(a.X, b.X) > scenario.WallX;
        await scenario.WalkAsync(600);
        Assert.True(scenario.Capture.Any(Key(Code.WallCrossed)), scenario.Capture.Describe());
    }

    /// <summary>
    /// The mesh walk is horizontal: it starts on the polygon nearest the leg's start (up to 5 yd up or down) and ends on whatever storey
    /// it reaches at the end's x/y. A walk that succeeds on a storey 12 yd from the leg's own end (Ironforge, Undercity: stacked levels)
    /// does not show the bot stayed on the mesh: the leg went through the floor, ramp or wall between the storeys, and is still reported.
    /// The same leg with the walk landing at its end's height is not (the control: the walk itself is what clears it).
    /// </summary>
    [Theory]
    [InlineData(12f, true)]
    [InlineData(-12f, true)]
    [InlineData(0.5f, false)]
    public async Task AMeshLegThroughAWall_WhoseWalkLandsOnAnotherStorey_IsStillACrossing(float storeyOffset, bool reported)
    {
        var mesh = new ScriptedMesh();
        await using Scenario scenario = await Scenario.StartAsync(blocked: static (_, _, _) => false, start: false, mesh: mesh);
        scenario.Ground.Blocked = (a, b, _) => Math.Min(a.X, b.X) < scenario.WallX && Math.Max(a.X, b.X) > scenario.WallX;
        await scenario.Host.OnWorldAsync(() =>
        {
            Player player = scenario.Session.Player!;
            var from = new Vector3(player.X, player.Y, Floor);
            var to = from + new Vector3(4f, 0, 0);
            // Only the walk across the wall lands elsewhere; the zero-length start probe stays on the leg's storey.
            mesh.SurfaceOf = (a, b) => Vector2.Distance(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y)) > 0.01f ? Floor + storeyOffset : Floor;
            PlayerbotMotionAnomalies.CheckCrossing(player, player.Map!, new PlayerbotMotionAnomalies.MotionWatch(), scenario.Route, 1, 1,
                from, to, scenario.Host.World.NowMs);
            return true;
        });
        Assert.True(mesh.Walks > 0, "the mesh was never asked");
        Assert.Equal(reported, scenario.Capture.Any(Key(Code.WallCrossed)));
    }

    /// <summary>
    /// The leg's start must be on the storey the walk starts from: the walk's start polygon is the nearest within 5 yd up or down, so a
    /// start 4 yd above a floor (a bot on a lintel, a wall top) would otherwise walk the floor below and clear the leg.
    /// </summary>
    [Fact]
    public async Task AMeshLegThroughAWall_WhoseStartIsOffTheWalksStorey_IsStillACrossing()
    {
        var mesh = new ScriptedMesh { Surface = Floor - 4f };
        await using Scenario scenario = await Scenario.StartAsync(blocked: static (_, _, _) => false, start: false, mesh: mesh);
        scenario.Ground.Blocked = (a, b, _) => Math.Min(a.X, b.X) < scenario.WallX && Math.Max(a.X, b.X) > scenario.WallX;
        await scenario.Host.OnWorldAsync(() =>
        {
            Player player = scenario.Session.Player!;
            var from = new Vector3(player.X, player.Y, Floor);
            // The end is on the walk's storey; the start is not.
            var to = new Vector3(player.X + 4f, player.Y, Floor - 4f);
            PlayerbotMotionAnomalies.CheckCrossing(player, player.Map!, new PlayerbotMotionAnomalies.MotionWatch(), scenario.Route, 1, 1,
                from, to, scenario.Host.World.NowMs);
            return true;
        });
        Assert.True(mesh.Walks > 0, "the mesh was never asked");
        Assert.True(scenario.Capture.Any(Key(Code.WallCrossed)), scenario.Capture.Describe());
    }

    /// <summary>
    /// The walk stands on the mesh surface it walks on, however far that is from the leg's own height: a leg is a chord between two
    /// string-pulled corners, and over a hill the ground lies 8-30 yd above it (live, the Barrens and Dun Morogh). Before, the surface
    /// was taken only within 4 yd of the chord, and the bot walked on at the chord's height, under the hill.
    /// </summary>
    [Fact]
    public async Task TheWalkStandsOnTheMeshSurface_HoweverFarFromTheLegsChord()
    {
        var mesh = new ScriptedMesh { Surface = Floor + 12f };
        await using Scenario scenario = await Scenario.StartAsync(blocked: static (_, _, _) => false, mesh: mesh);
        await scenario.WalkAsync(600);
        float z = await scenario.Host.OnWorldAsync(() => scenario.Session.Player!.Z);
        Assert.InRange(z, Floor + 11.5f, Floor + 12.5f);
    }

    [Fact]
    public async Task ChaosProbeMotion_IsNotJudged()
    {
        await using Scenario scenario = await Scenario.StartAsync(blocked: static (_, _, _) => false);
        scenario.Ground.Blocked = (a, b, _) => Math.Min(a.X, b.X) < scenario.WallX && Math.Max(a.X, b.X) > scenario.WallX;
        scenario.Session.IsInChaosProbe = true;
        await scenario.WalkAsync(600);
        Assert.Empty(scenario.Capture.Notices);
    }

    // ---- air ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AMeshLegFarAboveTheFloor_IsReportedAsAirwalk()
    {
        await using Scenario scenario = await Scenario.StartAsync(blocked: static (_, _, _) => false);
        scenario.Ground.Floor = Floor - 13f; // the only ground lies 13 yards below the leg
        await scenario.WalkAsync(600);
        Assert.True(scenario.Capture.Any(Key(Code.Airwalk)), scenario.Capture.Describe());
    }

    [Fact]
    public async Task AMeshLegOnItsFloor_IsNotAirwalk()
    {
        await using Scenario scenario = await Scenario.StartAsync(blocked: static (_, _, _) => false);
        await scenario.WalkAsync(600);
        scenario.Ground.Floor = Floor + 2f; // a 2 yard rise under the leg: the bot is snapped onto it
        await scenario.WalkAsync(1000);
        Assert.False(scenario.Capture.Any(Key(Code.Airwalk)), scenario.Capture.Describe());
    }

    // ---- jumps -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ABotMovedBySomethingElseWhileItsRouteIsWalked_IsReportedAsAJump()
    {
        await using Scenario scenario = await Scenario.StartAsync(blocked: static (_, _, _) => false);
        await scenario.WalkAsync(600);
        await scenario.Host.OnWorldAsync(() =>
        {
            Player player = scenario.Session.Player!;
            player.SetPosition(player.X + 40, player.Y, player.Z, player.Orientation); // Relocate would also clear the walk flags
            return true;
        });
        await scenario.WalkAsync(100);
        Assert.True(scenario.Capture.Any(Key(Code.PositionJump)), scenario.Capture.Describe());
    }

    [Fact]
    public async Task OrdinaryWalking_IsNeitherAJump_NorStuck_NorBlocked()
    {
        await using Scenario scenario = await Scenario.StartAsync(blocked: static (_, _, _) => false);
        for (int i = 0; i < 2; i++) await scenario.WalkAsync(500);
        Assert.Empty(scenario.Capture.Notices);
    }

    // ---- lease -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARouteWhoseLeaseRanOutWithYardsLeft_IsReported()
    {
        await using Scenario scenario = await Scenario.StartAsync(blocked: static (_, _, _) => false, routeYards: 200);
        await scenario.WalkAsync(4500); // 4.5 s with no renewing think: past the 4 s floor of the lease, with > 165 yards left
        Assert.True(scenario.Capture.Any(Key(Code.LeaseExpired)), scenario.Capture.Describe());
        Assert.Equal(AnomalySeverity.Info, scenario.Capture.Notices.First(n => n.Key == Key(Code.LeaseExpired)).Severity);
    }

    [Fact]
    public async Task ARouteThatEndsBeforeItsLease_AndOneThatIsRenewed_AreSilent()
    {
        await using (Scenario shortRoute = await Scenario.StartAsync(blocked: static (_, _, _) => false, routeYards: 5))
        {
            await shortRoute.WalkAsync(1500); // arrives (5 yd at 7 yd/s) before the lease ends
            Assert.False(shortRoute.Capture.Any(Key(Code.LeaseExpired)), shortRoute.Capture.Describe());
        }

        await using Scenario renewed = await Scenario.StartAsync(blocked: static (_, _, _) => false, routeYards: 200);
        await renewed.Host.World.AdvanceClockAsync(1500);
        await renewed.Host.OnWorldAsync(() =>
        {
            renewed.Session.ManagedBudget = new ManagedActionBudget(4);
            Assert.True(PlayerbotMotion.Follow(renewed.Session, renewed.Session.Player!, renewed.Route, new PlayerbotOptions { Enabled = true },
                renewed.Host.World.NowMs));
            return true;
        });
        await renewed.WalkAsync(1500); // 1.5 s since the renewal: inside the 2 s lease
        Assert.False(renewed.Capture.Any(Key(Code.LeaseExpired)), renewed.Capture.Describe());
    }

    // ---- blocked starts and routes -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASteppedRouteWhoseFirstStretchHasNoFloor_IsReportedAsStartBlocked()
    {
        await using Scenario scenario = await Scenario.StartAsync(blocked: static (_, _, _) => false, navigated: false, start: false);
        scenario.Ground.Floor = Floor - 30f;
        bool started = await scenario.StartAsync();
        Assert.False(started);
        Assert.True(scenario.Capture.Any(Key(Code.StartBlocked)), scenario.Capture.Describe());
    }

    [Fact]
    public async Task ASteppedRouteOnItsFloor_StartsWithoutAReport()
    {
        await using Scenario scenario = await Scenario.StartAsync(blocked: static (_, _, _) => false, navigated: false, start: false);
        Assert.True(await scenario.StartAsync());
        Assert.Empty(scenario.Capture.Notices);
    }

    [Fact]
    public async Task ASteppedRouteWhoseGroundVanishesMidWalk_IsReportedAsRouteBlocked()
    {
        await using Scenario scenario = await Scenario.StartAsync(blocked: static (_, _, _) => false, navigated: false);
        scenario.Ground.Floor = Floor - 30f;
        await scenario.WalkAsync(600);
        Assert.True(scenario.Capture.Any(Key(Code.RouteBlocked)), scenario.Capture.Describe());
    }

    [Fact]
    public async Task ANavigatedMeshRoute_WithMeshPoints_IsNotRefusedAtStartForStaticWallGraze()
    {
        var mesh = new ScriptedMesh { Edge = static (_, _) => true };
        await using Scenario scenario = await Scenario.StartAsync(blocked: static (_, _, _) => true, navigated: true, start: false, mesh: mesh);
        scenario.Route.MeshPoints = scenario.Route.Points;
        bool started = await scenario.StartAsync();
        Assert.True(started, "a navigated mesh route must not be refused by Validate for LineBlocked: " + scenario.Capture.Describe());
        Assert.False(scenario.Capture.Any(Key(Code.StartBlocked)), scenario.Capture.Describe());
    }

    [Fact]
    public async Task ANavigatedMeshRoute_DoesNotDrapeDownAcrossAStaticFloorSlab_WhenTheLegStoreyIsClear()
    {
        await using Scenario scenario = await Scenario.StartAsync(blocked: static (_, _, _) => false);
        // A lower floor (basement/tunnel) 3.5 yd below the leg whose static ceiling blocks any vertical drop through the slab.
        scenario.Ground.Floor = Floor - 3.5f;
        scenario.Ground.Blocked = (a, b, _) => MathF.Abs(a.Z - b.Z) > 1.5f;
        await scenario.WalkAsync(600);
        float z = await scenario.Host.OnWorldAsync(() => scenario.Session.Player!.Z);
        Assert.InRange(z, Floor - 0.5f, Floor + 0.5f);
        Assert.False(scenario.Capture.Any(Key(Code.WallCrossed)), scenario.Capture.Describe());
    }

    [Fact]
    public async Task AnIntermediateCorner_ThatDipsBelowTheWalkFloor_SnapsUpToTheWalkFloor_NotWallCrossed()
    {
        await using Scenario scenario = await Scenario.StartAsync(blocked: static (_, _, _) => false, start: false);
        scenario.Ground.Blocked = (a, b, _) => MathF.Min(a.Z, b.Z) < Floor + PlayerbotMoveCheck.ChestYards - 1.0f;
        await scenario.Host.OnWorldAsync(() =>
        {
            Player player = scenario.Session.Player!;
            var from = new Vector3(player.X, player.Y, Floor);
            var dippedCorner = new Vector3(player.X + 2f, player.Y, Floor - 2.4f);
            var to = new Vector3(player.X + 4f, player.Y, Floor);
            var route = new PlayerbotRoute([from, dippedCorner, to], 4f, navigated: true) { MeshPoints = [from, dippedCorner, to] };
            PlayerbotMotionAnomalies.CheckCrossing(player, player.Map!, new PlayerbotMotionAnomalies.MotionWatch(), route, 1, 2,
                from, to, scenario.Host.World.NowMs);
            return true;
        });
        Assert.False(scenario.Capture.Any(Key(Code.WallCrossed)), scenario.Capture.Describe());
    }

    [Fact]
    public async Task AMeshLegStartingAtATileBoundary_WhereForwardRaycastClips_IsClearedByReverseWalkOnSameStorey()
    {
        var mesh = new ScriptedMesh
        {
            // Forward raycast (a.X < b.X) hits the tile/portal border; reverse raycast (b.X -> a.X) walks cleanly on the same storey.
            Edge = static (a, b) => a.X < b.X,
        };
        await using Scenario scenario = await Scenario.StartAsync(blocked: static (_, _, _) => false, start: false, mesh: mesh);
        scenario.Ground.Blocked = static (_, _, _) => true;
        await scenario.Host.OnWorldAsync(() =>
        {
            Player player = scenario.Session.Player!;
            var from = new Vector3(player.X, player.Y, Floor);
            var to = new Vector3(player.X + 3f, player.Y, Floor);
            PlayerbotMotionAnomalies.CheckCrossing(player, player.Map!, new PlayerbotMotionAnomalies.MotionWatch(), scenario.Route, 1, 1,
                from, to, scenario.Host.World.NowMs);
            return true;
        });
        Assert.False(scenario.Capture.Any(Key(Code.WallCrossed)), scenario.Capture.Describe());
    }

    [Fact]
    public async Task AMeshLegMidwayAlongACornerSegment_BeyondThreeYards_IsClearedByCornerWalk()
    {
        var mesh = new ScriptedMesh();
        await using Scenario scenario = await Scenario.StartAsync(blocked: static (_, _, _) => false, start: false, mesh: mesh);
        scenario.Ground.Blocked = static (_, _, _) => true;
        await scenario.Host.OnWorldAsync(() =>
        {
            Player player = scenario.Session.Player!;
            var corner0 = new Vector3(player.X, player.Y, Floor);
            var corner1 = new Vector3(player.X + 20f, player.Y, Floor);
            var previous = new Vector3(player.X + 8f, player.Y, Floor);
            var end = new Vector3(player.X + 12f, player.Y, Floor);
            // Direct walk from previous to end clips an edge, while the walk from corner0 to end succeeds on the mesh.
            mesh.Edge = (a, b) => MathF.Abs(a.X - previous.X) < 0.1f && MathF.Abs(b.X - end.X) < 0.1f;
            var route = new PlayerbotRoute([corner0, corner1], 20f, navigated: true) { MeshPoints = [corner0, corner1] };
            PlayerbotMotionAnomalies.CheckCrossing(player, player.Map!, new PlayerbotMotionAnomalies.MotionWatch(), route, 1, 1,
                previous, end, scenario.Host.World.NowMs);
            return true;
        });
        Assert.False(scenario.Capture.Any(Key(Code.WallCrossed)), scenario.Capture.Describe());
    }

    /// <summary>
    /// #1475: a bot the walk keeps on the mesh surface of an upper WMO storey (<see cref="PlayerbotMotion"/> places a navigated step on
    /// the surface <see cref="MapCollision.TryWalkMeshSurface"/> answers) while the model query answers the storey 9 yd below is on its
    /// floor: the mesh polygon under it (the zero-length walk <see cref="PlayerbotMotionAnomalies.StaysOnMesh"/> also uses) is within <see cref="PlayerbotMotionAnomalies.MeshStoreyYards"/>.
    /// The control: the same bot with the mesh only on the lower storey is still walking on air.
    /// </summary>
    [Theory]
    [InlineData(9f, false)]
    [InlineData(0f, true)]
    public async Task ABotOnAnUpperStoreysMesh_TheModelQueryMisses_IsNotAirwalk(float meshAboveModelFloor, bool reported)
    {
        var mesh = new ScriptedMesh { Surface = Floor + meshAboveModelFloor };
        await using Scenario scenario = await Scenario.StartAsync(blocked: static (_, _, _) => false, start: false, mesh: mesh);
        await scenario.Host.OnWorldAsync(() =>
        {
            Player player = scenario.Session.Player!;
            var at = new Vector3(player.X, player.Y, Floor + 9f);
            PlayerbotMotionAnomalies.CheckGround(player, player.Map!, new PlayerbotMotionAnomalies.MotionWatch(), at, scenario.Host.World.NowMs);
            return true;
        });
        Assert.Equal(reported, scenario.Capture.Any(Key(Code.Airwalk)));
    }

    /// <summary>
    /// #1475 review: a bot walking on air up to 3 yd beyond a ledge's edge still gets the ledge polygon from the zero-length walk (its
    /// start polygon is the nearest within NavMeshPathfinder.NearExtents, 3 yd sideways), which answers a hit on the edge at ledge
    /// height. That edge is not a floor under the bot, so the airwalk is still reported, while the floor query
    /// (<see cref="IMeshFloorQuery"/>) sees no polygon straight under it. The control (0 yd, no hit): the polygon is under the bot.
    /// </summary>
    [Theory]
    [InlineData(1f, true)]
    [InlineData(2f, true)]
    [InlineData(3f, true)]
    [InlineData(0f, false)]
    public async Task ABotBeyondALedgeEdge_TheStartPolygonAnswersTheEdge_IsStillAirwalk(float sidewaysYards, bool reported)
    {
        var mesh = new ScriptedMesh
        {
            StartPolygon = at => new MeshRaycastResult(true, sidewaysYards > 0f, sidewaysYards > 0f ? 0f : 1f,
                new Vector3(at.X + sidewaysYards, at.Y, at.Z - 0.5f)),
            FloorStraightUnder = static (_, _, _) => null,
        };
        await using Scenario scenario = await Scenario.StartAsync(blocked: static (_, _, _) => false, start: false, mesh: mesh);
        await scenario.Host.OnWorldAsync(() =>
        {
            Player player = scenario.Session.Player!;
            var at = new Vector3(player.X, player.Y, Floor + 9f);
            PlayerbotMotionAnomalies.CheckGround(player, player.Map!, new PlayerbotMotionAnomalies.MotionWatch(), at, scenario.Host.World.NowMs);
            return true;
        });
        Assert.Equal(reported, scenario.Capture.Any(Key(Code.Airwalk)));
    }

    // ---- fixture -----------------------------------------------------------------------------------------------------------------

    private static PlayerbotRoute LineRoute(Player player, float yards, bool navigated)
    {
        var start = new Vector3(player.X, player.Y, Floor);
        return new PlayerbotRoute([start, start + new Vector3(yards, 0, 0)], yards, navigated);
    }

    /// <summary>A bot on a flat scripted floor walking a straight route east.</summary>
    private sealed class Scenario : IAsyncDisposable
    {
        private Scenario(WorldTestHost host, WorldSession session, ScriptedGround ground, PlayerbotRoute route, AnomalyCapture capture, float wallX)
        {
            Host = host; Session = session; Ground = ground; Route = route; Capture = capture; WallX = wallX;
        }

        internal WorldTestHost Host { get; }
        internal WorldSession Session { get; }
        internal ScriptedGround Ground { get; }
        internal PlayerbotRoute Route { get; }
        internal AnomalyCapture Capture { get; }

        /// <summary>The x of the wall two yards ahead of the start.</summary>
        internal float WallX { get; }

        internal static async Task<Scenario> StartAsync(Func<Vector3, Vector3, bool, bool> blocked, bool navigated = true, float routeYards = 30,
            bool start = true, ScriptedMesh? mesh = null)
        {
            WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            var ground = new ScriptedGround { Blocked = blocked };
            AnomalyCapture capture = H1AnomalyKit.CaptureFor(session.Player!);
            (PlayerbotRoute route, float wallX) = await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: ground, pathfinder: mesh);
                Player player = session.Player!;
                player.Relocate(player.X, player.Y, Floor, 0, host.World.NowMs);
                PlayerbotRoute made = LineRoute(player, routeYards, navigated);
                return (made, player.X + 2f);
            });
            var scenario = new Scenario(host, session, ground, route, capture, wallX);
            if (start) Assert.True(await scenario.StartAsync(), "the route did not start: " + capture.Describe());
            return scenario;
        }

        internal Task<bool> StartAsync() => Host.OnWorldAsync(() =>
        {
            Session.ManagedBudget = new ManagedActionBudget(4);
            return PlayerbotMotion.Follow(Session, Session.Player!, Route, new PlayerbotOptions { Enabled = true }, Host.World.NowMs);
        });

        /// <summary>Advance the world clock and pump the bot's client half once.</summary>
        internal async Task WalkAsync(uint ms)
        {
            await Host.World.AdvanceClockAsync(ms);
            await Host.OnWorldAsync(() => PlayerbotMotion.Pump(Session, Session.Player!, Host.World.NowMs));
        }

        public async ValueTask DisposeAsync()
        {
            Capture.Dispose();
            Session.Kick();
            await Session.ManagedClosed;
            await Host.DisposeAsync();
        }
    }

    /// <summary>
    /// A navigation mesh everywhere (a straight-line path), whose surface lies at <see cref="Surface"/> and whose walks stop at the edges
    /// the test scripts (<see cref="Edge"/>).
    /// </summary>
    internal sealed class ScriptedMesh : IPathfinder, IMeshPointQuery, IMeshFloorQuery
    {
        internal float Surface { get; set; } = Floor;

        /// <summary>
        /// When set, the whole answer of a zero-length walk (the start polygon's surface under a point), so a test can model the walk
        /// and the floor query (<see cref="FloorStraightUnder"/>) disagreeing.
        /// </summary>
        internal Func<Vector3, MeshRaycastResult>? StartPolygon { get; set; }

        /// <summary>
        /// The polygon surface straight under or over (x, y) the floor query (<see cref="IMeshFloorQuery.TryGetMeshFloor"/>) answers, or
        /// null for none. Unset: the floor query finds nothing, as when this mesh did not answer it.
        /// </summary>
        internal Func<float, float, float, float?>? FloorStraightUnder { get; set; }
        internal Func<Vector3, Vector3, bool>? Edge { get; set; }

        /// <summary>When set, the height a walk from the first point to the second ends at (else <see cref="Surface"/>).</summary>
        internal Func<Vector3, Vector3, float>? SurfaceOf { get; set; }

        internal int Walks { get; private set; }

        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null) => new(PathType.Normal, [start, end]);

        public bool TryGetRandomPointAround(uint mapId, Vector3 center, float radius, Random random, PathOptions? options, out Vector3 point)
        { point = center; return false; }

        public MeshRaycastResult Raycast(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            Walks++;
            if (start == end && StartPolygon is { } startPolygon) return startPolygon(start);
            float surface = SurfaceOf?.Invoke(start, end) ?? Surface;
            return Edge?.Invoke(start, end) == true
                ? new MeshRaycastResult(true, true, 0.5f, Vector3.Lerp(start, end, 0.5f) with { Z = surface })
                : new MeshRaycastResult(true, false, 1f, end with { Z = surface });
        }

        public bool TryGetMeshFloor(uint mapId, float x, float y, float z, float below, float above, out float height)
        {
            float? found = FloorStraightUnder?.Invoke(x, y, z);
            height = found ?? 0f;
            return found is { } h && h >= z - below && h <= z + above;
        }
    }

    /// <summary>A flat floor at <see cref="Floor"/> (movable) with walls the test scripts.</summary>
    private sealed class ScriptedGround : ILineOfSight
    {
        internal float Floor { get; set; } = PlayerbotMotionAnomalyTests.Floor;
        internal Func<Vector3, Vector3, bool, bool>? Blocked { get; set; }

        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true)
            => Blocked is null || !Blocked(from, to, ignoreM2);

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        { hit = to; return false; }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => Floor;

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        { info = default; return false; }
    }
}
