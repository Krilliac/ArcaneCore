using System.Diagnostics;
using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed partial class PlayerbotRealTerrainNavigationTests
{
    private static readonly (string Name, uint Map, Vector3 From, Vector3 To)[] ProbeCases =
    [
        ("1656 mesa edge", 1, new(-2764.0f, -312.0f, 54.5f), new(-2365.4f, -347.3f, -8.9f)),
        ("1656 hill", 1, new(-2937.9f, -67.2f, 111.2f), new(-2365.4f, -347.3f, -8.9f)),
        ("752 chief", 1, new(-2881.9f, -229.1f, 55.2f), new(-3052.5f, -522.5f, 27.0f)),
        ("2159 hill", 1, new(10176.0f, 842.4f, 1395.4f), new(9802.2f, 982.6f, 1314.0f)),
        ("2159 b", 1, new(10199.2f, 991.2f, 1376.4f), new(9802.2f, 982.6f, 1314.0f)),
        ("2159 c", 1, new(10429.6f, 729.9f, 1318.4f), new(9802.2f, 982.6f, 1314.0f)),
        ("4495 a", 1, new(10486.4f, 744.3f, 1313.9f), new(10711.6f, 1034.9f, 1347.3f)),
        ("4495 b", 1, new(10577.5f, 894.5f, 1312.8f), new(10711.6f, 1034.9f, 1347.3f)),
        ("459 a", 1, new(9740.8f, 835.7f, 1299.2f), new(10296.9f, 870.2f, 1335.9f)),
        ("459 b", 1, new(10037.1f, 958.1f, 1337.4f), new(10296.9f, 870.2f, 1335.9f)),
        ("3087 far", 1, new(-212.3f, -4559.7f, 72.0f), new(-635.5f, -4227.5f, 38.4f)),
        ("3087 galgar", 1, new(-585.1f, -4223.8f, 38.5f), new(-561.6f, -4221.8f, 41.7f)),
        ("3087 jenshan", 1, new(-585.1f, -4223.8f, 38.5f), new(-635.5f, -4227.5f, 38.4f)),
        ("3361 felix", 0, new(-6135.3f, 386.1f, 395.6f), new(-6098.1f, 396.1f, 395.5f)),
        ("7 stormwind", 0, new(-8842.1f, 464.0f, 109.8f), new(-8902.6f, -162.6f, 82.0f)),
        ("2161 grosk", 1, new(207.5f, -4458.7f, 27.7f), new(340.4f, -4686.3f, 16.5f)),
        ("2161 ukor", 1, new(207.5f, -4458.7f, 27.7f), new(-599.4f, -4715.3f, 35.2f)),
    ];

    [RealTerrainBotFact]
    public async Task Probe_StallPatterns()
    {
        string only = Environment.GetEnvironmentVariable("PROBE_ONLY") ?? "";
        foreach ((string name, uint mapId, Vector3 from, Vector3 to) in ProbeCases)
        {
            if (only.Length > 0 && !name.StartsWith(only, StringComparison.Ordinal)) continue;
            await using Terrain terrain = await Terrain.StartAsync();
            await terrain.PlaceAsync(from, mapId);
            string info = await terrain.Host.OnWorldAsync(() =>
            {
                var map = terrain.Player.Map!;
                var sb = new System.Text.StringBuilder();
                foreach (NavTerrain ex in new[] { NavTerrain.SteepSlopes, NavTerrain.Empty })
                    foreach (int nodes in new[] { 2048, 8192, 32768 })
                    {
                        var watch = Stopwatch.StartNew();
                        PathResult p = map.Collision.FindPath(from, to, new PathOptions { MaxPoints = 128, Mover = PathMover.Player,
                            ExcludeFlags = ex, AllowPartial = true, MaxSearchNodes = nodes, LoadTiles = true });
                        sb.AppendLine($"  ex {ex} nodes {nodes}: {p.Type} n={p.Points.Count} len={p.Length:F0} end={p.End} left={Vector3.Distance(p.End, to):F0} {watch.Elapsed.TotalMilliseconds:F1}ms");
                    }
                return sb.ToString();
            });
            output.WriteLine($"{name}: straight {Vector3.Distance(from, to):F0}");
            output.WriteLine(info);
            float left;
            try { left = await terrain.WalkTowardAsync(to, 5f, 240_000, output, new PlayerbotOptions { MaxPathPoints = 128, MaxRouteYards = 2000 }); }
            catch (Exception ex) { output.WriteLine($"  walk failed: {ex.Message}"); continue; }
            output.WriteLine($"  walked: left {left:F1}");
        }
    }
}

