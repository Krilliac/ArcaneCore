using System.Numerics;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed partial class PlayerbotRealTerrainNavigationTests
{
    // The Dun Morogh - Loch Modan tunnel (Smalltunnel.wmo), where the live server reported bots 2-5 yards under the tunnel floor
    // (move/under-map at -5590.54,-2475.30,394.35; the floor there is 396.97): both mouths on the navigation mesh.
    // Points of Piltrus's live walk (bot-motion lease/under-map reports, 18:36-18:38): the west mouth, then on through the tunnel.
    private static readonly Vector3 TunnelWest = new(-5616.8f, -2504.5f, 380.3f);
    private static readonly Vector3 TunnelEast = new(-5584.44f, -2468.27f, 396.6f);

    [RealTerrainBotFact]
    public async Task ThroughTheLochModanTunnel_TheBotWalksOnTheFloor_NotUnderIt()
    {
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(TunnelWest); // loads the grids, and with them the mesh tiles
        Vector3 start = await terrain.Host.OnWorldAsync(() => OnMesh(terrain.Player.Map!, TunnelWest));
        Vector3 goal = await terrain.Host.OnWorldAsync(() => OnMesh(terrain.Player.Map!, TunnelEast));
        await terrain.PlaceAsync(start);
        var options = new PlayerbotOptions { MaxPathPoints = 128, MaxRouteYards = 400 };
        PlayerbotRoute? route = null;
        for (int attempt = 0; attempt < 50 && route is null; attempt++)
        {
            route = await terrain.Host.OnWorldAsync(() => PlayerbotNavigation.TryPlanToward(terrain.Player, goal, options, out PlayerbotRoute? r) ? r : null);
            if (route is null)
            {
                await Task.Delay(20);
                await terrain.Host.World.AdvanceClockAsync(100);
            }
        }

        PathResult direct = await terrain.Host.OnWorldAsync(() => terrain.Player.Map!.Collision.FindPath(start, goal));
        output.WriteLine($"start {start} goal {goal} at {terrain.Player.X},{terrain.Player.Y},{terrain.Player.Z}; mesh path {direct.Type} {direct.Points.Count} points");
        Assert.NotNull(route);
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n =>
            n.Key is "bot-motion/under-map" or "bot-motion/airwalk" or "move/under-map"
            && n.Subject.Contains(terrain.Player.Name, StringComparison.Ordinal));
        float worstUnder = 0f, worstAbove = 0f;
        int samples = 0;
        for (int think = 0; think < 120; think++)
        {
            bool going = await terrain.Host.OnWorldAsync(() =>
                PlayerbotNavigation.TryAdvance(terrain.Session, route!, options, 500, terrain.Host.World.NowMs));
            for (int tick = 0; tick < 10; tick++)
            {
                await terrain.Host.World.AdvanceClockAsync(50);
                (float under, float above) = await terrain.Host.OnWorldAsync(() =>
                {
                    PlayerbotMotion.Pump(terrain.Session, terrain.Player, terrain.Host.World.NowMs);
                    return FloorGap(terrain.Player.Map!, new Vector3(terrain.Player.X, terrain.Player.Y, terrain.Player.Z));
                });
                worstUnder = MathF.Max(worstUnder, under);
                worstAbove = MathF.Max(worstAbove, above);
                samples++;
            }

            if (!going) break;
        }

        Vector3 end = await terrain.Host.OnWorldAsync(() => new Vector3(terrain.Player.X, terrain.Player.Y, terrain.Player.Z));
        output.WriteLine($"{samples} samples; worst under the floor {worstUnder:F2} yd, worst above {worstAbove:F2} yd; ended at {end} (goal {goal})");
        foreach (AnomalyNotice notice in capture.Notices) output.WriteLine($"{notice.Key}: {notice.Detail}");
        Assert.True(Vector2.Distance(new(end.X, end.Y), new(goal.X, goal.Y)) < 5f, $"the bot did not get through the tunnel (ended at {end})");
        Assert.Empty(capture.Notices);
        Assert.True(worstUnder <= 1.25f, $"the bot walked {worstUnder:F2} yd under the tunnel floor");
    }

    /// <summary>The navigation-mesh surface straight under (or over) <paramref name="near"/>.</summary>
    private static Vector3 OnMesh(Map map, Vector3 near)
    {
        Assert.True(map.Collision.TryGetMeshFloor(near.X, near.Y, near.Z, 6f, 6f, out float surface), $"no mesh at {near}");
        return near with { Z = surface + 0.1f };
    }

    /// <summary>
    /// How far the point is under the nearest model or terrain floor above it within 6 yd (0 when none), and how far above the floor
    /// under it (from 2 yd above, as <see cref="MapCollision.GetHeight"/> looks).
    /// </summary>
    private static (float Under, float Above) FloorGap(Map map, Vector3 at)
    {
        float under = 0f;
        float over = map.Collision.GetHeight(at.X, at.Y, at.Z + 6f, maxSearchDistance: 6f);
        if (over > at.Z + 0.5f && over - at.Z <= 6f) under = over - at.Z;
        float floor = map.Collision.GetHeight(at.X, at.Y, at.Z);
        float above = floor > -100000f ? at.Z - floor : 0f;
        return (under, above);
    }
}
