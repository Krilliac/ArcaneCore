using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Navigation;

/// <summary>
/// <c>bot-motion/route-blocked</c> (#1485, 234k occurrences from 256 bots at one spot): the loop guard counted consecutive rejections per
/// destination, so every new goal whose route broke at the same place restarted the count at 1 and the give-up never fired.
/// </summary>
public sealed class PlayerbotRouteAnchorRejectionTests
{
    private const float Floor = 47.0f;

    private static readonly Vector3 Start = new(1320.3f, -4656.3f, Floor);
    private static readonly Vector3 StuckWaypoint = new(1320.7f, -4656.3f, Floor);

    [Fact]
    public async Task RejectionsAtOneAnchor_ForDifferentDestinations_CountConsecutively_AndTripTheLoopGuard()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            var destinations = new[]
            {
                new Vector3(1324.0f, -4656.3f, Floor),
                new Vector3(1324.0f, -4660.0f, Floor),
                new Vector3(1322.0f, -4652.0f, Floor),
            };

            (int[] counts, bool loopPending, bool lastBlacklisted) = await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new BrokenPocket());
                Player player = session.Player!;
                player.Relocate(Start.X, Start.Y, Start.Z, 0f, host.World.NowMs);
                var options = new PlayerbotOptions { Enabled = true };
                var seen = new int[destinations.Length];
                for (int attempt = 0; attempt < destinations.Length; attempt++)
                {
                    session.ManagedBudget = new ManagedActionBudget(4);
                    var route = new PlayerbotRoute([Start, StuckWaypoint, destinations[attempt]], Vector3.Distance(Start, destinations[attempt]), navigated: false);
                    Assert.False(PlayerbotMotion.Follow(session, player, route, options, host.World.NowMs + (uint)((attempt + 1) * 400)));
                    seen[attempt] = PlayerbotMotion.ConsecutiveRejections(player);
                }

                return (seen, PlayerbotMotion.ConsumeLoop(player), PlayerbotMotion.IsBlacklisted(player, destinations[^1]));
            });

            // The guard clears the count in the call that reaches MaxRejectionsBeforeLoop, so the third read is 0 (loopPending and the blacklist prove it fired).
            Assert.Equal([1, 2, 0], counts);
            Assert.True(loopPending, "Expected the loop guard to fire at MaxRejectionsBeforeLoop rejections at one anchor.");
            Assert.True(lastBlacklisted, "Expected the third destination to be given up.");
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    [Fact]
    public async Task RejectionsAtDifferentAnchors_ForDifferentDestinations_DoNotCountTogether()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            int[] counts = await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new BrokenPocket());
                Player player = session.Player!;
                var options = new PlayerbotOptions { Enabled = true };
                var seen = new int[3];
                for (int attempt = 0; attempt < seen.Length; attempt++)
                {
                    // 40 yards between the places: the pocket (radius 8) is around the start only, so move the pocket with the bot.
                    var at = new Vector3(Start.X + (attempt * 40f), Start.Y, Floor);
                    WorldCollision.Of(host.World).Install(lineOfSight: new BrokenPocket(new Vector2(at.X + 0.4f, at.Y)));
                    player.Relocate(at.X, at.Y, at.Z, 0f, host.World.NowMs);
                    var waypoint = at + new Vector3(0.4f, 0f, 0f);
                    var destination = at + new Vector3(3.7f, -(attempt + 1) * 2f, 0f);
                    session.ManagedBudget = new ManagedActionBudget(4);
                    var route = new PlayerbotRoute([at, waypoint, destination], Vector3.Distance(at, destination), navigated: false);
                    Assert.False(PlayerbotMotion.Follow(session, player, route, options, host.World.NowMs + (uint)((attempt + 1) * 400)));
                    seen[attempt] = PlayerbotMotion.ConsecutiveRejections(player);
                }

                return seen;
            });

            Assert.Equal([1, 1, 1], counts);
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    /// <summary>Flat ground at <see cref="Floor"/> except a pocket with no floor (the walk breaks there).</summary>
    private sealed class BrokenPocket : ILineOfSight
    {
        private readonly Vector2 _center;

        internal BrokenPocket() : this(new Vector2(1320.7f, -4656.3f)) { }

        internal BrokenPocket(Vector2 center) => _center = center;

        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            return false;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance)
            => Vector2.Distance(new Vector2(x, y), _center) <= 8.0f ? TerrainTile.InvalidHeightValue : Floor;

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        {
            info = default;
            return false;
        }
    }
}
