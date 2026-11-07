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

/// <summary>The place-keyed rejection memory (#1485): a corner the walk broke at repeatedly is refused to later routes for a while.</summary>
public sealed class PlayerbotRejectedPlaceMemoryTests
{
    private const float Floor = 47.0f;

    private static readonly Vector3 Start = new(1320.3f, -4656.3f, Floor);

    [Fact]
    public async Task RememberedRejectedPlace_IsCrossedByRoutesThroughIt_UntilItExpires_AndFollowRefusesSuchARoute()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            (bool through, bool beside, bool startOnly, bool expired, bool followed, bool active, int rejections) = await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.Relocate(Start.X, Start.Y, Start.Z, 0f, host.World.NowMs);
                var options = new PlayerbotOptions { Enabled = true };
                PlayerbotMotion.MotionState state = PlayerbotMotion.Of(player);
                state.World = session.World;
                uint now = host.World.NowMs;
                var corner = new Vector3(Start.X + 20f, Start.Y, Floor);
                state.RejectedPlaces.Add((corner, unchecked(now + PlayerbotMotion.LoopBlacklistMs)));

                var viaCorner = new PlayerbotRoute([Start, corner + new Vector3(1f, 1f, 0f), corner + new Vector3(30f, 0f, 0f)], 50f, navigated: false);
                var nearby = new PlayerbotRoute([Start, corner + new Vector3(0f, 12f, 0f), corner + new Vector3(30f, 12f, 0f)], 50f, navigated: false);
                // The route's own start is not a corner it walks through: a bot standing at the remembered place can still leave it.
                var startsThere = new PlayerbotRoute([corner, corner + new Vector3(0f, 30f, 0f)], 30f, navigated: false);
                bool crossesThrough = PlayerbotMotion.CrossesRejectedPlace(player, viaCorner);
                bool crossesBeside = PlayerbotMotion.CrossesRejectedPlace(player, nearby);
                bool crossesStart = PlayerbotMotion.CrossesRejectedPlace(player, startsThere);

                session.ManagedBudget = new ManagedActionBudget(4);
                bool followedThrough = PlayerbotMotion.Follow(session, player, viaCorner, options, now);
                bool isActive = PlayerbotMotion.IsActive(player);
                int rejected = PlayerbotMotion.ConsecutiveRejections(player);

                // An entry whose time has come is dropped by the next question.
                state.RejectedPlaces.Clear();
                state.RejectedPlaces.Add((corner, now));
                bool crossesAfterExpiry = PlayerbotMotion.CrossesRejectedPlace(player, viaCorner);
                return (crossesThrough, crossesBeside, crossesStart, crossesAfterExpiry, followedThrough, isActive, rejected);
            });

            Assert.True(through, "A route with a corner within SamePlaceYards of the remembered place must be refused.");
            Assert.False(beside, "A route 12 yards from the remembered place is not affected.");
            Assert.False(startOnly, "The start of a route is not a corner it walks through.");
            Assert.False(expired, "An expired entry must not refuse anything.");
            Assert.False(followed, "Follow must not start a route through the remembered place.");
            Assert.False(active);
            Assert.Equal(0, rejections);
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }
}
