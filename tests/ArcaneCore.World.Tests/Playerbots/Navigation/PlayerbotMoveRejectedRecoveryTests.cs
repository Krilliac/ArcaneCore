using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Code = ArcaneCore.World.Playerbots.PlayerbotMotionAnomalies.Code;

namespace ArcaneCore.World.Tests.Playerbots.Navigation;

/// <summary>
/// Regression and recovery tests for <c>bot-motion/move-rejected</c> / <c>start-blocked</c> (<c>NoGroundSupport</c> spin loops) and
/// <c>bot-motion/airwalk</c> (<c>DirectStep</c> elevation drift above walkable floor).
/// </summary>
public sealed class PlayerbotMoveRejectedRecoveryTests
{
    private const float Floor = 47.0f;

    [Fact]
    public async Task RepeatedNoGroundSupportRejections_BlacklistDestination_AndBreakSpinLoopWithinThreeAttempts()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            using AnomalyCapture cap = H1AnomalyKit.CaptureFor(session.Player!);
            var ground = new PocketGround
            {
                DefaultFloor = Floor,
                InvalidPocketCenter = new Vector2(1320.7f, -4656.3f),
                InvalidPocketRadius = 8.0f,
            };

            int attemptsUntilBlacklisted = 0;
            bool loopConsumed = false;
            bool blacklistedAfterRecovery = false;
            int followCallsAcceptedOrRetried = 0;

            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: ground);
                Player player = session.Player!;
                var start = new Vector3(1320.3f, -4656.3f, Floor);
                var stuckWaypoint = new Vector3(1320.7f, -4656.3f, Floor);
                var destination = new Vector3(1324.0f, -4656.3f, Floor);
                player.Relocate(start.X, start.Y, start.Z, 0f, host.World.NowMs);

                var options = new PlayerbotOptions { Enabled = true };
                for (int attempt = 1; attempt <= 50; attempt++)
                {
                    if (PlayerbotMotion.IsBlacklisted(player, destination))
                    {
                        blacklistedAfterRecovery = true;
                        break;
                    }

                    followCallsAcceptedOrRetried++;
                    session.ManagedBudget = new ManagedActionBudget(4);
                    var route = new PlayerbotRoute([start, stuckWaypoint, destination], Vector3.Distance(start, destination), navigated: false);
                    bool started = PlayerbotMotion.Follow(session, player, route, options, host.World.NowMs + (uint)(attempt * 400));
                    Assert.False(started);
                    attemptsUntilBlacklisted = attempt;

                    if (PlayerbotMotion.ConsumeLoop(player))
                    {
                        loopConsumed = true;
                    }
                }

                blacklistedAfterRecovery = PlayerbotMotion.IsBlacklisted(player, destination);
                return true;
            });

            Assert.Equal(PlayerbotMotion.MaxRejectionsBeforeLoop, attemptsUntilBlacklisted);
            Assert.Equal(PlayerbotMotion.MaxRejectionsBeforeLoop, followCallsAcceptedOrRetried);
            Assert.True(loopConsumed, "Expected PlayerbotMotion.ConsumeLoop to fire once MaxRejectionsBeforeLoop was reached.");
            Assert.True(blacklistedAfterRecovery, "Expected the stuck destination to be blacklisted after MaxRejectionsBeforeLoop rejections.");
            Assert.True(cap.Any(PlayerbotMotionAnomalies.Key(Code.StartBlocked)), cap.Describe());
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    [Fact]
    public async Task NearbyBlockedIntermediateMicroCorner_AdvancesToNextWalkableWaypoint_AndStartsCleanly()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            using AnomalyCapture cap = H1AnomalyKit.CaptureFor(session.Player!);
            var start = new Vector3(1320.3f, -4656.3f, Floor);
            var badCorner = new Vector3(1322.1f, -4656.3f, Floor); // 1.80 yd east on an invalid lip
            var clearWaypoint = new Vector3(1320.3f, -4650.0f, Floor); // 6.30 yd north on valid ground

            var ground = new PocketGround
            {
                DefaultFloor = Floor,
                InvalidPocketCenter = new Vector2(badCorner.X, badCorner.Y),
                InvalidPocketRadius = 0.60f,
            };

            bool started = await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: ground);
                Player player = session.Player!;
                player.Relocate(start.X, start.Y, start.Z, 0f, host.World.NowMs);
                session.ManagedBudget = new ManagedActionBudget(4);
                var route = new PlayerbotRoute([start, badCorner, clearWaypoint], 8f, navigated: false);
                return PlayerbotMotion.Follow(session, player, route, new PlayerbotOptions { Enabled = true }, host.World.NowMs);
            });

            Assert.True(started, "Expected Follow to skip the blocked nearby intermediate corner and start along the clear leg.");
            Assert.False(cap.Any(PlayerbotMotionAnomalies.Key(Code.StartBlocked)), cap.Describe());
            Assert.False(cap.Any(PlayerbotMotionAnomalies.Key(Code.MoveRejected)), cap.Describe());
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    [Fact]
    public async Task DirectStepRouteWithElevatedWaypoints_DrapesToWalkableFloor_WithoutAirwalkAnomaly()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            using AnomalyCapture cap = H1AnomalyKit.CaptureFor(session.Player!);
            const float realFloorZ = 5.91f;
            const float floatingZ = 15.84f; // 9.93 yd above floor (live Gordrum DirectStep case)
            var start = new Vector3(119.3f, -4799.2f, floatingZ);
            var end = new Vector3(135.0f, -4799.2f, floatingZ);

            var ground = new PocketGround
            {
                DefaultFloor = realFloorZ,
                InvalidPocketRadius = 0f,
            };

            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: ground);
                Player player = session.Player!;
                player.Relocate(start.X, start.Y, start.Z, 0f, host.World.NowMs);
                session.ManagedBudget = new ManagedActionBudget(4);
                var route = new PlayerbotRoute([start, end], Vector3.Distance(start, end), navigated: false);
                Assert.True(PlayerbotMotion.Follow(session, player, route, new PlayerbotOptions { Enabled = true }, host.World.NowMs));
                return true;
            });

            for (int tick = 0; tick < 5; tick++)
            {
                await host.World.AdvanceClockAsync(600);
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    PlayerbotMotion.Pump(session, player, host.World.NowMs);
                    Assert.InRange(player.Z, realFloorZ - 0.1f, realFloorZ + 0.5f);
                    return true;
                });
            }

            Assert.False(cap.Any(PlayerbotMotionAnomalies.Key(Code.Airwalk)), cap.Describe());
            Assert.False(cap.Any(PlayerbotMotionAnomalies.Key(Code.RouteBlocked)), cap.Describe());
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    private sealed class PocketGround : ILineOfSight
    {
        internal float DefaultFloor { get; set; } = Floor;
        internal Vector2 InvalidPocketCenter { get; set; }
        internal float InvalidPocketRadius { get; set; }

        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            return false;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance)
        {
            if (InvalidPocketRadius > 0f && Vector2.Distance(new Vector2(x, y), InvalidPocketCenter) <= InvalidPocketRadius)
            {
                return TerrainTile.InvalidHeightValue;
            }

            return DefaultFloor;
        }

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        {
            info = default;
            return false;
        }
    }
}
