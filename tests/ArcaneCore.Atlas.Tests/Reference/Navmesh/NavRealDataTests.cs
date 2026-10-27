using System.Numerics;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Reference.Navmesh;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Terrain;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Atlas.Tests.Reference.Navmesh;

/// <summary>A fact that needs the extracted terrain data root; skipped, visibly, without one.</summary>
public sealed class NavRealTerrainFactAttribute : FactAttribute
{
    public NavRealTerrainFactAttribute()
    {
        if (NavRealTerrain.SkipReason() is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>The theory form of <see cref="NavRealTerrainFactAttribute"/>.</summary>
public sealed class NavRealTerrainTheoryAttribute : TheoryAttribute
{
    public NavRealTerrainTheoryAttribute()
    {
        if (NavRealTerrain.SkipReason() is { } reason)
        {
            Skip = reason;
        }
    }
}

internal static class NavRealTerrain
{
    public static string? SkipReason()
    {
        string? dir = Environment.GetEnvironmentVariable(AtlasPaths.TerrainEnvironmentVariable);
        return string.IsNullOrWhiteSpace(dir) || !Directory.Exists(Path.Combine(dir, "mmaps"))
            ? $"{AtlasPaths.TerrainEnvironmentVariable} is not set to a terrain data root with an mmaps folder (the extracted 1.12.1 navmesh tiles)."
            : null;
    }

    public static string Root => Environment.GetEnvironmentVariable(AtlasPaths.TerrainEnvironmentVariable)!;

    public static string Mmaps => Path.Combine(Root, "mmaps");
}

/// <summary>SplitMix64 (Steele, Lea and Flood 2014): a tiny deterministic generator, so the pairs of a run never change.</summary>
internal sealed class SplitMix64(ulong seed)
{
    private ulong _state = seed;

    public ulong Next()
    {
        ulong z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public int Next(int exclusiveMax) => (int)(Next() % (ulong)exclusiveMax);

    public static ulong Seed(string text)
    {
        ulong h = 0xcbf29ce484222325UL;
        foreach (char c in text)
        {
            h = (h ^ c) * 0x100000001b3UL;
        }

        return h;
    }
}

/// <summary>
/// Ground truth from the extracted client navmesh: what DotRecast reads from the same <c>.mmtile</c> files must equal what the server's
/// own reader reads, and the paths DotRecast finds must classify against the server's pathfinder (the existing
/// <c>DotRecastOracleTests</c> regions plus 50 deterministic pairs per region). Skipped without <c>ARCANECORE_TEST_TERRAIN_DIR</c>.
/// </summary>
public sealed class NavRealDataTests(ITestOutputHelper output)
{
    private static readonly (string Name, uint Map, Vector3[] Anchors)[] Regions =
    [
        ("Coldridge", 0, [new(-6240.32f, 331.033f, 382.758f)]),
        ("Kharanos-switchback", 0, [new(-5165, -876, 507), new(-5384, -716, 397)]),
        ("Goldshire-inn", 0, [new(-9462.66f, 16.1915f, 57.0459f)]),
        ("Deathknell-crypt", 0, [new(1676.35f, 1677.45f, 121.67f)]),
        ("Valley-of-Trials", 1, [new(-618.518f, -4251.67f, 38.718f)]),
        ("Shadowglen", 1, [new(10311.3f, 831.463f, 1326.41f)]),
    ];

    private const int ExtraPairsPerRegion = 50;

    [NavRealTerrainTheory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(189u)]
    [InlineData(36u)]
    [InlineData(34u)]
    public void EveryTile_ParsesTheSameInDotRecastAndInTheServersReader(uint mapId)
    {
        using var service = new ReferenceNavMeshService(NavRealTerrain.Mmaps, NullLogger.Instance);
        TileKey[] tiles = [.. service.AvailableTiles(mapId)];
        Assert.NotEmpty(tiles);
        NavMeshParams parameters = MmapParamsReader.Parse(File.ReadAllBytes(Path.Combine(NavRealTerrain.Mmaps, NavMeshFormat.ParamsFileName(mapId))));
        var mismatches = new List<string>();
        var classes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        long polys = 0, verts = 0, offMeshCons = 0, detailTris = 0;
        int nameMatches = 0, linkSizes16 = 0;

        foreach (TileKey key in tiles)
        {
            byte[] file = File.ReadAllBytes(Path.Combine(NavRealTerrain.Mmaps, WorldGrid.NavTileFileName(key)));
            ParsedNavTile dot;
            try
            {
                dot = MmapTileReader.Parse(file);
            }
            catch (InvalidDataException e)
            {
                Note(mismatches, classes, "dot-refused", $"{key}: {e.Message}");
                continue;
            }

            NavMeshTile server = NavMeshTile.ParseFile(file);
            DtMeshHeader h = dot.Data.header;
            polys += h.polyCount;
            verts += h.vertCount;
            offMeshCons += h.offMeshConCount;
            detailTris += h.detailTriCount;
            linkSizes16 += dot.LinkSize == 16 ? 1 : 0;

            // The terrain tile named by the file and the Detour tile inside it. The generator names a tile NNN + its tileY + its tileX
            // (vmangos@0e3ff01 contrib/mmap/src/TileWorker.cpp:358), so the server's NNNXXYY key has X = generator tileY and
            // Y = generator tileX; the tile spans Recast x up to (32 - tileX) * G and z up to (32 - tileY) * G (MapBuilder.cpp:503-506),
            // and the Detour coordinates are its centre's offset from the map's .mmap origin in whole tiles, truncated to int
            // (vmangos@0e3ff01 contrib/mmap/src/TileWorker.cpp:813-814; the origin is the corner of the map's largest tile,
            // MapBuilder.cpp:449-460).
            float g = parameters.TileWidth;
            int expectX = (int)((((32 - key.Y) * g) - (g / 2) - parameters.Origin.X) / g);
            int expectY = (int)((((32 - key.X) * g) - (g / 2) - parameters.Origin.Z) / g);
            if (h.x == expectX && h.y == expectY && h.layer == 0)
            {
                nameMatches++;
            }
            else
            {
                Note(mismatches, classes, "detour-vs-name", $"{key}: header ({h.x},{h.y},{h.layer})");
            }

            if ((h.x, h.y, h.layer) != (server.X, server.Y, server.Layer))
            {
                Note(mismatches, classes, "header-xyz", $"{key}");
            }

            if (dot.Prefix.Size != file.Length - MmapTileHeader.ByteSize)
            {
                Note(mismatches, classes, "prefix-size", $"{key}");
            }

            if (h.vertCount != server.Vertices.Length || dot.RawVertices.Length != server.Vertices.Length * 3)
            {
                Note(mismatches, classes, "vert-count", $"{key}: {h.vertCount} vs {server.Vertices.Length}");
            }
            else
            {
                for (int i = 0; i < server.Vertices.Length; i++)
                {
                    Vector3 s = server.Vertices[i];
                    if (BitConverter.SingleToInt32Bits(s.X) != BitConverter.SingleToInt32Bits(dot.RawVertices[i * 3])
                        || BitConverter.SingleToInt32Bits(s.Y) != BitConverter.SingleToInt32Bits(dot.RawVertices[(i * 3) + 1])
                        || BitConverter.SingleToInt32Bits(s.Z) != BitConverter.SingleToInt32Bits(dot.RawVertices[(i * 3) + 2]))
                    {
                        Note(mismatches, classes, "vert-coordinate", $"{key} vertex {i}");
                        break;
                    }
                }
            }

            if (h.polyCount != server.Polys.Length)
            {
                Note(mismatches, classes, "poly-count", $"{key}: {h.polyCount} vs {server.Polys.Length}");
            }
            else
            {
                for (int p = 0; p < server.Polys.Length; p++)
                {
                    NavPoly s = server.Polys[p];
                    DtPoly d = dot.Data.polys[p];
                    if (s.Flags != d.flags || s.Area != d.GetArea() || s.Type != d.GetPolyType() || s.VertexCount != d.vertCount)
                    {
                        Note(mismatches, classes, "poly-attributes", $"{key} poly {p}");
                        break;
                    }

                    if (!s.Vertices.Select(v => (int)v).SequenceEqual(d.verts.Take(d.vertCount)) || !s.Neighbours.Select(v => (int)v).SequenceEqual(d.neis.Take(d.vertCount)))
                    {
                        Note(mismatches, classes, "poly-indices", $"{key} poly {p}");
                        break;
                    }
                }

                int serverOffMesh = server.Polys.Count(p => p.IsOffMeshConnection);
                if (serverOffMesh != h.offMeshConCount)
                {
                    Note(mismatches, classes, "off-mesh-count", $"{key}: {h.offMeshConCount} vs {serverOffMesh}");
                }
            }

            if (h.detailMeshCount != server.Details.Length || h.detailVertCount != server.DetailVertices.Length || h.detailTriCount * 4 != server.DetailTriangles.Length)
            {
                Note(mismatches, classes, "detail-counts", $"{key}");
            }
            else
            {
                for (int i = 0; i < server.Details.Length; i++)
                {
                    NavPolyDetail s = server.Details[i];
                    DtPolyDetail d = dot.Data.detailMeshes[i];
                    if (s.VertexBase != (uint)d.vertBase || s.TriangleBase != (uint)d.triBase || s.VertexCount != d.vertCount || s.TriangleCount != d.triCount)
                    {
                        Note(mismatches, classes, "detail-mesh", $"{key} detail {i}");
                        break;
                    }
                }

                for (int i = 0; i < server.DetailVertices.Length; i++)
                {
                    Vector3 s = server.DetailVertices[i];
                    if (s.X != dot.Data.detailVerts[i * 3] || s.Y != dot.Data.detailVerts[(i * 3) + 1] || s.Z != dot.Data.detailVerts[(i * 3) + 2])
                    {
                        Note(mismatches, classes, "detail-vertex", $"{key} detail vertex {i}");
                        break;
                    }
                }

                for (int i = 0; i < server.DetailTriangles.Length; i++)
                {
                    if (server.DetailTriangles[i] != dot.Data.detailTris[i])
                    {
                        Note(mismatches, classes, "detail-triangle", $"{key} byte {i}");
                        break;
                    }
                }
            }

            if (h.bmin.X != server.BoundLow.X || h.bmin.Y != server.BoundLow.Y || h.bmin.Z != server.BoundLow.Z
                || h.bmax.X != server.BoundHigh.X || h.bmax.Y != server.BoundHigh.Y || h.bmax.Z != server.BoundHigh.Z
                || h.walkableClimb != server.WalkableClimb)
            {
                Note(mismatches, classes, "bounds", $"{key}");
            }
        }

        output.WriteLine($"map {mapId}: {tiles.Length} tiles, {polys} polygons, {verts} vertices, {detailTris} detail triangles, {offMeshCons} off-mesh connections; "
            + $"{nameMatches} Detour headers match their file names; {linkSizes16} tiles with 16-byte links");
        foreach ((string name, int count) in classes)
        {
            output.WriteLine($"  mismatch class {name}: {count}");
        }

        Assert.True(mismatches.Count == 0, $"{mismatches.Count} mismatches in {tiles.Length} tiles of map {mapId}: {string.Join("; ", mismatches.Take(12))}");
        Assert.Equal(tiles.Length, nameMatches);
        Assert.Equal(tiles.Length, linkSizes16);
    }

    [NavRealTerrainFact]
    public void ScarletMonasterysOffMeshConnection_IsReadAndLinked()
    {
        using var service = new ReferenceNavMeshService(NavRealTerrain.Mmaps, NullLogger.Instance);
        var source = new ReferenceNavLayerSource(service);
        TileKey tile = new(189, 28, 29);

        GeometryChunk links = Assert.Single(source.Build(LayerIds.RefNavLinks, tile, CancellationToken.None).Chunks);

        Assert.Equal(1, links.Stats["offMeshConnections"]!.GetValue<int>());
        Assert.True(links.Stats["offMeshLinks"]!.GetValue<int>() >= 2, "an off-mesh connection links at least its start polygon both ways");
        KeyValuePair<uint, JsonObject> connection = Assert.Single(links.Attributes, a => a.Value["colorKey"]!.GetValue<string>() == "offmesh");
        output.WriteLine($"189 {tile}: {connection.Value.ToJsonString()}; stats {links.Stats.ToJsonString()}");
        Assert.True(connection.Value["startLinked"]!.GetValue<bool>());
        Assert.True(links.PointCount >= 2);
    }

    [NavRealTerrainFact]
    public void Layers_OnRealTiles_AreWholeAndTheirCrossTileLinksAreSymmetric()
    {
        using var service = new ReferenceNavMeshService(NavRealTerrain.Mmaps, NullLogger.Instance);
        var source = new ReferenceNavLayerSource(service);
        var sample = new List<TileKey>();
        uint[] bigMaps = [0, 1];
        foreach (uint bigMap in bigMaps)
        {
            TileKey[] all = [.. service.AvailableTiles(bigMap)];
            for (int i = 0; i < all.Length; i += Math.Max(1, all.Length / 24))
            {
                sample.Add(all[i]);
            }
        }

        uint[] smallMaps = [189, 34, 43];
        foreach (uint smallMap in smallMaps)
        {
            sample.AddRange(service.AvailableTiles(smallMap));
        }

        int polysDrawn = 0, links = 0, asymmetric = 0, pairsChecked = 0, stale = 0;
        var asymmetries = new List<string>();
        foreach (TileKey tile in sample)
        {
            LayerBuild poly = source.Build(LayerIds.RefNavPoly, tile, CancellationToken.None);
            LayerBuild link = source.Build(LayerIds.RefNavLinks, tile, CancellationToken.None);
            Assert.True(poly.Status.State == TileState.Ok, $"{tile} poly {poly.Status.State}: {poly.Status.Detail}");
            Assert.True(link.Status.State == TileState.Ok, $"{tile} links {link.Status.State}: {link.Status.Detail}");
            Assert.All(poly.Chunks.Concat(link.Chunks), c => c.Validate());
            Assert.All(poly.Chunks.Concat(link.Chunks), c => Assert.Equal(c.Positions, GeometryWire.Read(GeometryWire.Write(c)).Positions));
            polysDrawn += poly.Chunks.Sum(c => c.Stats["polys"]!.GetValue<int>());
            links += link.Chunks.Sum(c => c.LineCount);

            (int tilePolys, int offMesh) = service.OpenMap(tile.MapId).Map!.Read([tile], view =>
            {
                DtMeshHeader h = view.Resident(tile)!.Tile.data.header;
                return (h.polyCount, h.offMeshConCount);
            });
            Assert.Equal(tilePolys - offMesh, poly.Chunks.Sum(c => c.Stats["polys"]!.GetValue<int>()));
            Assert.Equal(poly.Chunks.Sum(c => c.TriangleCount), poly.Chunks.Sum(c => c.Attributes.Values.Sum(a => a["vertCount"]!.GetValue<int>() - 2)));

            // Every neighbouring pair: the links from this tile into a neighbour equal the links from that neighbour into this tile.
            ReferenceNavMap map = service.OpenMap(tile.MapId).Map!;
            Dictionary<(TileKey From, TileKey To), int> counts = map.Read(ReferenceNavLayerSource.Neighbourhood(tile), view =>
            {
                var result = new Dictionary<(TileKey, TileKey), int>();
                foreach (TileKey key in view.Tiles.Where(t => t.Value.State == NavTileState.Ok).Select(t => t.Key))
                {
                    ResidentNavTile resident = view.Resident(key)!;
                    List<NavLink> chain = NavLinkInventory.Collect(view, resident);
                    stale += chain.Count(l => l.TargetPoly is null);
                    foreach (NavLink external in chain.Where(l => l.Kind == NavLinkKind.External && l.TargetKey is not null))
                    {
                        TileKey target = external.TargetKey!.Value;
                        result[(key, target)] = result.GetValueOrDefault((key, target)) + 1;
                    }
                }

                return result;
            });
            foreach (TileKey neighbour in ReferenceNavLayerSource.Neighbourhood(tile).Where(t => t != tile))
            {
                int outgoing = counts.GetValueOrDefault((tile, neighbour));
                int incoming = counts.GetValueOrDefault((neighbour, tile));
                pairsChecked++;
                if (outgoing != incoming)
                {
                    asymmetric++;
                    asymmetries.Add($"{tile}->{neighbour}: {outgoing} out, {incoming} back");
                }
            }
        }

        output.WriteLine($"{sample.Count} tiles: {polysDrawn} polygons drawn, {links} link/outline lines, {pairsChecked} neighbour pairs checked, {asymmetric} asymmetric, {stale} stale links");
        Assert.True(asymmetric == 0, string.Join("; ", asymmetries.Take(10)));
        Assert.Equal(0, stale);
    }

    [NavRealTerrainFact]
    public void EveryParamsFile_ReadsTheSameInTheReferenceAndTheServersReader()
    {
        string[] files = [.. Directory.EnumerateFiles(NavRealTerrain.Mmaps, "*.mmap").Order(StringComparer.Ordinal)];
        Assert.NotEmpty(files);
        int tileCountsWithinMax = 0;
        foreach (string file in files)
        {
            byte[] bytes = File.ReadAllBytes(file);
            NavMeshParams reference = MmapParamsReader.Parse(bytes);
            Assert.Equal(NavMeshParams.Parse(bytes), reference);

            // The generator's maxTiles is the number of tiles it built (MapBuilder.cpp:450), so the map's .mmtile files fit in it.
            uint mapId = uint.Parse(Path.GetFileNameWithoutExtension(file), System.Globalization.CultureInfo.InvariantCulture);
            int tiles = Directory.EnumerateFiles(NavRealTerrain.Mmaps, $"{mapId:D3}????.mmtile").Count();
            tileCountsWithinMax += tiles <= reference.MaxTiles ? 1 : 0;
            output.WriteLine($"{Path.GetFileName(file)}: origin {reference.Origin}, tile {reference.TileWidth} x {reference.TileHeight}, maxTiles {reference.MaxTiles}, maxPolys {reference.MaxPolys}, {tiles} tile files");
        }

        Assert.Equal(files.Length, tileCountsWithinMax);
    }

    private sealed record PairCase(string Region, string Kind, uint Map, Vector3 Start, Vector3 End);

    private sealed record PairOutcome(
        PairCase Case,
        string Category,
        string CorridorCategory,
        string Detail,
        bool StartNearestAgrees,
        bool EndNearestAgrees,
        bool OffMeshChangesResult,
        int Nodes);

    [NavRealTerrainFact]
    public void Paths_OnRealTiles_ClassifyAgainstTheServersPathfinder()
    {
        var outcomes = new List<PairOutcome>();
        foreach ((string name, uint map, Vector3[] anchors) in Regions)
        {
            outcomes.AddRange(CompareRegion(name, map, anchors));
        }

        output.WriteLine($"{outcomes.Count} pairs in {Regions.Length} regions (existing oracle pairs plus {ExtraPairsPerRegion} deterministic ones per region)");
        foreach (IGrouping<string, PairOutcome> region in outcomes.GroupBy(o => o.Case.Region))
        {
            output.WriteLine($"  {region.Key}: {region.Count()} pairs; " + string.Join(", ", region.GroupBy(o => o.Category).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}")));
        }

        foreach (IGrouping<string, PairOutcome> kind in outcomes.GroupBy(o => o.Case.Kind).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            output.WriteLine($"  pairs of kind {kind.Key}: {kind.Count()}; " + string.Join(", ", kind.GroupBy(o => o.Category).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}")));
        }

        output.WriteLine("product-level categories (NavMeshPathfinder.FindPath vs reference path with the server's rules, off-mesh refused):");
        foreach (IGrouping<string, PairOutcome> group in outcomes.GroupBy(o => o.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            output.WriteLine($"  {group.Key}: {group.Count()}");
        }

        output.WriteLine("corridor-level categories (NavMeshQuery.FindCorridor vs DotRecast FindPath on the same polygons):");
        foreach (IGrouping<string, PairOutcome> group in outcomes.GroupBy(o => o.CorridorCategory).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            output.WriteLine($"  {group.Key}: {group.Count()}");
        }

        output.WriteLine($"nearest polygon agreed for {outcomes.Count(o => o.StartNearestAgrees)} of {outcomes.Count} starts and {outcomes.Count(o => o.EndNearestAgrees)} of {outcomes.Count} ends; "
            + $"off-mesh connections changed the reference result for {outcomes.Count(o => o.OffMeshChangesResult)} pairs; "
            + $"{outcomes.Count(o => o.Nodes > PathOptions.DefaultMaxSearchNodes)} searches needed more than the server's {PathOptions.DefaultMaxSearchNodes} nodes");
        // Every pair the budget does not explain, then a sample of the budget-limited ones.
        string[] explained = ["Agree", "BothIncomplete", "NodeBudget"];
        foreach (PairOutcome o in outcomes.Where(o => !explained.Contains(o.Category) || o.CorridorCategory is "NearestPolyDiffers" or "OneSidePartial")
            .Concat(outcomes.Where(o => o.Category is "BothIncomplete" or "NodeBudget").Take(6)))
        {
            output.WriteLine($"  [{o.Category}/{o.CorridorCategory}] {o.Case.Region} {o.Case.Kind} {o.Case.Start} -> {o.Case.End}: {o.Detail}");
        }

        Assert.True(outcomes.Count >= 6 * (ExtraPairsPerRegion + 20), $"only {outcomes.Count} pairs");
        Assert.All(Regions, r => Assert.Contains(outcomes, o => o.Case.Region == r.Name));
        // Same start and end polygons and both searches complete within the server's budget: DotRecast's A* and the server's port
        // walk the same corridor, polygon for polygon.
        Assert.DoesNotContain(outcomes, o => o.CorridorCategory == "CorridorDiffers");
        Assert.True(outcomes.Count(o => o.CorridorCategory == "SameCorridor") * 2 > outcomes.Count, "fewer than half the corridors are identical");
        Assert.True(outcomes.Count(o => o.Category == "Agree") * 2 > outcomes.Count, "fewer than half the paths agree");
    }

    /// <summary>
    /// Both engines' findNearestPoly score (Detour: over the polygon only the height beyond walkableClimb counts, else the squared
    /// distance; vmangos@0e3ff01 dep/recastnavigation/Detour/Source/DetourNavMesh.cpp:781-800) for the server's polygon
    /// and the reference's, when both lie in one tile: which engine sees a different closest point.
    /// </summary>
    private static string ScoreBoth(ReferenceNavMap reference, TileKey[] tiles, NavPolyRef server, NavLocate dot, Vector3 queryRc)
    {
        if (dot.Poly.DetourX != server.Tile.X || dot.Poly.DetourY != server.Tile.Y)
        {
            return "(different tiles)";
        }

        static float Score(Vector3 closest, bool over, Vector3 query, float climb)
        {
            float excess = MathF.Max(0, MathF.Abs(closest.Y - query.Y) - climb);
            return over ? excess * excess : Vector3.DistanceSquared(closest, query);
        }

        Vector3 sOwn = server.Tile.ClosestPointOnPoly(server.Poly, queryRc, out bool sOwnOver);
        Vector3 sOther = server.Tile.ClosestPointOnPoly(dot.Poly.Poly, queryRc, out bool sOtherOver);
        return reference.Read(tiles, view =>
        {
            DtMeshTile tile = view.Mesh.GetTileByRef(view.Mesh.GetTileRefAt(dot.Poly.DetourX, dot.Poly.DetourY, dot.Poly.Layer));
            long baseRef = view.Mesh.GetPolyRefBase(tile);
            var q = new RcVec3f(queryRc.X, queryRc.Y, queryRc.Z);
            view.Mesh.ClosestPointOnPoly(baseRef | (uint)server.Poly, q, out RcVec3f dServer, out bool dServerOver);
            view.Mesh.ClosestPointOnPoly(baseRef | (uint)dot.Poly.Poly, q, out RcVec3f dOwn, out bool dOwnOver);
            float climb = tile.data.header.walkableClimb;
            Vector3 dS = new(dServer.X, dServer.Y, dServer.Z), dO = new(dOwn.X, dOwn.Y, dOwn.Z);
            return $"server engine: poly {server.Poly} {Score(sOwn, sOwnOver, queryRc, server.Tile.WalkableClimb):0.###} (over {sOwnOver}), poly {dot.Poly.Poly} "
                + $"{Score(sOther, sOtherOver, queryRc, server.Tile.WalkableClimb):0.###} (over {sOtherOver}) at {NavMeshFormat.ToWorld(sOther)}; DotRecast: poly {server.Poly} "
                + $"{Score(dS, dServerOver, queryRc, climb):0.###} (over {dServerOver}) at {NavMeshFormat.ToWorld(dS)}, poly {dot.Poly.Poly} {Score(dO, dOwnOver, queryRc, climb):0.###} (over {dOwnOver})";
        });
    }

    private static string Describe(NavLocate? located)
        => located is null ? "none" : $"{located.Poly.CorridorKey} at {located.Nearest}";

    private static IEnumerable<PairOutcome> CompareRegion(string name, uint mapId, Vector3[] anchors)
    {
        string directory = NavRealTerrain.Mmaps;
        var server = new NavMeshPathfinder(directory);
        NavMesh mesh = Assert.IsType<NavMesh>(server.GetNavMesh(mapId));
        using var service = new ReferenceNavMeshService(directory, NullLogger.Instance);
        ReferenceNavMap reference = service.OpenMap(mapId).Map!;

        // The oracle's tile set: 3 x 3 terrain tiles around each anchor, in both engines.
        var loaded = new HashSet<(int X, int Y)>();
        foreach (Vector3 anchor in anchors)
        {
            (int cx, int cy) = TerrainTile.TileOf(anchor.X, anchor.Y)!.Value;
            for (int x = cx - 1; x <= cx + 1; x++)
            {
                for (int y = cy - 1; y <= cy + 1; y++)
                {
                    if (loaded.Add((x, y)) && File.Exists(Path.Combine(directory, NavMeshFormat.TileFileName(mapId, x, y))))
                    {
                        Assert.True(server.LoadTile(mapId, x, y), $"{name} tile {x},{y}");
                    }
                }
            }
        }

        TileKey[] tiles = [.. loaded.OrderBy(t => t.X).ThenBy(t => t.Y).Select(t => new TileKey(mapId, t.X, t.Y))];
        PathOptions options = PathOptions.Default;
        var candidates = new List<Vector3>();
        foreach (TileKey key in tiles)
        {
            string path = Path.Combine(directory, WorldGrid.NavTileFileName(key));
            if (!File.Exists(path))
            {
                continue;
            }

            NavMeshTile tile = NavMeshTile.ParseFile(File.ReadAllBytes(path));
            for (int p = 0; p < tile.Polys.Length; p++)
            {
                if (!tile.Passes(p, options.EffectiveIncludeFlags, options.ExcludeFlags))
                {
                    continue;
                }

                Vector3 world = NavMeshFormat.ToWorld(tile.Center(p));
                if (anchors.Any(a => Vector2.Distance(new Vector2(a.X, a.Y), new Vector2(world.X, world.Y)) < 250))
                {
                    candidates.Add(world);
                }
            }
        }

        Assert.True(candidates.Count >= 20, $"{name}: only {candidates.Count} nearby polygons");
        Vector3[] sampled = [.. Enumerable.Range(0, 20).Select(i => candidates[(int)((long)i * (candidates.Count - 1) / 19)])];
        var cases = new List<PairCase>();
        foreach (Vector3 anchor in anchors)
        {
            cases.Add(new PairCase(name, "anchor", mapId, anchor, sampled.OrderBy(p => Vector3.DistanceSquared(p, anchor)).First()));
        }

        for (int i = 0; i < sampled.Length; i++)
        {
            for (int offset = 1; offset <= 3; offset++)
            {
                cases.Add(new PairCase(name, "oracle", mapId, sampled[i], sampled[(i + (offset * 5)) % sampled.Length]));
            }
        }

        if (name == "Kharanos-switchback")
        {
            cases.Add(new PairCase(name, "anchors", mapId, anchors[0], anchors[1]));
        }

        var rng = new SplitMix64(SplitMix64.Seed(name));
        for (int i = 0; i < ExtraPairsPerRegion; i++)
        {
            Vector3 a = candidates[rng.Next(candidates.Count)];
            Vector3 b = candidates[rng.Next(candidates.Count)];
            cases.Add(new PairCase(name, "seeded", mapId, a, b));
        }

        foreach (PairCase c in cases)
        {
            yield return CompareOne(c, mesh, server, reference, tiles, options);
        }
    }

    private static PairOutcome CompareOne(PairCase c, NavMesh mesh, NavMeshPathfinder server, ReferenceNavMap reference, TileKey[] tiles, PathOptions options)
    {
        PathResult production = server.FindPath(c.Map, c.Start, c.End);
        Vector3 sr = NavMeshFormat.ToRecast(c.Start), er = NavMeshFormat.ToRecast(c.End);

        // The corridor-level view of the server (what the oracle does): nearest polygons in the near box, then the far column.
        bool startOk = mesh.TryFindNearestPoly(sr, NavMeshPathfinder.NearExtents, options, out NavPolyRef sp, out Vector3 sOn)
            || mesh.TryFindNearestPoly(sr, NavMeshPathfinder.FarExtents, options, out sp, out sOn);
        bool endOk = mesh.TryFindNearestPoly(er, NavMeshPathfinder.NearExtents, options, out NavPolyRef ep, out Vector3 eOn)
            || mesh.TryFindNearestPoly(er, NavMeshPathfinder.FarExtents, options, out ep, out eOn);
        string[] serverCorridor = [];
        bool serverComplete = false;
        if (startOk && endOk)
        {
            (var corridor, bool complete) = NavMeshQuery.FindCorridor(mesh, sp, sOn, ep, eOn, options);
            serverCorridor = [.. corridor.Select(p => $"{p.Poly.Tile.X},{p.Poly.Tile.Y},{p.Poly.Poly}")];
            serverComplete = complete;
        }

        (NavPathAnswer strict, NavPathAnswer permissive) = reference.Read(tiles, view =>
            (NavQuery.FindPath(view, c.Start, c.End, options, allowOffMesh: false), NavQuery.FindPath(view, c.Start, c.End, options, allowOffMesh: true)));

        bool startAgrees = startOk && strict.Start is { } ds && ds.Poly.CorridorKey == $"{sp.Tile.X},{sp.Tile.Y},{sp.Poly}"
            && Vector3.Distance(ds.Nearest, NavMeshFormat.ToWorld(sOn)) < 0.01f;
        bool endAgrees = endOk && strict.End is { } de && de.Poly.CorridorKey == $"{ep.Tile.X},{ep.Tile.Y},{ep.Poly}"
            && Vector3.Distance(de.Nearest, NavMeshFormat.ToWorld(eOn)) < 0.01f;

        string[] referenceCorridor = [.. strict.Corridor.Select(p => p.CorridorKey)];
        string corridorCategory = !startOk || !endOk || strict.Corridor.Count == 0 ? "NoCorridor"
            : !startAgrees || !endAgrees ? "NearestPolyDiffers"
            : !serverComplete && !strict.Complete ? "BothPartial"
            : !serverComplete && strict.Nodes > options.MaxSearchNodes ? "NodeBudgetPartial"
            : serverComplete != strict.Complete ? "OneSidePartial"
            : serverCorridor.SequenceEqual(referenceCorridor) ? "SameCorridor"
            : "CorridorDiffers";

        PathResult reference0 = strict.Result;
        bool sameType = production.Type == reference0.Type;
        bool samePoints = production.Points.Count == reference0.Points.Count
            && production.Points.Zip(reference0.Points).All(pair => Vector3.Distance(pair.First, pair.Second) <= 0.5f);
        string category;
        string detail = $"server {production.Type} {production.Points.Count} pts, reference {reference0.Type} {reference0.Points.Count} pts ({strict.Outcome}, {strict.Nodes} nodes)";
        if (!startAgrees || !endAgrees)
        {
            detail += $"; start server {(startOk ? $"{sp.Tile.X},{sp.Tile.Y},{sp.Poly} at {NavMeshFormat.ToWorld(sOn)}" : "none")} reference {Describe(strict.Start)}"
                + $"; end server {(endOk ? $"{ep.Tile.X},{ep.Tile.Y},{ep.Poly} at {NavMeshFormat.ToWorld(eOn)}" : "none")} reference {Describe(strict.End)}";
            if (!startAgrees && startOk && strict.Start is { } rs)
            {
                detail += "; start scores " + ScoreBoth(reference, tiles, sp, rs, sr);
            }

            if (!endAgrees && endOk && strict.End is { } re)
            {
                detail += "; end scores " + ScoreBoth(reference, tiles, ep, re, er);
            }
        }

        if (sameType && samePoints)
        {
            category = "Agree";
        }
        else if ((production.Type & PathType.Incomplete) != 0 && (reference0.Type & PathType.Incomplete) != 0)
        {
            category = "BothIncomplete";
        }
        else if ((production.Type & PathType.Incomplete) != 0 && strict.Nodes > options.MaxSearchNodes)
        {
            category = "NodeBudget";
        }
        else if (!sameType)
        {
            category = "TypeDiffers";
        }
        else if (corridorCategory == "CorridorDiffers")
        {
            category = "CorridorDiffers";
        }
        else
        {
            category = "StraightDiffers";
        }

        if (corridorCategory is "OneSidePartial" || category is "StraightDiffers")
        {
            // Where the two corridors part: the common prefix, and the step after it in each (a tile change shows as other x,y).
            int common = serverCorridor.Zip(referenceCorridor).TakeWhile(pair => pair.First == pair.Second).Count();
            detail += $"; corridors server {serverCorridor.Length} (complete {serverComplete}) reference {referenceCorridor.Length} (complete {strict.Complete}),"
                + $" common prefix {common}, last common {(common > 0 ? referenceCorridor[common - 1] : "none")},"
                + $" next server {(common < serverCorridor.Length ? serverCorridor[common] : "end")} reference {(common < referenceCorridor.Length ? referenceCorridor[common] : "end")}";
        }

        bool offMeshChanges = permissive.Result.Type != strict.Result.Type
            || permissive.Result.Points.Count != strict.Result.Points.Count
            || permissive.Corridor.Count != strict.Corridor.Count;
        return new PairOutcome(c, category, corridorCategory, detail, startAgrees, endAgrees, offMeshChanges, strict.Nodes);
    }

    [NavRealTerrainFact]
    public async Task Commands_OnRealTiles_AnswerFromTheModule()
    {
        using AtlasContext context = AtlasModules.Compose(new AtlasPaths(NavRealTerrain.Root, null), NullLoggerFactory.Instance, headless: true, [new ReferenceNavmeshModule()]);
        Vector3 a = new(-9462.66f, 16.1915f, 57.0459f);
        Vector3 b = a + new Vector3(60, 25, 0);

        async Task<JsonNode> Ok(string name, JsonObject args)
        {
            CommandResult r = await context.Commands.InvokeAsync(name, args, context.Services, CommandOrigin.Script, true, CancellationToken.None);
            Assert.True(r.Ok, $"{name}: {r.ErrorCode} {r.ErrorMessage}");
            return JsonNode.Parse(r.Data!.ToJsonString())!;
        }

        JsonNode nearest = await Ok("ref.query.nearest", new JsonObject { ["map"] = 0, ["at"] = new JsonArray(a.X, a.Y, a.Z) });
        JsonNode path = await Ok("ref.query.path", new JsonObject { ["map"] = 0, ["a"] = new JsonArray(a.X, a.Y, a.Z), ["b"] = new JsonArray(b.X, b.Y, b.Z) });
        TileKey tile = WorldGrid.TileOf(0, a.X, a.Y)!.Value;
        JsonNode header = await Ok("ref.nav.tile", new JsonObject { ["map"] = 0, ["x"] = tile.X, ["y"] = tile.Y });

        output.WriteLine($"nearest {nearest.ToJsonString()[..Math.Min(400, nearest.ToJsonString().Length)]}");
        output.WriteLine($"path {path["status"]} {path["pointCount"]} points, length {path["length"]}, {path["reference"]!["nodes"]} nodes, tiles {path["reference"]!["tiles"]!.ToJsonString()}");
        output.WriteLine($"tile {tile}: {header["header"]!.ToJsonString()} links {header["links"]!.ToJsonString()}");
        Assert.True(nearest["found"]!.GetValue<bool>());
        Assert.Equal("ok", header["state"]!.GetValue<string>());
        Assert.Empty(path["reference"]!["tiles"]!["rejected"]!.AsArray());
        Assert.True(header["header"]!["polyCount"]!.GetValue<int>() > 100);
        Assert.Equal(0, header["links"]!["stale"]!.GetValue<int>());
    }

    private static void Note(List<string> mismatches, SortedDictionary<string, int> classes, string kind, string text)
    {
        classes[kind] = classes.GetValueOrDefault(kind) + 1;
        mismatches.Add($"{kind} {text}");
    }
}
