using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Render;

namespace ArcaneCore.Atlas.Tests.Render;

/// <summary>A layer source that serves hand-built chunks; every other tile of its layers is reported missing.</summary>
internal sealed class FakeSource : ILayerSource
{
    private readonly Dictionary<(string Layer, TileKey Tile), LayerBuild> _builds = [];
    private readonly HashSet<string> _layers = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Layers => [.. _layers];

    /// <summary>Declares that this source builds <paramref name="layer"/> without giving it any tile.</summary>
    public FakeSource Serves(string layer)
    {
        _layers.Add(layer);
        return this;
    }

    public FakeSource Add(string layer, TileKey tile, params GeometryChunk[] chunks)
    {
        _layers.Add(layer);
        _builds[(layer, tile)] = LayerBuild.Of(layer, tile, chunks);
        return this;
    }

    public FakeSource Add(string layer, TileKey tile, LayerBuild build)
    {
        _layers.Add(layer);
        _builds[(layer, tile)] = build;
        return this;
    }

    public IReadOnlyCollection<TileKey> AvailableTiles(string layerId, uint mapId)
        => [.. _builds.Keys.Where(k => k.Layer == layerId && k.Tile.MapId == mapId).Select(k => k.Tile)];

    public TileStatus PeekStatus(string layerId, TileKey tile)
        => _builds.TryGetValue((layerId, tile), out LayerBuild? build) ? build.Status : new TileStatus(layerId, tile, TileState.Missing);

    public LayerBuild Build(string layerId, TileKey tile, CancellationToken cancellationToken)
        => _builds.TryGetValue((layerId, tile), out LayerBuild? build) ? build : LayerBuild.Missing(layerId, tile, "no such fake file");
}

/// <summary>A registry, cache and renderer over one fake source.</summary>
internal sealed class RenderRig
{
    public RenderRig(FakeSource source)
    {
        Source = source;
        Registry = new LayerRegistry();
        Registry.Add(source);
        Cache = new LayerCache(Registry);
        Renderer = new CpuRenderer(Registry, Cache);
    }

    public FakeSource Source { get; }

    public LayerRegistry Registry { get; }

    public LayerCache Cache { get; }

    public CpuRenderer Renderer { get; }

    public RenderedFrame Frame(ViewState view, int width, int height) => Renderer.Rasterize(view, width, height, CancellationToken.None);
}

/// <summary>Geometry builders for the synthetic scenes (world coordinates, chunk origin at the world origin so small integers stay exact in float).</summary>
internal static class Geometry
{
    /// <summary>A quad split into two triangles (a b c) and (a c d), with its four edges as lines.</summary>
    public static GeometryChunk Quad(
        string layer,
        TileKey tile,
        WorldPoint a,
        WorldPoint b,
        WorldPoint c,
        WorldPoint d,
        uint attribute = 0,
        IReadOnlyDictionary<uint, JsonObject>? attributes = null)
    {
        var builder = new GeometryBuilder(layer, tile, new WorldPoint(0, 0, 0));
        uint va = builder.AddVertex(a);
        uint vb = builder.AddVertex(b);
        uint vc = builder.AddVertex(c);
        uint vd = builder.AddVertex(d);
        builder.AddTriangle(va, vb, vc, attribute);
        builder.AddTriangle(va, vc, vd, attribute);
        if (attributes is not null)
        {
            foreach ((uint id, JsonObject table) in attributes)
            {
                builder.DescribeAttribute(id, () => (JsonObject)table.DeepClone());
            }
        }

        return builder.Build();
    }

    /// <summary>An axis-aligned horizontal rectangle at height <paramref name="z"/>.</summary>
    public static GeometryChunk Rect(
        string layer,
        TileKey tile,
        double x0,
        double x1,
        double y0,
        double y1,
        double z,
        uint attribute = 0,
        IReadOnlyDictionary<uint, JsonObject>? attributes = null)
        => Quad(layer, tile, new WorldPoint(x0, y0, z), new WorldPoint(x1, y0, z), new WorldPoint(x1, y1, z), new WorldPoint(x0, y1, z), attribute, attributes);

