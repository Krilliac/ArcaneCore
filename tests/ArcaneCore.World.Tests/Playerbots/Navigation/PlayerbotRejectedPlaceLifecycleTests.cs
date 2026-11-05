using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Navigation;

/// <summary>
/// The lifecycle of the place-keyed rejection memory (#1485): the count restarts after the loop guard fires, the bot's own standing place is never
/// remembered, the memory is dropped by a teleport and by a forced push, and it does not reach a bot leaving it or walking a floor above it.
/// </summary>
public sealed class PlayerbotRejectedPlaceLifecycleTests
{
    private const float Floor = 47.0f;

    private static readonly Vector3 Start = new(1320.3f, -4656.3f, Floor);
    private static readonly Vector3 StuckWaypoint = new(1320.7f, -4656.3f, Floor);

    [Fact]
    public async Task LoopGuard_NeverRemembersTheBotsOwnStandingPlace()
    {
        (bool leavesBack, _, _, _, bool remembersStanding) = await TripAsync();
        Assert.False(remembersStanding, "The place the bot stands at must not be remembered: every route away from it would be refused.");
        Assert.True(leavesBack, "A route leaving the anchor by a corner 3 yards away must not be refused.");
    }

    [Fact]
    public async Task LoopGuard_RestartsTheCount_SoTheNextGoalGetsItsOwnAttempts()
    {
        (_, int afterTrip, int fourthCount, bool fourthBlacklisted, _) = await TripAsync();
        Assert.Equal(0, afterTrip);
        Assert.Equal(1, fourthCount);
        Assert.False(fourthBlacklisted, "A new goal gets its own MaxRejectionsBeforeLoop attempts after the loop guard fired.");
    }

    /// <summary>Three rejections at one anchor (the loop guard fires), then a fourth goal from the same place.</summary>
    private static async Task<(bool LeavesBack, int AfterTrip, int FourthCount, bool FourthBlacklisted, bool RemembersStanding)> TripAsync()
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
            var fourth = new Vector3(1324.0f, -4662.0f, Floor);

