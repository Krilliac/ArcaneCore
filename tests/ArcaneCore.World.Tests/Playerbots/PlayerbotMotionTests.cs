using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Code = ArcaneCore.World.Playerbots.PlayerbotMotionAnomalies.Code;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotMotionTests
{
    private const float Floor = 83.53f;

    private static string Key(Code code) => PlayerbotMotionAnomalies.Key(code);

    [Fact]
    public async Task Follow_ThenPump_WhenObstacleAtThreeYards_InvalidatesRouteAndDoesNotLoopRouteBlocked()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            using AnomalyCapture capture = H1AnomalyKit.CaptureFor(session.Player!);
            (PlayerbotRoute route, float startX) = await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                float originX = player.X;
                WorldCollision.Of(host.World).Install(lineOfSight: new CliffAtX(Floor, originX + 2.5f));
                player.Relocate(originX, player.Y, Floor, 0, host.World.NowMs);
                var start = new Vector3(originX, player.Y, Floor);
                var made = new PlayerbotRoute([start, start + new Vector3(30f, 0f, 0f)], 30f, navigated: false);
                return (made, originX);
            });

            var options = new PlayerbotOptions { Enabled = true, MoveSpeed = 7.0f };
            bool firstFollow = await host.OnWorldAsync(() =>
            {
                session.ManagedBudget = new ManagedActionBudget(4);
                return PlayerbotMotion.Follow(session, session.Player!, route, options, host.World.NowMs);
            });

            if (firstFollow)
            {
                await host.World.AdvanceClockAsync(PlayerbotMotion.HeartbeatIntervalMs);
                await host.OnWorldAsync(() => PlayerbotMotion.Pump(session, session.Player!, host.World.NowMs));
            }

            Assert.True(route.Complete, "Rejected route must be marked Complete so callers holding a cached reference do not re-submit it.");
            Assert.Equal(1, await host.OnWorldAsync(() => PlayerbotMotion.ConsecutiveRejections(session.Player!)));

            int reSubmittedCalls = 0;
            for (int tick = 0; tick < 5; tick++)
            {
                await host.World.AdvanceClockAsync(PlayerbotMotion.HeartbeatIntervalMs);
                await host.OnWorldAsync(() =>
                {
                    if (route is { Complete: false })
                    {
                        reSubmittedCalls++;
                        session.ManagedBudget = new ManagedActionBudget(4);
                        PlayerbotMotion.Follow(session, session.Player!, route, options, host.World.NowMs);
                    }
                    PlayerbotMotion.Pump(session, session.Player!, host.World.NowMs);
                });
            }

            Assert.Equal(0, reSubmittedCalls);
            Assert.Equal(0, capture.Count(Key(Code.RouteBlocked)));
            _ = startX;
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task Follow_WhenBotDriftedOffNavigatedRoute_MarksRouteCompleteWithoutStartBlockedAnomaly()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            using AnomalyCapture capture = H1AnomalyKit.CaptureFor(session.Player!);
            (bool started, bool complete, int consecutive) = await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                // Off-leg at (15, 10, Floor) while route runs along X=0 from (0, 0, Floor) to (0, 50, Floor).
                // Step toward (0, 50, Floor) crosses into X <= 14.5 where a 30-yard cliff drop rejects the un-navigated DirectStep.
                WorldCollision.Of(host.World).Install(lineOfSight: new CliffBelowX(Floor, 14.5f));
                player.Relocate(15f, 10f, Floor, 0f, host.World.NowMs);
                var route = new PlayerbotRoute([new Vector3(0f, 0f, Floor), new Vector3(0f, 50f, Floor)], 50f, navigated: true);
                session.ManagedBudget = new ManagedActionBudget(4);
                bool follow = PlayerbotMotion.Follow(session, player, route, new PlayerbotOptions { Enabled = true, MoveSpeed = 7.0f }, host.World.NowMs);
                return (follow, route.Complete, PlayerbotMotion.ConsecutiveRejections(player));
            });

            Assert.False(started);
            Assert.True(complete);
            Assert.Equal(0, consecutive);
            Assert.Equal(0, capture.Count(Key(Code.StartBlocked)));
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task Follow_WhenBotDriftedOffNavigatedRouteEvenWithClearFirstTwoYards_RejectsRouteImmediatelyInsteadOfFailingInPump()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            using AnomalyCapture capture = H1AnomalyKit.CaptureFor(session.Player!);
            (bool started, bool complete, int consecutive) = await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                // First 2 yards are flat/clear, cliff at X <= 11.0f would only be hit on the next heartbeat in Pump.
                WorldCollision.Of(host.World).Install(lineOfSight: new CliffBelowX(Floor, 11.0f));
                player.Relocate(15f, 10f, Floor, 0f, host.World.NowMs);
                var route = new PlayerbotRoute([new Vector3(0f, 0f, Floor), new Vector3(0f, 50f, Floor)], 50f, navigated: true);
                session.ManagedBudget = new ManagedActionBudget(4);
                bool follow = PlayerbotMotion.Follow(session, player, route, new PlayerbotOptions { Enabled = true, MoveSpeed = 7.0f }, host.World.NowMs);
                return (follow, route.Complete, PlayerbotMotion.ConsecutiveRejections(player));
            });

            Assert.False(started);
            Assert.True(complete);
            Assert.Equal(0, consecutive);
            Assert.Equal(0, capture.Count(Key(Code.StartBlocked)));
            Assert.Equal(0, capture.Count(Key(Code.RouteBlocked)));
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task FromBeside_MarksJoiningLegNavigated_AndRejectsJoiningLegBlockedByM2Wall()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            (bool blockedPlanned, bool clearPlanned, bool clearLeg1Navigated, bool clearLeg2Navigated) = await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                var options = new PlayerbotOptions { Enabled = true, MoveSpeed = 7.0f };
                player.Relocate(0f, 0f, Floor, 0f, host.World.NowMs);

                // First: M2 wall at X=1.25 blocks all beside candidates in +X, and fragment pathfinder only has full paths for X >= 2.0.
                WorldCollision.Of(host.World).Install(
                    lineOfSight: new M2WallAtX(Floor, 1.25f),
                    pathfinder: new FragmentAtOriginPathfinder(Floor));
                bool blocked = PlayerbotNavigation.TryPlanToward(player, new Vector3(20f, 0f, Floor), options, out _);

                // Second: Clear LOS -> FromBeside succeeds; because FromBeside verified LineClear and floor height on start->beside,
                // leg 1 is marked navigated so corner turns across Points[1] in Pump do not fail route.LegsNavigated(1, 2).
                WorldCollision.Of(host.World).Install(
                    lineOfSight: new FlatFloor(Floor),
                    pathfinder: new FragmentAtOriginPathfinder(Floor));
                bool clear = PlayerbotNavigation.TryPlanToward(player, new Vector3(20f, 0f, Floor), options, out PlayerbotRoute? clearRoute);

                return (
                    blocked,
                    clear,
                    clearRoute?.LegNavigatedAt(1) ?? false,
                    clearRoute?.LegNavigatedAt(2) ?? false);
            });

            Assert.False(blockedPlanned);
            Assert.True(clearPlanned);
            Assert.True(clearLeg1Navigated);
            Assert.True(clearLeg2Navigated);
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task FromBeside_WithRockUnderBot_PlansFromOneYardOut_AndStillRefusesM2WallAcrossTheHop()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            (bool rockPlanned, bool wallPlanned) = await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                var options = new PlayerbotOptions { Enabled = true, MoveSpeed = 7.0f };
                player.Relocate(0f, 0f, Floor, 0f, host.World.NowMs);

                // A rock under the bot blocks every doodad-aware sight that starts within a yard of it; one starting a yard out (OffStart) sees past it.
                WorldCollision.Of(host.World).Install(
                    lineOfSight: new RockUnderBot(Floor, 0.9f, float.NaN),
                    pathfinder: new FragmentAtOriginPathfinder(Floor));
                bool rock = PlayerbotNavigation.TryPlanToward(player, new Vector3(20f, 0f, Floor), options, out _);

                // The same rock with an M2 wall at X=1.25: the hop's sight from one yard out still crosses the wall, so no beside start is taken.
                WorldCollision.Of(host.World).Install(
                    lineOfSight: new RockUnderBot(Floor, 0.9f, 1.25f),
                    pathfinder: new FragmentAtOriginPathfinder(Floor));
                bool wall = PlayerbotNavigation.TryPlanToward(player, new Vector3(20f, 0f, Floor), options, out _);
                return (rock, wall);
            });

            Assert.True(rockPlanned);
            Assert.False(wallPlanned);
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task ObserveAfterStep_IntermediateCornerInDoorwayWithLintelFourYardsAbove_DoesNotSnapCornerOntoLintel()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            using AnomalyCapture capture = H1AnomalyKit.CaptureFor(session.Player!);
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                Map map = player.Map!;
                WorldCollision.Of(host.World).Install(lineOfSight: new DoorwayWithLintelAtX(Floor, lintelX: 2f, lintelZ: Floor + 3.9f));

                var from = new Vector3(0f, 0f, Floor);
                var corner = new Vector3(2f, 0f, Floor);
                var to = new Vector3(4f, 0f, Floor);
                var route = new PlayerbotRoute([from, corner, to], 4f, navigated: true);
                PlayerbotMotionAnomalies.AfterHeartbeat(session, player, map, PlayerbotMotion.Of(player), route, from, 1, 2, to, host.World.NowMs);
            });

            Assert.Equal(0, capture.Count(Key(Code.WallCrossed)));
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task ObserveAfterStep_IntermediateCornerWithUnresolvedFloorZ_ClampsToEndpointsAndStaysOnSteepSlopeMesh()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            using AnomalyCapture capture = H1AnomalyKit.CaptureFor(session.Player!);
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                Map map = player.Map!;
                WorldCollision.Of(host.World).Install(
                    lineOfSight: new WallAtXWithHoleAtCorner(Floor, wallX: 3f, holeX: 2f),
                    pathfinder: new SteepSlopePathfinder(Floor));

                var from = new Vector3(0f, 0f, Floor);
                var corner = new Vector3(2f, 0f, 0f);
                var to = new Vector3(4f, 0f, Floor);
                var route = new PlayerbotRoute([from, corner, to], 4f, navigated: true);
                PlayerbotMotionAnomalies.AfterHeartbeat(session, player, map, PlayerbotMotion.Of(player), route, from, 1, 2, to, host.World.NowMs);
            });

            Assert.Equal(0, capture.Count(Key(Code.WallCrossed)));
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task ObserveAfterStep_DetectsLedgeWithinFourYardsAboveFeet()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            using AnomalyCapture capture = H1AnomalyKit.CaptureFor(session.Player!);
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                Map map = player.Map!;
                WorldCollision.Of(host.World).Install(lineOfSight: new HighProbeOnlyLedge(Floor));

                var from = new Vector3(0f, 0f, Floor);
                var to = new Vector3(3f, 0f, Floor);
                var route = new PlayerbotRoute([from, to], 3f, navigated: true);
                PlayerbotMotionAnomalies.AfterHeartbeat(session, player, map, PlayerbotMotion.Of(player), route, from, 1, 1, to, host.World.NowMs);
            });

            Assert.Equal(0, capture.Count(Key(Code.Airwalk)));
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    // Bot-QA #1475 / #1495 (live: Orgrimmar 1695,-4268 and the Menethil docks). The tests below drive Follow and Pump on a navigated
    // route whose steps the mesh walk places on another storey, with the chest-height sight to that place (and to every fallback
    // Validate tries) blocked. Rules the fakes encode: a mesh walk answers the surface under the end of the segment, and a zero-length
    // walk (the "is the start on the mesh" probe of StaysOnMesh) is unavailable, so the step is not proven to stay on the mesh.

    [Fact]
    public async Task Pump_WhenNavigatedStepLandsOnAnotherStoreyBehindASlab_DoesNotMoveBotThroughTheFloor()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            using AnomalyCapture capture = H1AnomalyKit.CaptureFor(session.Player!);
            const float upperRise = 18.7f; // live: +18.6 yd over 3.7 yd of travel at Orgrimmar 1695,-4268
            List<(float X, float Z)> steps = await WalkNavigatedRouteAsync(host, session, originX =>
            {
                // Nothing crosses the slab at x0 + 5: the upper storey is behind it.
                WorldCollision.Of(host.World).Install(
                    lineOfSight: new StoreyLineOfSight(originX + 5f, Floor, Floor + upperRise, float.NaN, float.NaN),
                    pathfinder: new StoreyMeshPathfinder(originX + 5f, Floor, Floor + upperRise));
            }, endRise: 0f, pumps: 5);

            // Every step is a 3.5 yd walk on the lower floor, or refused; none may put the bot on the storey behind the slab.
            Assert.All(steps, s => Assert.True(s.Z <= Floor + PlayerbotMoveCheck.DirectStepMaxStepUpYards,
                $"bot left the floor it walks on: z {s.Z} (floor {Floor}) at x {s.X}"));
            Assert.True(capture.Count(Key(Code.RouteBlocked)) >= 1, "the refused step must be reported as route-blocked");
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task Pump_WhenNavigatedStepPassesTheDockEdge_DoesNotLeaveBotHoveringOverTheSeabed()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            using AnomalyCapture capture = H1AnomalyKit.CaptureFor(session.Player!);
            const float seabedDrop = 20.3f; // live: Menethil dock at z 7.7 over a seabed at -12.6
            float edgeX = 0f;
            List<(float X, float Z)> steps = await WalkNavigatedRouteAsync(host, session, originX =>
            {
                edgeX = originX + 5f;
                // The only sight across the edge is a level one at the height the bot stands at; the route itself rises, so a
                // step at the route's own height is blocked too, and only "keep the old height" would be clear.
                WorldCollision.Of(host.World).Install(
                    lineOfSight: new StoreyLineOfSight(edgeX, Floor, Floor - seabedDrop, Floor + 1.1f, Floor + 1.3f),
                    pathfinder: new StoreyMeshPathfinder(edgeX, Floor, Floor - seabedDrop));
            }, endRise: 4.3f, pumps: 5);

            Assert.All(steps, s =>
            {
                float floorUnder = s.X >= edgeX ? Floor - seabedDrop : Floor;
                Assert.True(s.Z - floorUnder <= PlayerbotMotionAnomalies.AirwalkYards,
                    $"bot hovers {s.Z - floorUnder} yd over the floor at x {s.X} (z {s.Z}, floor {floorUnder})");
            });
            Assert.True(capture.Count(Key(Code.RouteBlocked)) >= 1, "the refused step must be reported as route-blocked");
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task Pump_WhenArrivalStepIsRefused_StopsAtTheLastAnnouncedPositionNotTheRawRouteEnd()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            using AnomalyCapture capture = H1AnomalyKit.CaptureFor(session.Player!);
            var options = new PlayerbotOptions { Enabled = true, MoveSpeed = 7.0f };
            PlayerbotRoute? route = null;
            (float originX, float anchorX) = await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                float x0 = player.X;
                WorldCollision.Of(host.World).Install(lineOfSight: new CliffAtX(Floor, x0 + 5f));
                player.Relocate(x0, player.Y, Floor, 0, host.World.NowMs);
                var start = new Vector3(x0, player.Y, Floor);
                // A 6 yd route: one heartbeat (3.5 yd) is fine, the arrival at its end (x0 + 6) is over the cliff.
                route = new PlayerbotRoute([start, start + new Vector3(6f, 0f, 0f)], 6f, navigated: false);
                session.ManagedBudget = new ManagedActionBudget(4);
                Assert.True(PlayerbotMotion.Follow(session, player, route, options, host.World.NowMs));
                return (x0, x0 + 3.5f);
            });

            await host.World.AdvanceClockAsync(PlayerbotMotion.HeartbeatIntervalMs);
            float announcedX = await host.OnWorldAsync(() =>
            {
                PlayerbotMotion.Pump(session, session.Player!, host.World.NowMs);
                return session.Player!.X;
            });
            Assert.InRange(announcedX, anchorX - 0.05f, anchorX + 0.05f);

            // Long enough that the walk reaches the route's end in one pump.
            await host.World.AdvanceClockAsync(1000);
            (float stoppedX, float stoppedZ) = await host.OnWorldAsync(() =>
            {
                PlayerbotMotion.Pump(session, session.Player!, host.World.NowMs);
                return (session.Player!.X, session.Player.Z);
            });

            Assert.True(stoppedX < originX + 5f, $"bot stopped {stoppedX - originX} yd from the start, past the cliff edge at 5 yd");
            Assert.InRange(stoppedX, anchorX - 0.05f, anchorX + 0.05f);
            Assert.InRange(stoppedZ, Floor - 0.5f, Floor + 0.5f);
            Assert.True(capture.Count(Key(Code.RouteBlocked)) >= 1, "the refused arrival must be reported as route-blocked");
            Assert.False(route!.Complete, "the refused arrival must not mark the route complete");
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task Pump_WhenNavigatedStepIsLevel_IsAcceptedEvenWithNoSightLine()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            using AnomalyCapture capture = H1AnomalyKit.CaptureFor(session.Player!);
            float wallX = 0f, originX = 0f;
            // The same floor on both sides of a sight blocker at x0 + 5: a level step is never refused for its line of sight.
            List<(float X, float Z)> steps = await WalkNavigatedRouteAsync(host, session, x0 =>
            {
                originX = x0;
                wallX = x0 + 5f;
                WorldCollision.Of(host.World).Install(
                    lineOfSight: new StoreyLineOfSight(wallX, Floor, Floor, float.NaN, float.NaN),
                    pathfinder: new StoreyMeshPathfinder(wallX, Floor, Floor));
            }, endRise: 0f, pumps: 4);

            Assert.True(steps[^1].X > wallX + 1f, $"bot stopped at {steps[^1].X - originX} yd, short of the level step past the blocker at 5 yd");
            Assert.All(steps, s => Assert.InRange(s.Z, Floor - 0.5f, Floor + 0.5f));
            Assert.Equal(0, capture.Count(Key(Code.RouteBlocked)));
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task Pump_WhenNavigatedStepClimbsAStoreyWithAClearSightLine_IsAccepted()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            using AnomalyCapture capture = H1AnomalyKit.CaptureFor(session.Player!);
            const float stairRise = 4f; // well over the 2 yd step height
            float wallX = 0f;
            // A stair: the blocker at x0 + 5 is open between the floors (the door band spans both storeys), so the sight line clears the step.
            List<(float X, float Z)> steps = await WalkNavigatedRouteAsync(host, session, x0 =>
            {
                wallX = x0 + 5f;
                WorldCollision.Of(host.World).Install(
                    lineOfSight: new StoreyLineOfSight(wallX, Floor, Floor + stairRise, Floor, Floor + stairRise + 4f),
                    pathfinder: new StoreyMeshPathfinder(wallX, Floor, Floor + stairRise));
            }, endRise: stairRise, pumps: 4);

            Assert.True(steps[^1].X > wallX, "bot did not climb past the stair");
            Assert.InRange(steps[^1].Z, Floor + stairRise - 0.5f, Floor + stairRise + 0.5f);
            Assert.Equal(0, capture.Count(Key(Code.RouteBlocked)));
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task Pump_WhenNavigatedStepClimbsAStoreyTheMeshProves_IsAcceptedWithNoSightLine()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            using AnomalyCapture capture = H1AnomalyKit.CaptureFor(session.Player!);
            const float stairRise = 4f;
            float wallX = 0f;
            // Nothing sees across the blocker, but the mesh walk stays on the mesh from the step's start to its end (StaysOnMesh).
            List<(float X, float Z)> steps = await WalkNavigatedRouteAsync(host, session, x0 =>
            {
                wallX = x0 + 5f;
                WorldCollision.Of(host.World).Install(
                    lineOfSight: new StoreyLineOfSight(wallX, Floor, Floor + stairRise, float.NaN, float.NaN),
                    pathfinder: new StoreyMeshPathfinder(wallX, Floor, Floor + stairRise, proveStart: true));
            }, endRise: stairRise, pumps: 4);

            Assert.True(steps[^1].X > wallX, "bot did not climb past the stair");
            Assert.InRange(steps[^1].Z, Floor + stairRise - 0.5f, Floor + stairRise + 0.5f);
            Assert.Equal(0, capture.Count(Key(Code.RouteBlocked)));
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task Pump_WhenNavigatedStairStepHasNoProbeFloorAndNoSightLine_IsAcceptedOnTheMeshSurface()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            using AnomalyCapture capture = H1AnomalyKit.CaptureFor(session.Player!);
            const float stairRise = 3f; // over the 2 yd step height, within one step's run (MoveSpeed 7 x HeartbeatIntervalMs; the Deathknell crypt stairs: 2.3 over 4 yd)
            float wallX = 0f;
            // The crypt stairs: no floor is found under the steps, nothing sees across the blocker and the mesh cannot prove the start,
            // but the mesh's surface under the step is the floor.
            List<(float X, float Z)> steps = await WalkNavigatedRouteAsync(host, session, x0 =>
            {
                wallX = x0 + 5f;
                WorldCollision.Of(host.World).Install(
                    lineOfSight: new NoFloorLineOfSight(wallX),
                    pathfinder: new StoreyMeshPathfinder(wallX, Floor, Floor + stairRise));
            }, endRise: stairRise, pumps: 4);

            Assert.True(steps[^1].X > wallX, "bot did not climb past the stair");
            Assert.InRange(steps[^1].Z, Floor + stairRise - 0.5f, Floor + stairRise + 0.5f);
            Assert.Equal(0, capture.Count(Key(Code.RouteBlocked)));
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task Pump_WhenNavigatedMeshRiseIsSteeperThanTheStepsRun_IsRefusedEvenUnderTheRiseCap()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            using AnomalyCapture capture = H1AnomalyKit.CaptureFor(session.Player!);
            // A MoveSpeed under the base run speed walks at 2.5 yd/s: a heartbeat steps 1.25 yd, so the step across the wall (at 2 yd) runs 1.25 yd. The mesh rises 2.5 over it: over the step height, well under the 3.5 yd cap, steeper than 45 degrees.
            const float rise = 2.5f;
            float wallX = 0f;
            List<(float X, float Z)> steps = await WalkNavigatedRouteAsync(host, session, x0 =>
            {
                wallX = x0 + 2f;
                WorldCollision.Of(host.World).Install(
                    lineOfSight: new NoFloorLineOfSight(wallX),
                    pathfinder: new StoreyMeshPathfinder(wallX, Floor, Floor + rise));
            }, endRise: rise, pumps: 4, moveSpeed: 6.0f);

            Assert.All(steps, s => Assert.True(s.X <= wallX, $"bot climbed past the wall to x {s.X} (z {s.Z})"));
            Assert.True(capture.Count(Key(Code.RouteBlocked)) >= 1, "the refused step must be reported as route-blocked");
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task Pump_WhenNavigatedStepIsFlatAndTheProbeFindsAFloor_StandsOnTheProbedFloorNotTheMeshHeight()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            using AnomalyCapture capture = H1AnomalyKit.CaptureFor(session.Player!);
            const float meshAbove = 1.5f; // the mesh surface is above the probed floor, within the probe's tolerance
            List<(float X, float Z)> steps = await WalkNavigatedRouteAsync(host, session, x0 =>
            {
                float wallX = x0 + 5f;
                WorldCollision.Of(host.World).Install(
                    lineOfSight: new StoreyLineOfSight(wallX, Floor, Floor, float.NaN, float.NaN),
                    pathfinder: new StoreyMeshPathfinder(wallX, Floor + meshAbove, Floor + meshAbove));
            }, endRise: 0f, pumps: 4);

            Assert.All(steps, s => Assert.InRange(s.Z, Floor - 0.1f, Floor + 0.5f));
            Assert.Equal(0, capture.Count(Key(Code.RouteBlocked)));
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task Pump_WhenALongStepCoversAStoreyOfMeshRise_IsRefusedHoweverLongItsRun()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            using AnomalyCapture capture = H1AnomalyKit.CaptureFor(session.Player!);
            // A stalled bot's first pump covers three heartbeats (10.5 yd at MoveSpeed 7): the 8 yd rise is under 45 degrees over that run.
            const float rise = 8f;
            List<(float X, float Z)> steps = await WalkNavigatedRouteAsync(host, session, x0 =>
            {
                float wallX = x0 + 5f;
                WorldCollision.Of(host.World).Install(
                    lineOfSight: new NoFloorLineOfSight(wallX),
                    pathfinder: new StoreyMeshPathfinder(wallX, Floor, Floor + rise));
            }, endRise: rise, pumps: 2, heartbeatsPerPump: 3);

            Assert.All(steps, s => Assert.True(s.Z <= Floor + PlayerbotMoveCheck.DirectStepMaxStepUpYards, $"bot climbed to z {s.Z} at x {s.X}"));
            Assert.True(capture.Count(Key(Code.RouteBlocked)) >= 1, "the refused step must be reported as route-blocked");
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task Pump_WhenAClimbingStepHasNoMeshSurfaceAndNoFloor_IsRefused()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            using AnomalyCapture capture = H1AnomalyKit.CaptureFor(session.Player!);
            float wallX = 0f;
            // Past the wall the mesh walk fails and there is no floor query answer: no mesh surface, so the step keeps the route's own
            // straight-line height, which climbs 2.1 yd per step (12 yd over the 20 yd route), over the step height. Nothing sees across.
            List<(float X, float Z)> steps = await WalkNavigatedRouteAsync(host, session, x0 =>
            {
                wallX = x0 + 5f;
                WorldCollision.Of(host.World).Install(
                    lineOfSight: new NoFloorLineOfSight(wallX),
                    pathfinder: new UnconnectedMeshPathfinder(wallX, Floor, Floor + 3f, answersFloorQuery: false));
            }, endRise: 12f, pumps: 4);

            Assert.All(steps, s => Assert.True(s.X <= wallX, $"bot crossed the wall to x {s.X} (z {s.Z})"));
            Assert.True(capture.Count(Key(Code.RouteBlocked)) >= 1, "the refused step must be reported as route-blocked");
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task Pump_WhenTheMeshWalkDropsAStoreyHeightStepWithNoFloorOrSightLine_IsRefused()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            using AnomalyCapture capture = H1AnomalyKit.CaptureFor(session.Player!);
            const float drop = 3f; // over the step height, within the run and the stair cap: only the direction refuses it
            List<(float X, float Z)> steps = await WalkNavigatedRouteAsync(host, session, x0 =>
            {
                float wallX = x0 + 5f;
                WorldCollision.Of(host.World).Install(
                    lineOfSight: new NoFloorLineOfSight(wallX),
                    pathfinder: new StoreyMeshPathfinder(wallX, Floor, Floor - drop));
            }, endRise: -drop, pumps: 4);

            Assert.All(steps, s => Assert.True(s.Z >= Floor - PlayerbotMoveCheck.DirectStepMaxStepUpYards, $"bot dropped to z {s.Z} at x {s.X}"));
            Assert.True(capture.Count(Key(Code.RouteBlocked)) >= 1, "the refused step must be reported as route-blocked");
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task Pump_WhenTheMeshSurfaceIsOnlyFoundByAVerticalSearchAndTheStepClimbs_IsAcceptedAsAStair()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            using AnomalyCapture capture = H1AnomalyKit.CaptureFor(session.Player!);
            // The crypt stairs' foot: the mesh walk past the wall fails, and a floor query straight under the step finds the polygon
            // 3 yd up, within the step's run and the stair cap.
            const float rise = 3f;
            float wallX = 0f;
            List<(float X, float Z)> steps = await WalkNavigatedRouteAsync(host, session, x0 =>
            {
                wallX = x0 + 5f;
                WorldCollision.Of(host.World).Install(
                    lineOfSight: new NoFloorLineOfSight(wallX),
                    pathfinder: new UnconnectedMeshPathfinder(wallX, Floor, Floor + rise));
            }, endRise: rise, pumps: 4);

            Assert.True(steps[^1].X > wallX, "bot did not climb past the stair");
            Assert.InRange(steps[^1].Z, Floor + rise - 0.5f, Floor + rise + 0.5f);
            Assert.Equal(0, capture.Count(Key(Code.RouteBlocked)));
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task Pump_WhenTheMeshSurfaceIsOnlyFoundByAVerticalSearchAndTheStepDrops_IsRefused()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            using AnomalyCapture capture = H1AnomalyKit.CaptureFor(session.Player!);
            // Past the wall the mesh walk fails (the polygon there is not connected to the one the bot stands on), but a floor query
            // straight under the step finds a polygon 3 yd down: the surface is not trusted for a drop (#1648).
            List<(float X, float Z)> steps = await WalkNavigatedRouteAsync(host, session, x0 =>
            {
                float wallX = x0 + 5f;
                WorldCollision.Of(host.World).Install(
                    lineOfSight: new NoFloorLineOfSight(wallX),
                    pathfinder: new UnconnectedMeshPathfinder(wallX, Floor, Floor - 3f));
            }, endRise: -3f, pumps: 4);

            Assert.All(steps, s => Assert.True(s.Z >= Floor - PlayerbotMoveCheck.DirectStepMaxStepUpYards, $"bot dropped to z {s.Z} at x {s.X}"));
            Assert.True(capture.Count(Key(Code.RouteBlocked)) >= 1, "the refused step must be reported as route-blocked");
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>The mesh walks west of <paramref name="wallX"/> (surface <paramref name="westFloor"/>); a walk past it fails, and only a floor query under the point answers <paramref name="eastFloor"/>.</summary>
    private sealed class UnconnectedMeshPathfinder(float wallX, float westFloor, float eastFloor, bool answersFloorQuery = true) : IPathfinder, IMeshPointQuery, IMeshFloorQuery
    {
        public bool Enabled => true;
        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => new(PathType.Normal, [start, end]);
        public bool TryGetRandomPointAround(uint mapId, Vector3 center, float radius, Random random, PathOptions? options, out Vector3 point)
        { point = center; return false; }
        public MeshRaycastResult Raycast(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => start.X >= wallX ? MeshRaycastResult.Unavailable(end) // the polygon past the wall is not connected to the mesh the walks run on
            : end.X >= wallX
                ? new MeshRaycastResult(true, true, 0.5f, new Vector3(wallX, start.Y, start.Z))
                : new MeshRaycastResult(true, false, 1f, end with { Z = westFloor });
        public bool TryGetMeshFloor(uint mapId, float x, float y, float z, float below, float above, out float height)
        {
            // Only the symmetric window of the bot's vertical search answers: the floor probe asks for a drop below and a small offset above, and finds nothing.
            height = x >= wallX ? eastFloor : westFloor;
            return answersFloorQuery && MathF.Abs(below - above) < 1e-3f;
        }
    }

    /// <summary>A wall at <paramref name="wallX"/> that nothing sees across, and no model floor anywhere (the floor probe finds nothing).</summary>
    private sealed class NoFloorLineOfSight(float wallX) : ILineOfSight
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true)
            => !((from.X < wallX && to.X > wallX) || (from.X > wallX && to.X < wallX));
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit) { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => null;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info) { info = default; return false; }
    }

    /// <summary>
    /// Starts a navigated route along +X from the bot (at <see cref="Floor"/>, rising by <paramref name="endRise"/> over 20 yd) after
    /// <paramref name="install"/> set the collision up for the bot's x, then pumps once per heartbeat and returns the bot's x and z after each.
    /// </summary>
    private static async Task<List<(float X, float Z)>> WalkNavigatedRouteAsync(
        WorldTestHost host, WorldSession session, Action<float> install, float endRise, int pumps, int heartbeatsPerPump = 1, float moveSpeed = 7.0f)
    {
        var options = new PlayerbotOptions { Enabled = true, MoveSpeed = moveSpeed };
        bool started = await host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            float x0 = player.X;
            install(x0);
            player.Relocate(x0, player.Y, Floor, 0, host.World.NowMs);
            var start = new Vector3(x0, player.Y, Floor);
            var route = new PlayerbotRoute([start, start + new Vector3(20f, 0f, endRise)], 20f, navigated: true);
            session.ManagedBudget = new ManagedActionBudget(4);
            return PlayerbotMotion.Follow(session, player, route, options, host.World.NowMs);
        });
        Assert.True(started, "the first stretch of the route is on the lower floor and must start");

        var seen = new List<(float X, float Z)>();
        for (int i = 0; i < pumps; i++)
        {
            await host.World.AdvanceClockAsync(PlayerbotMotion.HeartbeatIntervalMs * (uint)heartbeatsPerPump);
            seen.Add(await host.OnWorldAsync(() =>
            {
                PlayerbotMotion.Pump(session, session.Player!, host.World.NowMs);
                return (session.Player!.X, session.Player.Z);
            }));
        }

        return seen;
    }

    /// <summary>
    /// A wall at <paramref name="wallX"/> across every height: a segment crossing it is blocked, except (door band not NaN) one whose two
    /// ends both lie within [<paramref name="doorLow"/>, <paramref name="doorHigh"/>]. The model floor is the west one before the wall
    /// and the east one at and beyond it.
    /// </summary>
    private sealed class StoreyLineOfSight(float wallX, float westFloor, float eastFloor, float doorLow, float doorHigh) : ILineOfSight
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true)
        {
            bool crosses = (from.X < wallX && to.X > wallX) || (from.X > wallX && to.X < wallX);
            if (!crosses) return true;
            if (float.IsNaN(doorLow)) return false;
            return from.Z >= doorLow && from.Z <= doorHigh && to.Z >= doorLow && to.Z <= doorHigh;
        }
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit) { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => x >= wallX ? eastFloor : westFloor;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info) { info = default; return false; }
    }

    /// <summary>A mesh walk answers the surface under the end of the segment (west floor before <paramref name="wallX"/>, else east); a zero-length walk has no answer unless <c>proveStart</c> (then it answers the start's surface, so a step is proven to stay on the mesh).</summary>
    private sealed class StoreyMeshPathfinder(float wallX, float westFloor, float eastFloor, bool proveStart = false) : IPathfinder, IMeshPointQuery
    {
        public bool Enabled => true;
        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => new(PathType.Normal, [start, end]);
        public bool TryGetRandomPointAround(uint mapId, Vector3 center, float radius, Random random, PathOptions? options, out Vector3 point)
        { point = center; return false; }
        public MeshRaycastResult Raycast(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            if (Vector2.DistanceSquared(new Vector2(start.X, start.Y), new Vector2(end.X, end.Y)) < 1e-6f)
            {
                return proveStart
                    ? new MeshRaycastResult(true, false, 1f, start with { Z = start.X >= wallX ? eastFloor : westFloor })
                    : MeshRaycastResult.Unavailable(end);
            }

            return new MeshRaycastResult(true, false, 1f, end with { Z = end.X >= wallX ? eastFloor : westFloor });
        }
    }

    private sealed class FlatFloor(float floor) : ILineOfSight
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit) { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => floor;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info) { info = default; return false; }
    }

    private sealed class M2WallAtX(float floor, float wallX) : ILineOfSight
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true)
            => ignoreM2 || !((from.X < wallX && to.X > wallX) || (from.X > wallX && to.X < wallX));
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit) { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => floor;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info) { info = default; return false; }
    }

    /// <summary>A doodad (rock) of <paramref name="rockYards"/> flat radius around the origin blocks every ignoreM2:false sight starting inside it; an optional M2 wall at <paramref name="wallX"/> (NaN: none) blocks those crossing it.</summary>
    private sealed class RockUnderBot(float floor, float rockYards, float wallX) : ILineOfSight
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true)
            => ignoreM2 || (MathF.Sqrt((from.X * from.X) + (from.Y * from.Y)) > rockYards
                && (float.IsNaN(wallX) || !((from.X < wallX && to.X > wallX) || (from.X > wallX && to.X < wallX))));
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit) { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => floor;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info) { info = default; return false; }
    }

    private sealed class WallAtXWithHoleAtCorner(float floor, float wallX, float holeX) : ILineOfSight
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true)
            => !((from.X < wallX && to.X > wallX) || (from.X > wallX && to.X < wallX));
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit) { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance)
            => MathF.Abs(x - holeX) < 0.25f ? null : floor;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info) { info = default; return false; }
    }

    private sealed class HighProbeOnlyLedge(float floor) : ILineOfSight
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit) { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance)
            => z >= floor + 3.5f ? floor + 0.5f : floor - 20f;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info) { info = default; return false; }
    }

    private sealed class FragmentAtOriginPathfinder(float floor) : IPathfinder
    {
        public bool Enabled => true;
        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            if (start.X < 2f)
            {
                return new PathResult(PathType.Incomplete, [new Vector3(start.X, start.Y, floor), new Vector3(start.X + 0.5f, start.Y, floor)]);
            }
            return new PathResult(PathType.Normal, [new Vector3(start.X, start.Y, floor), new Vector3(end.X, end.Y, floor)]);
        }
        public bool TryGetRandomPointAround(uint mapId, Vector3 center, float radius, Random random, PathOptions? options, out Vector3 point)
        { point = center; return false; }
    }

    private sealed class SteepSlopePathfinder(float floor) : IPathfinder, IMeshPointQuery
    {
        public bool Enabled => true;
        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => new(PathType.Normal, [start, end]);
        public bool TryGetRandomPointAround(uint mapId, Vector3 center, float radius, Random random, PathOptions? options, out Vector3 point)
        { point = center; return false; }
        public MeshRaycastResult Raycast(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            if (options is not { } opts || (opts.IncludeFlags & NavTerrain.SteepSlopes) == 0 || MathF.Abs(start.Z - floor) > 4f)
            {
                return new MeshRaycastResult(false, false, 0f, start);
            }
            return new MeshRaycastResult(true, false, 1f, end with { Z = floor });
        }
    }

    private sealed class CliffAtX(float floor, float dropStartX) : ILineOfSight
    {
        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        { hit = to; return false; }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance)
            => x >= dropStartX ? floor - 30f : floor;

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        { info = default; return false; }
    }

    private sealed class CliffBelowX(float floor, float dropBelowX) : ILineOfSight
    {
        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        { hit = to; return false; }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance)
            => x <= dropBelowX ? floor - 30f : floor;

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        { info = default; return false; }
    }

    private sealed class DoorwayWithLintelAtX(float floor, float lintelX, float lintelZ) : ILineOfSight
    {
        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true)
            => MathF.Max(from.Z, to.Z) < lintelZ + 0.5f;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        { hit = to; return false; }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance)
            => MathF.Abs(x - lintelX) < 0.5f && z >= lintelZ ? lintelZ : floor;

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        { info = default; return false; }
    }
}
