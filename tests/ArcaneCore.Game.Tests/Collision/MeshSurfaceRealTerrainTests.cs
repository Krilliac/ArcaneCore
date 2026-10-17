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
/// <see cref="MapCollision.TryWalkMeshSurface"/> on the real navigation mesh: the surface reached by walking the mesh is the ground's,
/// however far the straight line between two route corners runs from it. In the northern Barrens a mesh route's corners lie at 92-93 yd
/// while the hill between them rises to 108 (live: a bot reported 12.1 yd under the terrain at 493.0,-3452.7,95.7).
/// </summary>
public sealed class MeshSurfaceRealTerrainTests(ITestOutputHelper output)
{
    private static string Root => Environment.GetEnvironmentVariable(RealTerrainFactAttribute.Variable)!;

    [RealTerrainFact]
    public void WalkingTheMesh_OverAHill_GivesTheGround_NotTheChord()
    {
        // A corner of the live route (TryPlanToward from 430,-3400 to 580,-3530) and the point on the hill where the walk went under it.
        var corner = new Vector3(469.3332f, -3434.6667f, 92.81875f);
        var onHill = new Vector3(493.2f, -3455.2f, 93.9f);
        using WorldRuntime world = Load(1, corner.X, corner.Y);
        MapCollision collision = world.GetMap(1).Collision;
        float ground = collision.Map.Terrain.GetHeight(onHill.X, onHill.Y, 1000f);
        Assert.True(ground - onHill.Z > 8f, $"the chord is not under the hill here (ground {ground:F2})");

        Assert.True(collision.TryWalkMeshSurface(corner, onHill, out float surface), "no mesh walk from the corner");
        output.WriteLine($"ground {ground:F2}, mesh surface {surface:F2}, chord {onHill.Z:F2}");
        Assert.InRange(surface, ground - 1.5f, ground + 1.5f);

        // The window around the chord that the walk used before does not reach the ground.
        Assert.False(collision.TryGetMeshFloor(onHill.X, onHill.Y, onHill.Z, 4f, 4f, out _));
    }

    [RealTerrainFact]
    public void WalkingOffTheMesh_OrFromFarOffIt_IsNoAnswer()
    {
        var corner = new Vector3(469.3332f, -3434.6667f, 92.81875f);
        using WorldRuntime world = Load(1, corner.X, corner.Y);
        MapCollision collision = world.GetMap(1).Collision;
        // 40 yd under the ground the start is on no polygon.
        Assert.False(collision.TryWalkMeshSurface(corner with { Z = corner.Z - 40f }, corner + new Vector3(10f, 0f, 0f), out _));
    }

    /// <summary>A world whose map has terrain, models and the navigation mesh loaded around (x, y).</summary>
    private static WorldRuntime Load(uint map, float x, float y)
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
                vmaps.LoadTile(map, tx + dx, ty + dy);
                nav.LoadTile(map, tx + dx, ty + dy);
            }
        }

        return world;
    }
}
