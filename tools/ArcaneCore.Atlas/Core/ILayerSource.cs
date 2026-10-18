using System.Text.Json.Nodes;

namespace ArcaneCore.Atlas.Core;

/// <summary>What a layer has for one tile. A missing or unreadable file is never reported as empty space.</summary>
public enum TileState
{
    /// <summary>Data was read and produced geometry.</summary>
    Ok,

    /// <summary>The file exists and was read, and holds nothing for this layer (for example a tile without liquid).</summary>
    Empty,

    /// <summary>No file for this tile.</summary>
    Missing,

    /// <summary>The file exists but the reader refused it; <see cref="TileStatus.Detail"/> carries the reason.</summary>
    Rejected,

    /// <summary>Not built yet (status asked before the tile was read).</summary>
    Unknown,

    /// <summary>The layer has no meaning for this tile (for example a global-only map).</summary>
    NotApplicable,
}

/// <summary>Status of one layer on one tile.</summary>
public sealed record TileStatus(string LayerId, TileKey Tile, TileState State, string? Detail = null, long Primitives = 0)
{
    public JsonObject ToJson() => new()
    {
        ["layer"] = LayerId,
        ["tile"] = new JsonArray(Tile.MapId, Tile.X, Tile.Y),
        ["state"] = State.ToString(),
        ["detail"] = Detail,
        ["primitives"] = Primitives,
    };
}

/// <summary>
/// Produces geometry for one or more layers. Implementations are thread-safe (the host calls them from request
/// threads) and may cache parsed files; they never hold references to a live world daemon's state.
/// </summary>
public interface ILayerSource
{
    /// <summary>The layer ids (from <see cref="LayerIds"/>) this source builds.</summary>
    IReadOnlyList<string> Layers { get; }

    /// <summary>
    /// Tiles of <paramref name="mapId"/> for which this layer has a data file, from directory listings only (cheap).
    /// Includes <see cref="TileKey.Global(uint)"/> when the map has untiled content.
    /// </summary>
    IReadOnlyCollection<TileKey> AvailableTiles(string layerId, uint mapId);

    /// <summary>The tile's state without building geometry when that can be known cheaply; else <see cref="TileState.Unknown"/>.</summary>
    TileStatus PeekStatus(string layerId, TileKey tile);

    /// <summary>
    /// Builds the layer for a tile. Returns the chunks (empty when the tile has nothing) plus the tile's status.
    /// Throws <see cref="ArgumentException"/> only for an unknown layer or an invalid tile; data problems are reported
    /// through <see cref="LayerBuild.Status"/>.
    /// </summary>
    LayerBuild Build(string layerId, TileKey tile, CancellationToken cancellationToken);
}

/// <summary>The result of <see cref="ILayerSource.Build"/>.</summary>
public sealed record LayerBuild(TileStatus Status, IReadOnlyList<GeometryChunk> Chunks)
{
    public static LayerBuild Missing(string layerId, TileKey tile, string? detail = null)
        => new(new TileStatus(layerId, tile, TileState.Missing, detail), []);

    public static LayerBuild Rejected(string layerId, TileKey tile, string detail)
        => new(new TileStatus(layerId, tile, TileState.Rejected, detail), []);

    public static LayerBuild Empty(string layerId, TileKey tile, string? detail = null)
        => new(new TileStatus(layerId, tile, TileState.Empty, detail), []);

    public static LayerBuild Of(string layerId, TileKey tile, IReadOnlyList<GeometryChunk> chunks, string? detail = null)
    {
        long primitives = chunks.Sum(c => (long)c.TriangleCount + c.LineCount + c.PointCount);
        TileState state = primitives > 0 ? TileState.Ok : TileState.Empty;
        return new LayerBuild(new TileStatus(layerId, tile, state, detail, primitives), chunks);
    }
}
