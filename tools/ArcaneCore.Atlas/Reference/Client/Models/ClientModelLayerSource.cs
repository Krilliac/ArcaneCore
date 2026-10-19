using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ArcaneCore.Atlas.Core;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Atlas.Reference.Client.Models;

/// <summary>
/// Builds <c>ref.wmo</c>, <c>ref.m2.collision</c> and <c>ref.m2.render</c> from the client's own files: the tile's ADT
/// placements (from <see cref="IClientPlacementSource"/>) are resolved against the WMO and M2 files
/// (<see cref="IClientFileSource"/>) and drawn in world space. An instance is drawn only in the tile that contains its
/// placement position, once per unique id. Files and placements are looked up through <see cref="AtlasServices"/> at
/// call time, so a layer reports <see cref="TileState.Missing"/> with the reason while a client directory is not
/// configured. Thread-safe.
/// </summary>
public sealed partial class ClientModelLayerSource : ILayerSource
{
    private static readonly string[] LayerList = [LayerIds.RefWmo, LayerIds.RefM2Collision, LayerIds.RefM2Render];

    private readonly AtlasServices _services;
    private readonly ClientModelCache _cache;
    private readonly ILogger _log;

    public ClientModelLayerSource(AtlasServices services, ClientModelCache cache, ILogger log)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public IReadOnlyList<string> Layers => LayerList;

