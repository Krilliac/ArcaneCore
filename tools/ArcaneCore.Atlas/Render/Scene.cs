using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Render;

/// <summary>Which half of a compare view a layer belongs to.</summary>
internal enum LayerSide
{
    /// <summary>What the server sees (<see cref="LayerView.Server"/>).</summary>
    Server,

    /// <summary>Client files and DotRecast (<see cref="LayerView.Client"/> and <see cref="LayerView.DotRecast"/>).</summary>
    Reference,

    /// <summary>Server-versus-reference difference layers.</summary>
    Diff,
}

/// <summary>A tile that did not produce geometry for a layer, with why. Missing and rejected tiles are never drawn as empty space silently.</summary>
internal sealed record TileProblem(string Layer, TileKey Tile, string State, string? Detail)
{
    public JsonObject ToJson() => new()
    {
        ["layer"] = Layer,
        ["tile"] = new JsonArray(Tile.MapId, Tile.X, Tile.Y),
        ["state"] = State,
        ["detail"] = Detail,
    };
}

/// <summary>Counts of tiles by outcome.</summary>
internal sealed class TileTally
{
    private readonly int[] _byState = new int[Enum.GetValues<TileState>().Length];

    /// <summary>Tiles whose build threw instead of reporting a status (a source bug or an I/O fault).</summary>
    public int Failed { get; private set; }

    public int Requested => _byState.Sum() + Failed;

    public int Count(TileState state) => _byState[(int)state];

    public void Add(TileState state) => _byState[(int)state]++;

    public void AddFailed() => Failed++;

    public void Add(TileTally other)
    {
        for (int i = 0; i < _byState.Length; i++)
        {
            _byState[i] += other._byState[i];
        }

        Failed += other.Failed;
    }

    public JsonObject ToJson() => new()
    {
        ["requested"] = Requested,
        ["ok"] = Count(TileState.Ok),
        ["empty"] = Count(TileState.Empty),
        ["missing"] = Count(TileState.Missing),
        ["rejected"] = Count(TileState.Rejected),
        ["unknown"] = Count(TileState.Unknown),
        ["notApplicable"] = Count(TileState.NotApplicable),
        ["failed"] = Failed,
    };
}

/// <summary>A chunk to draw with its per-attribute colour table (only when the layer colours by attribute).</summary>
internal sealed class SceneChunk(GeometryChunk chunk, Dictionary<uint, Rgb>? attributeColours)
{
    public GeometryChunk Chunk { get; } = chunk;

    public Dictionary<uint, Rgb>? AttributeColours { get; } = attributeColours;

    /// <summary>The colour of a primitive whose attribute id is <paramref name="attribute"/>.</summary>
    public Rgb ColourOf(uint attribute, Rgb layerColour)
        => AttributeColours is not null && AttributeColours.TryGetValue(attribute, out Rgb c) ? c : layerColour;
}

/// <summary>One visible layer ready to draw: its style, the compare side it draws on and the chunks gathered for it.</summary>
internal sealed class SceneLayer(LayerInfo info, LayerStyle style, Rgb colour, double opacity, LayerSide side)
{
    public LayerInfo Info { get; } = info;

    public LayerStyle Style { get; } = style;

    public Rgb Colour { get; } = colour;

    /// <summary>The style's opacity after the diff view's dimming.</summary>
    public double Opacity { get; } = opacity;

    public LayerSide Side { get; } = side;

    public RenderMode Mode => Style.Mode;

    public List<SceneChunk> Chunks { get; } = [];

    public TileTally Tiles { get; } = new();

    public long Triangles { get; set; }

    public long Lines { get; set; }

    public long Points { get; set; }
}

/// <summary>
/// Everything a render draws, gathered through the shared <see cref="LayerCache"/>: for each visible tiled layer that has
/// a source, the tiles around the camera target (<see cref="WorldGrid.Around"/> at the view's load radius) plus the
/// map's global tile when the source lists it. Layers that cannot be drawn are listed with the reason.
/// </summary>
internal sealed class Scene
{
    /// <summary>Most problem tiles recorded individually (the counts stay exact).</summary>
    public const int MaxRecordedProblems = 200;

    /// <summary>The view's compare mode (a split view clips each layer to its half).</summary>
    public CompareMode Compare { get; private init; }

    public List<SceneLayer> Layers { get; } = [];

    public List<TileProblem> Problems { get; } = [];

    public int ProblemsOmitted { get; private set; }

    /// <summary>Visible tiled layers that no module provides a source for (for example reference layers without a client directory).</summary>
    public List<string> LayersWithoutSource { get; } = [];

    /// <summary>Visible layers the compare mode leaves out.</summary>
    public List<string> HiddenByCompare { get; } = [];

    /// <summary>Visible layers that have no tiles (live layers), which the UI draws from live frames and the headless renderer does not.</summary>
    public List<string> UntiledSkipped { get; } = [];

    public TileTally Tiles { get; } = new();

    public long Triangles => Layers.Sum(l => l.Triangles);

    public long Lines => Layers.Sum(l => l.Lines);

    public long Points => Layers.Sum(l => l.Points);

