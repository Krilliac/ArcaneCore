using System.Numerics;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Navmesh;
using ArcaneCore.Game.Maps.Collision.MMaps;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Navmesh;

public sealed class ReferenceNavLayerTests : IDisposable
{
    // Flags cycling over the cells: ground, water, magma, slime, steep, ground + steep, water + magma, none.
    private static readonly ushort[] Pattern = [0x01, 0x08, 0x02, 0x04, 0x10, 0x11, 0x0a, 0x00];

    private static readonly string[] PatternColors = ["ground", "water", "magma", "slime", "steep", "steep", "magma", "empty"];

    private readonly SyntheticMap _data = new();
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (IDisposable d in _disposables)
        {
            d.Dispose();
        }

        _data.Dispose();
    }

    private static TileKey Key(int x, int y) => new(0, x, y);

    private BuiltTile Add(int x, int y, int cells = 4, Func<int, int, ushort>? flags = null, Func<int, int, bool>? present = null, IReadOnlyList<OffMeshSpec>? offMesh = null)
        => _data.Add(new TileSpec
        {
            TerrainX = x,
            TerrainY = y,
            Cells = cells,
            Flags = flags ?? ((i, j) => Pattern[((i * cells) + j) % Pattern.Length]),
            Present = present ?? ((_, _) => true),
            OffMesh = offMesh ?? [],
        });

    private ReferenceNavMeshService NewService(int maxResident = 96)
    {
        var service = new ReferenceNavMeshService(_data.Mmaps, NullLogger.Instance, maxResident);
        _disposables.Add(service);
        return service;
    }

    private ReferenceNavLayerSource Source(long maxChunkBytes = GeometryWire.MaxChunkBytes / 4, int maxResident = 96)
        => new(NewService(maxResident), maxChunkBytes);

    private static GeometryChunk Single(LayerBuild build)
    {
        Assert.Equal(TileState.Ok, build.Status.State);
        return Assert.Single(build.Chunks);
    }

    private static string ColorKey(GeometryChunk chunk, uint attribute) => chunk.Attributes[attribute]["colorKey"]!.GetValue<string>();

    [Fact]
    public void Layers_AreTheTwoReferenceNavLayers()
    {
        ReferenceNavLayerSource source = Source();

        Assert.Equal([LayerIds.RefNavPoly, LayerIds.RefNavLinks], source.Layers);
        Assert.All(source.Layers, id => Assert.True(LayerCatalog.TryGet(id, out _)));
    }

    [Fact]
    public void AvailableTiles_ListsTheTileFiles_ForBothLayers()
    {
        Add(32, 32);
        Add(10, 12);
        ReferenceNavLayerSource source = Source();

        Assert.Equal(new[] { Key(10, 12), Key(32, 32) }, source.AvailableTiles(LayerIds.RefNavPoly, 0));
        Assert.Equal(new[] { Key(10, 12), Key(32, 32) }, source.AvailableTiles(LayerIds.RefNavLinks, 0));
        Assert.Empty(source.AvailableTiles(LayerIds.RefNavPoly, 1));
        Assert.Throws<ArgumentException>(() => source.AvailableTiles(LayerIds.Terrain, 0));
    }

    [Fact]
    public void Build_Poly_DrawsEveryPolygon_ColouredByItsFlags()
    {
        BuiltTile built = Add(32, 32);
        ReferenceNavLayerSource source = Source();

        GeometryChunk chunk = Single(source.Build(LayerIds.RefNavPoly, Key(32, 32), CancellationToken.None));

        chunk.Validate();
        Assert.Equal(0, chunk.Part);
        Assert.Equal(32, chunk.TriangleCount);

        // A full 4 x 4 grid has 5 x 4 horizontal and 5 x 4 vertical outline edges, each shared edge once.
        Assert.Equal(40, chunk.LineCount);
        Assert.Equal(25, chunk.VertexCount);
        Assert.Equal(16, chunk.Attributes.Count);
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                uint poly = (uint)((i * 4) + j);
                JsonObject attribute = chunk.Attributes[poly];
                Assert.Equal(PatternColors[(int)poly % 8], attribute["colorKey"]!.GetValue<string>());
                Assert.Equal(Pattern[(int)poly % 8], attribute["flags"]!.GetValue<int>());
                Assert.Equal(4, attribute["vertCount"]!.GetValue<int>());
                Assert.Equal(1 + ((i + j) % 4), attribute["area"]!.GetValue<int>());
                Assert.Equal((int)poly, attribute["poly"]!.GetValue<int>());
            }
        }

        Assert.Equal("Ground", chunk.Attributes[0]["flagNames"]!.GetValue<string>());
        Assert.Equal("Water", chunk.Attributes[1]["flagNames"]!.GetValue<string>());
        Assert.Equal("Ground|SteepSlopes", chunk.Attributes[5]["flagNames"]!.GetValue<string>());
        Assert.Equal("None", chunk.Attributes[7]["flagNames"]!.GetValue<string>());
        Assert.Equal(16, chunk.Stats["polys"]!.GetValue<int>());
        Assert.Equal(16, chunk.Stats["tilePolys"]!.GetValue<int>());
        Assert.Equal(0, chunk.Stats["offMeshPolysSkipped"]!.GetValue<int>());
        Assert.Equal(new[] { built.DetourX, built.DetourY, 0 }, chunk.Stats["detour"]!.AsArray().Select(n => n!.GetValue<int>()));
        Assert.False(chunk.Stats["usesLiquids"]!.GetValue<bool>());
        Assert.All(chunk.TriangleAttributes, a => Assert.InRange(a, 0u, 15u));
    }

    [Fact]
    public void Build_Poly_PlacesVerticesInsideTheTile_AtTheMeshHeight()
    {
        Add(32, 32);
        ReferenceNavLayerSource source = Source();

        GeometryChunk chunk = Single(source.Build(LayerIds.RefNavPoly, Key(32, 32), CancellationToken.None));

        (double minX, double maxX, double minY, double maxY) = WorldGrid.Bounds(Key(32, 32));
        Assert.Equal(WorldGrid.Origin(Key(32, 32)), chunk.Origin);
        for (uint v = 0; v < chunk.VertexCount; v++)
        {
            WorldPoint p = chunk.Vertex(v);
            Assert.InRange(p.X, minX - 0.01, maxX + 0.01);
            Assert.InRange(p.Y, minY - 0.01, maxY + 0.01);
            Assert.Equal(10.0, p.Z, 3);
        }

        // Cell (0, 0) is the corner nearest the tile's origin in Recast axes: its centre is where the synthetic writer put it.
        Vector3 centre = SyntheticNav.CellCenterWorld(32, 32, 4, 0, 0, 10f);
        IEnumerable<uint> corners = Enumerable.Range(0, chunk.TriangleCount).Where(t => chunk.TriangleAttributes[t] == 0).SelectMany(t => chunk.Triangles.Skip(t * 3).Take(3)).Distinct();
        WorldPoint[] quad = [.. corners.Select(chunk.Vertex)];
        Assert.Equal(4, quad.Length);
        Assert.Equal(centre.X, quad.Average(p => p.X), 2);
        Assert.Equal(centre.Y, quad.Average(p => p.Y), 2);
    }

    [Fact]
    public void Build_Poly_OfATileWithGaps_DrawsOnlyThePresentPolygons()
    {
        Add(32, 32, cells: 3, present: (i, j) => (i + j) % 2 == 0);
        ReferenceNavLayerSource source = Source();

        GeometryChunk chunk = Single(source.Build(LayerIds.RefNavPoly, Key(32, 32), CancellationToken.None));

        Assert.Equal(5 * 2, chunk.TriangleCount);
        Assert.Equal(5, chunk.Stats["polys"]!.GetValue<int>());
    }

    [Fact]
    public void Build_Poly_OfATileWithNoPolygons_IsRefused_BecauseDetourCannotAddIt()
    {
        Add(32, 32, cells: 2, present: (_, _) => false);
        ReferenceNavLayerSource source = Source();

        LayerBuild build = source.Build(LayerIds.RefNavPoly, Key(32, 32), CancellationToken.None);

        Assert.Equal(TileState.Rejected, build.Status.State);
        Assert.Contains("link table of 0 links", build.Status.Detail, StringComparison.Ordinal);
        Assert.Empty(build.Chunks);
    }

    [Fact]
    public void Build_Poly_SkipsOffMeshPolygons_AndSaysSo()
    {
        Vector3 a = SyntheticNav.CellCenterRecast(32, 32, 3, 0, 1, 10f);
        Vector3 b = SyntheticNav.CellCenterRecast(32, 32, 3, 2, 1, 10f);
        Add(32, 32, cells: 3, present: (i, _) => i != 1, offMesh: [new OffMeshSpec(a, b, 3f, Bidirectional: true)]);
        ReferenceNavLayerSource source = Source();

        GeometryChunk chunk = Single(source.Build(LayerIds.RefNavPoly, Key(32, 32), CancellationToken.None));

        Assert.Equal(12, chunk.TriangleCount);
        Assert.Equal(6, chunk.Stats["polys"]!.GetValue<int>());
        Assert.Equal(7, chunk.Stats["tilePolys"]!.GetValue<int>());
        Assert.Equal(1, chunk.Stats["offMeshPolysSkipped"]!.GetValue<int>());
    }

    [Fact]
    public void Build_Links_CountsAndDrawsInternalAndExternalLinks()
    {
        Add(32, 32);
        Add(32, 31);
        ReferenceNavLayerSource source = Source();

        GeometryChunk chunk = Single(source.Build(LayerIds.RefNavLinks, Key(32, 32), CancellationToken.None));

        chunk.Validate();
        Assert.Equal(48, chunk.Stats["internalLinks"]!.GetValue<int>());
        Assert.Equal(4, chunk.Stats["externalLinks"]!.GetValue<int>());
        Assert.Equal(0, chunk.Stats["offMeshLinks"]!.GetValue<int>());
        Assert.Equal(0, chunk.Stats["staleLinks"]!.GetValue<int>());
        Assert.Equal(0, chunk.Stats["offMeshConnections"]!.GetValue<int>());

        // One line per pair of internal links (24 shared edges), three lines per external link (portal, out, in).
        Assert.Equal(24, chunk.LineAttributes.Count(a => a == 1));
        Assert.Equal("internal", ColorKey(chunk, 1));
        uint[] externals = [.. chunk.Attributes.Where(a => a.Value["colorKey"]!.GetValue<string>() == "external").Select(a => a.Key)];
        Assert.Equal(4, externals.Length);
        Assert.All(externals, id =>
        {
            Assert.Equal(3, chunk.LineAttributes.Count(a => a == id));
            Assert.Equal([32, 31], chunk.Attributes[id]["toTile"]!.AsArray().Select(n => n!.GetValue<int>()));
        });
        Assert.Equal(24 + 12, chunk.LineCount);
    }

    [Fact]
    public void Build_Links_BeforeTheNeighbourIsRead_StillFindsItsExternalLinks()
    {
        Add(32, 32);
        Add(32, 31);
        ReferenceNavLayerSource source = Source();

        // Building the second tile first must not leave the first without its links: each build loads its 3 x 3 neighbourhood.
        GeometryChunk second = Single(source.Build(LayerIds.RefNavLinks, Key(32, 31), CancellationToken.None));
        GeometryChunk first = Single(source.Build(LayerIds.RefNavLinks, Key(32, 32), CancellationToken.None));

        Assert.Equal(4, second.Stats["externalLinks"]!.GetValue<int>());
        Assert.Equal(4, first.Stats["externalLinks"]!.GetValue<int>());
    }

    [Fact]
    public void Build_Links_DrawsAnOffMeshConnection_WithItsLandingLinks()
    {
        Vector3 a = SyntheticNav.CellCenterRecast(32, 32, 3, 0, 1, 10f);
        Vector3 b = SyntheticNav.CellCenterRecast(32, 32, 3, 2, 1, 10f);
        Add(32, 32, cells: 3, present: (i, _) => i != 1, offMesh: [new OffMeshSpec(a, b, 3f, Bidirectional: true, UserId: 4242)]);
        ReferenceNavLayerSource source = Source();

        GeometryChunk chunk = Single(source.Build(LayerIds.RefNavLinks, Key(32, 32), CancellationToken.None));

        Assert.Equal(1, chunk.Stats["offMeshConnections"]!.GetValue<int>());
        Assert.Equal(4, chunk.Stats["offMeshLinks"]!.GetValue<int>());
        Assert.Equal(0, chunk.Stats["staleLinks"]!.GetValue<int>());
        KeyValuePair<uint, JsonObject> connection = Assert.Single(chunk.Attributes, x => x.Value["colorKey"]!.GetValue<string>() == "offmesh");
        JsonObject info = connection.Value;
        Assert.Equal(4242, info["userId"]!.GetValue<int>());
        Assert.True(info["bidirectional"]!.GetValue<bool>());
        Assert.True(info["startLinked"]!.GetValue<bool>());
        Assert.True(info["endLinked"]!.GetValue<bool>());
        Assert.Equal(3f, info["radius"]!.GetValue<float>());

        // The connection's own line, plus one line per link of the connection polygon or into it.
        Assert.Equal(1 + 4, chunk.LineAttributes.Count(x => x == connection.Key));
        Assert.Equal(2, chunk.PointAttributes.Count(x => x == connection.Key));
        WorldPoint from = ToPoint(info["from"]!);
        WorldPoint to = ToPoint(info["to"]!);
        Vector3 expectedFrom = NavMeshFormat.ToWorld(a);
        Assert.Equal(expectedFrom.X, from.X, 2);
        Assert.Equal(expectedFrom.Y, from.Y, 2);
        Vector3 expectedTo = NavMeshFormat.ToWorld(b);
        Assert.Equal(expectedTo.X, to.X, 2);
        Assert.Equal(expectedTo.Y, to.Y, 2);
    }

    [Fact]
    public void Build_Links_DrawsAnOffMeshConnection_ThatLeavesTheTile()
    {
        Vector3 a = SyntheticNav.CellCenterRecast(32, 32, 3, 1, 1, 10f);
        Vector3 b = SyntheticNav.CellCenterRecast(32, 31, 3, 1, 1, 10f);
        Add(32, 32, cells: 3, offMesh: [new OffMeshSpec(a, b, 3f, Bidirectional: true)]);
        Add(32, 31, cells: 3);
        ReferenceNavLayerSource source = Source();

        GeometryChunk owner = Single(source.Build(LayerIds.RefNavLinks, Key(32, 32), CancellationToken.None));
        GeometryChunk landing = Single(source.Build(LayerIds.RefNavLinks, Key(32, 31), CancellationToken.None));

        // The owner links the connection to its start polygon (both ways) and to the landing polygon; the landing tile holds the way back.
        Assert.Equal(3, owner.Stats["offMeshLinks"]!.GetValue<int>());
        Assert.Equal(1, landing.Stats["offMeshLinks"]!.GetValue<int>());
        Assert.Equal(0, landing.Stats["offMeshConnections"]!.GetValue<int>());
        JsonObject info = Assert.Single(owner.Attributes, x => x.Value["colorKey"]!.GetValue<string>() == "offmesh").Value;
        Assert.True(info["startLinked"]!.GetValue<bool>());
        Assert.True(info["endLinked"]!.GetValue<bool>());
        KeyValuePair<uint, JsonObject> seen = Assert.Single(landing.Attributes, x => x.Value["colorKey"]!.GetValue<string>() == "offmesh");
        Assert.Null(seen.Value["startLinked"]);
        Assert.Equal(1, landing.LineAttributes.Count(x => x == seen.Key));
    }

    [Fact]
    public void Build_Links_NamesAConnectionThatNeverLinked()
    {
        // The start is 40 yards from any polygon, beyond the connection's radius.
        Vector3 a = SyntheticNav.CellCenterRecast(32, 32, 3, 0, 1, 10f);
        Vector3 b = SyntheticNav.CellCenterRecast(32, 32, 3, 2, 1, 10f);
        Add(32, 32, cells: 3, present: (i, _) => i == 2, offMesh: [new OffMeshSpec(a, b, 3f, Bidirectional: false)]);
        ReferenceNavLayerSource source = Source();

        GeometryChunk chunk = Single(source.Build(LayerIds.RefNavLinks, Key(32, 32), CancellationToken.None));

        JsonObject info = Assert.Single(chunk.Attributes, x => x.Value["colorKey"]!.GetValue<string>() == "offmesh").Value;
        Assert.False(info["startLinked"]!.GetValue<bool>());
        Assert.False(info["bidirectional"]!.GetValue<bool>());
        Assert.Equal(0, chunk.Stats["offMeshLinks"]!.GetValue<int>());
    }

    [Fact]
    public void Build_IsIndependentOfTheOrderTilesWereLoadedIn()
    {
        // A 3 x 3 block with connections across its borders; the second service loads the block back to front first.
        for (int x = 31; x <= 33; x++)
        {
            for (int y = 31; y <= 33; y++)
            {
                IReadOnlyList<OffMeshSpec> cons = x == 32 && y == 32
                    ? [
                        new OffMeshSpec(SyntheticNav.CellCenterRecast(32, 32, 3, 1, 1, 10f), SyntheticNav.CellCenterRecast(32, 31, 3, 1, 1, 10f), 3f, Bidirectional: true, UserId: 1),
                        new OffMeshSpec(SyntheticNav.CellCenterRecast(32, 32, 3, 0, 0, 10f), SyntheticNav.CellCenterRecast(33, 32, 3, 2, 2, 10f), 3f, Bidirectional: false, UserId: 2),
                    ]
                    : [];
                Add(x, y, cells: 3, offMesh: cons);
            }
        }

        ReferenceNavLayerSource forward = Source();
        ReferenceNavMeshService backwardService = NewService();
        var order = new List<TileKey>();
        for (int x = 33; x >= 31; x--)
        {
            for (int y = 33; y >= 31; y--)
            {
                order.Add(Key(x, y));
            }
        }

        // One tile at a time, back to front: the link chains DotRecast leaves behind differ from the forward service's 3 x 3 loads.
        ReferenceNavMap backwardMap = backwardService.OpenMap(0).Map!;
        foreach (TileKey tile in order)
        {
            backwardMap.Read([tile], _ => 0);
        }

        var backward = new ReferenceNavLayerSource(backwardService);

        foreach (string layer in new[] { LayerIds.RefNavPoly, LayerIds.RefNavLinks })
        {
            foreach (TileKey tile in order)
            {
                byte[][] a = [.. forward.Build(layer, tile, CancellationToken.None).Chunks.Select(GeometryWire.Write)];
                byte[][] b = [.. backward.Build(layer, tile, CancellationToken.None).Chunks.Select(GeometryWire.Write)];
                Assert.Equal(a.Length, b.Length);
                for (int i = 0; i < a.Length; i++)
                {
                    Assert.True(a[i].AsSpan().SequenceEqual(b[i]), $"{layer} {tile} part {i} differs between load orders");
                }
            }
        }
    }

    [Fact]
    public void Build_ChunksRoundTripThroughTheWireFormat()
    {
        Add(32, 32);
        Add(32, 31);
        ReferenceNavLayerSource source = Source();

        foreach (string layer in new[] { LayerIds.RefNavPoly, LayerIds.RefNavLinks })
        {
            GeometryChunk chunk = Single(source.Build(layer, Key(32, 32), CancellationToken.None));
            GeometryChunk back = GeometryWire.Read(GeometryWire.Write(chunk));
            Assert.Equal(chunk.Positions, back.Positions);
            Assert.Equal(chunk.Triangles, back.Triangles);
            Assert.Equal(chunk.Lines, back.Lines);
            Assert.Equal(chunk.LineAttributes, back.LineAttributes);
            Assert.Equal(chunk.Attributes.Keys.Order(), back.Attributes.Keys.Order());
            Assert.Equal(chunk.Stats.ToJsonString(), back.Stats.ToJsonString());
        }
    }

    [Fact]
    public void Build_SplitsALargeTileIntoParts_WithoutLosingAnything()
    {
        Add(32, 32, cells: 12);
        Add(32, 31, cells: 12);
        ReferenceNavLayerSource whole = Source();
        ReferenceNavLayerSource split = Source(maxChunkBytes: 4096);

        foreach (string layer in new[] { LayerIds.RefNavPoly, LayerIds.RefNavLinks })
        {
            LayerBuild one = whole.Build(layer, Key(32, 32), CancellationToken.None);
            LayerBuild many = split.Build(layer, Key(32, 32), CancellationToken.None);

            Assert.Single(one.Chunks);
            Assert.True(many.Chunks.Count > 1, $"{layer} should split at 4096 bytes");
            Assert.Equal(Enumerable.Range(0, many.Chunks.Count), many.Chunks.Select(c => c.Part));
            Assert.All(many.Chunks, c => c.Validate());
            Assert.Equal(one.Chunks[0].TriangleCount, many.Chunks.Sum(c => c.TriangleCount));
            Assert.Equal(one.Status.Primitives > 0, many.Status.Primitives > 0);
            Assert.Equal(TileState.Ok, many.Status.State);
            foreach (GeometryChunk chunk in many.Chunks)
            {
                GeometryChunk back = GeometryWire.Read(GeometryWire.Write(chunk));
                Assert.Equal(chunk.Part, back.Part);
                Assert.Equal(chunk.Triangles, back.Triangles);
            }
        }

        // Every polygon is drawn exactly once across the parts.
        LayerBuild polys = split.Build(LayerIds.RefNavPoly, Key(32, 32), CancellationToken.None);
        Assert.Equal(144, polys.Chunks.Sum(c => c.Stats["polys"]!.GetValue<int>()));
        uint[] ids = [.. polys.Chunks.SelectMany(c => c.Attributes.Keys)];
        Assert.Equal(144, ids.Distinct().Count() is var distinct && distinct == ids.Length ? distinct : -1);

        // Internal links are counted tile-wide in every part; the lines add up to the single chunk's.
        LayerBuild links = split.Build(LayerIds.RefNavLinks, Key(32, 32), CancellationToken.None);
        LayerBuild linksWhole = whole.Build(LayerIds.RefNavLinks, Key(32, 32), CancellationToken.None);
        Assert.Equal(linksWhole.Chunks[0].LineCount, links.Chunks.Sum(c => c.LineCount));
        Assert.Equal(linksWhole.Chunks[0].PointCount, links.Chunks.Sum(c => c.PointCount));
        Assert.All(links.Chunks, c => Assert.Equal(linksWhole.Chunks[0].Stats["internalLinks"]!.GetValue<int>(), c.Stats["internalLinks"]!.GetValue<int>()));
    }

    [Fact]
    public void Build_ReportsMissingRejectedAndGlobalTiles_WithoutBuilding()
    {
        Add(32, 32);
        _data.WriteRaw(31, 32, [1, 2, 3]);
        ReferenceNavLayerSource source = Source();

        LayerBuild missing = source.Build(LayerIds.RefNavPoly, Key(5, 5), CancellationToken.None);
        LayerBuild rejected = source.Build(LayerIds.RefNavPoly, Key(31, 32), CancellationToken.None);
        LayerBuild global = source.Build(LayerIds.RefNavLinks, TileKey.Global(0), CancellationToken.None);

        Assert.Equal(TileState.Missing, missing.Status.State);
        Assert.Empty(missing.Chunks);
        Assert.Equal(TileState.Rejected, rejected.Status.State);
        Assert.Contains("shorter than", rejected.Status.Detail, StringComparison.Ordinal);
        Assert.Equal(TileState.NotApplicable, global.Status.State);
        Assert.Throws<ArgumentException>(() => source.Build(LayerIds.Terrain, Key(32, 32), CancellationToken.None));
        Assert.Throws<ArgumentException>(() => source.Build(LayerIds.RefNavPoly, new TileKey(0, 70, 3), CancellationToken.None));
    }

    [Fact]
    public void Build_OfAMapWithoutNavmesh_IsMissing_AndOfABrokenOneRejected()
    {
        ReferenceNavLayerSource source = Source();
        Assert.Equal(TileState.Missing, source.Build(LayerIds.RefNavPoly, new TileKey(5, 32, 32), CancellationToken.None).Status.State);

        File.WriteAllBytes(Path.Combine(_data.Mmaps, "007.mmap"), new byte[3]);
        LayerBuild broken = source.Build(LayerIds.RefNavPoly, new TileKey(7, 32, 32), CancellationToken.None);

        Assert.Equal(TileState.Rejected, broken.Status.State);
        Assert.Contains("28 bytes", broken.Status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_Links_MentionsARefusedNeighbour()
    {
        Add(32, 32);
        _data.WriteRaw(32, 31, new byte[200]);
        ReferenceNavLayerSource source = Source();

        LayerBuild build = source.Build(LayerIds.RefNavLinks, Key(32, 32), CancellationToken.None);

        Assert.Equal(TileState.Ok, build.Status.State);
        Assert.Contains("32,31", build.Status.Detail, StringComparison.Ordinal);
        Assert.Contains("refused", build.Status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void PeekStatus_KnowsWhatItCanWithoutReading()
    {
        Add(32, 32);
        _data.WriteRaw(31, 32, [1, 2, 3]);
        ReferenceNavLayerSource source = Source();

        Assert.Equal(TileState.Unknown, source.PeekStatus(LayerIds.RefNavPoly, Key(32, 32)).State);
        Assert.Equal(TileState.Missing, source.PeekStatus(LayerIds.RefNavPoly, Key(5, 5)).State);
        Assert.Equal(TileState.NotApplicable, source.PeekStatus(LayerIds.RefNavPoly, TileKey.Global(0)).State);
        Assert.Equal(TileState.Missing, source.PeekStatus(LayerIds.RefNavPoly, new TileKey(9, 32, 32)).State);

        source.Build(LayerIds.RefNavPoly, Key(31, 32), CancellationToken.None);
        TileStatus refused = source.PeekStatus(LayerIds.RefNavLinks, Key(31, 32));
        Assert.Equal(TileState.Rejected, refused.State);
        Assert.Contains("shorter than", refused.Detail, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => source.PeekStatus(LayerIds.RefNavPoly, new TileKey(0, 99, 99)));
        Assert.Throws<ArgumentException>(() => source.PeekStatus(LayerIds.Terrain, Key(32, 32)));
    }

    [Fact]
    public void Constructor_ChecksItsArguments()
    {
        using var service = new ReferenceNavMeshService(_data.Mmaps, NullLogger.Instance);

        Assert.Throws<ArgumentNullException>(() => new ReferenceNavLayerSource(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReferenceNavLayerSource(service, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReferenceNavLayerSource(service, GeometryWire.MaxChunkBytes));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReferenceNavMeshService(_data.Mmaps, NullLogger.Instance, 4));
    }

    [Fact]
    public void Build_StopsWhenCancelled()
    {
        Add(32, 32, cells: 6);
        ReferenceNavLayerSource source = Source();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => source.Build(LayerIds.RefNavPoly, Key(32, 32), cts.Token));
        Assert.Throws<OperationCanceledException>(() => source.Build(LayerIds.RefNavLinks, Key(32, 32), cts.Token));
    }

    [Fact]
    public void Module_RegistersTheLayersCommandsAndService_AndServesATile()
    {
        Add(32, 32);
        IReadOnlyList<IAtlasModule> found = AtlasModules.Discover(typeof(AtlasModules).Assembly);
        Assert.Contains(found, m => m is ReferenceNavmeshModule);

        using AtlasContext context = AtlasModules.Compose(new AtlasPaths(_data.Root, null), NullLoggerFactory.Instance, headless: true, [new ReferenceNavmeshModule()]);

        Assert.Empty(context.Problems);
        Assert.Contains(LayerIds.RefNavPoly, context.Layers.Available);
        Assert.Contains(LayerIds.RefNavLinks, context.Layers.Available);
        Assert.NotNull(context.Services.Get<ReferenceNavMeshService>());
        foreach (string name in new[] { "ref.query.path", "ref.query.nearest", "ref.nav.tile" })
        {
            Assert.True(context.Commands.TryGet(name, out _), name);
        }

        LayerBuild build = context.Cache.Get(LayerIds.RefNavPoly, Key(32, 32), CancellationToken.None);
        Assert.Equal(TileState.Ok, build.Status.State);
        Assert.Equal(32, Assert.Single(build.Chunks).TriangleCount);
        Assert.Single(context.Cache.GetWire(LayerIds.RefNavLinks, Key(32, 32), CancellationToken.None));
    }

    private static WorldPoint ToPoint(JsonNode node)
    {
        JsonArray a = node.AsArray();
        return new WorldPoint(a[0]!.GetValue<double>(), a[1]!.GetValue<double>(), a[2]!.GetValue<double>());
    }
}