    /// <summary>
    /// ADT tiles of the map from the archives' file lists (<c>World\Maps\Dir\Dir_Y_X.adt</c> is server tile X, Y), plus the
    /// global tile when the map's WDT places a WMO.
    /// </summary>
    public IReadOnlyCollection<TileKey> AvailableTiles(string layerId, uint mapId)
    {
        RequireKnown(layerId);
        if (_services.Get<IClientFileSource>() is not { } files || _services.Get<IMapDirectory>() is not { } maps
            || !maps.TryGetMap(mapId, out ClientMap? map))
        {
            return [];
        }

        var tiles = new List<TileKey>();
        string prefix = $"World\\Maps\\{map.Directory}\\{map.Directory}_";
        foreach (string path in files.List(prefix))
        {
            // ref: Atlas WorldGrid.VMapTileFileName and VMapFormat.TileFileName: files name the grid pair as (tile Y, tile X)
            Match match = AdtName().Match(path[prefix.Length..]);
            if (match.Success
                && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int fileFirst)
                && int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int fileSecond))
            {
                var tile = new TileKey(mapId, fileSecond, fileFirst);
                if (tile.IsInGrid)
                {
                    tiles.Add(tile);
                }
            }
        }

        if (_services.Get<IClientPlacementSource>() is { } placements && HasGlobalPlacements(placements, mapId))
        {
            tiles.Add(TileKey.Global(mapId));
        }

        return tiles;
    }

    public TileStatus PeekStatus(string layerId, TileKey tile)
    {
        RequireKnown(layerId);
        RequireValid(tile);
        if (Unavailable(layerId, tile) is { } unavailable)
        {
            return unavailable.Status;
        }

        if (!tile.IsGlobal && _services.Get<IClientFileSource>() is { } files && _services.Get<IMapDirectory>() is { } maps
            && maps.TryGetMap(tile.MapId, out ClientMap? map) && !files.Exists(AdtPath(map, tile)))
        {
            return new TileStatus(layerId, tile, TileState.Missing, $"no {AdtPath(map, tile)} in the client files.");
        }

        return new TileStatus(layerId, tile, TileState.Unknown);
    }

    public LayerBuild Build(string layerId, TileKey tile, CancellationToken cancellationToken)
    {
        RequireKnown(layerId);
        RequireValid(tile);
        if (Unavailable(layerId, tile) is { } unavailable)
        {
            return unavailable;
        }

        IClientFileSource files = _services.Require<IClientFileSource>();
        IClientPlacementSource placementSource = _services.Require<IClientPlacementSource>();
        IReadOnlyList<ClientPlacement>? placements;
        try
        {
            placements = placementSource.Placements(tile);
        }
        catch (InvalidDataException e)
        {
            return LayerBuild.Rejected(layerId, tile, $"the tile's placements could not be read: {e.Message}");
        }

        if (placements is null)
        {
            return LayerBuild.Missing(layerId, tile, tile.IsGlobal ? "the map's WDT has no global WMO." : "the client has no ADT file for this tile.");
        }

        try
        {
            return BuildFrom(layerId, tile, placements, files, cancellationToken);
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            _log.LogWarning("{Layer} {Tile}: model files could not be read: {Message}", layerId, tile, e.Message);
            return LayerBuild.Rejected(layerId, tile, $"model files could not be read: {e.Message}");
        }
    }

    private static string AdtPath(ClientMap map, TileKey tile)
        => string.Create(CultureInfo.InvariantCulture, $"World\\Maps\\{map.Directory}\\{map.Directory}_{tile.Y}_{tile.X}.adt");

    private static bool HasGlobalPlacements(IClientPlacementSource placements, uint mapId)
    {
        try
        {
            return placements.Placements(TileKey.Global(mapId)) is { Count: > 0 };
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private LayerBuild? Unavailable(string layerId, TileKey tile)
    {
        if (_services.Get<IClientFileSource>() is null)
        {
            return LayerBuild.Missing(layerId, tile, "no client files are configured (the client file source is not registered).");
        }

        if (_services.Get<IClientPlacementSource>() is null)
        {
            return LayerBuild.Missing(layerId, tile, "no client placement source is registered, so model placements cannot be read.");
        }

        return null;
    }

    private LayerBuild BuildFrom(string layerId, TileKey tile, IReadOnlyList<ClientPlacement> placements, IClientFileSource files, CancellationToken cancellationToken)
    {
        PlanScope scope = layerId == LayerIds.RefWmo ? PlanScope.Wmo : PlanScope.M2 | PlanScope.Doodads;
        TilePlan plan = ModelPlanner.Plan(tile.MapId, tile, placements, scope, files, _cache, cancellationToken);
        var geometry = new PartedGeometry(layerId, tile, WorldGrid.Origin(tile));
        long triangles = 0;
        int instances = 0;
        int skipped = 0;
        if (layerId == LayerIds.RefWmo)
        {
            foreach (PlannedWmo wmo in plan.Wmos)
            {
                int added = ModelGeometry.AddWmo(geometry, wmo, cancellationToken);
                triangles += added;
                instances += added > 0 ? 1 : 0;
                skipped += wmo.SkipReason is null ? 0 : 1;
            }
        }
        else
        {
            bool collision = layerId == LayerIds.RefM2Collision;
            foreach (PlannedM2 m2 in plan.M2s)
            {
                int added = collision
                    ? ModelGeometry.AddM2Collision(geometry, m2, cancellationToken)
                    : ModelGeometry.AddM2Render(geometry, m2, cancellationToken);
                triangles += added;
                instances += added > 0 ? 1 : 0;
                skipped += m2.SkipReason is null ? 0 : 1;
            }
        }

        JsonObject stats = plan.ToJson();
        stats["instancesDrawn"] = instances;
        stats["extractorSkippedInstances"] = skipped;
        stats["trianglesDrawn"] = triangles;
        IReadOnlyList<GeometryChunk> chunks = geometry.Finish(stats);

        string? detail = plan.Problems.Total > 0
            ? $"{plan.Problems.Total} model file(s) unavailable ({plan.Problems.Missing} missing, {plan.Problems.Rejected} rejected): {string.Join("; ", plan.Problems.Samples)}"
            : null;
        if (chunks.Count == 0 && plan.Problems.Total > 0)
        {
            // Nothing could be drawn and files were unreadable: never present that as empty space.
            return LayerBuild.Rejected(layerId, tile, detail!);
        }

        return LayerBuild.Of(layerId, tile, chunks, detail);
    }

    private static void RequireKnown(string layerId)
    {
        if (!LayerList.Contains(layerId, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Layer '{layerId}' is not built by the client model source.", nameof(layerId));
        }
    }

    private static void RequireValid(TileKey tile)
    {
        if (!tile.IsValid)
        {
            throw new ArgumentException($"Tile {tile} is outside the map grid.", nameof(tile));
        }
    }

    [GeneratedRegex(@"^(\d+)_(\d+)\.adt$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AdtName();
}
