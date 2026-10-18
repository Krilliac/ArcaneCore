using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;

namespace ArcaneCore.Atlas.Render;

/// <summary>What <c>export.obj</c> and <c>export.json</c> export: one map, the tiles around a point, a set of tiled layers.</summary>
/// <param name="MapId">The map.</param>
/// <param name="X">World X of the centre (north).</param>
/// <param name="Y">World Y of the centre (west).</param>
/// <param name="Radius">Tiles in each direction around the centre tile.</param>
/// <param name="Layers">The layers to export, in catalog order.</param>
/// <param name="View">The view whose layer colours and opacities the OBJ materials take.</param>
internal sealed record ExportRequest(uint MapId, double X, double Y, int Radius, IReadOnlyList<LayerInfo> Layers, ViewState View)
{
    /// <summary>Default tile radius when the command omits one.</summary>
    public const int DefaultRadius = 1;

    /// <summary>
    /// Reads the shared arguments of both export commands: <c>map</c>, <c>x</c> and <c>y</c> (both or neither; neither means
    /// the view's camera target when the view shows the same map), <c>radius</c> and <c>layers</c> (default: the layers the
    /// view shows that have a tile source).
    /// </summary>
    public static ExportRequest Parse(CommandInvocation invocation, ViewState view, LayerRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        string command = invocation.Definition.Name;
        uint map = invocation.GetMapId("map");
        double? x = invocation.OptionalNumber("x");
        double? y = invocation.OptionalNumber("y");
        if (x.HasValue != y.HasValue)
        {
            throw new CommandException(CommandException.BadArguments, $"{command}: give both 'x' and 'y' or neither.");
        }

        if (x is null || y is null)
        {
            if (view.MapId != map)
            {
                throw new CommandException(
                    CommandException.BadArguments,
                    $"{command}: 'x' and 'y' are required because the current view shows map {view.MapId}, not map {map}.");
            }

            x = view.Camera.Target.X;
            y = view.Camera.Target.Y;
        }

        long radius = invocation.OptionalInt("radius") ?? DefaultRadius;
        if (radius is < 0 or > ViewState.MaxLoadRadiusTiles)
        {
            throw new CommandException(CommandException.BadArguments, $"{command}: 'radius' must be 0 to {ViewState.MaxLoadRadiusTiles} tiles.");
        }

        return new ExportRequest(map, x.Value, y.Value, (int)radius, ChooseLayers(invocation, view, registry), view);
    }

    private static List<LayerInfo> ChooseLayers(CommandInvocation invocation, ViewState view, LayerRegistry registry)
    {
        string command = invocation.Definition.Name;
        IReadOnlyList<string> requested = invocation.GetStringList("layers");
        if (requested.Count == 0)
        {
            List<LayerInfo> shown =
            [
                .. LayerCatalog.All.Where(l => l.Tiled
                    && view.Layers.TryGetValue(l.Id, out LayerStyle? style) && style.Visible && style.Opacity > 0
                    && registry.TryGet(l.Id, out _)),
            ];
            return shown.Count > 0
                ? shown
                : throw new CommandException(
                    CommandException.Unavailable,
                    $"{command}: no layer is visible in the current view with a data source; name layers with 'layers'.");
        }

        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in requested)
        {
            if (!LayerCatalog.TryGet(id, out LayerInfo info))
            {
                throw new CommandException(CommandException.BadArguments, $"{command}: unknown layer '{id}'.");
            }

            if (!info.Tiled)
            {
                throw new CommandException(CommandException.BadArguments, $"{command}: layer '{id}' has no tiles (live layers are drawn from live frames).");
            }

            if (!registry.TryGet(id, out _))
            {
                throw new CommandException(
                    CommandException.Unavailable,
                    $"{command}: no source provides layer '{id}' (its data directory or client files are not configured).");
            }

            wanted.Add(id);
        }

        return [.. LayerCatalog.All.Where(l => wanted.Contains(l.Id))];
    }
}

/// <summary>One layer of one tile as the export reads it.</summary>
internal sealed record ExportTile(LayerInfo Layer, TileKey Tile, LayerBuild Build);

/// <summary>What an export wrote, for the command result: totals, and the status of every tile asked for.</summary>
internal sealed class ExportSummary
{
    private readonly JsonArray _tiles = [];

    public TileTally Tiles { get; } = new();

    public int Objects { get; set; }

    public long Vertices { get; set; }

    public long Triangles { get; set; }

    public long Lines { get; set; }

    public long Points { get; set; }

    /// <summary>The geometry written, in primitives. Zero means every requested tile was missing, rejected or empty.</summary>
    public long Primitives => Triangles + Lines + Points;

    /// <summary>True when the files were moved into place; false when there was nothing to export and nothing was written.</summary>
    public bool Written { get; set; }

    public void Add(ExportTile tile)
    {
        Tiles.Add(tile.Build.Status.State);
        _tiles.Add(tile.Build.Status.ToJson());
    }

    /// <summary>Adds the totals and the status of every tile to a command result.</summary>
    public void AddTo(JsonObject result)
    {
        result["objects"] = Objects;
        result["vertices"] = Vertices;
        result["triangles"] = Triangles;
        result["lines"] = Lines;
        result["points"] = Points;
        result["tileCounts"] = Tiles.ToJson();
        result["tiles"] = _tiles.DeepClone();
    }

    /// <summary>One line naming how many tiles were in each state, for the refusal when nothing could be exported.</summary>
    public string Describe()
    {
        TileTally t = Tiles;
        return $"{t.Requested} tiles: {t.Count(TileState.Ok)} ok, {t.Count(TileState.Empty)} empty, {t.Count(TileState.Missing)} missing, "
            + $"{t.Count(TileState.Rejected)} rejected";
    }
}

/// <summary>Reads the tiles an export covers through the shared cache.</summary>
internal static class ExportSource
{
    /// <summary>
    /// The builds for each requested layer, tile by tile, in catalog layer order then the order of
    /// <see cref="Scene.TilesFor"/>. A tile whose build throws is reported as <see cref="TileState.Rejected"/> with the reason.
    /// </summary>
    public static IEnumerable<ExportTile> Tiles(ExportRequest request, LayerRegistry registry, LayerCache cache, CancellationToken cancellationToken)
    {
        var centre = new WorldPoint(request.X, request.Y, 0);
        foreach (LayerInfo layer in request.Layers)
        {
            if (!registry.TryGet(layer.Id, out ILayerSource? source))
            {
                throw new CommandException(CommandException.Unavailable, $"No source provides layer '{layer.Id}'.");
            }

            foreach (TileKey tile in Scene.TilesFor(source, layer.Id, request.MapId, centre, request.Radius))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ExportTile(layer, tile, Build(cache, layer.Id, tile, cancellationToken));
            }
        }
    }

    private static LayerBuild Build(LayerCache cache, string layerId, TileKey tile, CancellationToken cancellationToken)
    {
        try
        {
            return cache.Get(layerId, tile, cancellationToken);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or InvalidOperationException or KeyNotFoundException
            or ArgumentException or NotSupportedException or UnauthorizedAccessException)
        {
            return LayerBuild.Rejected(layerId, tile, $"build failed: {e.GetType().Name}: {e.Message}");
        }
    }
}
