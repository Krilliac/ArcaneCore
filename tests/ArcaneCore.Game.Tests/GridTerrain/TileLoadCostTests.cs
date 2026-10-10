using System.Diagnostics;
using System.Globalization;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Game.Maps.Terrain;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Game.Tests.GridTerrain;

/// <summary>
/// What creating one grid costs the world thread on real data, split by loader: the terrain <c>.map</c> tile, the vmap tile (its
/// models included the first time they are needed) and the navmesh tile. Opt-in (<c>ARCANECORE_TILE_LOAD_COST=1</c> and
/// <c>ARCANECORE_TEST_TERRAIN_DIR</c>); wall time is only reported (docs/integration/tick-scaling-20261009.md).
/// </summary>
[Collection("World tick load")]
public sealed class TileLoadCostTests(ITestOutputHelper output)
{
    [Fact]
    public void GridCreation_TileLoads_AreReportedPerLoader()
    {
        string? root = Environment.GetEnvironmentVariable("ARCANECORE_TEST_TERRAIN_DIR");
        if (Environment.GetEnvironmentVariable("ARCANECORE_TILE_LOAD_COST") != "1" || string.IsNullOrEmpty(root) || !Directory.Exists(Path.Combine(root, "mmaps")))
        {
            output.WriteLine("ARCANECORE_TILE_LOAD_COST=1 and ARCANECORE_TEST_TERRAIN_DIR are not both set: nothing measured.");
            return;
        }

        var terrain = new TerrainManager(root);
        var vmaps = new VMapManager(Path.Combine(root, "vmaps"));
        var mmaps = new NavMeshPathfinder(Path.Combine(root, "mmaps"));
        output.WriteLine("map,tile_x,tile_y,terrain_ms,vmap_ms,mmap_ms,models_loaded");
        double[] totals = new double[3];
        int tiles = 0;
        // The starting areas the bots roam (ClassicDB playercreateinfo positions), 2 x 2 tiles each: Northshire, Westfall, Valley of
        // Trials, Camp Narache, Shadowglen, Coldridge Valley, Deathknell.
        (uint Map, float X, float Y)[] areas = [(0, -8949.95f, -132.49f), (0, -10628f, 1037f), (1, -618.5f, -4251.7f), (1, -2917.6f, -257.98f),
            (1, 10311.3f, 832.46f), (0, -6240.3f, 331.03f), (0, 1676.35f, 1677.45f)];
        foreach ((uint map, float px, float py) in areas)
        {
            (int x0, int y0) = TerrainTile.TileOf(px, py)!.Value;
            for (int dx = 0; dx < 2; dx++)
            {
                for (int dy = 0; dy < 2; dy++)
                {
                    int x = x0 + dx, y = y0 + dy;
                    int models = vmaps.ModelFilesLoaded;
                    long a = Stopwatch.GetTimestamp();
                    terrain.For(map).Load(x, y);
                    long b = Stopwatch.GetTimestamp();
                    vmaps.LoadTile(map, x, y);
                    long c = Stopwatch.GetTimestamp();
                    mmaps.LoadTile(map, x, y);
                    long d = Stopwatch.GetTimestamp();
                    double[] ms = [Ms(b - a), Ms(c - b), Ms(d - c)];
                    for (int i = 0; i < 3; i++) totals[i] += ms[i];
                    tiles++;
                    output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{map},{x},{y},{ms[0]:F2},{ms[1]:F2},{ms[2]:F2},{vmaps.ModelFilesLoaded - models}"));
                }
            }
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"mean per tile over {tiles}: terrain {totals[0] / tiles:F2} ms, vmap {totals[1] / tiles:F2} ms, mmap {totals[2] / tiles:F2} ms"));
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
