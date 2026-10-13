using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Terrain;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// The random-point query on the real Goldshire navmesh (vmangos-built tiles, WMO and M2 collision baked in). Runs only with
/// <c>ARCANECORE_TEST_TERRAIN_DIR</c> set (see <see cref="RealTerrainDataTests"/>); skipped, visibly, otherwise.
/// </summary>
public sealed class NavMeshPointQueryRealTerrainTests(ITestOutputHelper output)
{
    private const uint EasternKingdoms = 0;

    /// <summary>Inside the Lion's Pride Inn, Goldshire (classic-db position, also the DotRecast oracle anchor).</summary>
    private static readonly Vector3 GoldshireInn = new(-9462.66f, 16.1915f, 57.0459f);

    [RealTerrainFact]
    public void GoldshireInn_WanderPoints_AreAllOnTheMesh_AndReachable()
    {
        string root = Environment.GetEnvironmentVariable(RealTerrainFactAttribute.Variable)!;
        var nav = new NavMeshPathfinder(Path.Combine(root, "mmaps"));
        (int tx, int ty) = TerrainTile.TileOf(GoldshireInn.X, GoldshireInn.Y)!.Value;
        for (int x = tx - 1; x <= tx + 1; x++)
        {
            for (int y = ty - 1; y <= ty + 1; y++)
            {
                nav.LoadTile(EasternKingdoms, x, y);
            }
        }

        NavMesh mesh = nav.GetNavMesh(EasternKingdoms)!;
        var random = new Random(99);
        var options = new PathOptions { ExcludeFlags = NavTerrain.SteepSlopes };
        int blockedStraight = 0;
        for (int i = 0; i < 200; i++)
        {
            Assert.True(nav.TryGetRandomPointAround(EasternKingdoms, GoldshireInn, 12f, random, options, out Vector3 p));
            float dx = p.X - GoldshireInn.X;
            float dy = p.Y - GoldshireInn.Y;
            Assert.True((dx * dx) + (dy * dy) <= 12.05f * 12.05f, $"{p} is outside the 12 yd radius");
            Assert.True(mesh.TryFindNearestPoly(NavMeshFormat.ToRecast(p), new Vector3(0.05f, 1f, 0.05f), options, out _, out _), $"{p} is off the mesh");
            Assert.True(nav.FindPath(EasternKingdoms, GoldshireInn, p, options).HasPath, $"no path from the inn to {p}");

            // The straight line to the point crosses the mesh boundary (a wall of the inn) for some of them: that is what wander used to walk.
            MeshRaycastResult ray = nav.Raycast(EasternKingdoms, GoldshireInn, p, options);
            blockedStraight += ray.Available && ray.Hit ? 1 : 0;
        }

        output.WriteLine($"{blockedStraight} of 200 wander points are not in a straight line over the mesh from the inn");

        // The inn's walls stand between the centre and some points (a raycast that never hit would be a dead query), but not all of them.
        Assert.InRange(blockedStraight, 1, 199);

        // Small wander radii must still spread over the disc: a fall-back is the centre itself.
        foreach (float radius in new[] { 1f, 3f, 5f })
        {
            int fallbacks = 0;
            float farthest = 0;
            for (int i = 0; i < 200; i++)
            {
                Assert.True(nav.TryGetRandomPointAround(EasternKingdoms, GoldshireInn, radius, random, options, out Vector3 p));
                float d = MathF.Sqrt(((p.X - GoldshireInn.X) * (p.X - GoldshireInn.X)) + ((p.Y - GoldshireInn.Y) * (p.Y - GoldshireInn.Y)));
                fallbacks += d < 0.01f ? 1 : 0;
                farthest = MathF.Max(farthest, d);
            }

            output.WriteLine($"radius {radius}: {fallbacks} of 200 fell back to the centre, farthest {farthest:F2}");
            Assert.True(fallbacks <= 20, $"radius {radius}: {fallbacks} of 200 draws fell back to the centre");
            Assert.True(farthest > 0.7f * radius, $"radius {radius}: farthest point only {farthest} yd out");
        }
    }
}
