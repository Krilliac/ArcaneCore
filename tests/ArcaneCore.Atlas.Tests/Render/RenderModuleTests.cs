using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Render;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Render;

public sealed class RenderModuleTests
{
    private static readonly WorldPoint Inn = new(-9462.66, 16.1915, 57.0459);

    private sealed class FakeModule(string name, int order, Action<AtlasContext> register) : IAtlasModule
    {
        public string Name { get; } = name;

        public int Order { get; } = order;

        public void Register(AtlasContext context) => register(context);
    }

    /// <summary>A composed context: a fake layer source (registered first, as the real layer modules are) plus the render module.</summary>
    private sealed class Host : IDisposable
    {
        public Host(FakeSource source)
        {
            Source = source;
            Context = AtlasModules.Compose(
                new AtlasPaths(null, null),
                NullLoggerFactory.Instance,
                headless: true,
                [new FakeModule("fake-layers", ModuleOrder.Foundation, c => c.Layers.Add(source)), new RenderModule()]);
            Assert.Empty(Context.Problems);
        }

        public FakeSource Source { get; }

        public AtlasContext Context { get; }

        public Task<CommandResult> Run(string command, JsonObject args, bool headless = true)
            => Context.Commands.InvokeAsync(command, args, Context.Services, CommandOrigin.Cli, headless, CancellationToken.None);

        public void Dispose() => Context.Dispose();
    }

    private static Host TerrainHost(out ViewState view)
    {
        TileKey tile = Views.TileOf(Views.TopCamera);
        var host = new Host(new FakeSource().Add(LayerIds.Terrain, tile, Geometry.Rect(LayerIds.Terrain, tile, 90, 110, 190, 210, 0)));
        view = Views.Of(Views.TopCamera, (LayerIds.Terrain, Views.Style(mode: RenderMode.Fill)));
        host.Context.View.Set(view, "test");
        return host;
    }

    private static Host WmoHost()
    {
        TileKey tile = WorldGrid.TileOf(0, Inn.X, Inn.Y)!.Value;
        var builder = new GeometryBuilder(LayerIds.VMapWmo, tile, Inn);
        uint a = builder.AddVertex(Inn.X + 1, Inn.Y + 1, Inn.Z);
        uint b = builder.AddVertex(Inn.X + 5, Inn.Y + 1, Inn.Z + 0.5);
        uint c = builder.AddVertex(Inn.X + 1, Inn.Y + 6, Inn.Z + 1);
        builder.AddTriangle(a, b, c, 1);
        builder.DescribeAttribute(1, () => new JsonObject { ["colorKey"] = ColorKeys.Indoor });
        return new Host(new FakeSource().Add(LayerIds.VMapWmo, tile, builder.Build()));
    }

    private static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static JsonObject Data(CommandResult result)
    {
        Assert.True(result.Ok, $"{result.Command} failed: {result.ErrorCode}: {result.ErrorMessage}");
        return Assert.IsType<JsonObject>(result.Data);
    }

