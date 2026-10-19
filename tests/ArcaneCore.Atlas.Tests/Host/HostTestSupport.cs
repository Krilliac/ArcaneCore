using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Host;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Host;

/// <summary>A PNG file built from scratch (signature, IHDR, one IDAT, IEND) so tests can check what the host does with real PNG bytes.</summary>
internal static class TestPng
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>An 8-bit grayscale PNG of one flat shade.</summary>
    public static byte[] Create(int width, int height, byte shade = 0x80)
    {
        using var file = new MemoryStream();
        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = 8; // bit depth
        header[9] = 0; // colour type: grayscale
        WriteChunk(file, "IHDR", header);

        byte[] row = new byte[width + 1]; // filter type 0 + samples
        Array.Fill(row, shade, 1, width);
        using var deflated = new MemoryStream();
        using (var zlib = new ZLibStream(deflated, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (int y = 0; y < height; y++)
            {
                zlib.Write(row);
            }
        }

        WriteChunk(file, "IDAT", deflated.ToArray());
        WriteChunk(file, "IEND", []);
        return file.ToArray();
    }

    private static void WriteChunk(Stream file, string type, byte[] data)
    {
        byte[] word = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(word, (uint)data.Length);
        file.Write(word);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        file.Write(typeBytes);
        file.Write(data);
        uint crc = 0xFFFFFFFF;
        foreach (byte b in typeBytes.Concat(data))
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        BinaryPrimitives.WriteUInt32BigEndian(word, ~crc);
        file.Write(word);
    }

    // CRC-32 as the PNG specification defines it (annex D): reflected polynomial 0xEDB88320, initial value and final XOR 0xFFFFFFFF.
    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}

/// <summary>Geometry builders for fixtures: flat quads, walls, segments and points placed in world coordinates.</summary>
internal static class Geo
{
    /// <summary>The tile holding a world position of map 0.</summary>
    public static TileKey TileAt(double x, double y, uint map = 0) => WorldGrid.TileOf(map, x, y) ?? throw new InvalidOperationException($"({x}, {y}) is off the map");

    /// <summary>A horizontal quad (two triangles) at height <paramref name="z"/>.</summary>
    public static GeometryChunk Quad(string layer, TileKey tile, double minX, double maxX, double minY, double maxY, double z, uint attribute = 0, int part = 0, string? kind = null, bool edges = true)
    {
        var builder = new GeometryBuilder(layer, tile, WorldGrid.Origin(tile), part);
        uint a = builder.AddVertex(minX, minY, z);
        uint b = builder.AddVertex(maxX, minY, z);
        uint c = builder.AddVertex(maxX, maxY, z);
        uint d = builder.AddVertex(minX, maxY, z);
        builder.AddTriangle(a, b, c, attribute, edges);
        builder.AddTriangle(a, c, d, attribute, edges);
        Describe(builder, attribute, kind);
        return builder.Build();
    }

    /// <summary>A vertical wall in the plane x = <paramref name="x"/>.</summary>
    public static GeometryChunk Wall(string layer, TileKey tile, double x, double minY, double maxY, double minZ, double maxZ, uint attribute = 0, string? kind = null)
    {
        var builder = new GeometryBuilder(layer, tile, WorldGrid.Origin(tile));
        uint a = builder.AddVertex(x, minY, minZ);
        uint b = builder.AddVertex(x, maxY, minZ);
        uint c = builder.AddVertex(x, maxY, maxZ);
        uint d = builder.AddVertex(x, minY, maxZ);
        builder.AddTriangle(a, b, c, attribute);
        builder.AddTriangle(a, c, d, attribute);
        Describe(builder, attribute, kind);
        return builder.Build();
    }

