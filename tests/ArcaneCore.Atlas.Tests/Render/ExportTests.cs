using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Render;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Render;

/// <summary>A scratch directory outside every repository, deleted on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "atlas-h-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        string? tree = OutputPaths.FindWorkingTree(Path.Combine(Root, "probe.png"));
        Assert.True(tree is null, $"the temp directory {Root} is inside the repository working tree {tree}; these tests need one outside");
    }

    public string Root { get; }

    public string File(string name) => Path.Combine(Root, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a scan holding a file open must not fail the test that already passed.
        }
    }
}

internal sealed class ParsedObj
{
    public string? MaterialLibrary { get; set; }

    public List<(double X, double Y, double Z)> Vertices { get; } = [];

    public List<ObjObject> Objects { get; } = [];

    public List<string> Comments { get; } = [];

    public static ParsedObj Parse(string text)
    {
        var obj = new ParsedObj();
        ObjObject? current = null;
        ObjGroup? group = null;
        foreach (string raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.TrimEnd('\r');
            string[] part = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (part[0])
            {
                case "#":
                    obj.Comments.Add(line);
                    break;
                case "mtllib":
                    obj.MaterialLibrary = line[7..];
                    break;
                case "o":
                    current = new ObjObject(part[1], obj.Vertices.Count);
                    obj.Objects.Add(current);
                    group = null;
                    break;
                case "v":
                    obj.Vertices.Add((Num(part[1]), Num(part[2]), Num(part[3])));
                    current!.VertexCount++;
                    break;
                case "g":
                    group = new ObjGroup(part[1]);
                    current!.Groups.Add(group);
                    break;
                case "usemtl":
                    group!.Material = part[1];
                    break;
                case "f":
                    group!.Faces.Add([.. part[1..].Select(Index)]);
                    break;
                case "l":
                    group!.Lines.Add([.. part[1..].Select(Index)]);
                    break;
                case "p":
                    group!.Points.Add(Index(part[1]));
                    break;
                default:
                    throw new FormatException($"Unexpected OBJ line '{line}'.");
            }
        }

        return obj;
    }

    private static double Num(string text) => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static int Index(string text) => int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
}

internal sealed class ObjObject(string name, int firstVertex)
{
    public string Name { get; } = name;

    /// <summary>Zero-based index of this object's first vertex in the file.</summary>
    public int FirstVertex { get; } = firstVertex;

    public int VertexCount { get; set; }

    public List<ObjGroup> Groups { get; } = [];
}

internal sealed class ObjGroup(string name)
{
    public string Name { get; } = name;

    public string? Material { get; set; }

    public List<int[]> Faces { get; } = [];

    public List<int[]> Lines { get; } = [];

    public List<int> Points { get; } = [];
}

public sealed class ExportTests
{
    private static readonly WorldPoint InnOrigin = new(-9462.66, 16.1915, 57.0459);

    /// <summary>Three triangles, two lines and a point at awkward double coordinates, in two colour keys.</summary>
    private static GeometryChunk Sample(string layer, TileKey tile, WorldPoint origin, int part = 0)
    {
        var builder = new GeometryBuilder(layer, tile, origin, part);
        uint a = builder.AddVertex(origin.X + 1.234567891, origin.Y + 2.345678912, origin.Z + 0.5);
        uint b = builder.AddVertex(origin.X + 4.5, origin.Y + 2.25, origin.Z + 0.75);
        uint c = builder.AddVertex(origin.X + 3.3333333, origin.Y + 6.6666667, origin.Z + 1.125);
        uint d = builder.AddVertex(origin.X + 0.1, origin.Y + 5.5, origin.Z + 0.25);
        builder.AddTriangle(a, b, c, 1, edges: false);
        builder.AddTriangle(a, c, d, 2, edges: false);
        builder.AddTriangle(b, c, d, 1, edges: false);
        builder.AddLine(a, d, 2);
        builder.AddPoint(c, 2);
        builder.DescribeAttribute(1, () => new JsonObject { ["colorKey"] = ColorKeys.Indoor, ["name"] = "inn floor" });
        builder.DescribeAttribute(2, () => new JsonObject { ["colorKey"] = ColorKeys.Outdoor });
        builder.Stats["note"] = "sample";
        return builder.Build();
    }

