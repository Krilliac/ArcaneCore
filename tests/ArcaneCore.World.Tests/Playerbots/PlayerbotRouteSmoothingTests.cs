using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Verification of <see cref="PlayerbotRouteSmoothing"/>: per-bot outdoor corridor variation (eliminating single-file conga lines),
/// quadratic Bezier corner-cutting spline smoothing, smooth heading progression, and obstacle/navmesh safety fallbacks.
/// </summary>
public sealed class PlayerbotRouteSmoothingTests
{
    private const float Floor = 83.53f;

    [Fact]
    public async Task MultipleBotsOnTheSameOutdoorCorridor_FollowDistinctVariedPaths_WhileSharingExactEndpoints()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                Map map = player.Map!;
                Vector3 start = new(player.X, player.Y, Floor);
                Vector3 end = start + new Vector3(90f, 0f, 0f);

                WorldCollision.Of(host.World).Install(
                    lineOfSight: new OpenOutdoorFloor(Floor),
                    pathfinder: new StraightCorridorMesh([start, end]));

                IReadOnlyList<Vector3> pathBot1 = PlayerbotRouteSmoothing.VaryAndSmoothPoints(101UL, map, [start, end], varyLateral: true);
                IReadOnlyList<Vector3> pathBot2 = PlayerbotRouteSmoothing.VaryAndSmoothPoints(202UL, map, [start, end], varyLateral: true);
                IReadOnlyList<Vector3> pathBot3 = PlayerbotRouteSmoothing.VaryAndSmoothPoints(303UL, map, [start, end], varyLateral: true);

                // All three bots start and end at the exact same coordinates.
                Assert.Equal(start, pathBot1[0]);
                Assert.Equal(start, pathBot2[0]);
                Assert.Equal(start, pathBot3[0]);
                Assert.Equal(end, pathBot1[^1]);
                Assert.Equal(end, pathBot2[^1]);
                Assert.Equal(end, pathBot3[^1]);

                // Each bot's path is subdivided and laterally offset off the Y = start.Y centerline.
                Assert.True(pathBot1.Count >= 6, $"expected subdivided varied path, got {pathBot1.Count} points");
                Assert.True(pathBot2.Count >= 6, $"expected subdivided varied path, got {pathBot2.Count} points");
                Assert.True(pathBot3.Count >= 6, $"expected subdivided varied path, got {pathBot3.Count} points");

                float midX = start.X + 45f;
                float y1 = SampleYAtX(pathBot1, midX);
                float y2 = SampleYAtX(pathBot2, midX);
                float y3 = SampleYAtX(pathBot3, midX);

