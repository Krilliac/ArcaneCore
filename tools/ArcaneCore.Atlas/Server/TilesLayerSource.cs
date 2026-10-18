using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Game.Maps.Terrain;

namespace ArcaneCore.Atlas.Server;

/// <summary>
/// The <c>tiles</c> layer: one closed outline per terrain tile at the terrain's border height, coloured by what the tile's data files hold.
/// A tile is listed when any of its <c>.map</c>, <c>.vmtile</c> or <c>.mmtile</c> files exists. Each file is read with the server's reader, so
/// the state is what the server would find: <c>rejected</c> when any file exists but its reader refuses it, <c>missing</c> when none exists,
/// <c>empty</c> when every file that exists holds nothing (a terrain tile without heights, a model tile without spawns, a navmesh tile without
/// polygons), and <c>ok</c> otherwise. The attribute's <c>bits</c> say which files are present and usable (1 terrain, 2 models, 4 navmesh).
/// </summary>
public sealed class TilesLayerSource : ServerLayerSource
{
    /// <summary>The <c>bits</c> value of a usable <c>.map</c> file.</summary>
    public const int TerrainBit = 1;

    /// <summary>The <c>bits</c> value of a usable <c>.vmtile</c> file.</summary>
    public const int ModelBit = 2;

    /// <summary>The <c>bits</c> value of a usable <c>.mmtile</c> file.</summary>
    public const int NavBit = 4;

    private const int Resolution = WorldGrid.MapResolution;

    /// <summary>Creates the source over <paramref name="data"/>.</summary>
    public TilesLayerSource(ServerData data)
        : base(data, [LayerIds.Tiles])
    {
    }

    /// <inheritdoc />
    protected override IReadOnlyCollection<TileKey> ListTiles(string layerId, uint mapId)
    {
        var all = new HashSet<TileKey>();
        all.UnionWith(Data.TerrainTiles(mapId));
        all.UnionWith(Data.VMapTileKeys(mapId));
        all.UnionWith(Data.NavTiles(mapId));
        var list = new List<TileKey>(all);
        list.Sort(static (l, r) => l.X != r.X ? l.X.CompareTo(r.X) : l.Y.CompareTo(r.Y));
        return list;
    }

    /// <inheritdoc />
    protected override TileStatus Peek(string layerId, TileKey tile)
    {
        if (!tile.IsInGrid)
        {
            return new TileStatus(layerId, tile, TileState.NotApplicable, "the tile outline exists for grid tiles only");
        }

        bool terrain = Data.TerrainFileExists(tile);
        bool models = Data.VMapTileFileExists(tile);
        bool nav = Data.NavFileExists(tile);
        if (!terrain && !models && !nav)
        {
            return new TileStatus(layerId, tile, TileState.Missing, "no .map, .vmtile or .mmtile file");
        }

        return new TileStatus(layerId, tile, TileState.Unknown, $"files present: {Present(terrain, models, nav)}");
    }

    /// <inheritdoc />
    protected override LayerBuild BuildTile(string layerId, TileKey tile, CancellationToken cancellationToken)
    {
        if (!tile.IsInGrid)
        {
            return NotApplicable(layerId, tile, "the tile outline exists for grid tiles only");
        }

        ParsedFile<TerrainTile> terrain = Data.TerrainFile(tile);
        cancellationToken.ThrowIfCancellationRequested();
        FileReport terrainReport = ReportTerrain(terrain);

        FileReport modelReport = ReportModels(tile);
        cancellationToken.ThrowIfCancellationRequested();

        ParsedFile<NavTileData> nav = Data.NavFile(tile);
        FileReport navReport = ReportNav(nav);

        FileReport[] reports = [terrainReport, modelReport, navReport];
        string colorKey;
        if (reports.Any(r => r.State == TileState.Rejected))
        {
            colorKey = ColorKeys.Rejected;
        }
        else if (reports.All(r => r.State is TileState.Missing or TileState.NotApplicable))
        {
            colorKey = ColorKeys.Missing;
        }
        else if (reports.Where(r => r.State is not (TileState.Missing or TileState.NotApplicable)).All(r => r.State == TileState.Empty))
        {
            colorKey = ColorKeys.Empty;
        }
        else
        {
            colorKey = ColorKeys.Ok;
        }

        int bits = (terrainReport.Usable ? TerrainBit : 0) | (modelReport.Usable ? ModelBit : 0) | (navReport.Usable ? NavBit : 0);
        WorldPoint origin = WorldGrid.Origin(tile);
        var builder = new GeometryBuilder(LayerIds.Tiles, tile, origin);
        uint first = 0;
        uint previous = 0;
        int count = 0;

        void Corner(int r, int c)
        {
            double h = terrain.IsOk ? TerrainLayerSource.DrawHeight(terrain.Value, r, c) : 0;
            uint v = builder.AddVertex(origin.X - (r * WorldGrid.SquareSize), origin.Y - (c * WorldGrid.SquareSize), h);
            if (count == 0)
            {
                first = v;
            }
            else
            {
                builder.AddLine(previous, v);
            }

            previous = v;
            count++;
        }

        // The loop around the tile, one segment per height square: along row 0, down column 128, back along row 128, up column 0.
        for (int c = 0; c < Resolution; c++)
        {
            Corner(0, c);
        }

        for (int r = 0; r < Resolution; r++)
        {
            Corner(r, Resolution);
        }

        for (int c = Resolution; c > 0; c--)
        {
            Corner(Resolution, c);
        }

        for (int r = Resolution; r > 0; r--)
        {
            Corner(r, 0);
        }

        builder.AddLine(previous, first);
        builder.DescribeAttribute(0, () => new JsonObject
        {
            ["kind"] = "tile",
            ["tile"] = new JsonArray(tile.X, tile.Y),
            ["colorKey"] = colorKey,
            ["bits"] = bits,
            ["terrain"] = terrainReport.ToJson(),
            ["models"] = modelReport.ToJson(),
            ["navmesh"] = navReport.ToJson(),
        });
        builder.Stats["bits"] = bits;
        builder.Stats["colorKey"] = colorKey;
        builder.Stats["segments"] = builder.LineCount;
        return LayerBuild.Of(LayerIds.Tiles, tile, [builder.Build()], $"{colorKey}: {Present(terrainReport.Usable, modelReport.Usable, navReport.Usable)}");
    }

