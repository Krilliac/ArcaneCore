using System.Globalization;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Render;
using ArcaneCore.Game.Maps.Terrain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Render;

/// <summary>A fact that needs the real 1.12.1 terrain extraction; skipped, visibly, without one.</summary>
public sealed class RenderRealTerrainFactAttribute : FactAttribute
{
    public const string Variable = "ARCANECORE_TEST_TERRAIN_DIR";

    public RenderRealTerrainFactAttribute()
    {
        if (RealTerrainData.SkipReason() is { } reason)
        {
            Skip = reason;
        }
    }
}

internal static class RealTerrainData
{
    /// <summary>The Goldshire tile, which holds the Lion's Pride Inn (the view default and the repository's real-terrain anchor).</summary>
    public static readonly TileKey Goldshire = new(0, 49, 31);

    public static string MapsDirectory => Path.Combine(Environment.GetEnvironmentVariable(RenderRealTerrainFactAttribute.Variable)!, "maps");

    public static string? SkipReason()
    {
        string? dir = Environment.GetEnvironmentVariable(RenderRealTerrainFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(Path.Combine(dir, "maps")))
        {
            return $"{RenderRealTerrainFactAttribute.Variable} is not set to a terrain data root (maps/ from the 1.12.1 client).";
        }

        string goldshire = Path.Combine(dir, "maps", WorldGrid.MapFileName(Goldshire));
        return File.Exists(goldshire) ? null : $"{goldshire} does not exist.";
    }

    public static TerrainTile Load(TileKey tile)
        => TerrainTile.Parse(File.ReadAllBytes(Path.Combine(MapsDirectory, WorldGrid.MapFileName(tile))));
}

/// <summary>
/// A test-only terrain layer over the real <c>.map</c> files: per height square the four corner samples and the centre
/// sample as the server interpolates them (<see cref="TerrainTile.GetHeight"/>), as four triangles around the centre.
/// Corner heights are read a few float steps inside the square so a hole or edge on the far side of a corner cannot
/// change the answer. Production layers belong to the terrain lane; this stands in for them so the renderer is exercised
/// against real data without depending on them.
/// </summary>
internal sealed class RealTerrainSource : ILayerSource
{
    /// <summary>How far inside the square corner heights are sampled, in yards (several float steps at |x| near 9500).</summary>
    private const double Nudge = 0.004;

    public IReadOnlyList<string> Layers { get; } = [LayerIds.Terrain];

    public IReadOnlyCollection<TileKey> AvailableTiles(string layerId, uint mapId)
    {
        var tiles = new List<TileKey>();
        foreach (string file in Directory.EnumerateFiles(RealTerrainData.MapsDirectory, string.Create(CultureInfo.InvariantCulture, $"{mapId:D3}*.map")))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (name.Length == 7
                && int.TryParse(name.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int x)
                && int.TryParse(name.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int y))
            {
                tiles.Add(new TileKey(mapId, x, y));
            }
        }

        return tiles;
    }

    public TileStatus PeekStatus(string layerId, TileKey tile)
        => new(layerId, tile, File.Exists(Path.Combine(RealTerrainData.MapsDirectory, WorldGrid.MapFileName(tile))) ? TileState.Unknown : TileState.Missing);

    public LayerBuild Build(string layerId, TileKey tile, CancellationToken cancellationToken)
    {
        string path = Path.Combine(RealTerrainData.MapsDirectory, WorldGrid.MapFileName(tile));
        if (!File.Exists(path))
        {
            return LayerBuild.Missing(layerId, tile, $"{WorldGrid.MapFileName(tile)} does not exist");
        }

        TerrainTile terrain;
        try
        {
            terrain = TerrainTile.Parse(File.ReadAllBytes(path));
        }
        catch (InvalidDataException e)
        {
            return LayerBuild.Rejected(layerId, tile, e.Message);
        }

        var builder = new GeometryBuilder(layerId, tile, WorldGrid.Origin(tile));
        const double square = WorldGrid.SquareSize;
        WorldPoint origin = builder.Origin;
        for (int row = 0; row < WorldGrid.MapResolution; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int col = 0; col < WorldGrid.MapResolution; col++)
            {
                double x0 = origin.X - (row * square);
                double x1 = origin.X - ((row + 1) * square);
                double y0 = origin.Y - (col * square);
                double y1 = origin.Y - ((col + 1) * square);
                double cx = (x0 + x1) / 2;
                double cy = (y0 + y1) / 2;
                float centre = terrain.GetHeight((float)cx, (float)cy);
                float a = terrain.GetHeight((float)(x0 - Nudge), (float)(y0 - Nudge));
                float b = terrain.GetHeight((float)(x1 + Nudge), (float)(y0 - Nudge));
                float c = terrain.GetHeight((float)(x1 + Nudge), (float)(y1 + Nudge));
                float d = terrain.GetHeight((float)(x0 - Nudge), (float)(y1 + Nudge));
                if (Math.Min(Math.Min(centre, a), Math.Min(Math.Min(b, c), d)) <= TerrainTile.InvalidHeight)
                {
                    continue;
                }

                uint vm = builder.AddVertex(cx, cy, centre);
                uint va = builder.AddVertex(x0, y0, a);
                uint vb = builder.AddVertex(x1, y0, b);
                uint vc = builder.AddVertex(x1, y1, c);
                uint vd = builder.AddVertex(x0, y1, d);
                builder.AddTriangle(vm, va, vb, edges: false);
                builder.AddTriangle(vm, vb, vc, edges: false);
                builder.AddTriangle(vm, vc, vd, edges: false);
                builder.AddTriangle(vm, vd, va, edges: false);
            }
        }

        return builder.IsEmpty ? LayerBuild.Empty(layerId, tile, "no height squares") : LayerBuild.Of(layerId, tile, [builder.Build()]);
    }
}

