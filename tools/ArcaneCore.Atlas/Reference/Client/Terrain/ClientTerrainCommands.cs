using System.Globalization;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;

namespace ArcaneCore.Atlas.Reference.Client.Terrain;

/// <summary>
/// The commands the client terrain module adds: <c>ref.query.height</c> (what the client's ADT says at a point),
/// <c>ref.adt.info</c> (a summary of one tile's ADT), and <c>client.terrain.analyze</c> (elevation relief, slope, hole,
/// liquid coverage, and area analysis for a tile). Handlers run on request threads and only read through
/// <see cref="ClientTerrainService"/>, which is thread-safe.
/// </summary>
internal static class ClientTerrainCommands
{
    /// <summary>Name of the height query.</summary>
    public const string HeightName = "ref.query.height";

    /// <summary>Name of the tile summary.</summary>
    public const string InfoName = "ref.adt.info";

    /// <summary>Name of the tile terrain analysis command.</summary>
    public const string AnalyzeName = "client.terrain.analyze";

    private const int MaxListedAreas = 16;
    private const int MaxListedWmos = 32;

    /// <summary>Adds the client terrain commands to <paramref name="registry"/>.</summary>
    public static void Register(CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(new CommandDefinition(
            HeightName,
            "Client terrain height, surface normal, slope and liquid levels at a world position, read from the 1.12.1 ADT (the reference counterpart of query.height).",
            [
                new CommandArg("map", ArgType.Integer, "Map id (Map.dbc).", Required: true),
                new CommandArg("at", ArgType.Point3, "World position [x, y, z] (x north, y west, z up, yards). Z only sets deltaZ and depth; the query is by x and y.", Required: true),
            ],
            Height));
        registry.Register(new CommandDefinition(
            InfoName,
            "Summary of one tile's client ADT: file, chunks, holes, liquid layers, areas and model placements.",
            [
                new CommandArg("map", ArgType.Integer, "Map id (Map.dbc).", Required: true),
                new CommandArg("x", ArgType.Integer, "Server tile X (0..63; north, from world X).", Required: true),
                new CommandArg("y", ArgType.Integer, "Server tile Y (0..63; west, from world Y).", Required: true),
            ],
            Info));
        registry.Register(new CommandDefinition(
            AnalyzeName,
            "Analyzes one tile's client ADT terrain: elevation min/max/mean, steep-triangle count/ratio, hole count, liquid coverage ratio, unique AreaIds, and steepest-chunk overlay.",
            [
                new CommandArg("map", ArgType.Integer, "Map id (Map.dbc).", Required: true),
                new CommandArg("tileX", ArgType.Integer, "Server tile X (0..63; north, from world X).", Required: true),
                new CommandArg("tileY", ArgType.Integer, "Server tile Y (0..63; west, from world Y).", Required: true),
            ],
            Analyze));
    }

    private static Task<JsonNode?> Height(CommandInvocation call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        uint mapId = call.GetMapId();
        WorldPoint at = call.GetPoint("at");
        ClientTerrainService service = call.Service<ClientTerrainService>();
        ClientMap map = RequireMap(service, mapId, HeightName);
        TileKey tile = TileAt(mapId, at.X, at.Y)
            ?? throw new CommandException(CommandException.BadArguments, FormattableString.Invariant($"{HeightName}: ({at.X}, {at.Y}) is outside the 64x64 tile grid of map {mapId}."));

        AdtLoad load = service.LoadAdt(tile);
        var result = new JsonObject
        {
            ["map"] = mapId,
            ["mapName"] = map.Name,
            ["at"] = Point(at),
            ["tile"] = new JsonArray(mapId, tile.X, tile.Y),
            ["adt"] = FileJson(load),
        };

        var points = new JsonArray();
        if (load.File is { } adt)
        {
            TerrainSample? sample = AdtGeometry.SampleAt(adt, tile, at.X, at.Y);
            result["terrain"] = sample is { } s ? TerrainJson(s, adt, tile, at, service, points) : null;
            result["liquid"] = LiquidJson(adt, tile, at, sample, points);
        }

        result["overlay"] = new JsonObject { ["points"] = points };
        return Task.FromResult<JsonNode?>(result);
    }

