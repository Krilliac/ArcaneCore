using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Diagnostics;
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
            (bool started, bool complete) = await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                WorldCollision.Of(host.World).Install(lineOfSight: new CliffAtX(Floor, -1000f));
                player.Relocate(15f, 10f, 0f, 0f, host.World.NowMs);
                var route = new PlayerbotRoute([new Vector3(0f, 0f, 0f), new Vector3(0f, 50f, 0f)], 50f, navigated: true);
                session.ManagedBudget = new ManagedActionBudget(4);
                bool follow = PlayerbotMotion.Follow(session, player, route, new PlayerbotOptions { Enabled = true, MoveSpeed = 7.0f }, host.World.NowMs);
                return (follow, route.Complete);
            });

            Assert.False(started);
            Assert.True(complete);
            Assert.Equal(0, capture.Count(Key(Code.StartBlocked)));
        }
        finally { session.Kick(); await session.ManagedClosed; }
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
}