    private static string Present(bool terrain, bool models, bool nav)
    {
        var names = new List<string>();
        if (terrain)
        {
            names.Add(".map");
        }

        if (models)
        {
            names.Add(".vmtile");
        }

        if (nav)
        {
            names.Add(".mmtile");
        }

        return names.Count == 0 ? "none" : string.Join(", ", names);
    }

    private static FileReport ReportTerrain(ParsedFile<TerrainTile> file)
    {
        if (!file.IsOk)
        {
            return new FileReport(file.State, false, file.Detail, null);
        }

        bool flat = file.Value.Storage == TerrainTile.HeightStorage.Flat;
        return new FileReport(
            flat ? TileState.Empty : TileState.Ok,
            true,
            file.Detail,
            new JsonObject { ["storage"] = TerrainLayerSource.StorageName(file.Value), ["gridHeight"] = file.Value.GridHeight });
    }

    private FileReport ReportModels(TileKey tile)
    {
        ParsedFile<VMapTreeData> tree = Data.VMapTreeFile(tile.MapId);
        if (tree.IsOk && !tree.Value.IsTiled)
        {
            return new FileReport(TileState.NotApplicable, false, $"{tree.Detail} holds all models of the map; the server reads no tile files for it", null);
        }

        ParsedFile<VMapTileData> file = Data.VMapTileFile(tile);
        if (!file.IsOk)
        {
            return new FileReport(file.State, false, file.Detail, null);
        }

        int spawns = file.Value.Spawns.Count;
        int m2 = file.Value.Spawns.Count(s => s.Spawn.IsM2);
        var extra = new JsonObject { ["spawns"] = spawns, ["m2"] = m2, ["wmo"] = spawns - m2 };
        if (!tree.IsOk)
        {
            extra["note"] = $"{tree.Detail}: the server loads no models of this map without the tree file";
        }

        return new FileReport(spawns == 0 ? TileState.Empty : TileState.Ok, tree.IsOk, file.Detail, extra);
    }

    private static FileReport ReportNav(ParsedFile<NavTileData> file)
    {
        if (!file.IsOk)
        {
            return new FileReport(file.State, false, file.Detail, null);
        }

        NavTileData data = file.Value;
        int walkable = data.Tile.Polys.Count(p => !p.IsOffMeshConnection);
        return new FileReport(
            walkable == 0 ? TileState.Empty : TileState.Ok,
            true,
            file.Detail,
            new JsonObject
            {
                ["polygons"] = walkable,
                ["headerPolyCount"] = data.HeaderPolyCount,
                ["bvTree"] = data.Tile.HasBvTree,
            });
    }

    private sealed record FileReport(TileState State, bool Usable, string? Detail, JsonObject? Extra)
    {
        public JsonObject ToJson()
        {
            var o = new JsonObject
            {
                ["state"] = State.ToString(),
                ["detail"] = Detail,
            };
            if (Extra is not null)
            {
                foreach ((string key, JsonNode? value) in Extra)
                {
                    o[key] = value?.DeepClone();
                }
            }

            return o;
        }
    }
}