    /// <summary>A chunk holding one line segment and nothing else.</summary>
    public static GeometryChunk Segment(string layer, TileKey tile, WorldPoint from, WorldPoint to, uint attribute = 0)
    {
        var builder = new GeometryBuilder(layer, tile, WorldGrid.Origin(tile));
        uint a = builder.AddVertex(from);
        uint b = builder.AddVertex(to);
        builder.AddLine(a, b, attribute);
        return builder.Build();
    }

    /// <summary>A chunk holding one point and nothing else.</summary>
    public static GeometryChunk Dot(string layer, TileKey tile, WorldPoint at, uint attribute = 0)
    {
        var builder = new GeometryBuilder(layer, tile, WorldGrid.Origin(tile));
        builder.AddPoint(builder.AddVertex(at), attribute);
        return builder.Build();
    }

    private static void Describe(GeometryBuilder builder, uint attribute, string? kind)
    {
        if (kind is not null)
        {
            builder.DescribeAttribute(attribute, () => new JsonObject { ["kind"] = kind, ["colorKey"] = ColorKeys.Ground });
        }
    }
}

/// <summary>A layer source over hand-made tiles. Counts builds so tests can prove the cache is used.</summary>
internal sealed class FakeLayerSource(params string[] layers) : ILayerSource
{
    private readonly ConcurrentDictionary<(string Layer, TileKey Tile), Func<LayerBuild>> _tiles = new();
    private readonly ConcurrentDictionary<(string Layer, TileKey Tile), int> _builds = new();

    public IReadOnlyList<string> Layers { get; } = layers;

    /// <summary>Total number of <see cref="Build"/> calls.</summary>
    public int Builds => _builds.Values.Sum();

    public int BuildsOf(string layer, TileKey tile) => _builds.GetValueOrDefault((layer, tile));

    public void AddChunks(string layer, TileKey tile, params GeometryChunk[] chunks)
        => _tiles[(layer, tile)] = () => LayerBuild.Of(layer, tile, chunks);

    public void AddEmpty(string layer, TileKey tile) => _tiles[(layer, tile)] = () => LayerBuild.Empty(layer, tile, "no features");

    public void AddRejected(string layer, TileKey tile, string detail) => _tiles[(layer, tile)] = () => LayerBuild.Rejected(layer, tile, detail);

    public void AddFailing(string layer, TileKey tile, Exception failure) => _tiles[(layer, tile)] = () => throw failure;

    public IReadOnlyCollection<TileKey> AvailableTiles(string layerId, uint mapId)
        => [.. _tiles.Keys.Where(k => k.Layer == layerId && k.Tile.MapId == mapId).Select(k => k.Tile)];

    public TileStatus PeekStatus(string layerId, TileKey tile)
        => new(layerId, tile, _tiles.ContainsKey((layerId, tile)) ? TileState.Unknown : TileState.Missing);

    public LayerBuild Build(string layerId, TileKey tile, CancellationToken cancellationToken)
    {
        _builds.AddOrUpdate((layerId, tile), 1, (_, n) => n + 1);
        return _tiles.TryGetValue((layerId, tile), out Func<LayerBuild>? build) ? build() : LayerBuild.Missing(layerId, tile, "no such tile in the fixture");
    }
}

/// <summary>A renderer that returns a real PNG of the requested size and remembers the views it was given.</summary>
internal sealed class FakeRenderer : IHeadlessRenderer
{
    private readonly ConcurrentQueue<ViewState> _views = new();

    public IReadOnlyCollection<ViewState> Views => _views;

    public RenderedImage Render(ViewState view, int width, int height, CancellationToken cancellationToken)
    {
        _views.Enqueue(view);
        return new RenderedImage(TestPng.Create(width, height), width, height, new JsonObject { ["tilesDrawn"] = 0, ["ms"] = 1 });
    }
}

internal sealed class FakeMapDirectory(params ClientMap[] maps) : IMapDirectory
{
    public IReadOnlyList<ClientMap> Maps { get; } = maps;

    public bool TryGetMap(uint mapId, out ClientMap map)
    {
        map = Maps.FirstOrDefault(m => m.Id == mapId)!;
        return map is not null;
    }

