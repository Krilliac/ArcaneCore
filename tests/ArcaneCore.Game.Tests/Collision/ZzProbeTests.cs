using System.Numerics;
using System.Text.Json;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Game.Maps.Terrain;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Game.Tests.Collision;

public sealed class ZzProbeTests(ITestOutputHelper output)
{
    private static string Root => Environment.GetEnvironmentVariable(RealTerrainFactAttribute.Variable)!;

    [RealTerrainFact]
    public void Probe()
    {
        string file = Environment.GetEnvironmentVariable("PROBE_IN")!;
        var rows = JsonDocument.Parse(File.ReadAllText(file)).RootElement.EnumerateArray().ToList();
        var vmaps = new VMapManager(Path.Combine(Root, "vmaps"));
        var nav = new NavMeshPathfinder(Path.Combine(Root, "mmaps"));
        WorldRuntime world = TestWorld.CreateRuntime();
        world.Options.Maps.DataDirectory = Root;
        WorldCollision.Of(world).Install(lineOfSight: vmaps, pathfinder: nav);
        var loaded = new HashSet<(uint, int, int)>();
        foreach (var r in rows)
        {
            uint map = r.GetProperty("map").GetUInt32();
            var p = r.GetProperty("p").EnumerateArray().Select(e => e.GetSingle()).ToArray();
            (int tx, int ty) = TerrainTile.TileOf(p[0], p[1])!.Value;
            for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++)
                if (loaded.Add((map, tx + dx, ty + dy))) { vmaps.LoadTile(map, tx + dx, ty + dy); nav.LoadTile(map, tx + dx, ty + dy); }
            MapCollision c = world.GetMap(map).Collision;
            string k = r.GetProperty("k").GetString()!;
            float x = p[0], y = p[1], z = p[2];
            float terrain = c.Map.Terrain.GetHeight(x, y, z);
            float h = c.GetHeight(x, y, z);
            float? mh = vmaps.GetModelHeight(map, x, y, z + 2, 60f);
            bool area = vmaps.TryGetAreaInfo(map, x, y, z, out var ai);
            bool mesh = c.TryGetMeshFloor(x, y, z, 8f, 8f, out float mf);
            bool meshWide = c.TryGetMeshFloor(x, y, z, 60f, 30f, out float mf2);
            string line = $"{k} {r.GetProperty("b").GetString()} m{map} {x:F1},{y:F1},{z:F1} terr={terrain:F1} GetHeight={h:F1} model={(mh?.ToString("F1") ?? "-")} area={(area ? ai.GroundZ.ToString("F1") + "/" + ai.RootId : "-")} mesh8={(mesh ? mf.ToString("F1") : "-")} meshWide={(meshWide ? mf2.ToString("F1") : "-")}";
            if (k == "wall")
            {
                var a = r.GetProperty("a").EnumerateArray().Select(e => e.GetSingle()).ToArray();
                var from = new Vector3(a[0], a[1], a[2] + 1.5f); var to = new Vector3(x, y, z + 1.5f);
                bool los = vmaps.IsInLineOfSight(map, from, to, true);
                var w = c.MeshPoints!.Raycast(map, new Vector3(a[0], a[1], a[2]), new Vector3(x, y, z), null);
                var s = c.MeshPoints!.Raycast(map, new Vector3(a[0], a[1], a[2]), new Vector3(a[0], a[1], a[2]), null);
                bool hit = vmaps.TryGetObjectHit(map, from, to, 0f, out var hp);
                line += $" LOSignoreM2={los} hit={(hit ? hp.ToString("F1") : "-")} meshWalk(av={w.Available},hit={w.Hit},z={w.Point.Z:F1}) start(av={s.Available},z={s.Point.Z:F1}) aZ={a[2]:F1}";
            }
            output.WriteLine(line);
        }
    }

    [RealTerrainFact]
    public void Grid()
    {
        string[] a = Environment.GetEnvironmentVariable("PROBE_GRID")!.Split(',');
        float x0 = float.Parse(a[0]), x1 = float.Parse(a[1]), y0 = float.Parse(a[2]), y1 = float.Parse(a[3]), st = float.Parse(a[4]), z = float.Parse(a[5]), tol = float.Parse(a[6]);
        var vmaps = new VMapManager(Path.Combine(Root, "vmaps"));
        var nav = new NavMeshPathfinder(Path.Combine(Root, "mmaps"));
        WorldRuntime world = TestWorld.CreateRuntime();
        world.Options.Maps.DataDirectory = Root;
        WorldCollision.Of(world).Install(lineOfSight: vmaps, pathfinder: nav);
        for (float x = x0; x <= x1; x += 200) for (float y = y0; y <= y1; y += 200)
        {
            (int tx, int ty) = TerrainTile.TileOf(x, y)!.Value;
            for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) { vmaps.LoadTile(0, tx + dx, ty + dy); nav.LoadTile(0, tx + dx, ty + dy); }
        }
        MapCollision c = world.GetMap(0).Collision;
        output.WriteLine($"legend: M=mesh within +-{tol} of z={z}, V=model floor within, B=both, .=neither, m/v=only lower (mesh/model) floor present");
        for (float y = y1; y >= y0; y -= st)
        {
            var sb = new System.Text.StringBuilder($"{y,7:F0} ");
            for (float x = x0; x <= x1; x += st)
            {
                bool m = c.TryGetMeshFloor(x, y, z, tol, tol, out _);
                float? mh = vmaps.GetModelHeight(0, x, y, z + tol, 2 * tol);
                bool v = mh is { } h && MathF.Abs(h - z) <= tol;
                sb.Append(m && v ? 'B' : m ? 'M' : v ? 'V' : '.');
            }
            output.WriteLine(sb.ToString());
        }
    }

    [RealTerrainFact]
    public void Profile()
    {
        var vmaps = new VMapManager(Path.Combine(Root, "vmaps"));
        var nav = new NavMeshPathfinder(Path.Combine(Root, "mmaps"));
        WorldRuntime world = TestWorld.CreateRuntime();
        world.Options.Maps.DataDirectory = Root;
        WorldCollision.Of(world).Install(lineOfSight: vmaps, pathfinder: nav);
        foreach (string pt in Environment.GetEnvironmentVariable("PROBE_PTS")!.Split(';'))
        {
            string[] a = pt.Split(',');
            float x = float.Parse(a[0]), y = float.Parse(a[1]);
            (int tx, int ty) = TerrainTile.TileOf(x, y)!.Value;
            for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) { vmaps.LoadTile(0, tx + dx, ty + dy); nav.LoadTile(0, tx + dx, ty + dy); }
            var sb = new System.Text.StringBuilder($"({x},{y}) surfaces top-down:");
            float z = 700;
            for (int i = 0; i < 12; i++)
            {
                float? h = vmaps.GetModelHeight(0, x, y, z, 700f);
                if (h is not { } v) break;
                sb.Append($" {v:F1}");
                z = v - 0.05f;
            }
            output.WriteLine(sb.ToString());
            // horizontal wall hits at 503.2 around the point
            var hits = new System.Text.StringBuilder("  wall ring r=6 @z+1.5:");
            for (int deg = 0; deg < 360; deg += 45)
            {
                float rx = x + 6 * MathF.Cos(deg * MathF.PI / 180), ry = y + 6 * MathF.Sin(deg * MathF.PI / 180);
                bool hit = vmaps.TryGetObjectHit(0, new Vector3(x, y, 503.2f), new Vector3(rx, ry, 503.2f), 0f, out var hp);
                hits.Append($" {deg}:{(hit ? MathF.Sqrt((hp.X - x) * (hp.X - x) + (hp.Y - y) * (hp.Y - y)).ToString("F1") : "-")}");
            }
            output.WriteLine(hits.ToString());
        }
    }

    [RealTerrainFact]
    public void Stats()
    {
        var rows = JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("PROBE_IN")!)).RootElement.EnumerateArray().ToList();
        var vmaps = new VMapManager(Path.Combine(Root, "vmaps"));
        var nav = new NavMeshPathfinder(Path.Combine(Root, "mmaps"));
        WorldRuntime world = TestWorld.CreateRuntime();
        world.Options.Maps.DataDirectory = Root;
        WorldCollision.Of(world).Install(lineOfSight: vmaps, pathfinder: nav);
        var loaded = new HashSet<(uint, int, int)>();
        var agg = new SortedDictionary<string, int>();
        foreach (var r in rows)
        {
            uint map = r.GetProperty("map").GetUInt32();
            var p = r.GetProperty("p").EnumerateArray().Select(e => e.GetSingle()).ToArray();
            (int tx, int ty) = TerrainTile.TileOf(p[0], p[1])!.Value;
            for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++)
                if (loaded.Add((map, tx + dx, ty + dy))) { vmaps.LoadTile(map, tx + dx, ty + dy); nav.LoadTile(map, tx + dx, ty + dy); }
            MapCollision c = world.GetMap(map).Collision;
            string k = r.GetProperty("k").GetString()!;
            float x = p[0], y = p[1], z = p[2];
            float terrain = c.Map.Terrain.GetHeight(x, y, z);
            float h = c.GetHeight(x, y, z);
            float? mh = vmaps.GetModelHeight(map, x, y, z + 2, 60f);
            bool area = vmaps.TryGetAreaInfo(map, x, y, z, out _);
            bool mesh = c.TryGetMeshFloor(x, y, z, 8f, 8f, out _);
            bool meshNear = false;
            for (float dx = -3; dx <= 3 && !meshNear; dx += 1) for (float dy = -3; dy <= 3 && !meshNear; dy += 1)
                meshNear = c.TryGetMeshFloor(x + dx, y + dy, z, 3f, 3f, out _);
            string key = $"{k,-9} m{map} {Math.Round(x / 100) * 100,6},{Math.Round(y / 100) * 100,6} | terrOver={(terrain - z > 8 ? "Y" : "n")} floor={(h > -100000 ? (z - h > 6 ? "farBelow" : "near") : "none")} area={(area ? "Y" : "n")} mesh8={(mesh ? "Y" : "n")} meshWithin3yd={(meshNear ? "Y" : "n")}";
            agg[key] = agg.GetValueOrDefault(key) + 1;
        }
        foreach (var kv in agg.Where(kv => kv.Value >= 3)) output.WriteLine($"{kv.Value,4} {kv.Key}");
    }
}
