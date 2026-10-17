using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Motion defects behind bots that stood still or crawled on the live server (2026-10-08): a route around an obstacle the motion
/// refused, and a ghost that walked instead of running. Time moves only on the manual world clock.
/// </summary>
public sealed class PlayerbotRouteMotionTests
{
    private const float Floor = 83.53f;

    /// <summary>
    /// A navigation-mesh route turns around a pillar: its legs are clear, but a straight line from a point before the corner to one
    /// after it crosses the pillar. The bot walks the route to its end. Before, the motion tested exactly such lines (from its last
    /// heartbeat, and two yards ahead at every start) and stopped short of the corner for good: Dawnrover beside William Pestle in
    /// the Goldshire inn.
    /// </summary>
    [Fact]
    public async Task ARouteAroundAPillar_IsWalkedToItsEnd_ThoughALineAcrossTheCornerIsBlocked()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IWorldFeature, ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Vector3 origin = await host.OnWorldAsync(() => new Vector3(session.Player!.X, session.Player.Y, Floor));
            // One yard before a right-angle corner, with a pillar inside the corner one yard from both legs.
            Vector3 start = origin + new Vector3(2, 0, 0);
            Vector3 corner = origin + new Vector3(3, 0, 0);
            Vector3 end = origin + new Vector3(3, -4, 0);
            var pillar = new PillarFloor(origin + new Vector3(2, -1, 0), 0.9f);
            Vector3[] path = [start, corner, end];
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: pillar, pathfinder: new AroundThePillar(path));
                session.Player!.Relocate(start.X, start.Y, start.Z, 0, host.World.NowMs);
                return true;
            });
            Vector3 eye = Vector3.UnitZ * 2;
            Assert.True(pillar.IsInLineOfSight(0, start, corner) && pillar.IsInLineOfSight(0, corner, end)); // the legs are clear
            Assert.False(pillar.IsInLineOfSight(0, start + eye, corner + new Vector3(0, -1, 0) + eye)); // two yards along: blocked

            var options = new PlayerbotOptions { Enabled = true };
            PlayerbotRoute? route = null;
            for (int think = 0; think < 60; think++)
            {
                bool done = await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    if (Vector2.Distance(new(player.X, player.Y), new(end.X, end.Y)) < 0.5f) return true;
                    session.ManagedBudget = new ManagedActionBudget(4);
                    if (route is null && !PlayerbotNavigation.TryPlan(player, end, options, out route)) return false;
                    if (!PlayerbotNavigation.TryAdvance(session, route!, options, 100, host.World.NowMs)) route = null;
                    return false;
                });
                if (done) break;
                await host.World.AdvanceClockAsync(50);
                await host.OnWorldAsync(() => PlayerbotMotion.Pump(session, session.Player!, host.World.NowMs));
                await host.World.AdvanceClockAsync(50);
                await host.OnWorldAsync(() => PlayerbotMotion.Pump(session, session.Player!, host.World.NowMs));
            }

            float left = await host.OnWorldAsync(() => Vector2.Distance(new(session.Player!.X, session.Player.Y), new(end.X, end.Y)));
            Assert.True(left < 0.5f, $"stopped {left:F2} yards from the end of the route");
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// A mesh route's corner is stored at the coarse polygon height, a chord 12 yd under the detail surface — further than the mesh
    /// query's vertical window (5 yd), so a walk that starts at that corner is "no mesh". The bot is on the surface. One tick passes the
    /// corner. The bot stays on the surface. Before, the step walked from the corner's stored height, the walk failed, the 4 yd floor
    /// window around the chord found nothing, and the bot kept the chord (live bot-motion/under-map and airwalk, 8-30 yd off the ground,
    /// the moment a heartbeat passes such a corner).
    /// </summary>
    [Fact]
    public async Task AStepThatPassesACorner_KeepsTheMeshSurface_WhenTheCornerHeightIsAChord()
    {
        const float surface = Floor + 12f;
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IWorldFeature, ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Vector3 origin = await host.OnWorldAsync(() => new Vector3(session.Player!.X, session.Player.Y, surface));
            Vector3 start = origin;
            Vector3 corner = new(origin.X + 4f, origin.Y, Floor); // the chord, 12 yd under the surface
            Vector3 end = new(corner.X, corner.Y - 20f, Floor);
            var mesh = new SurfaceWithinFiveYards(surface);
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatModelFloor(surface), pathfinder: mesh);
                session.Player!.Relocate(start.X, start.Y, start.Z, 0, host.World.NowMs);
                return true;
            });

            var route = new PlayerbotRoute([start, corner, end], 24f, navigated: true);
            bool started = await host.OnWorldAsync(() =>
            {
                session.ManagedBudget = new ManagedActionBudget(4);
                return PlayerbotMotion.Follow(session, session.Player!, route, new PlayerbotOptions { Enabled = true }, host.World.NowMs);
            });
            Assert.True(started, "the route did not start");

            // 1.5 s at 7 yd/s is 10.5 yd: past the corner 4 yd ahead, still inside the 2 s lease.
            await host.World.AdvanceClockAsync(1500);
            await host.OnWorldAsync(() => PlayerbotMotion.Pump(session, session.Player!, host.World.NowMs));
            (float x, float y, float z, bool moving) = await host.OnWorldAsync(() =>
                (session.Player!.X, session.Player!.Y, session.Player!.Z, (session.Player!.Movement.Flags & MovementFlags.Forward) != 0));
            Assert.True(moving, "the tick stopped the bot");
            Assert.True(y < origin.Y - 2f, $"the tick did not pass the corner (y {y}, start {origin.Y})");
            Assert.InRange(z, surface - 0.5f, surface + 1f);
            Assert.True(mesh.WalksFromOffTheSurface == 0, "a walk started at the corner's chord height, outside the mesh window");
            _ = x;
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// A bot whose run speed is raised (a ghost runs faster) runs at it under the default cap, the base run speed; a cap below the
    /// base run speed still walks. Before, the cap was compared with the raised speed, so every ghost walked: Mirthblade's 359-yard
    /// corpse run took two minutes.
    /// </summary>
    [Fact]
    public async Task ARaisedRunSpeed_IsRun_UnderTheDefaultCap()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                UnitSpeed.SetRate(player, MoveType.Run, 1.5f);
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(PlayerbotMovementControl.Update(session, player));
                Assert.Equal(Unit.BaseRunSpeed * 1.5f, UnitSpeed.Get(player, MoveType.Run), 3);

                Assert.True(PlayerbotMotion.Speed(player, new PlayerbotOptions().MoveSpeed, out float speed, out bool walk));
                Assert.False(walk);
                Assert.Equal(Unit.BaseRunSpeed * 1.5f, speed, 3);

                Assert.True(PlayerbotMotion.Speed(player, 3f, out _, out walk));
                Assert.True(walk);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>A flat floor with a round pillar: a line between two points (in the plane) closer than the radius to it is blocked.</summary>
    private sealed class PillarFloor(Vector3 pillar, float radius) : ILineOfSight
    {
        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true)
        {
            Vector2 a = new(from.X, from.Y), b = new(to.X, to.Y), p = new(pillar.X, pillar.Y);
            Vector2 ab = b - a;
            float t = ab.LengthSquared() < 1e-6f ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
            return Vector2.Distance(a + (ab * t), p) >= radius;
        }

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        { hit = to; return false; }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => Floor;

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        { info = default; return false; }
    }

    /// <summary>The navigation mesh's answer around the pillar (from wherever the bot stands to the route's end).</summary>
    private sealed class AroundThePillar(Vector3[] path) : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            if (Vector2.Distance(new(end.X, end.Y), new(path[^1].X, path[^1].Y)) > 0.5f) return PathResult.None(start);
            Vector3 corner = path[1];
            // Before the corner (on the first leg) the path turns at it; past it only the last leg is left.
            return start.Y > corner.Y - 0.05f && start.X < corner.X - 0.05f
                ? new PathResult(PathType.Normal, [start, corner, path[^1]])
                : new PathResult(PathType.Normal, [start, path[^1]]);
        }
    }

    private sealed class ManualClock : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }

    /// <summary>A model floor at one height, so the motion's height query does not invent a second storey.</summary>
    private sealed class FlatModelFloor(float floor) : ILineOfSight
    {
        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        { hit = to; return false; }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => floor;

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        { info = default; return false; }
    }

    /// <summary>
    /// A flat detail surface. A walk starts only within 5 yards of it, the navigation mesh's nearest-polygon window; a query from a
    /// corner's chord does not find it and is counted.
    /// </summary>
    private sealed class SurfaceWithinFiveYards(float surface) : IPathfinder, IMeshPointQuery, IMeshFloorQuery
    {
        internal int WalksFromOffTheSurface { get; private set; }

        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => new(PathType.Normal, [start, end]);

        public bool TryGetRandomPointAround(uint mapId, Vector3 center, float radius, Random random, PathOptions? options, out Vector3 point)
        { point = center; return false; }

        public MeshRaycastResult Raycast(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            if (MathF.Abs(start.Z - surface) > 5f)
            {
                WalksFromOffTheSurface++;
                return MeshRaycastResult.Unavailable(end);
            }

            return new MeshRaycastResult(true, false, 1f, end with { Z = surface });
        }

        public bool TryGetMeshFloor(uint mapId, float x, float y, float z, float below, float above, out float height)
        {
            height = surface;
            return surface >= z - below && surface <= z + above;
        }
    }
}