    private static void AssertRefused(CommandResult result, string code, string fragment)
    {
        Assert.False(result.Ok, $"{result.Command} should have been refused");
        Assert.Equal(code, result.ErrorCode);
        Assert.Contains(fragment, result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void TheModule_IsDiscovered_AndRegistersTheRendererAndTheThreeCommands()
    {
        IReadOnlyList<IAtlasModule> found = AtlasModules.Discover(typeof(RenderModule).Assembly);
        IAtlasModule module = Assert.Single(found, m => m is RenderModule);
        Assert.Equal("render", module.Name);

        using var host = new Host(new FakeSource());

        Assert.IsType<CpuRenderer>(host.Context.Services.Require<IHeadlessRenderer>());
        foreach (string name in new[] { "render.png", "export.obj", "export.json" })
        {
            Assert.True(host.Context.Commands.TryGet(name, out CommandDefinition? command), name);
            Assert.Equal(CommandSurfaces.All, command.Surfaces);
            JsonObject schema = CommandSchema.InputSchema(command);
            Assert.Contains("out", ((JsonArray)schema["required"]!).Select(n => n!.GetValue<string>()));
        }
    }

    [Fact]
    public async Task ThePublicRenderer_ProducesTheBytesTheCommandWrites()
    {
        using Host host = TerrainHost(out ViewState view);
        using var dir = new TempDirectory();

        RenderedImage viaService = host.Context.Services.Require<IHeadlessRenderer>().Render(view, 96, 64, CancellationToken.None);
        CommandResult result = await host.Run("render.png", new JsonObject { ["width"] = 96, ["height"] = 64, ["out"] = dir.File("a.png") });

        Assert.Equal(viaService.Png, File.ReadAllBytes(Data(result)["path"]!.GetValue<string>()));
    }

    [Fact]
    public async Task RenderPng_WritesTheRenderedView_AndReportsPathAndStats()
    {
        using Host host = TerrainHost(out ViewState view);
        using var dir = new TempDirectory();
        string path = dir.File("shot.png");

        JsonObject data = Data(await host.Run("render.png", new JsonObject { ["width"] = 160, ["height"] = 120, ["out"] = path }));

        DecodedPng png = DecodedPng.Parse(File.ReadAllBytes(path));
        Assert.Equal(path, data["path"]!.GetValue<string>());
        Assert.Equal(160, png.Width);
        Assert.Equal(120, png.Height);
        Assert.Equal(new FileInfo(path).Length, Json.Int(data["bytes"]));
        Assert.Equal(1, data["stats"]!["tiles"]!["ok"]!.GetValue<int>());
        Assert.Equal(2, Json.Int(data["stats"]!["triangles"]));
        var direct = new CpuRenderer(host.Context.Layers, host.Context.Cache);
        Assert.Equal(direct.Rasterize(view, 160, 120, CancellationToken.None).Buffer.Pixels, png.Pixels());
        Assert.Equal(new[] { "shot.png" }, Directory.GetFiles(dir.Root).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public async Task RenderPng_DefaultsTo1280x720()
    {
        using Host host = TerrainHost(out _);
        using var dir = new TempDirectory();

        JsonObject data = Data(await host.Run("render.png", new JsonObject { ["out"] = dir.File("default.png") }));

        Assert.Equal(RenderModule.DefaultWidth, data["width"]!.GetValue<int>());
        Assert.Equal(RenderModule.DefaultHeight, data["height"]!.GetValue<int>());
        DecodedPng png = DecodedPng.Parse(File.ReadAllBytes(dir.File("default.png")));
        Assert.Equal((1280, 720), (png.Width, png.Height));
    }

    [Fact]
    public async Task RenderPng_AppliesTheViewPatchToACopy_AndLeavesTheSharedViewAlone()
    {
        using Host host = TerrainHost(out ViewState shared);
        using var dir = new TempDirectory();
        long version = host.Context.View.Version;
        var centre = (X: 64, Y: 48);

        await host.Run("render.png", new JsonObject { ["width"] = 128, ["height"] = 96, ["out"] = dir.File("plain.png") });
        CommandResult patched = await host.Run("render.png", new JsonObject
        {
            ["width"] = 128,
            ["height"] = 96,
            ["out"] = dir.File("patched.png"),
            ["view"] = Obj("""{"layers":{"terrain":{"visible":false}}}"""),
        });

        byte[] plain = DecodedPng.Parse(File.ReadAllBytes(dir.File("plain.png"))).Pixels();
        byte[] hidden = DecodedPng.Parse(File.ReadAllBytes(dir.File("patched.png"))).Pixels();
        int at = ((centre.Y * 128) + centre.X) * 4;
        Assert.True(patched.Ok);
        Assert.NotEqual(Rgb.Background, new Rgb(plain[at], plain[at + 1], plain[at + 2]));
        Assert.Equal(Rgb.Background, new Rgb(hidden[at], hidden[at + 1], hidden[at + 2]));
        Assert.Same(shared, host.Context.View.Current);
        Assert.Equal(version, host.Context.View.Version);
    }

    [Fact]
    public async Task RenderPng_RefusesWhatItCannotRender_BeforeWritingAnything()
    {
        using Host host = TerrainHost(out _);
        using var dir = new TempDirectory();
        string good = dir.File("x.png");

        AssertRefused(await host.Run("render.png", new JsonObject { ["width"] = 8, ["height"] = 64, ["out"] = good }), CommandException.BadArguments, "width");
        AssertRefused(await host.Run("render.png", new JsonObject { ["width"] = 64, ["height"] = 9000, ["out"] = good }), CommandException.BadArguments, "height");
        AssertRefused(await host.Run("render.png", new JsonObject { ["width"] = 8192, ["height"] = 8192, ["out"] = good }), CommandException.BadArguments, "pixel limit");
        AssertRefused(await host.Run("render.png", new JsonObject { ["width"] = 64, ["height"] = 64 }), CommandException.BadArguments, "out");
        AssertRefused(await host.Run("render.png", new JsonObject { ["out"] = dir.File("x.jpg") }), CommandException.BadArguments, ".png");
        AssertRefused(await host.Run("render.png", new JsonObject { ["out"] = "relative.png" }, headless: false), CommandException.BadArguments, "absolute");
        AssertRefused(
            await host.Run("render.png", new JsonObject { ["out"] = good, ["view"] = Obj("""{"camera":{"distance":-5}}""") }),
            CommandException.BadArguments,
            "distance");
        AssertRefused(
            await host.Run("render.png", new JsonObject { ["out"] = good, ["view"] = Obj("""{"layers":{"no.such.layer":{"visible":true}}}""") }),
            CommandException.BadArguments,
            "no.such.layer");
        AssertRefused(await host.Run("render.png", new JsonObject { ["out"] = good, ["view"] = "top" }), CommandException.BadArguments, "view");
        AssertRefused(await host.Run("render.png", new JsonObject { ["out"] = good, ["colour"] = "red" }), CommandException.BadArguments, "colour");

        Assert.Empty(Directory.GetFileSystemEntries(dir.Root));
    }

    [Fact]
    public async Task RenderPng_RefusesToWriteInsideTheRepository()
    {
        using Host host = TerrainHost(out _);
        string inside = Path.Combine(AppContext.BaseDirectory, "must-not-exist.png");

        CommandResult result = await host.Run("render.png", new JsonObject { ["out"] = inside });

        AssertRefused(result, CommandException.BadArguments, "repository working tree");
        Assert.False(File.Exists(inside));
    }

    [Fact]
    public async Task ExportObj_WritesTheObjAndMtl_AndReportsTileStatus()
    {
        using Host host = WmoHost();
        using var dir = new TempDirectory();
        string path = dir.File("inn.obj");

        JsonObject data = Data(await host.Run("export.obj", new JsonObject
        {
            ["map"] = 0,
            ["x"] = Inn.X,
            ["y"] = Inn.Y,
            ["radius"] = 1,
            ["layers"] = new JsonArray(LayerIds.VMapWmo),
            ["out"] = path,
        }));

        Assert.Equal(path, data["path"]!.GetValue<string>());
        Assert.Equal(dir.File("inn.mtl"), data["mtl"]!.GetValue<string>());
        Assert.True(File.Exists(dir.File("inn.mtl")));
        Assert.Equal(1, Json.Int(data["objects"]));
        Assert.Equal(3, Json.Int(data["vertices"]));
        Assert.Equal(1, Json.Int(data["triangles"]));
        Assert.Equal(9, data["tileCounts"]!["requested"]!.GetValue<int>());
        Assert.Equal(1, data["tileCounts"]!["ok"]!.GetValue<int>());
        Assert.Equal(8, data["tileCounts"]!["missing"]!.GetValue<int>());
        Assert.Equal(9, data["tiles"]!.AsArray().Count);
        ParsedObj obj = ParsedObj.Parse(File.ReadAllText(path));
        Assert.Equal(3, obj.Vertices.Count);
        Assert.Equal("inn.mtl", obj.MaterialLibrary);
        Assert.Equal(new FileInfo(path).Length, data["bytes"]!.GetValue<long>());
    }

    [Fact]
    public async Task ExportJson_WritesTheDocument()
    {
        using Host host = WmoHost();
        using var dir = new TempDirectory();
        string path = dir.File("inn.json");

        JsonObject data = Data(await host.Run("export.json", new JsonObject
        {
            ["map"] = 0,
            ["x"] = Inn.X,
            ["y"] = Inn.Y,
            ["radius"] = 0,
            ["layers"] = new JsonArray(LayerIds.VMapWmo),
            ["out"] = path,
        }));

        JsonObject document = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(path, data["path"]!.GetValue<string>());
        Assert.Equal(JsonExporter.Format, document["format"]!.GetValue<string>());
        Assert.Single(document["chunks"]!.AsArray());
        Assert.Equal(9, document["chunks"]![0]!["positions"]!.AsArray().Count);
        Assert.Equal(1, data["tileCounts"]!["requested"]!.GetValue<int>());
    }

    [Fact]
    public async Task Export_WithoutXAndY_UsesTheViewsCameraTarget_AndWithoutLayers_TheVisibleOnes()
    {
        using Host host = WmoHost();
        using var dir = new TempDirectory();
        ViewState view = host.Context.View.Current with
        {
            Camera = host.Context.View.Current.Camera with { Target = Inn },
            LoadRadiusTiles = 0,
        };
        host.Context.View.Set(view, "test");

        JsonObject data = Data(await host.Run("export.obj", new JsonObject { ["map"] = 0, ["radius"] = 0, ["out"] = dir.File("view.obj") }));

        Assert.Equal(1, data["tileCounts"]!["ok"]!.GetValue<int>());
        Assert.Equal(3, Json.Int(data["vertices"]));
    }

    [Fact]
    public async Task Export_RefusesBadRequests_WithTheRightCode()
    {
        using Host host = WmoHost();
        using var dir = new TempDirectory();
        string good = dir.File("x.obj");
        JsonObject Args(Action<JsonObject> change)
        {
            var args = new JsonObject { ["map"] = 0, ["x"] = Inn.X, ["y"] = Inn.Y, ["radius"] = 0, ["layers"] = new JsonArray(LayerIds.VMapWmo), ["out"] = good };
            change(args);
            return args;
        }

        AssertRefused(await host.Run("export.obj", Args(a => a.Remove("y"))), CommandException.BadArguments, "'x' and 'y'");
        AssertRefused(await host.Run("export.obj", Args(a => a.Remove("x"))), CommandException.BadArguments, "'x' and 'y'");
        AssertRefused(await host.Run("export.obj", Args(a => a["radius"] = 5)), CommandException.BadArguments, "radius");
        AssertRefused(await host.Run("export.obj", Args(a => a["radius"] = -1)), CommandException.BadArguments, "radius");
        AssertRefused(await host.Run("export.obj", Args(a => a["layers"] = new JsonArray("no.such"))), CommandException.BadArguments, "unknown layer");
        AssertRefused(await host.Run("export.obj", Args(a => a["layers"] = new JsonArray(LayerIds.LivePlayers))), CommandException.BadArguments, "no tiles");
        AssertRefused(await host.Run("export.obj", Args(a => a["out"] = dir.File("x.json"))), CommandException.BadArguments, ".obj");
        AssertRefused(await host.Run("export.json", Args(a => a["out"] = dir.File("x.obj"))), CommandException.BadArguments, ".json");
        AssertRefused(await host.Run("export.obj", Args(a => a["out"] = Path.Combine(AppContext.BaseDirectory, "x.obj"))), CommandException.BadArguments, "repository working tree");
        AssertRefused(await host.Run("export.obj", Args(a => a.Remove("map"))), CommandException.BadArguments, "map");
        AssertRefused(await host.Run("export.obj", Args(a => a["map"] = -3)), CommandException.BadArguments, "map");
        Assert.Empty(Directory.GetFileSystemEntries(dir.Root));
    }

    [Fact]
    public async Task Export_WithoutXAndY_ForAnotherMap_IsRefused()
    {
        using Host host = WmoHost();
        using var dir = new TempDirectory();

        CommandResult result = await host.Run("export.obj", new JsonObject { ["map"] = 1, ["out"] = dir.File("x.obj") });

        AssertRefused(result, CommandException.BadArguments, "not map 1");
    }

    [Fact]
    public async Task Export_OfALayerWithoutASource_IsUnavailable()
    {
        using Host host = WmoHost();
        using var dir = new TempDirectory();

        CommandResult result = await host.Run("export.obj", new JsonObject
        {
            ["map"] = 0,
            ["x"] = Inn.X,
            ["y"] = Inn.Y,
            ["layers"] = new JsonArray(LayerIds.Terrain),
            ["out"] = dir.File("x.obj"),
        });

        AssertRefused(result, CommandException.Unavailable, LayerIds.Terrain);
        Assert.Empty(Directory.GetFileSystemEntries(dir.Root));
    }

    [Fact]
    public async Task Export_WhenNoTileHasGeometry_IsUnavailable_AndWritesNothing()
    {
        using Host host = new(new FakeSource().Serves(LayerIds.VMapWmo));
        using var dir = new TempDirectory();

        foreach (string command in new[] { "export.obj", "export.json" })
        {
            CommandResult result = await host.Run(command, new JsonObject
            {
                ["map"] = 0,
                ["x"] = Inn.X,
                ["y"] = Inn.Y,
                ["radius"] = 1,
                ["layers"] = new JsonArray(LayerIds.VMapWmo),
                ["out"] = dir.File(command == "export.obj" ? "none.obj" : "none.json"),
            });

            AssertRefused(result, CommandException.Unavailable, "nothing to export");
            Assert.Contains("9 missing", result.ErrorMessage, StringComparison.Ordinal);
        }

        Assert.Empty(Directory.GetFileSystemEntries(dir.Root));
    }

    [Fact]
    public async Task Export_WithNoVisibleLayerHavingASource_AsksForLayers()
    {
        using Host host = WmoHost();
        using var dir = new TempDirectory();
        ViewState hidden = host.Context.View.Current with
        {
            Layers = host.Context.View.Current.Layers.SetItem(
                LayerIds.VMapWmo,
                host.Context.View.Current.Layers[LayerIds.VMapWmo] with { Visible = false }),
        };
        host.Context.View.Set(hidden, "test");

        CommandResult result = await host.Run("export.obj", new JsonObject { ["map"] = 0, ["x"] = Inn.X, ["y"] = Inn.Y, ["out"] = dir.File("x.obj") });

        AssertRefused(result, CommandException.Unavailable, "'layers'");
    }
}