                // None of the bots stays glued to the exact Y = start.Y Detour wire, and the three bots spread apart laterally.
                Assert.True(MathF.Abs(y1 - start.Y) > 0.25f, $"bot 1 stayed on centerline: dy={y1 - start.Y:F2}");
                Assert.True(MathF.Abs(y2 - start.Y) > 0.25f, $"bot 2 stayed on centerline: dy={y2 - start.Y:F2}");
                float spread = MathF.Max(MathF.Abs(y1 - y2), MathF.Max(MathF.Abs(y2 - y3), MathF.Abs(y1 - y3)));
                Assert.True(spread > 1.0f, $"expected lateral spread > 1.0 yard across bots, got {spread:F2}");
                return true;
            });
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    [Fact]
    public async Task ASharpCornerOnClearGround_IsSmoothedIntoAQuadraticBezierCurve_WithGradualTurnAngles()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IWorldFeature, ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                Map map = player.Map!;
                Vector3 start = new(player.X, player.Y, Floor);
                Vector3 corner = start + new Vector3(12f, 0f, 0f);
                Vector3 end = corner + new Vector3(0f, 12f, 0f);

                WorldCollision.Of(host.World).Install(
                    lineOfSight: new OpenOutdoorFloor(Floor),
                    pathfinder: new StraightCorridorMesh([start, corner, end]));
                player.Relocate(start.X, start.Y, start.Z, 0f, host.World.NowMs);

                var options = new PlayerbotOptions { Enabled = true };
                Assert.True(PlayerbotNavigation.TryPlan(player, end, options, out PlayerbotRoute? route));
                Assert.NotNull(route);

                // The 3-point right-angle path was smoothed into a 6-point quadratic Bezier curve (start, A, Q1, Q2, B, end).
                Assert.True(route!.Points.Count >= 6, $"expected >= 6 points after corner spline smoothing, got {route.Points.Count}");
                Assert.DoesNotContain(route.Points, p => Vector2.Distance(new(p.X, p.Y), new(corner.X, corner.Y)) < 0.2f);

                // Every consecutive chord turn is gradual (< 0.65 rad ~ 37 deg) instead of a single 90-degree (1.57 rad) snap.
                float maxStepTurn = MaxTurnBetweenSegments(route.Points);
                Assert.True(maxStepTurn < 0.65f, $"expected gradual turn steps < 0.65 rad, got {maxStepTurn:F3} rad");
                return true;
            });
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    [Fact]
    public async Task ACornerBlockedByAnInnerObstacle_IsNotCutThroughTheObstacle()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                Map map = player.Map!;
                Vector3 start = new(player.X, player.Y, Floor);
                Vector3 corner = start + new Vector3(12f, 0f, 0f);
                Vector3 end = corner + new Vector3(0f, 12f, 0f);
                // Place a pillar inside the inner corner so any corner-cutting chord intersects it.
                Vector3 pillarCenter = corner + new Vector3(-1.5f, 1.5f, 0f);
                var blockedFloor = new PillarOutdoorFloor(Floor, pillarCenter, 1.4f);

                WorldCollision.Of(host.World).Install(
                    lineOfSight: blockedFloor,
                    pathfinder: new StraightCorridorMesh([start, corner, end]));

                IReadOnlyList<Vector3> points = PlayerbotRouteSmoothing.VaryAndSmoothPoints(
                    player.Guid.Value, map, [start, corner, end], varyLateral: false);

                // Because the inner corner is blocked by the pillar, the sharp safe corner is preserved untouched.
                Assert.Equal(3, points.Count);
                Assert.Equal(corner, points[1]);
                return true;
            });
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    private static float SampleYAtX(IReadOnlyList<Vector3> points, float x)
    {
        for (int i = 1; i < points.Count; i++)
        {
            Vector3 a = points[i - 1], b = points[i];
            if ((a.X <= x && x <= b.X) || (b.X <= x && x <= a.X))
            {
                float dx = b.X - a.X;
                if (MathF.Abs(dx) < 1e-4f) return a.Y;
                float t = (x - a.X) / dx;
                return a.Y + ((b.Y - a.Y) * t);
            }
        }

        return points[^1].Y;
    }

    private static float MaxTurnBetweenSegments(IReadOnlyList<Vector3> points)
    {
        float maxTurn = 0f;
        for (int i = 1; i < points.Count - 1; i++)
        {
            Vector2 inVec = new(points[i].X - points[i - 1].X, points[i].Y - points[i - 1].Y);
            Vector2 outVec = new(points[i + 1].X - points[i].X, points[i + 1].Y - points[i].Y);
            if (inVec.LengthSquared() < 1e-6f || outVec.LengthSquared() < 1e-6f) continue;
            float cos = Math.Clamp(Vector2.Dot(Vector2.Normalize(inVec), Vector2.Normalize(outVec)), -1f, 1f);
            float turn = MathF.Acos(cos);
            if (turn > maxTurn) maxTurn = turn;
        }

        return maxTurn;
    }

    private sealed class OpenOutdoorFloor(float floor) : ILineOfSight
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit) { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => floor;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info) { info = default; return false; }
    }

    private sealed class PillarOutdoorFloor(float floor, Vector3 pillar, float radius) : ILineOfSight
    {
        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true)
        {
            Vector2 a = new(from.X, from.Y), b = new(to.X, to.Y), p = new(pillar.X, pillar.Y);
            Vector2 ab = b - a;
            float t = ab.LengthSquared() < 1e-6f ? 0f : Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0f, 1f);
            return Vector2.Distance(a + (ab * t), p) >= radius;
        }

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit) { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => floor;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info) { info = default; return false; }
    }

    private sealed class StraightCorridorMesh(Vector3[] waypoints) : IPathfinder, IMeshPointQuery
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => new(PathType.Normal, [start, .. waypoints.Skip(1)]);

        public bool TryGetRandomPointAround(uint mapId, Vector3 center, float radius, Random random, PathOptions? options, out Vector3 point)
        {
            point = center;
            return true;
        }

        public MeshRaycastResult Raycast(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => new(Available: true, Hit: false, Fraction: 1f, end);
    }

    private sealed class ManualClock : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }
}