            return await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new BrokenPocket());
                Player player = session.Player!;
                player.Relocate(Start.X, Start.Y, Start.Z, 0f, host.World.NowMs);
                var options = new PlayerbotOptions { Enabled = true };
                uint at = host.World.NowMs;
                for (int attempt = 0; attempt < destinations.Length; attempt++)
                {
                    session.ManagedBudget = new ManagedActionBudget(4);
                    at += 400;
                    var route = new PlayerbotRoute([Start, StuckWaypoint, destinations[attempt]], Vector3.Distance(Start, destinations[attempt]), navigated: false);
                    Assert.False(PlayerbotMotion.Follow(session, player, route, options, at));
                }

                Assert.True(PlayerbotMotion.ConsumeLoop(player));
                int count = PlayerbotMotion.ConsecutiveRejections(player);
                bool standing = PlayerbotMotion.Of(player).RejectedPlaces.Exists(entry => Vector2.Distance(new Vector2(entry.Place.X, entry.Place.Y), new Vector2(Start.X, Start.Y)) <= PlayerbotMotion.SamePlaceYards);

                // A realistic way out: the first navmesh corner 3 yards behind the bot, away from the broken pocket.
                var back = new PlayerbotRoute([Start, Start + new Vector3(-3f, 0f, 0f), Start + new Vector3(-30f, 0f, 0f)], 30f, navigated: false);
                bool leaves = !PlayerbotMotion.CrossesRejectedPlace(player, back);

                session.ManagedBudget = new ManagedActionBudget(4);
                at += 400;
                var next = new PlayerbotRoute([Start, StuckWaypoint, fourth], Vector3.Distance(Start, fourth), navigated: false);
                Assert.False(PlayerbotMotion.Follow(session, player, next, options, at));
                return (leaves, count, PlayerbotMotion.ConsecutiveRejections(player), PlayerbotMotion.IsBlacklisted(player, fourth), standing);
            });
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    [Fact]
    public async Task RejectedPlace_DoesNotRefuse_ABotLeavingItByANearCorner()
    {
        // Navmesh corners are often 1-4 yards apart: the first corner of a way out lies within SamePlaceYards of where the bot stands.
        bool crosses = await ProbeAsync(static (_, _, player, corner) => PlayerbotMotion.CrossesRejectedPlace(player,
            new PlayerbotRoute([corner, corner + new Vector3(0f, 3f, 0f), corner + new Vector3(0f, 30f, 0f)], 30f, navigated: false)));
        Assert.False(crosses, "A bot standing at the remembered place leaves it by a corner 3 yards away.");
    }

    [Fact]
    public async Task RejectedPlace_DoesNotRefuse_ARouteOnAnotherFloor()
    {
        bool crosses = await ProbeAsync(static (_, _, player, corner) => PlayerbotMotion.CrossesRejectedPlace(player,
            new PlayerbotRoute([Start + new Vector3(0f, 0f, 35f), corner + new Vector3(1f, 1f, 35f), corner + new Vector3(30f, 0f, 35f)], 50f, navigated: false)));
        Assert.False(crosses, "A route 35 yards above the remembered place is on another floor.");
    }

    [Fact]
    public async Task RejectedPlace_StillRefuses_ARouteThroughItOnItsFloor()
    {
        bool crosses = await ProbeAsync(static (_, _, player, corner) => PlayerbotMotion.CrossesRejectedPlace(player, ViaCorner(corner)));
        Assert.True(crosses, "A route through the remembered corner on its floor is refused.");
    }

    [Fact]
    public async Task RejectedPlace_IsLiftedByUnblacklist()
    {
        bool crosses = await ProbeAsync(static (host, _, player, corner) =>
        {
            Vector3 destination = corner + new Vector3(30f, 0f, 0f);
            PlayerbotMotion.Blacklist(player, [destination], host.World.NowMs);
            PlayerbotMotion.Unblacklist(player, destination);
            return PlayerbotMotion.CrossesRejectedPlace(player, ViaCorner(corner));
        });
        Assert.False(crosses, "Unblacklist (a group pushing through) must also lift the remembered places.");
    }

    [Fact]
    public async Task RejectedPlace_IsDroppedByATeleport()
    {
        bool crosses = await ProbeAsync(static (host, session, player, corner) =>
        {
            Assert.True(host.WorldServices.GetRequiredService<TeleportFeature>().Teleports.TeleportTo(player, player.MapId, player.X + 50f, player.Y, player.Z, 0f));
            PlayerbotMovementControl.Update(session, player);
            return PlayerbotMotion.CrossesRejectedPlace(player, ViaCorner(corner));
        });
        Assert.False(crosses, "A teleport drops the remembered places of the place the bot left.");
    }

    /// <summary>
    /// The refusal in PlayerbotNavigation.Usable: a whole mesh answer whose corner lies at a remembered rejected place is no route (and no verdict
    /// on the destination: NonPathRefusal), while the same answer is a route once nothing is remembered.
    /// </summary>
    [Fact]
    public async Task Usable_RefusesAMeshAnswerThroughARememberedPlace_AndTakesItOnceItIsForgotten()
    {
        (bool refused, bool routeNull, bool nonPath, bool takenAfterForgetting, bool controlWithNoMemory) = await ProbeAsync2(static (host, player, corner) =>
        {
            var options = new PlayerbotOptions { Enabled = true };
            Vector3 destination = corner + new Vector3(30f, 0f, 0f);
            var path = new PathResult(PathType.Normal, [Start, corner + new Vector3(1f, 1f, 0f), destination]);

            PlayerbotNavigation.NonPathRefusal = false;
            bool usable = PlayerbotNavigation.Usable(player, path, Start, destination, destination, partial: false, nearestEnd: false, options, out PlayerbotRoute? route);
            bool nonPathRefusal = PlayerbotNavigation.NonPathRefusal;

            // A path that avoids the remembered corner is a route even with the memory in place.
            var around = new PathResult(PathType.Normal, [Start, Start + new Vector3(0f, 12f, 0f), destination + new Vector3(0f, 12f, 0f)]);
            bool control = PlayerbotNavigation.Usable(player, around, Start, around.End, around.End, partial: false, nearestEnd: false, options, out _);

            PlayerbotMotion.ForgetRejections(player);
            bool after = PlayerbotNavigation.Usable(player, path, Start, destination, destination, partial: false, nearestEnd: false, options, out PlayerbotRoute? taken);
            return (!usable, route is null, nonPathRefusal, after && taken is not null, control);
        });

        Assert.True(refused, "Usable must refuse a mesh answer with a corner at a remembered rejected place.");
        Assert.True(routeNull);
        Assert.True(nonPath, "The refusal is no verdict on the destination: the route-plan watch must not be charged.");
        Assert.True(controlWithNoMemory, "A mesh answer that does not cross the remembered place is a route.");
        Assert.True(takenAfterForgetting, "The same answer is a route once the places are forgotten.");
    }

    /// <summary><see cref="ProbeAsync"/> with a probe that returns a tuple, run on the world thread with the corner remembered 20 yards east.</summary>
    private static async Task<(bool, bool, bool, bool, bool)> ProbeAsync2(Func<WorldTestHost, Player, Vector3, (bool, bool, bool, bool, bool)> probe)
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            return await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.Relocate(Start.X, Start.Y, Start.Z, 0f, host.World.NowMs);
                PlayerbotMotion.MotionState state = PlayerbotMotion.Of(player);
                state.World = session.World;
                state.RejectionsOn = player.Map;
                var corner = new Vector3(Start.X + 20f, Start.Y, Floor);
                state.RejectedPlaces.Add((corner, unchecked(host.World.NowMs + PlayerbotMotion.LoopBlacklistMs)));
                return probe(host, player, corner);
            });
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    private static PlayerbotRoute ViaCorner(Vector3 corner)
        => new([Start, corner + new Vector3(1f, 1f, 0f), corner + new Vector3(30f, 0f, 0f)], 50f, navigated: false);

    /// <summary>A bot at <see cref="Start"/> with one remembered place 20 yards east; <paramref name="probe"/> runs on the world thread.</summary>
    private static async Task<bool> ProbeAsync(Func<WorldTestHost, WorldSession, Player, Vector3, bool> probe)
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            return await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.Relocate(Start.X, Start.Y, Start.Z, 0f, host.World.NowMs);
                PlayerbotMotion.MotionState state = PlayerbotMotion.Of(player);
                state.World = session.World;
                var corner = new Vector3(Start.X + 20f, Start.Y, Floor);
                state.RejectedPlaces.Add((corner, unchecked(host.World.NowMs + PlayerbotMotion.LoopBlacklistMs)));
                return probe(host, session, player, corner);
            });
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    /// <summary>Flat ground at <see cref="Floor"/> except a pocket with no floor around the stuck waypoint (the walk breaks there).</summary>
    private sealed class BrokenPocket : ILineOfSight
    {
        private static readonly Vector2 Center = new(1320.7f, -4656.3f);

        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            return false;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance)
            => Vector2.Distance(new Vector2(x, y), Center) <= 8.0f ? TerrainTile.InvalidHeightValue : Floor;

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        {
            info = default;
            return false;
        }
    }
}
