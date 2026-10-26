using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace ArcaneCore.Atlas.Core;

/// <summary>
/// In-process publish/subscribe for pushes to connected UIs (the host forwards every event over its WebSocket):
/// view changes, the automation log, live frames. Handlers run on the publisher's thread and must not block.
/// </summary>
public sealed class EventHub
{
    private readonly ConcurrentDictionary<long, Action<string, JsonNode?>> _subscribers = new();
    private long _next;

    /// <summary>Topics the host and UI know. Others are allowed (they are forwarded as-is).</summary>
    public static class Topics
    {
        /// <summary>
        /// <c>{"version","origin","view","cameraChanged","followOrigin"}</c> after every view change; <c>cameraChanged</c> is true when the
        /// change altered the camera or the followed entity, so a UI that flies its own camera knows whether the event concerns it.
        /// <c>followOrigin</c> is the origin that started the current follow (null when nothing is followed): the live feed's per-frame
        /// camera moves carry origin <c>live</c> but belong to whoever asked to follow.
        /// </summary>
        public const string View = "view";

        /// <summary><c>CommandResult.ToJson()</c> plus <c>"origin"</c> after every command.</summary>
        public const string Command = "command";

        /// <summary>One map-feed frame as received (doc section 9).</summary>
        public const string LiveFrame = "live.frame";

        /// <summary><c>{"connected","pid","sha","detail"}</c> when the live connection changes.</summary>
        public const string LiveStatus = "live.status";

        /// <summary><c>{"layer","tile":[map,x,y]}</c> when a cached tile is dropped and should be refetched.</summary>
        public const string TileInvalidated = "tile.invalidated";
    }

    public IDisposable Subscribe(Action<string, JsonNode?> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        long id = Interlocked.Increment(ref _next);
        _subscribers[id] = handler;
        return new Subscription(this, id);
    }

    public void Publish(string topic, JsonNode? payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        foreach (Action<string, JsonNode?> handler in _subscribers.Values)
        {
            handler(topic, payload);
        }
    }

    private sealed class Subscription(EventHub hub, long id) : IDisposable
    {
        public void Dispose() => hub._subscribers.TryRemove(id, out _);
    }
}

/// <summary>
/// Builds each (layer, tile) once and keeps the results, bounded by wire size. Concurrent requests for the same tile
/// share one build. Every consumer (HTTP tiles, picking, headless render, diffs, export) goes through it.
/// </summary>
public sealed class LayerCache(LayerRegistry registry, long maxBytes = LayerCache.DefaultMaxBytes)
{
    public const long DefaultMaxBytes = 1L * 1024 * 1024 * 1024;

    private readonly CostBoundedLazyCache<(string Layer, TileKey Tile), Entry> _cache = new(maxBytes);

    private sealed record Entry(LayerBuild Build, IReadOnlyList<byte[]> Wire);

    public long Bytes => _cache.Cost;

    /// <summary>The build for a tile (from cache when present). Unknown layers throw <see cref="KeyNotFoundException"/>.</summary>
    public LayerBuild Get(string layerId, TileKey tile, CancellationToken cancellationToken)
        => Resolve(layerId, tile, cancellationToken).Build;

    /// <summary>The ATG1 bytes of each chunk of a tile, in part order.</summary>
    public IReadOnlyList<byte[]> GetWire(string layerId, TileKey tile, CancellationToken cancellationToken)
        => Resolve(layerId, tile, cancellationToken).Wire;

    /// <summary>The status without building when the tile is not cached.</summary>
    public TileStatus Peek(string layerId, TileKey tile)
    {
        if (_cache.TryPeek((layerId, tile), out Entry? entry))
        {
            return entry.Build.Status;
        }

        return Source(layerId).PeekStatus(layerId, tile);
    }

    /// <summary>Drops cached tiles (all, one layer, or one tile) so the next request rebuilds them.</summary>
    public int Invalidate(string? layerId = null, TileKey? tile = null)
        => _cache.Invalidate(key => (layerId is null || key.Layer == layerId) && (tile is null || key.Tile == tile));

    private Entry Resolve(string layerId, TileKey tile, CancellationToken cancellationToken)
    {
        ILayerSource source = Source(layerId);
        if (!tile.IsValid)
        {
            throw new ArgumentException($"Tile {tile} is outside the map grid.");
        }

        return _cache.GetOrLoad((layerId, tile), () => Create(source, layerId, tile, cancellationToken), cancellationToken: cancellationToken);
    }

    private static (Entry Value, long Cost) Create(ILayerSource source, string layerId, TileKey tile, CancellationToken cancellationToken)
    {
        LayerBuild build = source.Build(layerId, tile, cancellationToken);
        byte[][] wire = [.. build.Chunks.OrderBy(c => c.Part).Select(GeometryWire.Write)];
        long bytes = wire.Sum(w => (long)w.Length) + 256;
        return (new Entry(build, wire), bytes);
    }

    private ILayerSource Source(string layerId)
        => registry.TryGet(layerId, out ILayerSource source) ? source : throw new KeyNotFoundException($"No source builds layer '{layerId}'.");
}

/// <summary>A rendered image.</summary>
/// <param name="Png">PNG bytes.</param>
/// <param name="Stats">Tiles drawn, primitives, missing/rejected tiles, milliseconds.</param>
public sealed record RenderedImage(byte[] Png, int Width, int Height, JsonObject Stats);

/// <summary>The CPU renderer behind headless <c>capture</c> (registered by the render module).</summary>
public interface IHeadlessRenderer
{
    /// <summary>Renders the visible tiled layers of <paramref name="view"/> around its camera target.</summary>
    RenderedImage Render(ViewState view, int width, int height, CancellationToken cancellationToken);
}

/// <summary>What a placement places.</summary>
public enum PlacementKind
{
    /// <summary>A WMO (ADT MODF or WDT global MODF).</summary>
    Wmo,

    /// <summary>An M2 doodad (ADT MDDF).</summary>
    M2,
}

/// <summary>
/// One model placement as stored in the client file, before any transform: <see cref="Position"/>,
/// <see cref="Rotation"/> (degrees) and <see cref="Scale"/> are the raw fields, so the model lane owns (and cites)
/// the placement-to-world transform. <see cref="Scale"/> is 1 for WMOs (MODF has no scale in 1.12.1).
/// </summary>
public sealed record ClientPlacement(
    PlacementKind Kind,
    string ModelPath,
    uint UniqueId,
    WorldPoint Position,
    WorldPoint Rotation,
    double Scale,
    ushort Flags,
    ushort DoodadSet,
    TileKey Tile);

/// <summary>Model placements from the client's ADT/WDT files (registered by the client terrain module).</summary>
public interface IClientPlacementSource
{
    /// <summary>
    /// Placements stored in the ADT for <paramref name="tile"/> (server tile indices), or the WDT's global WMO for
    /// <see cref="TileKey.Global(uint)"/>. A WMO spanning tiles appears in each ADT that lists it, with the same
    /// <see cref="ClientPlacement.UniqueId"/>. Null when the file is missing; throws <see cref="InvalidDataException"/>
    /// when it is corrupt.
    /// </summary>
    IReadOnlyList<ClientPlacement>? Placements(TileKey tile);
}