    private static (RenderRig Rig, TileKey Tile, GeometryChunk Chunk) Rig()
    {
        TileKey tile = WorldGrid.TileOf(0, InnOrigin.X, InnOrigin.Y)!.Value;
        GeometryChunk chunk = Sample(LayerIds.VMapWmo, tile, InnOrigin);
        return (new RenderRig(new FakeSource().Add(LayerIds.VMapWmo, tile, chunk)), tile, chunk);
    }

    private static ExportRequest Request(ViewState? view = null, params LayerInfo[] layers)
        => new(0, InnOrigin.X, InnOrigin.Y, 0, layers.Length > 0 ? layers : [LayerCatalog.Get(LayerIds.VMapWmo)], view ?? ViewState.Default);

    [Fact]
    public void Obj_ParsesBackToTheSameWorldVertices()
    {
        (RenderRig rig, _, GeometryChunk chunk) = Rig();
        using var dir = new TempDirectory();
        ExportRequest request = Request();

        ExportSummary summary = ObjExporter.Write(
            request,
            ExportSource.Tiles(request, rig.Registry, rig.Cache, CancellationToken.None),
            dir.File("inn.obj"),
            dir.File("inn.mtl"),
            CancellationToken.None);
        ParsedObj obj = ParsedObj.Parse(File.ReadAllText(dir.File("inn.obj")));

        Assert.True(summary.Written);
        Assert.Equal(4, obj.Vertices.Count);
        for (uint i = 0; i < 4; i++)
        {
            WorldPoint expected = chunk.Vertex(i);
            Assert.Equal(expected.X, obj.Vertices[(int)i].X);
            Assert.Equal(expected.Y, obj.Vertices[(int)i].Y);
            Assert.Equal(expected.Z, obj.Vertices[(int)i].Z);
        }

        // The vertices are the double-precision world coordinates, to within the float rounding of the chunk-relative positions.
        Assert.Equal(InnOrigin.X + 1.234567891, obj.Vertices[0].X, 4);
        Assert.Equal(InnOrigin.Y + 2.345678912, obj.Vertices[0].Y, 4);
        Assert.Equal("inn.mtl", obj.MaterialLibrary);
        Assert.Equal(1, summary.Objects);
        Assert.Equal(4, summary.Vertices);
        Assert.Equal(3, summary.Triangles);
        Assert.Equal(chunk.LineCount, (int)summary.Lines);
        Assert.Equal(1, summary.Points);
    }

    [Fact]
    public void Obj_HasOneObjectPerLayerAndTile_AndOneGroupPerColourKey()
    {
        (RenderRig rig, TileKey tile, GeometryChunk chunk) = Rig();
        using var dir = new TempDirectory();
        ExportRequest request = Request();

        ObjExporter.Write(request, ExportSource.Tiles(request, rig.Registry, rig.Cache, CancellationToken.None), dir.File("a.obj"), dir.File("a.mtl"), CancellationToken.None);
        ParsedObj obj = ParsedObj.Parse(File.ReadAllText(dir.File("a.obj")));

        ObjObject only = Assert.Single(obj.Objects);
        Assert.Equal($"vmap_wmo_0_{tile.X}_{tile.Y}", only.Name);
        Assert.Equal(new[] { "indoor", "outdoor" }, only.Groups.Select(g => g.Name));
        ObjGroup indoor = only.Groups[0];
        ObjGroup outdoor = only.Groups[1];
        Assert.Equal("vmap_wmo_indoor", indoor.Material);
        Assert.Equal("vmap_wmo_outdoor", outdoor.Material);

        // Triangles 0 and 2 carry attribute 1 (indoor); triangle 1 carries attribute 2 (outdoor). Indices are absolute and 1-based.
        Assert.Equal(2, indoor.Faces.Count);
        Assert.Equal(new[] { 1, 2, 3 }, indoor.Faces[0]);
        Assert.Equal(new[] { 2, 3, 4 }, indoor.Faces[1]);
        Assert.Equal(new[] { 1, 3, 4 }, Assert.Single(outdoor.Faces));
        Assert.Empty(indoor.Lines);
        Assert.Equal(new[] { 1, 4 }, Assert.Single(outdoor.Lines));
        Assert.Equal(3, Assert.Single(outdoor.Points));

        // Every face resolves to the world positions of the chunk's triangle.
        foreach (int[] face in indoor.Faces.Concat(outdoor.Faces))
        {
            Assert.All(face, index => Assert.InRange(index, 1, 4));
        }

        Assert.Equal(chunk.TriangleCount, indoor.Faces.Count + outdoor.Faces.Count);
    }