    private static Task<JsonNode?> Info(CommandInvocation call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        uint mapId = call.GetMapId();
        long x = call.GetInt("x");
        long y = call.GetInt("y");
        if (x is < 0 or >= WorldGrid.TilesPerSide || y is < 0 or >= WorldGrid.TilesPerSide)
        {
            throw new CommandException(CommandException.BadArguments, $"{InfoName}: tile ({x}, {y}) must be inside 0..{WorldGrid.TilesPerSide - 1}.");
        }

        ClientTerrainService service = call.Service<ClientTerrainService>();
        ClientMap map = RequireMap(service, mapId, InfoName);
        var tile = new TileKey(mapId, (int)x, (int)y);
        WdtLoad wdt = service.LoadWdt(mapId);
        AdtLoad load = service.LoadAdt(tile);
        var result = new JsonObject
        {
            ["map"] = mapId,
            ["mapName"] = map.Name,
            ["tile"] = new JsonArray(mapId, tile.X, tile.Y),
            ["serverMapFile"] = WorldGrid.MapFileName(tile),
            ["wdt"] = new JsonObject
            {
                ["path"] = wdt.Path,
                ["state"] = wdt.State.ToString(),
                ["detail"] = wdt.Detail,
                ["globalWmo"] = wdt.File?.IsGlobalWmo,
                ["tiles"] = wdt.File?.TileCount,
                ["listsTile"] = wdt.File?.HasTile(tile.X, tile.Y),
            },
            ["adt"] = FileJson(load),
        };

        if (load.File is { } adt)
        {
            result["chunks"] = ChunksJson(adt, service);
            result["placements"] = PlacementsJson(adt);
            result["textures"] = adt.Textures.Count;
            result["positionMismatches"] = AdtGeometry.CountPositionMismatches(adt, tile);
            result["overlay"] = new JsonObject { ["boxes"] = new JsonArray(BoundsBox(adt, tile)) };
        }

        return Task.FromResult<JsonNode?>(result);
    }

    private static Task<JsonNode?> Analyze(CommandInvocation call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        uint mapId = call.GetMapId();
        long tileX = call.GetInt("tileX");
        long tileY = call.GetInt("tileY");
        if (tileX is < 0 or >= WorldGrid.TilesPerSide || tileY is < 0 or >= WorldGrid.TilesPerSide)
        {
            throw new CommandException(CommandException.BadArguments, $"{AnalyzeName}: tile ({tileX}, {tileY}) must be inside 0..{WorldGrid.TilesPerSide - 1}.");
        }

        ClientTerrainService service = call.Service<ClientTerrainService>();
        ClientMap map = RequireMap(service, mapId, AnalyzeName);
        var tile = new TileKey(mapId, (int)tileX, (int)tileY);
        AdtLoad load = service.LoadAdt(tile);
        var result = new JsonObject
        {
            ["map"] = mapId,
            ["mapName"] = map.Name,
            ["tile"] = new JsonArray(mapId, tile.X, tile.Y),
            ["adt"] = FileJson(load),
        };

        var boxes = new JsonArray();
        if (load.File is { } adt)
        {
            TerrainTileAnalysis analysis = ClientTerrainGeometry.AnalyzeTile(tile, adt);
            result["elevation"] = new JsonObject
            {
                ["min"] = FiniteOrNull(analysis.MinElevation),
                ["max"] = FiniteOrNull(analysis.MaxElevation),
                ["mean"] = FiniteOrNull(analysis.MeanElevation),
            };
            result["steepTriangles"] = analysis.SteepTriangles;
            result["totalTriangles"] = analysis.TotalTriangles;
            result["steepTriangleRatio"] = analysis.SteepTriangleRatio;
            result["holeCount"] = analysis.HoleSquares;
            result["liquidSquares"] = analysis.LiquidSquares;
            result["liquidCoverageRatio"] = analysis.LiquidCoverageRatio;

            var areaIds = new JsonArray();
            foreach (uint areaId in analysis.AreaIds)
            {
                areaIds.Add(areaId);
            }

            result["areaIds"] = areaIds;

            var steepest = new JsonArray();
            WorldPoint origin = WorldGrid.Origin(tile);
            double chunkSize = WorldGrid.TileSize / AdtFile.ChunksPerSide;
            foreach (TerrainChunkSteepness c in analysis.SteepestChunks)
            {
                steepest.Add(new JsonObject
                {
                    ["chunkIndex"] = c.ChunkIndex,
                    ["chunk"] = new JsonArray(c.ChunkRow, c.ChunkColumn),
                    ["steepTriangles"] = c.SteepTriangles,
                    ["totalTriangles"] = c.TotalTriangles,
                    ["maxSlopeDegrees"] = c.MaxSlopeDegrees,
                });

                double maxX = origin.X - (c.ChunkRow * chunkSize);
                double minX = maxX - chunkSize;
                double maxY = origin.Y - (c.ChunkColumn * chunkSize);
                double minY = maxY - chunkSize;
                boxes.Add(new JsonObject
                {
                    ["min"] = new JsonArray(minX, minY, c.MinZ),
                    ["max"] = new JsonArray(maxX, maxY, c.MaxZ),
                    ["label"] = string.Create(CultureInfo.InvariantCulture, $"steep MCNK ({c.ChunkRow}, {c.ChunkColumn}) max={c.MaxSlopeDegrees:F1} deg ({c.SteepTriangles} tris)"),
                });
            }

            result["steepestChunks"] = steepest;
        }

        result["overlay"] = new JsonObject { ["boxes"] = boxes };
        return Task.FromResult<JsonNode?>(result);
    }

