using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Terrain;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// Navigation tiles read for a query (<see cref="PathOptions.LoadTiles"/>). vmangos loads navmesh tiles only with the map's grids,
/// round the players; a search towards a far destination stops at the last loaded tile and answers the point nearest the goal there.
/// In the live stress test (2026-10-08) bots on the edge of the Red Cloud Mesa (Mulgore) got that answer for Innkeeper Kauth in
/// Bloodhoof Village (quest 1656, 99 stall reports): the road down runs south through a tile no grid held, and the nearest point was
/// the cliff edge they stood on.
/// </summary>
public sealed class NavMeshOnDemandTileTests(ITestOutputHelper output)
{
    private const uint Kalimdor = 1;
    private static readonly Vector3 MesaEdge = new(-2764.0f, -312.0f, 54.5f);
    private static readonly Vector3 InnkeeperKauth = new(-2365.4f, -347.3f, -8.9f);

    private static string Root => Environment.GetEnvironmentVariable(RealTerrainFactAttribute.Variable)!;

    private static PathOptions Options(bool loadTiles) => new()
    {
        MaxPoints = 128, Mover = PathMover.Player, ExcludeFlags = NavTerrain.SteepSlopes, MaxSearchNodes = 32_768, LoadTiles = loadTiles,
    };

    /// <summary>The tiles the grids round a bot on the mesa edge and round Bloodhoof Village hold.</summary>
    private static NavMeshPathfinder WithGridTiles()
    {
        var nav = new NavMeshPathfinder(Path.Combine(Root, "mmaps"));
        foreach (Vector3 at in new[] { MesaEdge, InnkeeperKauth })
        {
            (int x, int y) = TerrainTile.TileOf(at.X, at.Y)!.Value;
            Assert.True(nav.LoadTile(Kalimdor, x, y));
        }

        return nav;
    }

    [RealTerrainFact]
    public void WithOnlyTheGridTiles_TheSearchStopsOnTheMesaEdge()
    {
        PathResult path = WithGridTiles().FindPath(Kalimdor, MesaEdge, InnkeeperKauth, Options(loadTiles: false));
        output.WriteLine($"{path.Type} {path.Points.Count} points, length {path.Length:F0}, ends {path.End}");
        Assert.True((path.Type & PathType.Incomplete) != 0);
        Assert.True(Vector3.Distance(path.End, MesaEdge) < 3f, "the nearest point is the edge the bot stands on");
    }

    [RealTerrainFact]
    public void WithLoadTiles_TheSearchFollowsTheRoadDownToBloodhoof()
    {
        NavMeshPathfinder nav = WithGridTiles();
        PathResult path = nav.FindPath(Kalimdor, MesaEdge, InnkeeperKauth, Options(loadTiles: true));
        output.WriteLine($"{path.Type} {path.Points.Count} points, length {path.Length:F0}, {nav.OnDemandTileCount} tiles read");
        Assert.Equal(PathType.Normal, path.Type);
        Assert.True(Vector3.Distance(path.End, InnkeeperKauth) < 1f);
        Assert.InRange(nav.OnDemandTileCount, 1, NavMeshPathfinder.MaxTilesPerQuery);

        // The default query (creatures) is unchanged: vmangos' answer with the grid tiles.
        var creatures = WithGridTiles();
        Assert.True((creatures.FindPath(Kalimdor, MesaEdge, InnkeeperKauth, Options(loadTiles: false) with { ExcludeFlags = NavTerrain.Empty }).Type
            & PathType.Incomplete) != 0);
        Assert.Equal(0, creatures.OnDemandTileCount);
    }

    [RealTerrainFact]
    public void TilesReadForQueries_AreBounded_AndAGridTakesATileOver()
    {
        var nav = new NavMeshPathfinder(Path.Combine(Root, "mmaps"));
        // A query at the centre of each of 40 Kalimdor tiles reads that tile: past the bound the oldest are released.
        int most = 0;
        foreach (string file in Directory.GetFiles(Path.Combine(Root, "mmaps"), "001*.mmtile").Order().Take(NavMeshPathfinder.MaxOnDemandTiles + 8))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            int tx = int.Parse(name.AsSpan(3, 2)), ty = int.Parse(name.AsSpan(5, 2));
            Vector3 centre = new((32 - tx - 0.5f) * ArcaneCore.Game.Maps.Grid.GridDefines.SizeOfGrids, (32 - ty - 0.5f) * ArcaneCore.Game.Maps.Grid.GridDefines.SizeOfGrids, 0f);
            nav.FindPath(Kalimdor, centre, centre + new Vector3(5, 0, 0), Options(loadTiles: true));
            most = Math.Max(most, nav.OnDemandTileCount);
            Assert.True(nav.OnDemandTileCount <= NavMeshPathfinder.MaxOnDemandTiles);
        }

        Assert.Equal(NavMeshPathfinder.MaxOnDemandTiles, most); // the bound was reached, and held

        // A tile a grid loads is the grid's: it is no longer counted (nor released) as read for a query.
        (int x, int y) = TerrainTile.TileOf(MesaEdge.X, MesaEdge.Y)!.Value;
        nav.FindPath(Kalimdor, MesaEdge, MesaEdge + new Vector3(5, 0, 0), Options(loadTiles: true));
        int before = nav.OnDemandTileCount;
        ((ICollisionTileLifecycle)nav).OnTileLoaded(Kalimdor, x, y);
        Assert.True(nav.OnDemandTileCount < before);
        Assert.True(nav.GetNavMesh(Kalimdor)!.IsTerrainTileLoaded(x, y));
        ((ICollisionTileLifecycle)nav).OnTileUnloaded(Kalimdor, x, y);
        Assert.False(nav.GetNavMesh(Kalimdor)!.IsTerrainTileLoaded(x, y));
    }
}