    /// <summary>A single line segment (no triangles).</summary>
    public static GeometryChunk Segment(string layer, TileKey tile, WorldPoint from, WorldPoint to, uint attribute = 0)
    {
        var builder = new GeometryBuilder(layer, tile, new WorldPoint(0, 0, 0));
        builder.AddLine(builder.AddVertex(from), builder.AddVertex(to), attribute);
        return builder.Build();
    }

    /// <summary>An attribute table entry carrying only a colour key.</summary>
    public static IReadOnlyDictionary<uint, JsonObject> Keys(params (uint Id, string Key)[] entries)
        => entries.ToDictionary(e => e.Id, e => new JsonObject { ["colorKey"] = e.Key });
}

/// <summary>View construction for tests: only the named layers visible, no HUD, one tile.</summary>
internal static class Views
{
    public static readonly CameraState TopCamera = new(new WorldPoint(100, 200, 0), 32, 0, 0, 55, CameraMode.Top);

    public static LayerStyle Style(string color = "#B9A77A", RenderMode mode = RenderMode.Fill, double opacity = 1.0, bool byAttribute = false)
        => new(true, color, opacity, mode, byAttribute);

    public static ViewState Of(CameraState camera, params (string Layer, LayerStyle Style)[] layers)
    {
        ImmutableSortedDictionary<string, LayerStyle> all = ViewState.Default.Layers
            .ToImmutableSortedDictionary(p => p.Key, p => p.Value with { Visible = false }, StringComparer.Ordinal);
        foreach ((string layer, LayerStyle style) in layers)
        {
            all = all.SetItem(layer, style);
        }

        return ViewState.Default with { MapId = 0, Camera = camera, Layers = all, LoadRadiusTiles = 0, ShowHud = false };
    }

    /// <summary>The tile that holds the camera target, which the fake sources key their data on.</summary>
    public static TileKey TileOf(CameraState camera)
        => WorldGrid.TileOf(0, camera.Target.X, camera.Target.Y) ?? throw new InvalidOperationException("The test camera is outside the map.");
}

/// <summary>Pixel math shared by the tests.</summary>
internal static class Pixels
{
    /// <summary>The pixel a world point lands in (<see cref="CameraMath.Project"/> to normalized device coordinates, then to pixels).</summary>
    public static (int X, int Y) Of(CameraState camera, WorldPoint point, int width, int height)
    {
        (double x, double y, _) = CameraMath.Project(camera, point, (double)width / height)
            ?? throw new InvalidOperationException($"{point} is behind the camera.");
        return ((int)Math.Floor((x + 1) / 2 * width), (int)Math.Floor((1 - y) / 2 * height));
    }

    /// <summary>The world point on the ground plane z under the centre of pixel (<paramref name="px"/>, <paramref name="py"/>) for a top-down camera.</summary>
    public static WorldPoint TopDownWorld(CameraState camera, int width, int height, int px, int py, double z)
    {
        double ndcX = (((px + 0.5) / width) * 2) - 1;
        double ndcY = 1 - (((py + 0.5) / height) * 2);
        PickRay ray = CameraMath.RayThrough(camera, 0, ndcX, ndcY, (double)width / height);
        return new WorldPoint(ray.Origin.X, ray.Origin.Y, z);
    }

    public static Rgb Parse(string hex) => Rgb.TryParse(hex, out Rgb c) ? c : throw new FormatException(hex);

    /// <summary>The sum of absolute channel differences.</summary>
    public static int Distance(Rgb a, Rgb b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
}

/// <summary>JSON number reading that does not care whether the producer stored an int or a long.</summary>
internal static class Json
{
    public static long Int(JsonNode? node)
        => long.Parse((node ?? throw new ArgumentNullException(nameof(node))).ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
}
