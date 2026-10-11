using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Data.Graveyards;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.WorldData;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Population;

/// <summary>
/// <see cref="TerrainManager.ReadZoneAndAreaIdDetached"/> (reads one tile file itself, off the world thread) must give exactly the
/// (zone, area) of the cached <see cref="TerrainInfo.GetZoneAndAreaId"/> on the real 5875 terrain and AreaTable.dbc. Gated like the
/// other real-terrain tests: <c>ARCANECORE_TEST_TERRAIN_DIR</c> (holds maps/) and <c>ARCANECORE_TEST_DBC_DIR</c> (AreaTable.dbc,
/// WorldSafeLocs.dbc); reported Skipped, never a silent pass, without them.
/// </summary>
public sealed class DetachedZoneAreaParityTests(ITestOutputHelper output)
{
    private const uint AlteracMountains = 36;

    // The Alterac Valley exit graveyard (battleground return point in the Alterac Mountains, map 0).
    private const float AvExitX = 101.1f, AvExitY = -184.9f, AvExitZ = 127.3f;

    private static (TerrainManager Terrain, string Dbc) Load()
    {
        string dbc = Environment.GetEnvironmentVariable("ARCANECORE_TEST_DBC_DIR")!;
        string root = Environment.GetEnvironmentVariable(RealTerrainBotFactAttribute.Variable)!;
        var terrain = new TerrainManager(root)
        {
            Areas = new AreaTable(AreaTableDbcReader.Read(Path.Combine(dbc, "AreaTable.dbc")).Select(r => new AreaTemplate(
                r.Entry, r.MapId, r.ZoneId, r.ExploreFlag, r.Flags, r.AreaLevel, r.Name, r.Team, r.LiquidTypeId)), MapRegistry.Default),
        };
        return (terrain, dbc);
    }

    [RealAreaTerrainFact]
    public void RealTerrain_DetachedRead_EqualsTheCachedLookup_AtGraveyardsBordersAndMissingTiles()
    {
        (TerrainManager terrain, string dbc) = Load();
        var points = new List<(string Label, uint Map, float X, float Y)>();

        // Every graveyard of the client's WorldSafeLocs.dbc on the two continents.
        foreach (WorldSafeLoc loc in WorldSafeLocsDbcReader.Load(Path.Combine(dbc, "WorldSafeLocs.dbc")).Where(l => l.MapId is 0 or 1))
            points.Add(($"safe loc {loc.Id} {loc.Name}", loc.MapId, loc.X, loc.Y));
        int graveyards = points.Count;
        Assert.True(graveyards >= 30, $"expected the client's continent graveyards, found {graveyards}");
        points.Add(("AV exit", 0, AvExitX, AvExitY));

        // Tile borders (exact multiples of the tile size and one float step either side) and cell borders (16 per tile), on tiles
        // around Elwynn/Westfall/Duskwood (map 0) and the Barrens/Orgrimmar (map 1), both axes varied independently.
        float tile = GridDefines.SizeOfGrids, cell = tile / 16f;
        foreach ((uint map, float cx, float cy) in new[] { (0u, -9000f, -100f), (0u, -10600f, 500f), (0u, 1600f, 1600f), (1u, -600f, -4200f), (1u, 1500f, -4400f) })
        {
            float bx = MathF.Round(cx / tile) * tile, by = MathF.Round(cy / tile) * tile;
            foreach (float dx in new[] { -2 * cell, -cell, 0f, cell, 5 * cell })
            {
                foreach (float dy in new[] { -tile, -cell, 0f, cell, 7 * cell })
                {
                    float x = bx + dx, y = by + dy;
                    foreach (float ex in new[] { x, MathF.BitDecrement(x), MathF.BitIncrement(x) })
                    {
                        points.Add(("border", map, ex, y));
                        points.Add(("border", map, y, ex)); // swapped axes too: a different tile and cell
                    }
                }
            }
        }

        // Tiles that do not exist: inside the grid but no file (open sea), and outside it, and not-a-number.
        points.Add(("no file (sea)", 0, -1000f, 16000f));
        points.Add(("no file (sea)", 1, 15000f, -15000f));
        points.Add(("outside the grid", 0, 40000f, 0f));
        points.Add(("outside the grid", 1, 0f, -40000f));
        points.Add(("NaN", 0, float.NaN, 0f));
        points.Add(("infinity", 1, 0f, float.PositiveInfinity));

        var mismatches = new List<string>();
        int known = 0;
        foreach ((string label, uint map, float x, float y) in points)
        {
            (uint zone, uint area) cached = terrain.For(map).GetZoneAndAreaId(x, y, 0f);
            (uint zone, uint area) detached = terrain.ReadZoneAndAreaIdDetached(map, x, y, terrain.Areas);
            if (cached != detached)
                mismatches.Add($"{label} map {map} ({x}, {y}): cached {cached}, detached {detached}");
            if (cached.zone != 0)
                known++;
        }

        output.WriteLine($"points compared: {points.Count} ({graveyards} graveyards, {points.Count - graveyards} others); {known} resolved to a zone");
        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));

        // A check that read nothing would agree with itself: the real data must give real zones.
        Assert.True(known > graveyards, $"only {known} of {points.Count} points resolved to a zone: the terrain was not read");
        Assert.Equal((0u, 0u), terrain.ReadZoneAndAreaIdDetached(0, 40000f, 0f, terrain.Areas));
        Assert.Equal((0u, 0u), terrain.ReadZoneAndAreaIdDetached(0, float.NaN, 0f, terrain.Areas));
    }

    [RealAreaTerrainFact]
    public void RealTerrain_DetachedRead_PutsTheAlteracValleyExitInTheAlteracMountains()
    {
        (TerrainManager terrain, _) = Load();
        (uint zone, uint area) = terrain.ReadZoneAndAreaIdDetached(0, AvExitX, AvExitY, terrain.Areas);
        Assert.Equal(AlteracMountains, zone);
        Assert.NotEqual(0u, area);
        Assert.Equal((zone, area), terrain.For(0).GetZoneAndAreaId(AvExitX, AvExitY, AvExitZ));
    }
}
