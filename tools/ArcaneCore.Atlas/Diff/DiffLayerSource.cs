using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Client.Models;
using ArcaneCore.Atlas.Reference.Client.Terrain;
using ArcaneCore.Atlas.Reference.Navmesh;
using ArcaneCore.Atlas.Server;

namespace ArcaneCore.Atlas.Diff;

/// <summary>
/// Builds the three comparison layers (<see cref="LayerIds.DiffTerrain"/>, <see cref="LayerIds.DiffModels"/>,
/// <see cref="LayerIds.DiffNav"/>) by comparing the server services against the reference services registered in
/// <see cref="AtlasServices"/>.
/// </summary>
public sealed class DiffLayerSource : ILayerSource
{
    private static readonly string[] HandledLayers =
    [
        LayerIds.DiffTerrain,
        LayerIds.DiffModels,
        LayerIds.DiffNav,
    ];

    private readonly AtlasServices _services;

    /// <summary>Creates the diff layer source over <paramref name="services"/>.</summary>
    public DiffLayerSource(AtlasServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Layers => HandledLayers;

    /// <inheritdoc />
    public IReadOnlyCollection<TileKey> AvailableTiles(string layerId, uint mapId)
    {
        RequireLayer(layerId);
        var set = new SortedSet<TileKey>(TileComparer.Instance);
        ServerData? server = _services.Get<ServerData>();
        if (layerId == LayerIds.DiffTerrain)
        {
            if (server is not null)
            {
                foreach (TileKey tile in server.TerrainTiles(mapId))
                {
                    set.Add(tile);
                }
            }

            if (_services.Get<ClientTerrainService>() is { } clientTerrain)
            {
                foreach (TileKey tile in clientTerrain.AvailableTiles(mapId))
                {
                    set.Add(tile);
                }
            }
        }
        else if (layerId == LayerIds.DiffModels)
        {
            if (server is not null)
            {
                foreach (TileKey tile in server.VMapTiles(mapId))
                {
                    set.Add(tile);
                }
            }

            if (_services.Get<ClientTerrainService>() is { } clientTerrain)
            {
                foreach (TileKey tile in clientTerrain.AvailableTiles(mapId))
                {
                    set.Add(tile);
                }
            }
        }
        else
        {
            if (server is not null)
            {
                foreach (TileKey tile in server.NavTiles(mapId))
                {
                    set.Add(tile);
                }
            }

            if (_services.Get<ReferenceNavMeshService>() is { } nav)
            {
                foreach (TileKey tile in nav.AvailableTiles(mapId))
                {
                    set.Add(tile);
                }
            }
        }

        return [.. set];
    }

    /// <inheritdoc />
    public TileStatus PeekStatus(string layerId, TileKey tile)
    {
        RequireLayer(layerId);
        RequireValid(tile);
        if (layerId != LayerIds.DiffModels && tile.IsGlobal)
        {
            return new TileStatus(layerId, tile, TileState.NotApplicable, $"{layerId} compares per-grid tiles; the global pseudo-tile does not apply.");
        }

        ServerData? server = _services.Get<ServerData>();
        if (server is null)
        {
            return new TileStatus(layerId, tile, TileState.Missing, "No ServerData service is registered.");
        }

        if (layerId == LayerIds.DiffTerrain)
        {
            ClientTerrainService? client = _services.Get<ClientTerrainService>();
            if (client is null)
            {
                return new TileStatus(layerId, tile, TileState.Missing, "No ClientTerrainService is registered.");
            }

            if (!server.TerrainFileExists(tile))
            {
                return new TileStatus(layerId, tile, TileState.Missing, "Server .map file is absent.");
            }

            if (client.PeekAdt(tile) is { State: TileState.Missing or TileState.Rejected } adt)
            {
                return new TileStatus(layerId, tile, adt.State, adt.Detail);
            }

            return new TileStatus(layerId, tile, TileState.Unknown);
        }

        if (layerId == LayerIds.DiffModels)
        {
            if (_services.Get<IClientFileSource>() is null || _services.Get<IClientPlacementSource>() is null)
            {
                return new TileStatus(layerId, tile, TileState.Missing, "No client file or placement source is registered.");
            }

            if (!server.VMapTreeFileExists(tile.MapId))
            {
                return new TileStatus(layerId, tile, TileState.Missing, "no .vmtree file");
            }

            if (!tile.IsGlobal && !server.VMapTileFileExists(tile))
            {
                return new TileStatus(layerId, tile, TileState.Missing, "no .vmtile file");
            }

            return new TileStatus(layerId, tile, TileState.Unknown);
        }

        ReferenceNavMeshService? navService = _services.Get<ReferenceNavMeshService>();
        if (navService is null)
        {
            return new TileStatus(layerId, tile, TileState.Missing, "No ReferenceNavMeshService is registered.");
        }

        if (!server.NavFileExists(tile))
        {
            return new TileStatus(layerId, tile, TileState.Missing, "no .mmtile file");
        }

        return new TileStatus(layerId, tile, TileState.Unknown);
    }

    /// <inheritdoc />
    public LayerBuild Build(string layerId, TileKey tile, CancellationToken cancellationToken)
    {
        RequireLayer(layerId);
        RequireValid(tile);
        return layerId switch
        {
            LayerIds.DiffTerrain => TerrainDiff.BuildLayer(_services, tile, cancellationToken),
            LayerIds.DiffModels => ModelDiff.BuildLayer(_services, tile, cancellationToken),
            _ => NavDiff.BuildLayer(_services, tile, cancellationToken),
        };
    }

    private static void RequireLayer(string layerId)
    {
        if (layerId != LayerIds.DiffTerrain && layerId != LayerIds.DiffModels && layerId != LayerIds.DiffNav)
        {
            throw new ArgumentException($"Layer '{layerId}' is not built by DiffLayerSource.", nameof(layerId));
        }
    }

    private static void RequireValid(TileKey tile)
    {
        if (!tile.IsValid)
        {
            throw new ArgumentException($"Tile {tile} is outside the map grid.", nameof(tile));
        }
    }

    private sealed class TileComparer : IComparer<TileKey>
    {
        public static readonly TileComparer Instance = new();

        public int Compare(TileKey a, TileKey b)
        {
            int c = a.MapId.CompareTo(b.MapId);
            if (c != 0)
            {
                return c;
            }

            if (a.IsGlobal != b.IsGlobal)
            {
                return a.IsGlobal ? -1 : 1;
            }

            c = a.X.CompareTo(b.X);
            return c != 0 ? c : a.Y.CompareTo(b.Y);
        }
    }
}
