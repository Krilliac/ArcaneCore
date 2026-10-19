using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Reference.Navmesh;

/// <summary>
/// Builds <see cref="LayerIds.RefNavPoly"/> and <see cref="LayerIds.RefNavLinks"/> from the DotRecast mesh: the same
/// <c>.mmtile</c> files the server reads, parsed by DotRecast instead of the server's reader.
/// </summary>
/// <remarks>
/// Thread safety: any thread (all state is in <see cref="ReferenceNavMeshService"/>, which serialises tile changes). Building a tile
/// makes it and its eight neighbours resident first, so the links DotRecast built across tile borders and to off-mesh connections
/// are complete and the result does not depend on the order tiles were loaded in.
/// </remarks>
public sealed class ReferenceNavLayerSource : ILayerSource
{
    private static readonly string[] Handled = [LayerIds.RefNavPoly, LayerIds.RefNavLinks];

    private readonly ReferenceNavMeshService _service;
    private readonly long _maxChunkBytes;

    /// <summary>Creates the source over <paramref name="service"/>; a chunk is split into parts past <paramref name="maxChunkBytes"/>.</summary>
    public ReferenceNavLayerSource(ReferenceNavMeshService service, long maxChunkBytes = GeometryWire.MaxChunkBytes / 4)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChunkBytes, 1024L);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxChunkBytes, GeometryWire.MaxChunkBytes / 2);
        _service = service;
        _maxChunkBytes = maxChunkBytes;
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> Layers => Handled;

    /// <inheritdoc/>
    public IReadOnlyCollection<TileKey> AvailableTiles(string layerId, uint mapId)
    {
        RequireLayer(layerId);
        return _service.AvailableTiles(mapId);
    }

    /// <inheritdoc/>
    public TileStatus PeekStatus(string layerId, TileKey tile)
    {
        RequireLayer(layerId);
        RequireValid(tile);
        if (tile.IsGlobal)
        {
            return NotApplicable(layerId, tile);
        }

        NavMapOpen open = _service.OpenMap(tile.MapId);
        if (open.Map is not { } map)
        {
            return new TileStatus(layerId, tile, ToState(open.State), open.Detail);
        }

        return map.Peek(tile) is { } known
            ? new TileStatus(layerId, tile, known.State == NavTileState.Ok ? TileState.Unknown : ToState(known.State), known.Detail)
            : new TileStatus(layerId, tile, TileState.Unknown);
    }

    /// <inheritdoc/>
    public LayerBuild Build(string layerId, TileKey tile, CancellationToken cancellationToken)
    {
        RequireLayer(layerId);
        RequireValid(tile);
        if (tile.IsGlobal)
        {
            return new LayerBuild(NotApplicable(layerId, tile), []);
        }

        NavMapOpen open = _service.OpenMap(tile.MapId);
        if (open.Map is not { } map)
        {
            return open.State == NavTileState.Rejected
                ? LayerBuild.Rejected(layerId, tile, open.Detail ?? "navmesh parameters refused")
                : LayerBuild.Missing(layerId, tile, open.Detail);
        }

        return map.Read(Neighbourhood(tile), view => BuildTile(view, layerId, tile, cancellationToken));
    }

    /// <summary>The tile and its existing grid neighbours (3 x 3), the set whose links a tile's geometry depends on.</summary>
    internal static List<TileKey> Neighbourhood(TileKey tile)
    {
        var tiles = new List<TileKey>(9) { tile };
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                var neighbour = new TileKey(tile.MapId, tile.X + dx, tile.Y + dy);
                if ((dx != 0 || dy != 0) && neighbour.IsInGrid)
                {
                    tiles.Add(neighbour);
                }
            }
        }

        return tiles;
    }

    private LayerBuild BuildTile(NavMapView view, string layerId, TileKey tile, CancellationToken cancellationToken)
    {
        NavTileOutcome outcome = view.Tiles[tile];
        if (outcome.State == NavTileState.Missing)
        {
            return LayerBuild.Missing(layerId, tile, outcome.Detail);
        }

        if (outcome.State == NavTileState.Rejected)
        {
            return LayerBuild.Rejected(layerId, tile, outcome.Detail ?? "tile refused");
        }

        ResidentNavTile resident = view.Resident(tile)
            ?? throw new InvalidOperationException($"Tile {tile} reported Ok but is not resident.");
        if (layerId == LayerIds.RefNavPoly)
        {
            return LayerBuild.Of(layerId, tile, RefNavPolyGeometry.Build(layerId, tile, resident, _maxChunkBytes, cancellationToken));
        }

        List<NavLink> links = NavLinkInventory.Collect(view, resident);
        NavLinkCounts counts = NavLinkInventory.Count(links, resident.Tile);
        string[] refused = [.. view.Tiles.Where(t => t.Key != tile && t.Value.State == NavTileState.Rejected).Select(t => $"{t.Key.X},{t.Key.Y}")];
        string? detail = refused.Length == 0 ? null : $"Neighbour tile(s) {string.Join("; ", refused)} were refused, so links into them are absent.";
        return LayerBuild.Of(layerId, tile, RefNavLinkGeometry.Build(layerId, tile, resident, links, counts, _maxChunkBytes, cancellationToken), detail);
    }

    private static TileStatus NotApplicable(string layerId, TileKey tile)
        => new(layerId, tile, TileState.NotApplicable, "Navmesh layers are tiled; the map's global pseudo-tile has no navmesh.");

    private static TileState ToState(NavTileState state) => state switch
    {
        NavTileState.Ok => TileState.Ok,
        NavTileState.Missing => TileState.Missing,
        _ => TileState.Rejected,
    };

    private static void RequireLayer(string layerId)
    {
        if (layerId != LayerIds.RefNavPoly && layerId != LayerIds.RefNavLinks)
        {
            throw new ArgumentException($"Layer '{layerId}' is not built by the DotRecast navmesh source.", nameof(layerId));
        }
    }

    private static void RequireValid(TileKey tile)
    {
        if (!tile.IsValid)
        {
            throw new ArgumentException($"Tile {tile} is outside the map grid.", nameof(tile));
        }
    }
}