public sealed class RenderRealTerrainTests
{
    private static readonly WorldPoint Inn = new(-9462.66, 16.1915, 57.0459);

    private static CpuRenderer Renderer()
    {
        var registry = new LayerRegistry();
        registry.Add(new RealTerrainSource());
        return new CpuRenderer(registry, new LayerCache(registry));
    }

    private static ViewState TerrainView(CameraState camera, int radius, bool hud = false)
        => Views.Of(camera, (LayerIds.Terrain, Views.Style(mode: RenderMode.Fill))) with { LoadRadiusTiles = radius, ShowHud = hud };

    [RenderRealTerrainFact]
    public void TopDown_FillDepth_FollowsTheServersHeightFunction_AtEveryPixel()
    {
        // 24 yards high, 4:3: 32 x 24 yards around the inn, all inside the Goldshire tile (x -9474.7..-9450.7, y 0.19..32.2).
        CameraState camera = Views.TopCamera with { Target = Inn, Distance = 12 };
        TerrainTile terrain = RealTerrainData.Load(RealTerrainData.Goldshire);
        CpuRenderer renderer = Renderer();
        const int width = 320;
        const int height = 240;

        RenderedFrame frame = renderer.Rasterize(TerrainView(camera, radius: 0), width, height, CancellationToken.None);

        int compared = 0;
        int holes = 0;
        double worst = 0;
        for (int py = 1; py < height; py += 5)
        {
            for (int px = 1; px < width; px += 5)
            {
                WorldPoint ground = Pixels.TopDownWorld(camera, width, height, px, py, 0);
                float expected = terrain.GetHeight((float)ground.X, (float)ground.Y);
                float depth = frame.Buffer.FillDepth[(py * width) + px];
                if (expected <= TerrainTile.InvalidHeight)
                {
                    holes++;
                    Assert.True(float.IsPositiveInfinity(depth), $"pixel ({px},{py}) is over a hole but was drawn at depth {depth}");
                    continue;
                }

                double expectedDepth = camera.Target.Z + CameraMath.TopEyeHeight - expected;
                worst = Math.Max(worst, Math.Abs(depth - expectedDepth));
                Assert.True(Math.Abs(depth - expectedDepth) <= 0.05, $"pixel ({px},{py}) at ({ground.X:F3},{ground.Y:F3}): depth {depth} but the server's height {expected} gives {expectedDepth}");
                compared++;
            }
        }

        Assert.True(compared > 1000, $"only {compared} of 3072 samples were over terrain ({holes} over holes)");
        Assert.True(worst <= 0.05, $"worst depth error {worst}");
        Assert.Equal(1, frame.Stats["tiles"]!["ok"]!.GetValue<int>());
        long triangles = Json.Int(frame.Stats["triangles"]);
        Assert.True(triangles > 0 && triangles % 4 == 0, $"{triangles} triangles: four per drawn height square");
    }

    [RenderRealTerrainFact]
    public void TopDown_ProjectedWorldPoints_LandOnTheSurfaceDrawnThere()
    {
        // The other direction of the depth test: start from world points, let CameraMath.Project say where they land, and
        // check the surface drawn at that pixel is the server's surface at that point (a mirrored or rotated image would
        // fail almost everywhere, because the ground is not symmetric).
        CameraState camera = Views.TopCamera with { Target = Inn, Distance = 12 };
        TerrainTile terrain = RealTerrainData.Load(RealTerrainData.Goldshire);
        CpuRenderer renderer = Renderer();
        const int width = 320;
        const int height = 240;
        RenderedFrame frame = renderer.Rasterize(TerrainView(camera, radius: 0), width, height, CancellationToken.None);

        int compared = 0;
        for (double x = Inn.X - 11; x <= Inn.X + 11; x += 1.7)
        {
            for (double y = Inn.Y - 15; y <= Inn.Y + 15; y += 1.9)
            {
                float z = terrain.GetHeight((float)x, (float)y);
                if (z <= TerrainTile.InvalidHeight)
                {
                    continue;
                }

                (int px, int py) = Pixels.Of(camera, new WorldPoint(x, y, z), width, height);
                float depth = frame.Buffer.FillDepth[(py * width) + px];
                double expected = camera.Target.Z + CameraMath.TopEyeHeight - z;
                Assert.True(Math.Abs(depth - expected) <= 0.3, $"({x:F2},{y:F2}) lands on pixel ({px},{py}) where the drawn depth is {depth}, not {expected}");
                compared++;
            }
        }

        Assert.True(compared > 60, $"only {compared} world points were over terrain");
    }