    [Fact]
    public void Obj_IndicesRunOnAcrossObjects_AndMissingTilesAreNoted()
    {
        TileKey first = new(0, 49, 31);
        TileKey second = new(0, 49, 32);
        TileKey rejected = new(0, 48, 31);
        GeometryChunk one = Sample(LayerIds.VMapWmo, first, InnOrigin);
        GeometryChunk two = Sample(LayerIds.VMapWmo, second, InnOrigin with { Y = InnOrigin.Y - 533.33333 });
        var rig = new RenderRig(new FakeSource()
            .Add(LayerIds.VMapWmo, first, one)
            .Add(LayerIds.VMapWmo, second, two)
            .Add(LayerIds.VMapWmo, rejected, LayerBuild.Rejected(LayerIds.VMapWmo, rejected, "bad BIH\nsecond line")));
        using var dir = new TempDirectory();
        var request = new ExportRequest(0, InnOrigin.X, InnOrigin.Y, 1, [LayerCatalog.Get(LayerIds.VMapWmo)], ViewState.Default);

        ExportSummary summary = ObjExporter.Write(
            request,
            ExportSource.Tiles(request, rig.Registry, rig.Cache, CancellationToken.None),
            dir.File("b.obj"),
            dir.File("b.mtl"),
            CancellationToken.None);
        ParsedObj obj = ParsedObj.Parse(File.ReadAllText(dir.File("b.obj")));

        Assert.Equal(2, obj.Objects.Count);
        Assert.Equal(4, obj.Objects[1].FirstVertex);
        Assert.All(obj.Objects[1].Groups.SelectMany(g => g.Faces).SelectMany(f => f), index => Assert.InRange(index, 5, 8));
        Assert.Equal(9, summary.Tiles.Requested);
        Assert.Equal(2, summary.Tiles.Count(TileState.Ok));
        Assert.Equal(1, summary.Tiles.Count(TileState.Rejected));
        Assert.Equal(6, summary.Tiles.Count(TileState.Missing));
        Assert.Contains(obj.Comments, c => c.Contains("0/48/31", StringComparison.Ordinal) && c.Contains("Rejected", StringComparison.Ordinal) && c.Contains("bad BIH second line", StringComparison.Ordinal));
        Assert.Equal(6, obj.Comments.Count(c => c.Contains("Missing", StringComparison.Ordinal)));
    }