    private static ClientMap RequireMap(ClientTerrainService service, uint mapId, string command)
    {
        if (service.Files is null)
        {
            throw new CommandException(CommandException.Unavailable, $"{command} needs the client files, and none are registered (no client directory or extracted client was given).");
        }

        if (service.Maps is not { } maps)
        {
            throw new CommandException(CommandException.Unavailable, $"{command} needs the map directory (Map.dbc), and none is registered.");
        }

        return maps.TryGetMap(mapId, out ClientMap map)
            ? map
            : throw new CommandException(CommandException.BadArguments, $"{command}: map {mapId} is not in Map.dbc.");
    }

    // The server's own rule is (int)(32 - x / SIZE_OF_GRIDS) in float (TerrainTile.TileOf); doubles keep a point that sits
    // on a tile edge in the tile whose rows ADT sampling then computes, so the two always agree.
    private static TileKey? TileAt(uint mapId, double x, double y)
    {
        double tx = Math.Floor(32 - (x / WorldGrid.TileSize));
        double ty = Math.Floor(32 - (y / WorldGrid.TileSize));
        return tx is >= 0 and < WorldGrid.TilesPerSide && ty is >= 0 and < WorldGrid.TilesPerSide
            ? new TileKey(mapId, (int)tx, (int)ty)
            : null;
    }

    private static JsonArray Point(WorldPoint p) => new(p.X, p.Y, p.Z);

    private static JsonNode? FiniteOrNull(double value) => double.IsFinite(value) ? JsonValue.Create(value) : null;

    private static JsonObject FileJson(AdtLoad load) => new()
    {
        ["path"] = load.Path,
        ["archive"] = load.Archive,
        ["state"] = load.State.ToString(),
        ["detail"] = load.Detail,
        ["bytes"] = load.File?.ByteLength,
    };

    private static JsonObject TerrainJson(TerrainSample s, AdtFile adt, TileKey tile, WorldPoint at, ClientTerrainService service, JsonArray points)
    {
        AdtChunk chunk = adt.Chunk(s.ChunkRow, s.ChunkColumn);
        TerrainSurfaceSample? surface = ClientTerrainGeometry.SampleSurfaceAt(adt, tile, at.X, at.Y);
        var terrain = new JsonObject
        {
            ["hole"] = s.IsHole,
            ["z"] = s.IsHole ? null : s.Z,
            ["deltaZ"] = s.IsHole ? null : at.Z - s.Z,
            ["normal"] = surface is { Terrain.IsHole: false } sf
                ? new JsonObject
                {
                    ["x"] = sf.Normal.X,
                    ["y"] = sf.Normal.Y,
                    ["z"] = sf.Normal.Z,
                }
                : null,
            ["slopeDegrees"] = surface is { Terrain.IsHole: false } sfSlope ? sfSlope.SlopeDegrees : null,
            ["walkableSlope"] = surface is { Terrain.IsHole: false } sfWalk && sfWalk.WalkableSlope,
            ["liquidDepth"] = surface?.LiquidDepth ?? 0.0,
            ["chunk"] = new JsonArray(s.ChunkRow, s.ChunkColumn),
            ["square"] = new JsonArray(s.SquareRow, s.SquareColumn),
            ["triangle"] = s.IsHole ? null : s.Triangle,
            ["fraction"] = new JsonArray(s.FractionRow, s.FractionColumn),
            ["areaId"] = chunk.AreaId,
            ["area"] = service.AreaName(chunk.AreaId),
            ["noHeights"] = !chunk.HasHeights,
        };

        if (!s.IsHole)
        {
            points.Add(new JsonObject
            {
                ["at"] = new JsonArray(at.X, at.Y, s.Z),
                ["label"] = string.Create(CultureInfo.InvariantCulture, $"client terrain z={s.Z:F3}"),
                ["colorKey"] = ColorKeys.Ground,
            });
        }

        return terrain;
    }

