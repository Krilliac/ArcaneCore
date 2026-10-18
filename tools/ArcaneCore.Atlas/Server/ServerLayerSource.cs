using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Server;

/// <summary>
/// Base of the server-view layer sources: layer and tile validation, the shared <see cref="ServerData"/>, and the helpers every
/// source uses to report a tile that cannot be drawn. Sources are thread-safe; they hold no state besides the data caches.
/// </summary>
public abstract class ServerLayerSource : ILayerSource
{
    private readonly HashSet<string> _layers;

    /// <summary>Creates a source of <paramref name="layers"/> over <paramref name="data"/>.</summary>
    protected ServerLayerSource(ServerData data, IReadOnlyList<string> layers)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(layers);
        Data = data;
        Layers = layers;
        _layers = [.. layers];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Layers { get; }

    /// <summary>The parsed-file caches shared by every server source.</summary>
    protected ServerData Data { get; }

    /// <inheritdoc />
    public IReadOnlyCollection<TileKey> AvailableTiles(string layerId, uint mapId)
    {
        RequireLayer(layerId);
        return ListTiles(layerId, mapId);
    }

    /// <inheritdoc />
    public TileStatus PeekStatus(string layerId, TileKey tile)
    {
        RequireLayer(layerId);
        RequireTile(tile);
        return Peek(layerId, tile);
    }

    /// <inheritdoc />
    public LayerBuild Build(string layerId, TileKey tile, CancellationToken cancellationToken)
    {
        RequireLayer(layerId);
        RequireTile(tile);
        cancellationToken.ThrowIfCancellationRequested();
        return BuildTile(layerId, tile, cancellationToken);
    }

    /// <summary>The tiles of the map that have a data file for the layer (directory listings only).</summary>
    protected abstract IReadOnlyCollection<TileKey> ListTiles(string layerId, uint mapId);

    /// <summary>The tile's state when it is known without parsing, else <see cref="TileState.Unknown"/>.</summary>
    protected abstract TileStatus Peek(string layerId, TileKey tile);

    /// <summary>Builds a valid layer of a valid tile.</summary>
    protected abstract LayerBuild BuildTile(string layerId, TileKey tile, CancellationToken cancellationToken);

    /// <summary>A tile the layer has no meaning for.</summary>
    protected static LayerBuild NotApplicable(string layerId, TileKey tile, string detail)
        => new(new TileStatus(layerId, tile, TileState.NotApplicable, detail), []);

    /// <summary>The status of a layer whose file is missing, rejected or not yet read, from the cache or a directory lookup.</summary>
    protected static TileStatus StatusOf<T>(string layerId, TileKey tile, bool fileExists, bool cached, ParsedFile<T> parsed, string fileKind)
        where T : class
    {
        if (cached)
        {
            return new TileStatus(layerId, tile, parsed.State == TileState.Ok ? TileState.Unknown : parsed.State, parsed.Detail);
        }

        return fileExists
            ? new TileStatus(layerId, tile, TileState.Unknown, $"{fileKind} not read yet")
            : new TileStatus(layerId, tile, TileState.Missing, $"no {fileKind}");
    }

    /// <summary>Converts a failed parse into the layer result for it.</summary>
    protected static LayerBuild FromFailure<T>(string layerId, TileKey tile, ParsedFile<T> parsed)
        where T : class
        => parsed.State == TileState.Rejected
            ? LayerBuild.Rejected(layerId, tile, parsed.Detail ?? "the reader refused the file")
            : LayerBuild.Missing(layerId, tile, parsed.Detail);

    /// <summary>A JSON array <c>[x, y, z]</c> of a world point.</summary>
    protected static JsonArray Point(double x, double y, double z) => new(x, y, z);

    /// <summary>A JSON array <c>[x, y, z]</c> of a world point.</summary>
    protected static JsonArray Point(Vector3 p) => new(p.X, p.Y, p.Z);

    /// <summary><c>0x</c> followed by the value in upper-case hexadecimal.</summary>
    protected static string Hex(uint value) => "0x" + value.ToString("X", CultureInfo.InvariantCulture);

    private void RequireLayer(string layerId)
    {
        if (!_layers.Contains(layerId ?? throw new ArgumentNullException(nameof(layerId))))
        {
            throw new ArgumentException($"This source does not build layer '{layerId}'.", nameof(layerId));
        }
    }

    private static void RequireTile(TileKey tile)
    {
        if (!tile.IsValid)
        {
            throw new ArgumentException($"Tile {tile} is outside the map grid.", nameof(tile));
        }
    }
}

/// <summary>
/// Collects the geometry of one layer on one tile into chunks that stay under the wire limit. Call <see cref="RollIfFull"/> at
/// points where the next primitives do not need earlier vertices; a new part starts when the current one is past
/// <see cref="PartBytes"/> (the limit leaves room for attribute tables under <see cref="GeometryWire.MaxChunkBytes"/>).
/// <para>Thread affinity: single thread (a build owns its instance).</para>
/// </summary>
public sealed class ChunkParts
{
    /// <summary>Estimated size at which a part is closed.</summary>
    public const long PartBytes = 48L * 1024 * 1024;

    private readonly string _layerId;
    private readonly TileKey _tile;
    private readonly WorldPoint _origin;
    private readonly long _limit;
    private readonly List<GeometryBuilder> _parts = [];

    /// <summary>Starts with one empty part.</summary>
    public ChunkParts(string layerId, TileKey tile, WorldPoint origin, long limit = PartBytes)
    {
        _layerId = layerId;
        _tile = tile;
        _origin = origin;
        _limit = limit;
        _parts.Add(new GeometryBuilder(layerId, tile, origin, 0));
    }

    /// <summary>The part new primitives go to.</summary>
    public GeometryBuilder Current => _parts[^1];

    /// <summary>Parts started so far.</summary>
    public int PartCount => _parts.Count;

    /// <summary>Statistics copied into every chunk (the whole tile's counts, not the part's).</summary>
    public JsonObject Stats { get; } = [];

    /// <summary>Starts a new part when the current one is past the limit. Vertex indices do not carry over.</summary>
    public bool RollIfFull()
    {
        if (Current.EstimatedBytes < _limit)
        {
            return false;
        }

        _parts.Add(new GeometryBuilder(_layerId, _tile, _origin, _parts.Count));
        return true;
    }

    /// <summary>The non-empty parts as chunks.</summary>
    public IReadOnlyList<GeometryChunk> Build()
    {
        var chunks = new List<GeometryChunk>(_parts.Count);
        foreach (GeometryBuilder part in _parts)
        {
            if (part.IsEmpty)
            {
                continue;
            }

            foreach ((string key, JsonNode? value) in Stats)
            {
                part.Stats[key] = value?.DeepClone();
            }

            part.Stats["parts"] = _parts.Count;
            chunks.Add(part.Build());
        }

        return chunks;
    }
}