    [Fact]
    public void Mtl_CarriesTheViewsLayerColours()
    {
        (RenderRig rig, _, _) = Rig();
        using var dir = new TempDirectory();
        LayerStyle keyed = ViewState.Default.Layers[LayerIds.VMapWmo] with { ColorByAttribute = true, Opacity = 0.75 };
        ViewState byAttribute = ViewState.Default with { Layers = ViewState.Default.Layers.SetItem(LayerIds.VMapWmo, keyed) };
        ViewState plain = ViewState.Default with { Layers = ViewState.Default.Layers.SetItem(LayerIds.VMapWmo, keyed with { ColorByAttribute = false, Color = "#336699" }) };

        foreach ((string name, ViewState view) in new[] { ("keyed", byAttribute), ("plain", plain) })
        {
            ExportRequest request = Request(view);
            ObjExporter.Write(request, ExportSource.Tiles(request, rig.Registry, rig.Cache, CancellationToken.None), dir.File(name + ".obj"), dir.File(name + ".mtl"), CancellationToken.None);
        }

        string[] keyedLines = File.ReadAllLines(dir.File("keyed.mtl"));
        string[] plainLines = File.ReadAllLines(dir.File("plain.mtl"));
        Assert.Equal(new[] { "newmtl vmap_wmo_indoor", "# #D9C9A3" }, keyedLines.Skip(1).Take(2).ToArray());
        Assert.Contains("Kd 0.850980 0.788235 0.639216", keyedLines);
        Assert.Contains("newmtl vmap_wmo_outdoor", keyedLines);
        Assert.Contains("Kd 0.909804 0.890196 0.850980", keyedLines);
        Assert.Contains("d 0.750000", keyedLines);
        Assert.Equal(2, plainLines.Count(l => l == "Kd 0.200000 0.400000 0.600000"));
    }

    [Fact]
    public void ObjectNames_AreSafeAndDistinguishPartsAndTheGlobalTile()
    {
        TileKey tile = new(1, 7, 8);
        Assert.Equal("ref_m2_collision_1_7_8", ObjExporter.ObjectName(Sample(LayerIds.RefM2Collision, tile, InnOrigin)));
        Assert.Equal("ref_m2_collision_1_7_8_p2", ObjExporter.ObjectName(Sample(LayerIds.RefM2Collision, tile, InnOrigin, part: 2)));
        Assert.Equal("vmap_wmo_1_global", ObjExporter.ObjectName(Sample(LayerIds.VMapWmo, TileKey.Global(1), InnOrigin)));
        Assert.Equal("a_b_c", ObjExporter.Sanitize("a b/c"));
    }

    [Fact]
    public void Obj_WithNothingToExport_WritesNothing()
    {
        var rig = new RenderRig(new FakeSource().Serves(LayerIds.VMapWmo));
        using var dir = new TempDirectory();
        ExportRequest request = Request();

        ExportSummary summary = ObjExporter.Write(
            request,
            ExportSource.Tiles(request, rig.Registry, rig.Cache, CancellationToken.None),
            dir.File("none.obj"),
            dir.File("none.mtl"),
            CancellationToken.None);

        Assert.False(summary.Written);
        Assert.Equal(1, summary.Tiles.Count(TileState.Missing));
        Assert.Empty(Directory.GetFileSystemEntries(dir.Root));
    }

    [Fact]
    public void Obj_Success_LeavesNoTemporaryFiles()
    {
        (RenderRig rig, _, _) = Rig();
        using var dir = new TempDirectory();
        ExportRequest request = Request();

        ObjExporter.Write(request, ExportSource.Tiles(request, rig.Registry, rig.Cache, CancellationToken.None), dir.File("c.obj"), dir.File("c.mtl"), CancellationToken.None);

        Assert.Equal(new[] { "c.mtl", "c.obj" }, Directory.GetFiles(dir.Root).Select(Path.GetFileName).Order().ToArray());
    }

    [Fact]
    public void Json_CarriesWorldPositions_IndicesAttributesAndTileStatus()
    {
        (RenderRig rig, TileKey tile, GeometryChunk chunk) = Rig();
        using var dir = new TempDirectory();
        var request = new ExportRequest(0, InnOrigin.X, InnOrigin.Y, 1, [LayerCatalog.Get(LayerIds.VMapWmo)], ViewState.Default);

        ExportSummary summary = JsonExporter.Write(
            request,
            ExportSource.Tiles(request, rig.Registry, rig.Cache, CancellationToken.None),
            dir.File("inn.json"),
            CancellationToken.None);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(dir.File("inn.json")));
        JsonElement root = document.RootElement;