    [RenderRealTerrainFact]
    public void Perspective_ShowsTheTerrainShaded_AtTheProjectedGroundPoint()
    {
        CameraState camera = new(Inn, 70, 30, 40, 55, CameraMode.Orbit);
        TerrainTile terrain = RealTerrainData.Load(RealTerrainData.Goldshire);
        CpuRenderer renderer = Renderer();
        const int width = 320;
        const int height = 240;
        RenderedFrame frame = renderer.Rasterize(TerrainView(camera, radius: 1), width, height, CancellationToken.None);

        Rgb layer = Pixels.Parse("#B9A77A");
        WorldPoint eye = CameraMath.Eye(camera);
        WorldPoint forward = CameraMath.Direction(camera);
        int total = 0;
        for (int dx = -20; dx <= 20; dx += 5)
        {
            for (int dy = -10; dy <= 30; dy += 5)
            {
                double x = Inn.X + dx;
                double y = Inn.Y + dy;
                float z = terrain.GetHeight((float)x, (float)y);
                if (z <= TerrainTile.InvalidHeight)
                {
                    continue;
                }

                // Where CameraMath puts the ground point, the image must show terrain at the depth along the view axis.
                (int px, int py) = Pixels.Of(camera, new WorldPoint(x, y, z), width, height);
                Rgb colour = frame.Buffer.Get(px, py);
                double expectedDepth = ((x - eye.X) * forward.X) + ((y - eye.Y) * forward.Y) + ((z - eye.Z) * forward.Z);
                double depth = frame.Buffer.FillDepth[(py * width) + px];
                Assert.True(InShadingRange(colour, layer), $"({dx},{dy}) -> pixel ({px},{py}) is {colour}, not terrain");
                Assert.True(Math.Abs(depth - expectedDepth) <= 1.0, $"({dx},{dy}) -> pixel ({px},{py}): depth {depth} but the ground point is {expectedDepth} along the view axis");
                total++;
            }
        }

        Assert.True(total >= 20, $"only {total} ground points were usable");
        Assert.Equal(9, frame.Stats["tiles"]!["ok"]!.GetValue<int>());
        Assert.Equal(0, frame.Stats["tiles"]!["missing"]!.GetValue<int>());
        Assert.Equal(0, frame.Stats["tiles"]!["rejected"]!.GetValue<int>());
    }

    private static bool InShadingRange(Rgb colour, Rgb layer)
    {
        bool Within(byte value, byte full) => value >= (int)Math.Floor(full * Shading.Ambient) - 1 && value <= full + 1;
        return Within(colour.R, layer.R) && Within(colour.G, layer.G) && Within(colour.B, layer.B) && colour != Rgb.Background;
    }

