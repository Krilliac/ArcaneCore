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
