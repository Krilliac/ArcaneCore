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

    /// <summary>
    /// Over open, rolling ground the bot's height is the ground's at every heartbeat, at the motion's own cadence and when the world's
    /// ticks come seconds apart (the live server under load: think-starved bots, 2-15 s GC ticks). A mesh route's legs are straight
    /// lines between string-pulled corners (vmangos smooths its paths every 4 yd along the surface instead, PathFinder::findSmoothPath),
    /// so a leg's own height is a chord through the hill or over the hollow; before, the walk kept that chord wherever the ground lay more
    /// than 4 yd from it (live: 8-30 yd under the terrain, 6-10 yd above it, all over the open world). The walks run past live reports:
    /// south of Kharanos (Biskouth 11.5 yd under at -5577.8,-617.3; 10.2 yd under at -5604.4,-636.6) and in the northern Barrens (12.1 yd
    /// under at 493.0,-3452.7; 8.8 yd above at 519.3,-3472.5).
    /// </summary>
    [RealTerrainBotTheory]
    [InlineData(0u, -5540f, -580f, -5640f, -690f, 50u)]
    [InlineData(0u, -5540f, -580f, -5640f, -690f, 2000u)]
    [InlineData(1u, 430f, -3400f, 580f, -3530f, 50u)]
    [InlineData(1u, 430f, -3400f, 580f, -3530f, 2000u)]
    public async Task OverRollingGround_TheBotKeepsToTheGround_AtAnyTickCadence(uint map, float fromX, float fromY, float toX, float toY, uint tickMs)
    {
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(new Vector3(fromX, fromY, 1000f), map);
        Vector3 start = await terrain.Host.OnWorldAsync(() => OnGround(terrain.Player.Map!, fromX, fromY));
        await terrain.PlaceAsync(new Vector3(toX, toY, 1000f), map);
        Vector3 goal = await terrain.Host.OnWorldAsync(() => OnGround(terrain.Player.Map!, toX, toY));
        await terrain.PlaceAsync(start, map);
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

        Assert.NotNull(route);
        output.WriteLine($"tick {tickMs} ms: route {route!.Points.Count} points, navigated {route.Navigated}: {string.Join(" ", route.Points)}");
        // This bot's own reports in this world: other tests' bots share its name and guid (PlayerbotAnomalies.CaptureFor).
        using AnomalyCapture capture = await terrain.Host.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(terrain.Player));
        float worst = 0f;
        Vector3 worstAt = default;
        int samples = 0;
        for (int think = 0; think < 400; think++)
        {
            bool going = await terrain.Host.OnWorldAsync(() =>
                PlayerbotNavigation.TryAdvance(terrain.Session, route, options, 500, terrain.Host.World.NowMs));
            for (uint waited = 0; waited < 500; waited += Math.Min(tickMs, 500u))
            {
                await terrain.Host.World.AdvanceClockAsync(tickMs);
                (float gap, Vector3 at) = await terrain.Host.OnWorldAsync(() =>
                {
                    PlayerbotMotion.Pump(terrain.Session, terrain.Player, terrain.Host.World.NowMs);
                    var here = new Vector3(terrain.Player.X, terrain.Player.Y, terrain.Player.Z);
                    return (GroundGap(terrain.Player.Map!, here), here);
                });
                if (gap > worst) (worst, worstAt) = (gap, at);
                samples++;
            }

            if (!going) break;
        }

        Vector3 end = await terrain.Host.OnWorldAsync(() => new Vector3(terrain.Player.X, terrain.Player.Y, terrain.Player.Z));
        output.WriteLine($"{samples} samples; worst {worst:F2} yd off the open ground at {worstAt}; ended at {end} (goal {goal})");
        AnomalyNotice[] ground = [.. capture.Notices.Where(n => n.Key is "bot-motion/under-map" or "bot-motion/airwalk")];
        foreach (AnomalyNotice notice in ground) output.WriteLine($"{notice.Key}: {notice.Detail}");
        Assert.True(Vector2.Distance(new(end.X, end.Y), new(goal.X, goal.Y)) < 5f, $"the bot did not reach the goal (ended at {end})");
        Assert.True(worst <= 1.5f, $"the bot walked {worst:F2} yd off the ground (at {worstAt})");
        Assert.Empty(ground);
    }

    // The Valley of Trials (Durotar), the live server's hottest bot-motion/under-map spot: a mesa rises from the valley floor (terrain
    // 50-62) to 77 between about -540 and -490 west, -4220 and -4130 south, and bots were reported 10-36 yd under its top inside it
    // (-516.6,-4130.1,59.7; -546.2,-4161.8,58.2; -555.5,-4176.0,51.8). A route along the valley floor east of the mesa, and a place
    // north of it from where the straight line to the route's far end runs through the mesa.
    private static readonly Vector3 TrialsEastNorth = new(-470f, -4120f, 50f);
    private static readonly Vector3 TrialsEastSouth = new(-470f, -4200f, 51f);
    private static readonly Vector3 TrialsNorthOfMesa = new(-520f, -4110f, 61f);

    /// <summary>
    /// A bot taken off its route (a same-map teleport here; on the live server also a chase, an escape or a fear) and then handed the
    /// same route again walked a straight line from where it stood to the route's next point. That line is no leg of the mesh, yet the
    /// motion judged it by the route's mesh proof: no line-of-sight check, and the line's own height wherever no floor lay near it, so
    /// the bot walked through hills, rocks and city walls (live: 92-94% of the bot-motion/under-map samples on routed bots were more
    /// than 10 yd off their leg, Atlas evidence 2026-10-19). Here the line runs through the mesa: the bot must not go under the
    /// terrain on the way.
    /// </summary>
    [RealTerrainBotFact]
    public async Task ABotResumingItsRouteFromOffTheRoute_DoesNotWalkThroughTheMesaBetween()
    {
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(TrialsEastNorth with { Z = TrialsEastNorth.Z + 2f }, 1u);
        Vector3 start = await terrain.Host.OnWorldAsync(() => OnMesh(terrain.Player.Map!, TrialsEastNorth));
        Vector3 goal = await terrain.Host.OnWorldAsync(() => OnMesh(terrain.Player.Map!, TrialsEastSouth));
        Vector3 aside = await terrain.Host.OnWorldAsync(() => OnMesh(terrain.Player.Map!, TrialsNorthOfMesa));
        await terrain.PlaceAsync(start, 1u);
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

        Assert.NotNull(route);
        output.WriteLine($"route {route!.Points.Count} points, navigated {route.Navigated}: {string.Join(" ", route.Points)}");
        // One think along the route, then the bot is taken away from it (teleported north of the mesa) and the route handed back.
        Assert.True(await terrain.Host.OnWorldAsync(() => PlayerbotNavigation.TryAdvance(terrain.Session, route, options, 500, terrain.Host.World.NowMs)));
        await terrain.Host.World.AdvanceClockAsync(500);
        await terrain.Host.OnWorldAsync(() => PlayerbotMotion.Pump(terrain.Session, terrain.Player, terrain.Host.World.NowMs));
        await terrain.PlaceAsync(aside, 1u);
        using AnomalyCapture capture = await terrain.Host.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(terrain.Player));
        float worst = 0f;
        Vector3 worstAt = default;
        for (int think = 0; think < 60; think++)
        {
            bool going = await terrain.Host.OnWorldAsync(() =>
                PlayerbotNavigation.TryAdvance(terrain.Session, route, options, 500, terrain.Host.World.NowMs));
            for (int tick = 0; tick < 10; tick++)
            {
                await terrain.Host.World.AdvanceClockAsync(50);
                (float depth, Vector3 at) = await terrain.Host.OnWorldAsync(() =>
                {
                    PlayerbotMotion.Pump(terrain.Session, terrain.Player, terrain.Host.World.NowMs);
                    var here = new Vector3(terrain.Player.X, terrain.Player.Y, terrain.Player.Z);
                    UnderMapVerdict verdict = terrain.Player.Map!.Collision.ClassifyUnderMap(here.X, here.Y, here.Z, PlayerbotMotionAnomalies.UnderMapYards);
                    return (verdict.Kind == UnderMapKind.UnderMap ? verdict.DepthYards : 0f, here);
                });
                if (depth > worst) (worst, worstAt) = (depth, at);
            }

            if (!going) break;
        }

        Vector3 end = await terrain.Host.OnWorldAsync(() => new Vector3(terrain.Player.X, terrain.Player.Y, terrain.Player.Z));
        AnomalyNotice[] under = [.. capture.Notices.Where(n => n.Key == "bot-motion/under-map")];
        foreach (AnomalyNotice notice in under) output.WriteLine($"{notice.Key}: {notice.Detail}");
        output.WriteLine($"resumed from {aside}; ended at {end}; worst {worst:F2} yd under the terrain at {worstAt}");
        Assert.True(worst == 0f, $"the bot went {worst:F2} yd under the terrain (at {worstAt})");
        Assert.Empty(under);
    }

    /// <summary>The ground at (<paramref name="x"/>, <paramref name="y"/>): the mesh surface over the terrain there.</summary>
    private static Vector3 OnGround(Map map, float x, float y)
        => OnMesh(map, new Vector3(x, y, map.Terrain.GetHeight(x, y, 1000f)));

    /// <summary>How far the point is off the terrain, where the terrain is the ground (outdoors, valid height); 0 elsewhere.</summary>
    private static float GroundGap(Map map, Vector3 at)
    {
        float ground = map.Terrain.GetHeight(at.X, at.Y, at.Z + 100f);
        if (!(ground > -100000f) || !map.Collision.IsOutdoors(at.X, at.Y, at.Z + 1f)) return 0f;
        float floor = map.Collision.GetHeight(at.X, at.Y, at.Z);
        if (floor > ground + 0.5f && MathF.Abs(floor - at.Z) < MathF.Abs(ground - at.Z)) return 0f; // on a model (a bridge, a rock)
        return MathF.Abs(at.Z - ground);
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