    [RenderRealTerrainFact]
    public void TileStats_AgreeWithTheFilesOnDisk_ForATileAtTheEdgeOfTheMap()
    {
        CpuRenderer renderer = Renderer();
        var source = new RealTerrainSource();
        HashSet<TileKey> present = [.. source.AvailableTiles(LayerIds.Terrain, 0)];

        // The first tile (in grid order) that exists but has a missing neighbour, so "missing" is really exercised.
        TileKey edge = present.OrderBy(t => t.X).ThenBy(t => t.Y)
            .First(t => WorldGrid.Around(0, WorldGrid.Centre(t).X, WorldGrid.Centre(t).Y, 1).Any(n => !present.Contains(n)));
        (double cx, double cy) = WorldGrid.Centre(edge);
        TileKey[] around = [.. WorldGrid.Around(0, cx, cy, 1)];
        int expectedOk = around.Count(present.Contains);
        int expectedMissing = around.Length - expectedOk;
        CameraState camera = Views.TopCamera with { Target = new WorldPoint(cx, cy, 0), Distance = 300 };

        RenderedFrame frame = renderer.Rasterize(TerrainView(camera, radius: 1, hud: true), 320, 240, CancellationToken.None);

        JsonObject tiles = frame.Stats["tiles"]!.AsObject();
        Assert.True(expectedMissing > 0, "the chosen tile has no missing neighbour");
        Assert.Equal(around.Length, tiles["requested"]!.GetValue<int>());
        Assert.Equal(expectedOk, tiles["ok"]!.GetValue<int>());
        Assert.Equal(expectedMissing, tiles["missing"]!.GetValue<int>());
        Assert.Equal(0, tiles["rejected"]!.GetValue<int>());
        JsonArray problems = frame.Stats["problems"]!.AsArray();
        Assert.Equal(expectedMissing, problems.Count);
        Assert.All(problems, p => Assert.Equal("Missing", p!["state"]!.GetValue<string>()));
        Assert.Equal(
            around.Where(t => !present.Contains(t)).Select(t => $"{t.X}/{t.Y}").Order(StringComparer.Ordinal),
            problems.Select(p => $"{p!["tile"]![1]!.GetValue<int>()}/{p["tile"]![2]!.GetValue<int>()}").Order(StringComparer.Ordinal));
        int footerTop = 240 - Hud.FooterHeight(Hud.ScaleFor(240));
        int amber = 0;
        for (int y = footerTop; y < 240; y++)
        {
            for (int x = 0; x < 320; x++)
            {
                amber += frame.Buffer.Get(x, y) == Hud.Amber ? 1 : 0;
            }
        }

        Assert.True(amber > 10, "the HUD footer flags the missing tiles in amber");
    }

    [RenderRealTerrainFact]
    public async Task ExportObj_OfTheRealTile_ParsesBackToTheChunksWorldVertices()
    {
        using AtlasContext context = AtlasModules.Compose(
            new AtlasPaths(null, null),
            NullLoggerFactory.Instance,
            headless: true,
            [new RealLayersModule(), new RenderModule()]);
        using var dir = new TempDirectory();
        string path = dir.File("goldshire.obj");

        CommandResult result = await context.Commands.InvokeAsync(
            "export.obj",
            new JsonObject
            {
                ["map"] = 0,
                ["x"] = Inn.X,
                ["y"] = Inn.Y,
                ["radius"] = 0,
                ["layers"] = new JsonArray(LayerIds.Terrain),
                ["out"] = path,
            },
            context.Services,
            CommandOrigin.Cli,
            headless: true,
            CancellationToken.None);

        Assert.True(result.Ok, $"{result.ErrorCode}: {result.ErrorMessage}");
        GeometryChunk chunk = Assert.Single(context.Cache.Get(LayerIds.Terrain, RealTerrainData.Goldshire, CancellationToken.None).Chunks);
        ParsedObj obj = ParsedObj.Parse(File.ReadAllText(path));
        ObjObject only = Assert.Single(obj.Objects);
        TerrainTile terrain = RealTerrainData.Load(RealTerrainData.Goldshire);
        Assert.Equal("terrain_0_49_31", only.Name);
        Assert.Equal(chunk.VertexCount, obj.Vertices.Count);
        Assert.Equal(chunk.TriangleCount, only.Groups.Sum(g => g.Faces.Count));
        Assert.Equal(0, chunk.VertexCount % 5);
        for (int i = 0; i < chunk.VertexCount; i++)
        {
            WorldPoint expected = chunk.Vertex((uint)i);
            Assert.Equal((expected.X, expected.Y, expected.Z), obj.Vertices[i]);
        }

        Assert.All(only.Groups.SelectMany(g => g.Faces).SelectMany(f => f), index => Assert.InRange(index, 1, chunk.VertexCount));

        // Every centre vertex sits on the server's surface; every corner within the sampling nudge of it.
        int checkedCentres = 0;
        for (int i = 0; i < chunk.VertexCount; i += 5 * 97)
        {
            (double x, double y, double z) = obj.Vertices[i];
            float expected = terrain.GetHeight((float)x, (float)y);
            Assert.True(expected > TerrainTile.InvalidHeight, $"vertex {i} at ({x:F3},{y:F3}) is not over terrain");
            Assert.True(Math.Abs(z - expected) <= 0.001, $"centre vertex {i}: z {z} but the server's height is {expected}");
            checkedCentres++;
        }

        Assert.True(checkedCentres > 20);
        (double minX, double maxX, double minY, double maxY) = WorldGrid.Bounds(RealTerrainData.Goldshire);
        Assert.All(obj.Vertices, v =>
        {
            Assert.InRange(v.X, minX - 0.01, maxX + 0.01);
            Assert.InRange(v.Y, minY - 0.01, maxY + 0.01);
        });
    }

    private sealed class RealLayersModule : IAtlasModule
    {
        public string Name => "real-terrain";

        public int Order => ModuleOrder.Foundation;

        public void Register(AtlasContext context) => context.Layers.Add(new RealTerrainSource());
    }
}