        Assert.True(summary.Written);
        Assert.Equal(JsonExporter.Format, root.GetProperty("format").GetString());
        Assert.Equal(JsonExporter.Version, root.GetProperty("version").GetInt32());
        Assert.Equal(0u, root.GetProperty("map").GetUInt32());
        Assert.Equal(1, root.GetProperty("radius").GetInt32());
        JsonElement entry = root.GetProperty("chunks").EnumerateArray().Single();
        Assert.Equal(LayerIds.VMapWmo, entry.GetProperty("layer").GetString());
        Assert.Equal(new[] { 0, tile.X, tile.Y }, entry.GetProperty("tile").EnumerateArray().Select(e => e.GetInt32()).ToArray());
        Assert.Equal(0, entry.GetProperty("part").GetInt32());
        double[] positions = entry.GetProperty("positions").EnumerateArray().Select(e => e.GetDouble()).ToArray();
        Assert.Equal(12, positions.Length);
        for (uint i = 0; i < 4; i++)
        {
            WorldPoint expected = chunk.Vertex(i);
            Assert.Equal(new[] { expected.X, expected.Y, expected.Z }, positions[(3 * (int)i)..(3 * ((int)i + 1))]);
        }

        Assert.Equal(chunk.Triangles, entry.GetProperty("triangles").EnumerateArray().Select(e => e.GetUInt32()).ToArray());
        Assert.Equal(chunk.TriangleAttributes, entry.GetProperty("triangleAttributes").EnumerateArray().Select(e => e.GetUInt32()).ToArray());
        Assert.Equal(chunk.Lines, entry.GetProperty("lines").EnumerateArray().Select(e => e.GetUInt32()).ToArray());
        Assert.Equal(chunk.Points, entry.GetProperty("points").EnumerateArray().Select(e => e.GetUInt32()).ToArray());
        Assert.Equal("inn floor", entry.GetProperty("attributes").GetProperty("1").GetProperty("name").GetString());
        Assert.Equal(ColorKeys.Outdoor, entry.GetProperty("attributes").GetProperty("2").GetProperty("colorKey").GetString());
        Assert.Equal("sample", entry.GetProperty("stats").GetProperty("note").GetString());
        JsonElement[] tiles = root.GetProperty("tiles").EnumerateArray().ToArray();
        Assert.Equal(9, tiles.Length);
        Assert.Equal(1, tiles.Count(t => t.GetProperty("state").GetString() == "Ok"));
        Assert.Equal(8, tiles.Count(t => t.GetProperty("state").GetString() == "Missing"));
    }

    [Fact]
    public void Json_WithNothingToExport_WritesNothing()
    {
        var rig = new RenderRig(new FakeSource().Serves(LayerIds.VMapWmo));
        using var dir = new TempDirectory();
        ExportRequest request = Request();

        ExportSummary summary = JsonExporter.Write(request, ExportSource.Tiles(request, rig.Registry, rig.Cache, CancellationToken.None), dir.File("none.json"), CancellationToken.None);

        Assert.False(summary.Written);
        Assert.Empty(Directory.GetFileSystemEntries(dir.Root));
    }

    [Fact]
    public void ABuildThatThrows_IsReportedAsRejected_NotAsEmpty()
    {
        var source = new ThrowingSource();
        var registry = new LayerRegistry();
        registry.Add(source);
        var cache = new LayerCache(registry);
        ExportRequest request = Request();

        ExportTile tile = Assert.Single(ExportSource.Tiles(request, registry, cache, CancellationToken.None));

        Assert.Equal(TileState.Rejected, tile.Build.Status.State);
        Assert.Contains("boom", tile.Build.Status.Detail, StringComparison.Ordinal);
    }

    private sealed class ThrowingSource : ILayerSource
    {
        public IReadOnlyList<string> Layers { get; } = [LayerIds.VMapWmo];

        public IReadOnlyCollection<TileKey> AvailableTiles(string layerId, uint mapId) => [];

        public TileStatus PeekStatus(string layerId, TileKey tile) => new(layerId, tile, TileState.Unknown);

        public LayerBuild Build(string layerId, TileKey tile, CancellationToken cancellationToken) => throw new InvalidDataException("boom");
    }
}