public sealed partial class PlayerbotRealTerrainNavigationTests
{
    [RealTerrainBotFact]
    public async Task Probe_Islands()
    {
        (string, uint, Vector3, Vector3)[] islands =
        [
            ("752 chief", 1, new(-2881.9f, -229.1f, 55.2f), new(-3052.5f, -522.5f, 27.0f)),
            ("2159 c", 1, new(10429.6f, 729.9f, 1318.4f), new(9802.2f, 982.6f, 1314.0f)),
            ("4495 a", 1, new(10486.4f, 744.3f, 1313.9f), new(10711.6f, 1034.9f, 1347.3f)),
            ("459 a", 1, new(9740.8f, 835.7f, 1299.2f), new(10296.9f, 870.2f, 1335.9f)),
            ("2161", 1, new(207.5f, -4458.7f, 27.7f), new(340.4f, -4686.3f, 16.5f)),
            ("1656 mesa edge", 1, new(-2764.0f, -312.0f, 54.5f), new(-2365.4f, -347.3f, -8.9f)),
        ];
        foreach ((string name, uint mapId, Vector3 from, Vector3 to) in islands)
        {
            await using Terrain terrain = await Terrain.StartAsync();
            await terrain.PlaceAsync(from, mapId);
            string info = await terrain.Host.OnWorldAsync(() =>
            {
                var map = terrain.Player.Map!;
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"{name}: terrain {map.Terrain.GetHeight(from.X, from.Y, from.Z):F1} floor {map.Collision.GetHeight(from.X, from.Y, from.Z):F1} outdoors {map.Collision.IsOutdoors(from.X, from.Y, from.Z)} player z {terrain.Player.Z:F1}");
                for (float r = 2; r <= 24; r += 2)
                    for (int a = 0; a < 16; a++)
                    {
                        float ang = a * MathF.PI / 8;
                        float x = from.X + MathF.Cos(ang) * r, y = from.Y + MathF.Sin(ang) * r;
                        float floor = map.Collision.GetHeight(x, y, from.Z + 5);
                        Vector3 ring = new(x, y, floor);
                        PathResult p = map.Collision.FindPath(ring, to, new PathOptions { MaxPoints = 128, Mover = PathMover.Player,
                            ExcludeFlags = NavTerrain.SteepSlopes, AllowPartial = true, MaxSearchNodes = 32768, LoadTiles = true });
                        if ((p.Type & PathType.Normal) != 0)
                        {
                            bool stepped = PlayerbotNavigation.TryTerrainRoute(from, ring, new PlayerbotOptions(), (px, py, pz) => map.Collision.GetHeight(px, py, pz),
                                (s, e) => map.Collision.IsInLineOfSight(s.X, s.Y, s.Z + 2, e.X, e.Y, e.Z + 2), out _);
                            sb.AppendLine($"  ring r={r} a={a}: {ring} path {p.Type} len {p.Length:F0} stepped {stepped}");
                            r = 100; break;
                        }
                    }
                return sb.ToString();
            });
            output.WriteLine(info);
        }
    }
}

public sealed partial class PlayerbotRealTerrainNavigationTests
{
    [RealTerrainBotFact]
    public async Task Probe_Two()
    {
        (string, uint, Vector3, Vector3)[] cases =
        [
            ("2159 c", 1, new(10429.6f, 729.9f, 1318.4f), new(9802.2f, 982.6f, 1314.0f)),
            ("2161 ukor", 1, new(200.53328f, -4458.667f, 30.164087f), new(-599.4f, -4715.3f, 35.2f)),
        ];
        foreach ((string name, uint mapId, Vector3 from, Vector3 to) in cases)
        {
            await using Terrain terrain = await Terrain.StartAsync();
            await terrain.PlaceAsync(from, mapId);
            string info = await terrain.Host.OnWorldAsync(() =>
            {
                var map = terrain.Player.Map!;
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"{name}: player {terrain.Player.X:F1},{terrain.Player.Y:F1},{terrain.Player.Z:F1}");
                foreach (int nodes in new[] { 32768, 131072 })
                {
                    PathResult p = map.Collision.FindPath(from, to, new PathOptions { MaxPoints = 128, Mover = PathMover.Player,
                        ExcludeFlags = NavTerrain.SteepSlopes, AllowPartial = true, MaxSearchNodes = nodes, LoadTiles = true });
                    sb.AppendLine($"  nodes {nodes}: {p.Type} n={p.Points.Count} len={p.Length:F0} end={p.End}");
                }
                foreach (float yards in new[] { 2.5f, 5f })
                    for (int d = 0; d < 8; d++)
                    {
                        float angle = MathF.Atan2(to.Y - from.Y, to.X - from.X) + (d * MathF.PI / 4f);
                        float x = from.X + (MathF.Cos(angle) * yards), y = from.Y + (MathF.Sin(angle) * yards);
                        float floor = map.Collision.GetHeight(x, y, from.Z + 2f);
                        float terrainH = map.Terrain.GetHeight(x, y, from.Z + 2f);
                        bool los = map.Collision.IsInLineOfSight(from.X, from.Y, from.Z + 2, x, y, floor + 2);
                        PathResult p = map.Collision.FindPath(new Vector3(x, y, floor), to, new PathOptions { MaxPoints = 128, Mover = PathMover.Player,
                            ExcludeFlags = NavTerrain.SteepSlopes, AllowPartial = true, MaxSearchNodes = 32768, LoadTiles = true });
                        sb.AppendLine($"  {yards} d{d}: floor {floor:F1} terrain {terrainH:F1} los {los} -> {p.Type} n={p.Points.Count} len={p.Length:F0} end={p.End}");
                    }
                return sb.ToString();
            });
            output.WriteLine(info);
        }
    }
}