    public string? AreaName(uint areaId) => null;
}

/// <summary>Registers the fixture sources, renderer, directory and the <c>test.echo</c> command into a context.</summary>
internal sealed class TestModule(Action<AtlasContext> register) : IAtlasModule
{
    public string Name => "test";

    public int Order => ModuleOrder.Layers;

    public void Register(AtlasContext context) => register(context);
}

/// <summary>
/// A composed context (host module plus fixtures) with its own state folder, optionally behind a running server on port 0.
/// Disposing stops the server and deletes the folder.
/// </summary>
internal sealed class HostRig : IAsyncDisposable
{
    private readonly ConcurrentQueue<(string Topic, JsonNode? Data)> _events = new();
    private readonly IDisposable _subscription;

    private HostRig(string directory, bool headless, bool renderer, bool maps, AtlasPaths paths, Action<AtlasContext>? register)
    {
        Directory = directory;
        State = new AtlasState(Path.Combine(directory, "state"));
        Terrain = new FakeLayerSource(LayerIds.Terrain);
        Renderer = renderer ? new FakeRenderer() : null;
        var module = new TestModule(context =>
        {
            context.Layers.Add(Terrain);
            if (Renderer is not null)
            {
                context.Services.Add<IHeadlessRenderer>(Renderer);
            }

            if (maps)
            {
                context.Services.Add<IMapDirectory>(new FakeMapDirectory(
                    new ClientMap(0, "Azeroth", "Eastern Kingdoms", 0),
                    new ClientMap(1, "Kalimdor", "Kalimdor", 0),
                    new ClientMap(2, "Expansion01", "Outland", 0)));
            }

            context.Commands.Register(TestCommands.Echo);
            register?.Invoke(context);
        });
        Context = HostComposer.Compose(paths, NullLoggerFactory.Instance, headless, [new HostModule(State), module]);
        _subscription = Context.Events.Subscribe((topic, data) => _events.Enqueue((topic, data?.DeepClone())));
        Http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        Anonymous = new HttpClient(new SocketsHttpHandler { UseProxy = false });
    }

    public string Directory { get; }

    public AtlasState State { get; }

    public AtlasContext Context { get; }

    public FakeLayerSource Terrain { get; }

    public FakeRenderer? Renderer { get; }

    public AtlasServer? Server { get; private set; }

    /// <summary>Calls the API with the bearer token.</summary>
    public HttpClient Http { get; }

    /// <summary>Calls the API without credentials.</summary>
    public HttpClient Anonymous { get; }

    public IReadOnlyCollection<(string Topic, JsonNode? Data)> Events => _events;

    public static HostRig Create(bool headless = false, bool renderer = true, bool maps = true, AtlasPaths? paths = null, Action<AtlasContext>? register = null)
    {
        string directory = Path.Combine(Path.GetTempPath(), "atlas-host-tests-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        return new HostRig(directory, headless, renderer, maps, paths ?? new AtlasPaths(null, null), register);
    }

    public static async Task<HostRig> StartAsync(AtlasServerOptions? options = null, IWebAssets? assets = null, bool renderer = true, bool maps = true, AtlasPaths? paths = null, Action<AtlasContext>? register = null)
    {
        HostRig rig = Create(headless: false, renderer, maps, paths, register);
        AtlasServerOptions chosen = (options ?? new AtlasServerOptions()) with { Port = 0, Assets = assets ?? options?.Assets ?? new FakeAssets() };
        rig.Server = await AtlasServer.StartAsync(rig.Context, chosen);
        rig.Http.BaseAddress = rig.Server.BaseAddress;
        rig.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rig.Server.Token);
        rig.Anonymous.BaseAddress = rig.Server.BaseAddress;
        return rig;
    }

