using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Game.Maps.Terrain;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// <see cref="MapCollision.ClassifyUnderMap"/> on real terrain, model and navigation-mesh data under Ironforge (a WMO city under the
/// mountain terrain) and in the Dun Morogh - Loch Modan tunnel, where the live server reported "move/under-map reason=below-terrain"
/// for bots. A unit standing on the floor there is not under the map; a unit far under that floor, or under open ground, still is
/// (the mesh floor is searched from 2 yards above the point down, never higher).
/// </summary>
public sealed class WmoUnderMapRealTerrainTests(ITestOutputHelper output)
{
    private const float PlayerMargin = 50f;

    private static string Root => Environment.GetEnvironmentVariable(RealTerrainFactAttribute.Variable)!;

    /// <summary>Live under-map / airwalk report positions (map 0): name, x, y, z.</summary>
    public static TheoryData<string, float, float, float> Samples => new()
    {
        { "Ironforge commons", -4637.55f, -1033.04f, 501.38f },
        { "Ironforge (Dugro)", -4677.17f, -1013.01f, 502.34f },
        { "Ironforge (Seizihous)", -4601.65f, -896.47f, 503.01f },
        { "Loch Modan tunnel", -5590.54f, -2475.30f, 394.35f },
    };

    [RealTerrainTheory]
    [MemberData(nameof(Samples))]
    public void OnTheFloor_IsNotUnderTheMap_FarUnderIt_Is(string name, float x, float y, float z)
    {
        using WorldRuntime world = Load(x, y);
        MapCollision collision = world.GetMap(0).Collision;
        Assert.True(collision.TryGetMeshFloor(x, y, z, 8f, 8f, out float floor), $"{name}: no mesh floor near {z}");
        float terrain = collision.Map.Terrain.GetHeight(x, y, floor);
        output.WriteLine($"{name}: mesh floor {floor:F2}, terrain {terrain:F2}");
        Assert.True(terrain - floor > PlayerMargin, $"{name}: the floor is not deep under the terrain ({terrain:F2} over {floor:F2})");

        UnderMapVerdict standing = collision.ClassifyUnderMap(x, y, floor + 0.1f, PlayerMargin);
        Assert.Equal(UnderMapKind.Ok, standing.Kind);

        // Fallen through: 70 yards under that floor, in the rock. Neither the model nor the mesh floor above may excuse it.
        UnderMapVerdict fallen = collision.ClassifyUnderMap(x, y, floor - 70f, PlayerMargin);
        Assert.Equal(UnderMapKind.UnderMap, fallen.Kind);
    }

    [RealTerrainFact]
    public void UnderOpenGround_OutsideAnyModel_IsStillUnderTheMap()
    {
        // Dun Morogh snowfield west of Kharanos: open terrain, no cave or building under it.
        const float x = -5700f, y = -300f;
        using WorldRuntime world = Load(x, y);
        MapCollision collision = world.GetMap(0).Collision;
        float terrain = collision.Map.Terrain.GetHeight(x, y, 1000f);
        Assert.True(terrain > TerrainTile.InvalidHeight, "no terrain");
        Assert.Equal(UnderMapKind.Ok, collision.ClassifyUnderMap(x, y, terrain + 0.1f, PlayerMargin).Kind);
        UnderMapVerdict under = collision.ClassifyUnderMap(x, y, terrain - 80f, PlayerMargin);
        Assert.Equal(UnderMapKind.UnderMap, under.Kind);
        Assert.False(collision.TryGetMeshFloorUnder(x, y, terrain - 80f, MapCollision.UnderMapFloorSearch, out _));
    }

    /// <summary>A world whose map 0 has terrain, models and the navigation mesh loaded around (x, y).</summary>
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