    public static Scene Build(ViewState view, LayerRegistry layers, LayerCache cache, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(cache);
        var scene = new Scene { Compare = view.Compare };
        WorldPoint target = view.Camera.Target;
        foreach (LayerInfo info in LayerCatalog.All)
        {
            if (!view.Layers.TryGetValue(info.Id, out LayerStyle? style) || !style.Visible || style.Opacity <= 0)
            {
                continue;
            }

            if (!info.Tiled)
            {
                scene.UntiledSkipped.Add(info.Id);
                continue;
            }

            if (!TrySide(view.Compare, info.View, out LayerSide side, out double dim))
            {
                scene.HiddenByCompare.Add(info.Id);
                continue;
            }

            if (!layers.TryGet(info.Id, out ILayerSource? source))
            {
                scene.LayersWithoutSource.Add(info.Id);
                continue;
            }

            if (!Rgb.TryParse(style.Color, out Rgb colour))
            {
                throw new InvalidOperationException($"Layer {info.Id} has the colour '{style.Color}', which is not #RRGGBB.");
            }

            var layer = new SceneLayer(info, style, colour, style.Opacity * dim, side);
            Dictionary<string, Rgb>? keyed = style.ColorByAttribute ? ParseAttributeColours(info) : null;
            foreach (TileKey tile in TilesFor(source, info.Id, view.MapId, target, view.LoadRadiusTiles))
            {
                cancellationToken.ThrowIfCancellationRequested();
                scene.GatherTile(layer, cache, tile, keyed, cancellationToken);
            }

            scene.Tiles.Add(layer.Tiles);
            scene.Layers.Add(layer);
        }

        return scene;
    }

    /// <summary>
    /// The tiles a layer is drawn from: the tiles within <paramref name="radius"/> of the target, then the map's global
    /// tile when <see cref="ILayerSource.AvailableTiles"/> lists it.
    /// </summary>
    internal static IReadOnlyList<TileKey> TilesFor(ILayerSource source, string layerId, uint mapId, WorldPoint target, int radius)
    {
        List<TileKey> tiles = [.. WorldGrid.Around(mapId, target.X, target.Y, radius)];
        TileKey global = TileKey.Global(mapId);
        if (source.AvailableTiles(layerId, mapId).Contains(global))
        {
            tiles.Add(global);
        }

        return tiles;
    }

    /// <summary>Which half of the compare view a layer of <paramref name="source"/> draws on, and its opacity factor; false when the mode leaves it out.</summary>
    internal static bool TrySide(CompareMode mode, LayerView source, out LayerSide side, out double dim)
    {
        side = LayerSide.Server;
        dim = 1.0;
        switch (mode)
        {
            case CompareMode.Server:
                return source == LayerView.Server;
            case CompareMode.Reference:
                side = LayerSide.Reference;
                return source is LayerView.Client or LayerView.DotRecast;
            case CompareMode.Split:
                if (source is LayerView.Client or LayerView.DotRecast)
                {
                    side = LayerSide.Reference;
                    return true;
                }

                return source == LayerView.Server;
            case CompareMode.Diff:
                if (source == LayerView.Diff)
                {
                    side = LayerSide.Diff;
                    return true;
                }

                dim = Shading.DiffServerDim;
                return source == LayerView.Server;
            default:
                return false;
        }
    }

    private static Dictionary<string, Rgb>? ParseAttributeColours(LayerInfo info)
    {
        if (info.AttributeColors is not { Count: > 0 } colors)
        {
            return null;
        }

        var parsed = new Dictionary<string, Rgb>(StringComparer.Ordinal);
        foreach ((string key, string text) in colors)
        {
            if (Rgb.TryParse(text, out Rgb rgb))
            {
                parsed[key] = rgb;
            }
        }

        return parsed;
    }

    private void GatherTile(SceneLayer layer, LayerCache cache, TileKey tile, Dictionary<string, Rgb>? keyed, CancellationToken cancellationToken)
    {
        LayerBuild build;
        try
        {
            build = cache.Get(layer.Info.Id, tile, cancellationToken);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or InvalidOperationException or KeyNotFoundException
            or ArgumentException or NotSupportedException or UnauthorizedAccessException)
        {
            layer.Tiles.AddFailed();
            Record(new TileProblem(layer.Info.Id, tile, "Failed", $"{e.GetType().Name}: {e.Message}"));
            return;
        }

        TileState state = build.Status.State;
        layer.Tiles.Add(state);
        if (state is not (TileState.Ok or TileState.Empty))
        {
            Record(new TileProblem(layer.Info.Id, tile, state.ToString(), build.Status.Detail));
        }

        foreach (GeometryChunk chunk in build.Chunks)
        {
            Dictionary<uint, Rgb>? table = keyed is null ? null : AttributeTable(chunk, keyed);
            layer.Chunks.Add(new SceneChunk(chunk, table));
            layer.Triangles += chunk.TriangleCount;
            layer.Lines += chunk.LineCount;
            layer.Points += chunk.PointCount;
        }
    }

    private static Dictionary<uint, Rgb> AttributeTable(GeometryChunk chunk, Dictionary<string, Rgb> keyed)
    {
        var table = new Dictionary<uint, Rgb>();
        foreach ((uint id, JsonObject attribute) in chunk.Attributes)
        {
            if (attribute["colorKey"] is JsonValue value && value.TryGetValue(out string? key) && keyed.TryGetValue(key, out Rgb colour))
            {
                table[id] = colour;
            }
        }

        return table;
    }

    private void Record(TileProblem problem)
    {
        if (Problems.Count < MaxRecordedProblems)
        {
            Problems.Add(problem);
        }
        else
        {
            ProblemsOmitted++;
        }
    }
}
