using System.Diagnostics;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Atlas.Reference.Client.Terrain;

/// <summary>
/// Builds <c>ref.terrain</c> and <c>ref.liquid</c> from the client's ADT files. Thread-safe (the parsed files come from
/// <see cref="ClientTerrainService"/>, which caches them; each build owns its geometry). A tile the client files lack is
/// <see cref="TileState.Missing"/> with the reason; a file the reader refuses is <see cref="TileState.Rejected"/>
/// with the reader's message. Refusals are logged at Warning or below.
/// </summary>
/// <param name="service">The shared file and cache service.</param>
/// <param name="log">Logger for refused files.</param>
public sealed class ClientTerrainLayerSource(ClientTerrainService service, ILogger log) : ILayerSource
{
    private static readonly string[] Ids = [LayerIds.RefTerrain, LayerIds.RefLiquid];

    private readonly ClientTerrainService _service = service ?? throw new ArgumentNullException(nameof(service));
    private readonly ILogger _log = log ?? throw new ArgumentNullException(nameof(log));

    /// <inheritdoc />
    public IReadOnlyList<string> Layers => Ids;

    /// <summary>
    /// The tiles the map's cached WDT lists whose ADT passes an <see cref="IClientFileSource.Exists"/> check (no ADT is
    /// parsed). Never includes the global tile (terrain has none).
    /// </summary>
    public IReadOnlyCollection<TileKey> AvailableTiles(string layerId, uint mapId)
    {
        CheckLayer(layerId);
        return _service.AvailableTiles(mapId);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Cheap: it never reads an ADT. Missing and Rejected are reported when the WDT or an earlier load already settled
    /// them; every other tile is <see cref="TileState.Unknown"/> until it is built.
    /// </remarks>
    public TileStatus PeekStatus(string layerId, TileKey tile)
    {
        CheckLayer(layerId);
        CheckTile(tile);
        if (tile.IsGlobal)
        {
            return new TileStatus(layerId, tile, TileState.NotApplicable, GlobalDetail);
        }

        AdtLoad? known = _service.PeekAdt(tile);
        return known is { State: TileState.Missing or TileState.Rejected }
            ? new TileStatus(layerId, tile, known.State, known.Detail)
            : new TileStatus(layerId, tile, TileState.Unknown);
    }

    /// <inheritdoc />
    public LayerBuild Build(string layerId, TileKey tile, CancellationToken cancellationToken)
    {
        CheckLayer(layerId);
        CheckTile(tile);
        cancellationToken.ThrowIfCancellationRequested();
        if (tile.IsGlobal)
        {
            return new LayerBuild(new TileStatus(layerId, tile, TileState.NotApplicable, GlobalDetail), []);
        }

        AdtLoad load = _service.LoadAdt(tile);
        if (load.File is not { } adt)
        {
            return load.State == TileState.Rejected
                ? LayerBuild.Rejected(layerId, tile, load.Detail ?? "The ADT was refused.")
                : LayerBuild.Missing(layerId, tile, load.Detail);
        }

        long start = Stopwatch.GetTimestamp();
        try
        {
            GeometryChunk? chunk = layerId == LayerIds.RefTerrain
                ? ClientTerrainGeometry.BuildTerrain(tile, adt, _service.AreaName, cancellationToken)
                : ClientTerrainGeometry.BuildLiquid(tile, adt, cancellationToken);
            if (chunk is null)
            {
                return LayerBuild.Empty(layerId, tile, $"{load.Path} shows no liquid square.");
            }

            chunk.Stats["path"] = load.Path;
            chunk.Stats["archive"] = load.Archive;
            chunk.Stats["bytes"] = adt.ByteLength;
            chunk.Stats["buildMs"] = Math.Round(Stopwatch.GetElapsedTime(start).TotalMilliseconds, 3);
            return LayerBuild.Of(layerId, tile, [chunk]);
        }
        catch (InvalidDataException e)
        {
            _log.LogWarning("Client terrain: {Layer} {Tile} was refused: {Message}", layerId, tile, e.Message);
            return LayerBuild.Rejected(layerId, tile, $"{load.Path}: {e.Message}");
        }
    }

    private static string GlobalDetail => "Terrain has no untiled content; the global tile only holds a WMO.";

    private static void CheckLayer(string layerId)
    {
        if (layerId != LayerIds.RefTerrain && layerId != LayerIds.RefLiquid)
        {
            throw new ArgumentException($"The client terrain source does not build layer '{layerId}'.", nameof(layerId));
        }
    }

    private static void CheckTile(TileKey tile)
    {
        if (!tile.IsValid)
        {
            throw new ArgumentException($"Tile {tile} is outside the 64x64 grid.", nameof(tile));
        }
    }
}
