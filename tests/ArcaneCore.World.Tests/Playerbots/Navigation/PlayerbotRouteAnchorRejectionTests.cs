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

    /// <summary>
    /// The producer of the corner memory (NoteStepRejected): three rejections at one anchor whose routes end more than SamePlaceYards (5) from it
    /// remember the corner the walk broke towards, and a later route through that corner is refused. The destinations of the tests above end
    /// within 5 yards of the anchor, where the guard deliberately remembers nothing.
    /// </summary>
    [Fact]
    public async Task LoopGuard_RemembersTheCornerTheWalkBrokeTowards_WhenItLiesOverFiveYardsFromTheAnchor()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            var destinations = new[]
            {
                new Vector3(1330.0f, -4656.3f, Floor),
                new Vector3(1330.0f, -4662.0f, Floor),
                new Vector3(1328.0f, -4650.0f, Floor),
            };

            (bool remembered, bool crossesIt, bool crossesElsewhere, int places) = await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new BrokenPocket());
                Player player = session.Player!;
                player.Relocate(Start.X, Start.Y, Start.Z, 0f, host.World.NowMs);
                var options = new PlayerbotOptions { Enabled = true };
                foreach (Vector3 far in destinations)
                    Assert.True(Vector2.Distance(new Vector2(far.X, far.Y), new Vector2(Start.X, Start.Y)) > PlayerbotMotion.SamePlaceYards, "fixture: the destination must be over SamePlaceYards from the anchor");

                uint at = host.World.NowMs;
                for (int attempt = 0; attempt < destinations.Length; attempt++)
                {
                    session.ManagedBudget = new ManagedActionBudget(4);
                    at += 400;
                    var route = new PlayerbotRoute([Start, StuckWaypoint, destinations[attempt]], Vector3.Distance(Start, destinations[attempt]), navigated: false);
                    Assert.False(PlayerbotMotion.Follow(session, player, route, options, at));
                }

                Assert.True(PlayerbotMotion.ConsumeLoop(player));
                PlayerbotMotion.MotionState state = PlayerbotMotion.Of(player);
                Vector3 last = destinations[^1];
                bool found = state.RejectedPlaces.Exists(entry => Vector2.Distance(new Vector2(entry.Place.X, entry.Place.Y), new Vector2(last.X, last.Y)) <= PlayerbotMotion.SamePlaceYards);
                var through = new PlayerbotRoute([Start + new Vector3(-3f, 0f, 0f), last + new Vector3(1f, 1f, 0f), last + new Vector3(30f, 0f, 0f)], 40f, navigated: false);
                var elsewhere = new PlayerbotRoute([Start + new Vector3(-3f, 0f, 0f), Start + new Vector3(-30f, 0f, 0f)], 27f, navigated: false);
                return (found, PlayerbotMotion.CrossesRejectedPlace(player, through), PlayerbotMotion.CrossesRejectedPlace(player, elsewhere), state.RejectedPlaces.Count);
            });

            Assert.True(remembered, "The corner the third rejected walk broke towards (over 5 yards from the anchor) must be remembered.");
            Assert.Equal(1, places);
            Assert.True(crossesIt, "A route through the remembered corner must be refused.");
            Assert.False(crossesElsewhere, "A route away from the remembered corner is not refused.");
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    /// <summary>
    /// The live #1485 signature is route-blocked, not start-blocked: the bot started (the ground ahead was valid) and a later Pump heartbeat found
    /// the ground ahead gone. Three such stops at one anchor for three destinations count consecutively, trip the loop guard, give the last
    /// destination up and remember its corner.
    /// </summary>
    [Fact]
    public async Task PumpRouteBlocked_AtOneAnchor_ForDifferentDestinations_CountsConsecutively_AndTripsTheLoopGuard()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            using AnomalyCapture cap = H1AnomalyKit.CaptureFor(session.Player!);
            var destinations = new[]
            {
                new Vector3(Start.X + 30f, Start.Y, Floor),
                new Vector3(Start.X + 30f, Start.Y - 12f, Floor),
                new Vector3(Start.X + 25f, Start.Y + 14f, Floor),
            };

            var ground = new GroundWithAHole();
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: ground);
                return true;
            });

            var options = new PlayerbotOptions { Enabled = true };
            var counts = new int[destinations.Length];
            for (int attempt = 0; attempt < destinations.Length; attempt++)
            {
                int index = attempt;
                await host.OnWorldAsync(() =>
                {
                    ground.Hole = null;
                    Player player = session.Player!;
                    player.Relocate(Start.X, Start.Y, Start.Z, 0f, host.World.NowMs);
                    session.ManagedBudget = new ManagedActionBudget(4);
                    var route = new PlayerbotRoute([Start, destinations[index]], Vector3.Distance(Start, destinations[index]), navigated: false);
                    Assert.True(PlayerbotMotion.Follow(session, player, route, options, host.World.NowMs), "the route did not start: the ground ahead is valid at the start");

                    // The ground ahead goes (a pocket across the first stretch, the bot itself outside it) before the next heartbeat.
                    ground.Hole = (new Vector2(Start.X + 4f, Start.Y), 3.5f);
                    return true;
                });

                await host.World.AdvanceClockAsync(600);
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    PlayerbotMotion.Pump(session, player, host.World.NowMs);
                    Assert.False(PlayerbotMotion.IsActive(player), "the heartbeat over the hole must stop the bot");
                    counts[index] = PlayerbotMotion.ConsecutiveRejections(player);
                    return true;
                });
            }

            (bool loopPending, bool lastBlacklisted, bool remembered) = await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                Vector3 last = destinations[^1];
                bool found = PlayerbotMotion.Of(player).RejectedPlaces.Exists(entry => Vector2.Distance(new Vector2(entry.Place.X, entry.Place.Y), new Vector2(last.X, last.Y)) <= PlayerbotMotion.SamePlaceYards);
                return (PlayerbotMotion.ConsumeLoop(player), PlayerbotMotion.IsBlacklisted(player, last), found);
            });


            Assert.Equal([1, 2, 0], counts);
            Assert.True(loopPending, "Expected the loop guard to fire at MaxRejectionsBeforeLoop route-blocked stops at one anchor.");
            Assert.True(lastBlacklisted, "Expected the third destination to be given up.");
            Assert.True(remembered, "Expected the corner the third walk broke towards to be remembered.");
            Assert.True(cap.Any(PlayerbotMotionAnomalies.Key(PlayerbotMotionAnomalies.Code.RouteBlocked)), cap.Describe());
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    /// <summary>Flat ground at <see cref="Floor"/> with an optional pocket (centre, radius) that has no floor; the pocket can appear after a route started.</summary>
    private sealed class GroundWithAHole : ILineOfSight
    {
        internal (Vector2 Center, float Radius)? Hole { get; set; }

        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            return false;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance)
            => Hole is { } hole && Vector2.Distance(new Vector2(x, y), hole.Center) <= hole.Radius ? TerrainTile.InvalidHeightValue : Floor;

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        {
            info = default;
            return false;
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
