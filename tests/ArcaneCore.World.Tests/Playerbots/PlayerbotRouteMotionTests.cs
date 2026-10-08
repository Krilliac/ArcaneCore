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
}
