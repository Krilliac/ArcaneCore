using System.Globalization;
using System.Numerics;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Game.Maps.Terrain;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// #1475 on real terrain, model and mesh data (map 0): at both live airwalk reports there is no floor straight under the bot within the
/// airwalk judge's window, in the model data or on the mesh. The only polygon the mesh offers is the edge of a storey or platform
/// beside the bot (the zero-length walk answers a hit there), so the judge's airwalk verdict matches the data: the bot stood past an
/// edge, below that surface. The probe lines show each query's answer.
/// </summary>
public sealed class Airwalk1475RealTerrainTests(ITestOutputHelper output)
{
    private static string Root => Environment.GetEnvironmentVariable(RealTerrainFactAttribute.Variable)!;

    public static TheoryData<string, float, float, float> Sites => new()
    {
        { "site-a", -3746.9f, -706.8f, 7.8f },
        { "ironforge", -4599.4f, -902.4f, 523.9f },
    };

    [RealTerrainTheory]
    [MemberData(nameof(Sites))]
    public void NoFloorUnderTheBot_OnlyAPolygonEdgeBesideIt(string name, float x, float y, float z)
    {
        using WorldRuntime world = Load(x, y);
        MapCollision c = world.GetMap(0).Collision;
        var steep = new PathOptions { Mover = PathMover.Player, ExcludeFlags = NavTerrain.SteepSlopes };
        var playerAll = new PathOptions { Mover = PathMover.Player };
        string F(float v) => v.ToString("F2", CultureInfo.InvariantCulture);
        output.WriteLine($"PROBE {name} at {F(x)},{F(y)},{F(z)}");
        output.WriteLine($"PROBE {name} terrain={F(c.Map.Terrain.GetHeight(x, y, z))} GetHeight(z)={F(c.GetHeight(x, y, z))} GetHeight(z+4)={F(c.GetHeight(x, y, z + 4f))} GetHeight(z,noModels)={F(c.GetHeight(x, y, z, useModels: false))}");
        output.WriteLine($"PROBE {name} model(z+2,60)={c.LineOfSight.GetModelHeight(0, x, y, z + 2f, 60f)?.ToString("F2", CultureInfo.InvariantCulture) ?? "null"}");
        bool under = c.TryGetMeshFloorUnder(x, y, z, 6f, out float uh);
        output.WriteLine($"PROBE {name} TryGetMeshFloorUnder(6)={under} h={F(uh)}");
        foreach (float w in new[] { 2f, 8f, 15f, 30f })
        {
            bool m = c.TryGetMeshFloor(x, y, z, w, w, out float mh);
            output.WriteLine($"PROBE {name} TryGetMeshFloor(±{w})={m} h={F(mh)}");
        }

        output.WriteLine($"PROBE {name} ClassifyUnderMap(z,8)={c.ClassifyUnderMap(x, y, z, 8f)}");
        if (c.MeshPoints is { } mp)
        {
            var at = new Vector3(x, y, z);
            foreach ((string label, PathOptions o) in new[] { ("MeshOptions", steep), ("Player", playerAll), ("Default", PathOptions.Default) })
            {
                MeshRaycastResult r = mp.Raycast(0, at, at, o);
                float side = Vector2.Distance(new(r.Point.X, r.Point.Y), new(x, y));
                output.WriteLine($"PROBE {name} Raycast0({label}) avail={r.Available} hit={r.Hit} pt={F(r.Point.X)},{F(r.Point.Y)},{F(r.Point.Z)} side={F(side)} dz={F(r.Point.Z - z)}");
            }
        }

        for (int dx = -2; dx <= 2; dx++)
        {
            for (int dy = -2; dy <= 2; dy++)
            {
                if (c.TryGetMeshFloor(x + dx, y + dy, z, 30f, 30f, out float g))
                {
                    output.WriteLine($"PROBE {name} grid({dx},{dy}) mesh={F(g)} model={F(c.GetHeight(x + dx, y + dy, z))}");
                }
            }
        }

        Assert.False(under, $"{name}: a mesh floor under the bot at {F(uh)}");
        Assert.True(z - c.GetHeight(x, y, z) > 6f, $"{name}: a model or terrain floor within the airwalk window");
        MeshRaycastResult start = c.MeshPoints!.Raycast(0, new Vector3(x, y, z), new Vector3(x, y, z), steep);
        Assert.True(start.Available && start.Hit, $"{name}: the nearest polygon is under the bot");
        Assert.True(Vector2.Distance(new(start.Point.X, start.Point.Y), new(x, y)) > IMeshFloorQuery.MeshFloorHorizontalYards, $"{name}: the edge is straight under the bot");
    }

    private static WorldRuntime Load(float x, float y)
    {
        var vmaps = new VMapManager(Path.Combine(Root, "vmaps"));
        var nav = new NavMeshPathfinder(Path.Combine(Root, "mmaps"));
        WorldRuntime world = TestWorld.CreateRuntime();
        world.Options.Maps.DataDirectory = Root;
        WorldCollision.Of(world).Install(lineOfSight: vmaps, pathfinder: nav);
        (int tx, int ty) = TerrainTile.TileOf(x, y)!.Value;
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                vmaps.LoadTile(0, tx + dx, ty + dy);
                nav.LoadTile(0, tx + dx, ty + dy);
            }
        }

        return world;
    }
}