    private static JsonArray LiquidJson(AdtFile adt, TileKey tile, WorldPoint at, TerrainSample? terrain, JsonArray points)
    {
        var liquids = new JsonArray();
        foreach (LiquidSample l in AdtGeometry.LiquidAt(adt, tile, at.X, at.Y) ?? [])
        {
            double? depth = terrain is { IsHole: false } t ? l.Level - t.Z : null;
            liquids.Add(new JsonObject
            {
                ["liquid"] = ClientTerrainGeometry.KindName(l.Kind),
                ["level"] = l.Level,
                ["depth"] = depth,
                ["darkWater"] = l.IsDarkWater,
                ["squareFlags"] = l.SquareFlags,
                ["corners"] = new JsonArray(l.HeightRowColumn, l.HeightNextRow, l.HeightNextColumn, l.HeightNextBoth),
            });
            points.Add(new JsonObject
            {
                ["at"] = new JsonArray(at.X, at.Y, l.Level),
                ["label"] = string.Create(CultureInfo.InvariantCulture, $"client {ClientTerrainGeometry.KindName(l.Kind)} level={l.Level:F3}"),
                ["colorKey"] = ClientTerrainGeometry.LiquidColorKey(l.Kind, l.IsDarkWater),
            });
        }

        return liquids;
    }

    private static JsonObject ChunksJson(AdtFile adt, ClientTerrainService service)
    {
        int holeSquares = 0;
        int flatChunks = 0;
        int liquidChunks = 0;
        int stackedChunks = 0;
        int[] layersByKind = new int[4];
        var areas = new Dictionary<uint, int>();
        foreach (AdtChunk chunk in adt.Chunks)
        {
            holeSquares += chunk.HoleSquares;
            flatChunks += chunk.HasHeights ? 0 : 1;
            liquidChunks += chunk.LiquidLayers.Count > 0 ? 1 : 0;
            stackedChunks += chunk.LiquidLayers.Count > 1 ? 1 : 0;
            foreach (AdtLiquidLayer layer in chunk.LiquidLayers)
            {
                layersByKind[(int)layer.Kind]++;
            }

            areas[chunk.AreaId] = areas.GetValueOrDefault(chunk.AreaId) + 1;
        }

        var layers = new JsonObject();
        foreach (AdtLiquidKind kind in Enum.GetValues<AdtLiquidKind>())
        {
            layers[ClientTerrainGeometry.KindName(kind)] = layersByKind[(int)kind];
        }

        var areaList = new JsonArray();
        foreach ((uint id, int count) in areas.OrderByDescending(a => a.Value).ThenBy(a => a.Key).Take(MaxListedAreas))
        {
            areaList.Add(new JsonObject { ["areaId"] = id, ["area"] = service.AreaName(id), ["chunks"] = count });
        }

        return new JsonObject
        {
            ["count"] = adt.Chunks.Count,
            ["holeSquares"] = holeSquares,
            ["withoutHeights"] = flatChunks,
            ["liquidChunks"] = liquidChunks,
            ["stackedLiquidChunks"] = stackedChunks,
            ["liquidLayers"] = layers,
            ["distinctAreas"] = areas.Count,
            ["areas"] = areaList,
        };
    }

    private static JsonObject PlacementsJson(AdtFile adt)
    {
        var wmos = new JsonArray();
        foreach (AdtWmoObject o in adt.WmoObjects.Take(MaxListedWmos))
        {
            wmos.Add(new JsonObject
            {
                ["path"] = o.ModelPath,
                ["uniqueId"] = o.UniqueId,
                ["position"] = Point(o.Position),
                ["doodadSet"] = o.DoodadSet,
            });
        }

        return new JsonObject
        {
            ["doodads"] = adt.Doodads.Count,
            ["doodadModels"] = adt.DoodadNames.Count,
            ["wmos"] = adt.WmoObjects.Count,
            ["wmoModels"] = adt.WmoNames.Count,
            ["wmoList"] = wmos,
            ["wmoListTruncated"] = adt.WmoObjects.Count > MaxListedWmos,
        };
    }

    private static JsonObject BoundsBox(AdtFile adt, TileKey tile)
    {
        (double minX, double maxX, double minY, double maxY) = WorldGrid.Bounds(tile);
        float minZ = float.PositiveInfinity;
        float maxZ = float.NegativeInfinity;
        foreach (AdtChunk chunk in adt.Chunks)
        {
            foreach (float relative in chunk.RelativeHeights)
            {
                float z = chunk.BaseHeight + relative;
                minZ = Math.Min(minZ, z);
                maxZ = Math.Max(maxZ, z);
            }
        }

        return new JsonObject
        {
            ["min"] = new JsonArray(minX, minY, minZ),
            ["max"] = new JsonArray(maxX, maxY, maxZ),
            ["label"] = $"client ADT {tile}",
        };
    }
}