    /// <summary>Runs a command straight through the registry (no HTTP).</summary>
    public async Task<CommandResult> InvokeAsync(string name, JsonObject? args = null, CommandOrigin origin = CommandOrigin.Http)
        => await Context.Commands.InvokeAsync(name, args, Context.Services, origin, headless: Context.Headless, CancellationToken.None);

    /// <summary>Runs a command and returns its data, failing the test when it did not succeed.</summary>
    public async Task<JsonNode> DataAsync(string name, JsonObject? args = null)
    {
        CommandResult result = await InvokeAsync(name, args);
        Assert.True(result.Ok, $"{name} failed: {result.ErrorCode}: {result.ErrorMessage}");
        return result.Data!;
    }

    /// <summary>Posts to <c>/api/command</c> and returns the <see cref="CommandResult.ToJson"/> document.</summary>
    public async Task<JsonObject> PostCommandAsync(string name, JsonObject? args = null, string? origin = null)
    {
        var body = new JsonObject { ["name"] = name, ["args"] = args ?? [] };
        if (origin is not null)
        {
            body["origin"] = origin;
        }

        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await Http.PostAsync("api/command", content);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }

    /// <summary>The events of one topic published so far, in order.</summary>
    public IReadOnlyList<JsonNode> EventsOf(string topic) => [.. _events.Where(e => e.Topic == topic).Select(e => e.Data!)];

    public async ValueTask DisposeAsync()
    {
        _subscription.Dispose();
        Http.Dispose();
        Anonymous.Dispose();
        if (Server is not null)
        {
            await Server.DisposeAsync();
        }

        Context.Dispose();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder another process still holds open is left for the OS temp cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: nothing the test can do about it.
        }
    }
}

/// <summary>A tiny UI: index.html plus one script, as the embedded assets would serve them.</summary>
internal sealed class FakeAssets : IWebAssets
{
    public const string IndexHtml = "<!doctype html><title>fake atlas ui</title>";

    private readonly Dictionary<string, WebAsset> _files = new(StringComparer.Ordinal)
    {
        ["index.html"] = new WebAsset(Encoding.UTF8.GetBytes(IndexHtml), "text/html; charset=utf-8"),
        ["js/main.js"] = new WebAsset(Encoding.UTF8.GetBytes("console.log('atlas');"), "text/javascript; charset=utf-8"),
    };

    public bool TryGet(string path, out WebAsset asset) => _files.TryGetValue(path, out asset!);
}

/// <summary>No UI at all (the "UI not built" case).</summary>
internal sealed class NoAssets : IWebAssets
{
    public bool TryGet(string path, out WebAsset asset)
    {
        asset = null!;
        return false;
    }
}

internal static class JsonTestExtensions
{
    /// <summary>Reads a value by dotted path; numeric segments index arrays (<c>data.view.camera.target.2</c>).</summary>
    public static JsonNode? At(this JsonNode? node, string path)
    {
        foreach (string segment in path.Split('.'))
        {
            node = node switch
            {
                JsonArray array when int.TryParse(segment, out int index) && index >= 0 && index < array.Count => array[index],
                JsonObject obj => obj[segment],
                _ => null,
            };
        }

        return node;
    }

    public static double Num(this JsonNode? node, string path)
        => JsonScalars.TryGetDouble(node.At(path), out double value)
            ? value
            : throw new InvalidOperationException($"{path} is not a number: {node.At(path)?.ToJsonString() ?? "missing"}");

    public static string Str(this JsonNode? node, string path) => node.At(path)!.GetValue<string>();

    public static bool Flag(this JsonNode? node, string path) => node.At(path)!.GetValue<bool>();
}

internal static class Wait
{
    /// <summary>Polls until <paramref name="condition"/> holds; fails the test after <paramref name="timeoutMs"/>.</summary>
    public static async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        while (!condition())
        {
            try
            {
                await Task.Delay(10, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail($"Timed out after {timeoutMs} ms waiting for {what}.");
            }
        }
    }
}
