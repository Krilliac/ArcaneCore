using System.Numerics;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed partial class PlayerbotRealTerrainNavigationTests
{
    /// <summary>
    /// Bot QA #784 (live 2026-10-17 05:01, bots Tupizeir, Mokou and Dreirndreido, each several times): the destination below the road west
    /// of Goldshire has no walkable polygon near it, and the mesh's exact answer ended on the road 12.4 yards away and 9 yards up. The exact
    /// plan took it, the bot walked to the road, stood out of reach and planned it again. An exact plan refuses such an answer now
    /// (<see cref="PlayerbotNavigationAnomalies.PathEndShortYards"/>); a plan toward the goal still closes on it as far as the mesh goes.
    /// </summary>
    [RealTerrainBotFact]
    public async Task AnExactPlan_ToAnOffMeshSpotBelowTheGoldshireRoad_IsRefused()
    {
        Vector3 bot = new(-9247.0f, 448.4f, 89.7f), destination = new(-9235.9f, 450.5f, 84.4f);
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(bot);
        (bool planned, PlayerbotRoute? route) = await terrain.Host.OnWorldAsync(() =>
        {
            bool found = PlayerbotNavigation.TryPlan(terrain.Player, destination, new PlayerbotOptions(), out PlayerbotRoute? exact);
            return (found, exact);
        });
        Assert.False(planned, $"the exact plan took a route ending at {route?.Points[^1]}, "
            + $"{(route is null ? 0 : Vector2.Distance(new(route.Points[^1].X, route.Points[^1].Y), new(destination.X, destination.Y))):F1} yd (flat) from its destination");
    }

    /// <summary>The same rule keeps an exact plan whose destination is on the mesh: Westfall's road to Sentinel Hill.</summary>
    [RealTerrainBotFact]
    public async Task AnExactPlan_ToAnOnMeshSpot_StillReachesIt()
    {
        Vector3 bot = new(-10559.0f, 1206.9f, 31.8f), destination = new(-10532.3f, 1191.2f, 35.5f);
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(bot);
        (bool planned, PlayerbotRoute? route) = await terrain.Host.OnWorldAsync(() =>
        {
            bool found = PlayerbotNavigation.TryPlan(terrain.Player, destination, new PlayerbotOptions(), out PlayerbotRoute? exact);
            return (found, exact);
        });
        Assert.True(planned && route is not null, "no exact route to a point on the mesh");
        Assert.True(Vector3.Distance(route!.Points[^1], destination) <= 2f, $"the exact route ends at {route.Points[^1]}");
    }
}
