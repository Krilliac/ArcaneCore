using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using DotRecast.Detour;

namespace ArcaneCore.Atlas.Reference.Navmesh;

/// <summary>
/// The DotRecast reference commands: <c>ref.query.path</c>, <c>ref.query.nearest</c> and <c>ref.nav.tile</c>. Every handler runs the
/// engine work on the thread pool (a query may read tile files) and returns plain JSON; refusals are <see cref="CommandException"/>s
/// that the registry reports without logging above Warning.
/// </summary>
public static class NavCommands
{
    /// <summary>Name of the path query.</summary>
    public const string PathName = "ref.query.path";

    /// <summary>Name of the nearest-polygon query.</summary>
    public const string NearestName = "ref.query.nearest";

    /// <summary>Name of the tile header dump.</summary>
    public const string TileName = "ref.nav.tile";

    /// <summary>
    /// Most terrain tiles one path query loads (the box covering both end points plus one tile around). A tile is a few MB of DotRecast
    /// objects, and the server's own search stops after 2048 polygons, far short of a box this size.
    /// </summary>
    public const int MaxQueryTiles = 64;

    /// <summary>The longest corridor listed point by point in the overlay.</summary>
    private const int MaxOverlayCorridor = 256;

    private static readonly string[] BoxNames = ["ladder", "near", "far", "wide", "end-tolerance"];

    /// <summary>The three command definitions.</summary>
    public static IReadOnlyList<CommandDefinition> Definitions { get; } =
    [
        new CommandDefinition(
            PathName,
            "DotRecast path between two world points on the tiles the server reads, deciding the result type like the server does.",
            [
                new CommandArg("map", ArgType.Integer, "Map id.", Required: true),
                new CommandArg("a", ArgType.Point3, "Start, world [x, y, z].", Required: true),
                new CommandArg("b", ArgType.Point3, "End, world [x, y, z].", Required: true),
                new CommandArg("include", ArgType.StringList, "Terrain a polygon must have one of (ground, water, magma, slime, steep). Default: the server's PathOptions default (ground, water, magma, slime)."),
                new CommandArg("exclude", ArgType.StringList, "Terrain that disqualifies a polygon. Default: none (vmangos excludes nothing for ordinary movement)."),
                new CommandArg("maxPoints", ArgType.Integer, "Most points in the answer; longer paths are cut and flagged Short.", Default: PathOptions.DefaultMaxPoints),
                new CommandArg("allowPartial", ArgType.Boolean, "Answer the closest reachable point (Incomplete) when the end cannot be reached.", Default: true),
                new CommandArg("endTolerance", ArgType.Number, $"0: the end is located in the near, far and (with wideEndSearch) wide boxes. Above 0: vmangos' FindWalkPoly rule; {PathOptions.VmangosEndTolerance} is vmangos' value.", Default: 0),
                new CommandArg("wideEndSearch", ArgType.Boolean, "With endTolerance 0, also look for the end polygon in the wide box.", Default: false),
                new CommandArg("offMesh", ArgType.Boolean, "Let DotRecast traverse off-mesh connections (it does; the server's reader skips them). false matches the server.", Default: true),
            ],
            PathAsync),
        new CommandDefinition(
            NearestName,
            "The DotRecast polygon nearest a world point, with the server's search boxes.",
            [
                new CommandArg("map", ArgType.Integer, "Map id.", Required: true),
                new CommandArg("at", ArgType.Point3, "World [x, y, z].", Required: true),
                new CommandArg("box", ArgType.String, "Search box: ladder (near, then far, then wide), near, far, wide or end-tolerance (vmangos' 5 x 10 x 5).", Default: "ladder", Choices: BoxNames),
                new CommandArg("extents", ArgType.Point3, "Custom half-extents [x, y, z] in world axes; replaces box."),
                new CommandArg("include", ArgType.StringList, "Terrain a polygon must have one of. Default: ground, water, magma, slime."),
                new CommandArg("exclude", ArgType.StringList, "Terrain that disqualifies a polygon. Default: none."),
                new CommandArg("offMesh", ArgType.Boolean, "Accept off-mesh connection polygons (DotRecast does; the server does not).", Default: true),
            ],
            NearestAsync),
        new CommandDefinition(
            TileName,
            "Header counts of one DotRecast navmesh tile (polygons, vertices, detail, links, BV nodes, off-mesh connections).",
            [
                new CommandArg("map", ArgType.Integer, "Map id.", Required: true),
                new CommandArg("x", ArgType.Integer, "Terrain tile X (the NNNXXYY.mmtile name).", Required: true),
                new CommandArg("y", ArgType.Integer, "Terrain tile Y.", Required: true),
            ],
            TileAsync),
    ];

    private static Task<JsonNode?> PathAsync(CommandInvocation inv, CancellationToken cancellationToken)
    {
        uint mapId = inv.GetMapId();
        WorldPoint a = inv.GetPoint("a");
        WorldPoint b = inv.GetPoint("b");
        PathOptions options = new()
        {
            IncludeFlags = Flags(inv, "include", PathOptions.Default.IncludeFlags),
            ExcludeFlags = Flags(inv, "exclude", NavTerrain.Empty),
            MaxPoints = Bounded(inv, "maxPoints", PathOptions.DefaultMaxPoints, 2, NavQuery.MaxCorridorPolys),
            AllowPartial = inv.GetBool("allowPartial", true),
            EndTolerance = (float)Math.Clamp(inv.OptionalNumber("endTolerance") ?? 0, 0, 1000),
            WideEndSearch = inv.GetBool("wideEndSearch", false),
        };
        bool offMesh = inv.GetBool("offMesh", true);
        ReferenceNavMeshService service = inv.Service<ReferenceNavMeshService>();
        ReferenceNavMap map = Open(service, mapId);
        TileKey tileA = TileOf(mapId, a, "a");
        TileKey tileB = TileOf(mapId, b, "b");
        List<TileKey> tiles = PathTiles(tileA, tileB);
        Vector3 start = ToVector(a);
        Vector3 end = ToVector(b);
        return Task.Run<JsonNode?>(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                (NavPathAnswer answer, TileSummary summary) = map.Read(tiles, view => (NavQuery.FindPath(view, start, end, options, offMesh), Summarize(view)));
                return PathJson(mapId, a, b, options, offMesh, answer, summary);
            },
            cancellationToken);
    }

    private static Task<JsonNode?> NearestAsync(CommandInvocation inv, CancellationToken cancellationToken)
    {
        uint mapId = inv.GetMapId();
        WorldPoint at = inv.GetPoint("at");
        string boxName = inv.OptionalString("box") ?? "ladder";
        NavSearchBox box = boxName switch
        {
            "near" => NavSearchBox.Near,
            "far" => NavSearchBox.Far,
            "wide" => NavSearchBox.Wide,
            "end-tolerance" => NavSearchBox.EndTolerance,
            _ => NavSearchBox.Ladder,
        };
        Vector3? extents = null;
        if (inv.OptionalPoint("extents") is { } e)
        {
            if (e.X < 0 || e.Y < 0 || e.Z < 0 || e.X > 4096 || e.Y > 4096 || e.Z > 4096)
            {
                throw new CommandException(CommandException.BadArguments, $"{NearestName}: extents must be between 0 and 4096 on every axis.");
            }

            // World axes in, Recast axes to the query: Recast x = world Y, y = height, z = world X (NavMeshFormat.ToRecast).
            extents = NavMeshFormat.ToRecast(ToVector(e));
        }

        var filter = new NavQueryFilter(Flags(inv, "include", PathOptions.Default.IncludeFlags), Flags(inv, "exclude", NavTerrain.Empty), inv.GetBool("offMesh", true));
        ReferenceNavMeshService service = inv.Service<ReferenceNavMeshService>();
        ReferenceNavMap map = Open(service, mapId);
        TileKey tile = TileOf(mapId, at, "at");
        List<TileKey> tiles = ReferenceNavLayerSource.Neighbourhood(tile);
        Vector3 point = ToVector(at);
        return Task.Run<JsonNode?>(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                (NavLocate? found, IReadOnlyList<string> tried, TileSummary summary) = map.Read(tiles, view =>
                {
                    NavLocate? located = NavQuery.Nearest(view, point, filter, box, extents, out IReadOnlyList<string> boxes);
                    return (located, boxes, Summarize(view));
                });
                return NearestJson(mapId, at, filter, found, tried, summary);
            },
            cancellationToken);
    }

    private static Task<JsonNode?> TileAsync(CommandInvocation inv, CancellationToken cancellationToken)
    {
        uint mapId = inv.GetMapId();
        long x = inv.GetInt("x");
        long y = inv.GetInt("y");
        if (x is < 0 or >= WorldGrid.TilesPerSide || y is < 0 or >= WorldGrid.TilesPerSide)
        {
            throw new CommandException(CommandException.BadArguments, $"{TileName}: tile {x},{y} is outside the {WorldGrid.TilesPerSide} x {WorldGrid.TilesPerSide} grid.");
        }

        ReferenceNavMeshService service = inv.Service<ReferenceNavMeshService>();
        var tile = new TileKey(mapId, (int)x, (int)y);
        if (service.Directory is null)
        {
            throw NoNavmesh();
        }

        NavMapOpen open = service.OpenMap(mapId);
        if (open.Map is not { } map)
        {
            return Task.FromResult<JsonNode?>(new JsonObject
            {
                ["map"] = mapId,
                ["tile"] = new JsonArray((int)x, (int)y),
                ["state"] = StateName(open.State),
                ["detail"] = open.Detail,
            });
        }

        List<TileKey> tiles = ReferenceNavLayerSource.Neighbourhood(tile);
        return Task.Run<JsonNode?>(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return map.Read(tiles, view => TileJson(map, view, tile));
            },
            cancellationToken);
    }

    // ---- arguments -----------------------------------------------------------------------------------------------------------

    private static NavTerrain Flags(CommandInvocation inv, string name, NavTerrain fallback)
    {
        if (!inv.Has(name))
        {
            return fallback;
        }

        NavTerrain flags = NavTerrain.Empty;
        foreach (string text in inv.GetStringList(name))
        {
            flags |= NavTerrainNames.Parse(text)
                ?? throw new CommandException(CommandException.BadArguments, $"{inv.Definition.Name}: '{text}' is not a terrain name (ground, water, magma, slime, steep).");
        }

        return flags;
    }

    private static int Bounded(CommandInvocation inv, string name, int fallback, int min, int max)
    {
        long value = inv.OptionalInt(name) ?? fallback;
        return value >= min && value <= max
            ? (int)value
            : throw new CommandException(CommandException.BadArguments, $"{inv.Definition.Name}: {name} must be between {min} and {max}.");
    }

    private static ReferenceNavMap Open(ReferenceNavMeshService service, uint mapId)
    {
        if (service.Directory is null)
        {
            throw NoNavmesh();
        }

        NavMapOpen open = service.OpenMap(mapId);
        return open.Map ?? throw new CommandException(CommandException.Unavailable, open.Detail ?? $"Map {mapId} has no navmesh.");
    }

    private static CommandException NoNavmesh()
        => new(CommandException.Unavailable, "No navmesh data: the terrain data root (ARCANECORE_TEST_TERRAIN_DIR or --data) has no 'mmaps' folder.");

    private static TileKey TileOf(uint mapId, WorldPoint point, string argument)
        => WorldGrid.TileOf(mapId, point.X, point.Y)
            ?? throw new CommandException(CommandException.BadArguments, $"Argument '{argument}' ({point.X:0.###}, {point.Y:0.###}) is outside the map grid.");

    private static List<TileKey> PathTiles(TileKey a, TileKey b)
    {
        int minX = Math.Max(0, Math.Min(a.X, b.X) - 1);
        int maxX = Math.Min(WorldGrid.TilesPerSide - 1, Math.Max(a.X, b.X) + 1);
        int minY = Math.Max(0, Math.Min(a.Y, b.Y) - 1);
        int maxY = Math.Min(WorldGrid.TilesPerSide - 1, Math.Max(a.Y, b.Y) + 1);
        int count = (maxX - minX + 1) * (maxY - minY + 1);
        if (count > MaxQueryTiles)
        {
            throw new CommandException(
                CommandException.BadArguments,
                $"{PathName}: the points are {Math.Abs(a.X - b.X)} x {Math.Abs(a.Y - b.Y)} tiles apart; the tile box would hold {count} tiles (limit {MaxQueryTiles}).");
        }

        var tiles = new List<TileKey>(count);
        for (int x = minX; x <= maxX; x++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                tiles.Add(new TileKey(a.MapId, x, y));
            }
        }

        return tiles;
    }

    private static Vector3 ToVector(WorldPoint p) => new((float)p.X, (float)p.Y, (float)p.Z);

    private static JsonArray Json(Vector3 v) => new(v.X, v.Y, v.Z);

    private static JsonArray Json(WorldPoint p) => new(p.X, p.Y, p.Z);

    private static string StateName(NavTileState state) => state switch
    {
        NavTileState.Ok => "ok",
        NavTileState.Missing => "missing",
        _ => "rejected",
    };

    // ---- results -------------------------------------------------------------------------------------------------------------

    /// <summary>What a query's tile set looked like: how many files were read, absent or refused.</summary>
    private sealed record TileSummary(int Requested, int Ok, int Missing, string[] Rejected);

    private static TileSummary Summarize(NavMapView view)
    {
        int ok = 0, missing = 0;
        var rejected = new List<string>();
        foreach ((TileKey key, NavTileOutcome outcome) in view.Tiles.OrderBy(t => t.Key.X).ThenBy(t => t.Key.Y))
        {
            switch (outcome.State)
            {
                case NavTileState.Ok:
                    ok++;
                    break;
                case NavTileState.Missing:
                    missing++;
                    break;
                default:
                    rejected.Add($"{key.X},{key.Y}: {outcome.Detail}");
                    break;
            }
        }

        return new TileSummary(view.Tiles.Count, ok, missing, [.. rejected]);
    }

    private static JsonObject Json(TileSummary summary) => new()
    {
        ["requested"] = summary.Requested,
        ["ok"] = summary.Ok,
        ["missing"] = summary.Missing,
        ["rejected"] = new JsonArray([.. summary.Rejected.Select(r => (JsonNode)r)]),
    };

    private static JsonObject Json(NavLocate located) => new()
    {
        ["poly"] = located.Poly.ToJson(),
        ["nearest"] = Json(located.Nearest),
        ["overPoly"] = located.OverPoly,
        ["far"] = located.Far,
        ["box"] = located.Box,
    };

    private static JsonArray Names(NavTerrain flags) => new([.. NavTerrainNames.Names((ushort)flags).Select(n => (JsonNode)n)]);

    private static string Status(PathType type)
        => (type & PathType.NoPath) != 0 ? "NoPath"
            : (type & PathType.Incomplete) != 0 ? "Incomplete"
            : (type & PathType.NotUsingPath) != 0 ? "Shortcut"
            : "Normal";

    private static JsonArray TypeNames(PathType type)
        => new([.. Enum.GetValues<PathType>().Where(v => v != PathType.Blank && type.HasFlag(v)).Select(v => (JsonNode)v.ToString())]);

    private static JsonObject PathJson(uint mapId, WorldPoint a, WorldPoint b, PathOptions options, bool offMesh, NavPathAnswer answer, TileSummary summary)
    {
        PathResult result = answer.Result;
        string color = ReferenceColor();
        var points = new JsonArray([.. result.Points.Select(p => (JsonNode)Json(p))]);
        int offMeshPoints = answer.PointFlags.Count(f => (f & DtStraightPathFlags.DT_STRAIGHTPATH_OFFMESH_CONNECTION) != 0);
        bool path = result.HasPath;

        var overlayPoints = new JsonArray
        {
            new JsonObject { ["at"] = Json(a), ["label"] = "ref start", ["color"] = color },
            new JsonObject { ["at"] = Json(b), ["label"] = "ref end", ["color"] = color },
        };
        var overlaySegments = new JsonArray();
        foreach ((NavLocate? located, WorldPoint from, string label) in new[] { (answer.Start, a, "start"), (answer.End, b, "end") })
        {
            if (located is null)
            {
                continue;
            }

            overlayPoints.Add(new JsonObject { ["at"] = Json(located.Nearest), ["label"] = $"ref {label} on mesh ({located.Box})", ["color"] = color });
            overlaySegments.Add(new JsonObject { ["from"] = Json(from), ["to"] = Json(located.Nearest), ["dashed"] = true, ["color"] = color });
        }

        var polylines = new JsonArray
        {
            new JsonObject { ["pts"] = points.DeepClone(), ["label"] = $"ref path {Status(result.Type)}", ["color"] = color, ["dashed"] = !path },
        };
        if (answer.Corridor.Count is > 1 and <= MaxOverlayCorridor)
        {
            polylines.Add(new JsonObject
            {
                ["pts"] = new JsonArray([.. answer.Corridor.Select(p => (JsonNode)Json(p.Center))]),
                ["label"] = "ref corridor (polygon centres)",
                ["color"] = color,
                ["dashed"] = true,
            });
        }

        return new JsonObject
        {
            ["map"] = mapId,
            ["a"] = Json(a),
            ["b"] = Json(b),
            ["status"] = Status(result.Type),
            ["type"] = (int)result.Type,
            ["flags"] = TypeNames(result.Type),
            ["points"] = points,
            ["pointCount"] = result.Points.Count,
            ["length"] = result.Length,
            ["polys"] = new JsonArray([.. answer.Corridor.Select(p => (JsonNode)p.ToJson())]),
            ["reference"] = new JsonObject
            {
                ["outcome"] = answer.Outcome,
                ["complete"] = answer.Complete,
                ["endFar"] = answer.EndFar,
                ["start"] = answer.Start is { } s ? Json(s) : null,
                ["end"] = answer.End is { } e ? Json(e) : null,
                ["pointFlags"] = new JsonArray([.. answer.PointFlags.Select(f => (JsonNode)(int)f)]),
                ["offMeshPoints"] = offMeshPoints,
                ["nodes"] = answer.Nodes,
                ["nodeBudget"] = PathOptions.DefaultMaxSearchNodes,
                ["overServerNodeBudget"] = answer.Nodes > PathOptions.DefaultMaxSearchNodes,
                ["searchStatus"] = "0x" + answer.SearchStatus.ToString("x8", CultureInfo.InvariantCulture),
                ["include"] = Names(options.IncludeFlags),
                ["exclude"] = Names(options.ExcludeFlags),
                ["endTolerance"] = options.EndTolerance,
                ["wideEndSearch"] = options.WideEndSearch,
                ["offMesh"] = offMesh,
                ["tiles"] = Json(summary),
            },
            ["overlay"] = new JsonObject { ["points"] = overlayPoints, ["segments"] = overlaySegments, ["polylines"] = polylines },
        };
    }

    private static JsonObject NearestJson(uint mapId, WorldPoint at, NavQueryFilter filter, NavLocate? found, IReadOnlyList<string> tried, TileSummary summary)
    {
        string color = ReferenceColor();
        var overlayPoints = new JsonArray { new JsonObject { ["at"] = Json(at), ["label"] = "ref query", ["color"] = color } };
        var overlaySegments = new JsonArray();
        var json = new JsonObject
        {
            ["map"] = mapId,
            ["at"] = Json(at),
            ["found"] = found is not null,
            ["tried"] = new JsonArray([.. tried.Select(t => (JsonNode)t)]),
            ["include"] = Names(filter.Include),
            ["exclude"] = Names(filter.Exclude),
            ["offMesh"] = filter.AllowOffMesh,
            ["tiles"] = Json(summary),
        };
        if (found is not null)
        {
            Vector3 q = ToVector(at);
            Vector3 n = found.Nearest;
            json["poly"] = found.Poly.ToJson();
            json["nearest"] = Json(n);
            json["overPoly"] = found.OverPoly;
            json["far"] = found.Far;
            json["box"] = found.Box;
            json["distance"] = Vector3.Distance(q, n);
            json["deltaZ"] = n.Z - q.Z;
            overlayPoints.Add(new JsonObject { ["at"] = Json(n), ["label"] = $"ref nearest poly {found.Poly.Poly} ({found.Box})", ["color"] = color, ["colorKey"] = NavTerrainNames.ColorKey(found.Poly.Flags) });
            overlaySegments.Add(new JsonObject { ["from"] = Json(at), ["to"] = Json(n), ["dashed"] = true, ["color"] = color });
        }

        json["overlay"] = new JsonObject { ["points"] = overlayPoints, ["segments"] = overlaySegments };
        return json;
    }

    private static JsonObject TileJson(ReferenceNavMap map, NavMapView view, TileKey tile)
    {
        NavTileOutcome outcome = view.Tiles[tile];
        var json = new JsonObject
        {
            ["map"] = map.MapId,
            ["tile"] = new JsonArray(tile.X, tile.Y),
            ["state"] = StateName(outcome.State),
            ["detail"] = outcome.Detail,
            ["file"] = Path.GetFileName(map.FilePath(tile)),
        };
        if (view.Resident(tile) is not { } resident)
        {
            json["neighbours"] = Json(Summarize(view));
            return json;
        }

        DtMeshData data = resident.Tile.data;
        DtMeshHeader h = data.header;
        MmapTileHeader prefix = resident.File.Prefix;
        List<NavLink> links = NavLinkInventory.Collect(view, resident);
        NavLinkCounts counts = NavLinkInventory.Count(links, resident.Tile);

        var byColor = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var byArea = new SortedDictionary<int, int>();
        foreach (DtPoly poly in data.polys)
        {
            if (poly.GetPolyType() == DtPolyTypes.DT_POLYTYPE_OFFMESH_CONNECTION)
            {
                continue;
            }

            string key = NavTerrainNames.ColorKey((ushort)poly.flags);
            byColor[key] = byColor.GetValueOrDefault(key) + 1;
            byArea[poly.GetArea()] = byArea.GetValueOrDefault(poly.GetArea()) + 1;
        }

        json["prefix"] = new JsonObject
        {
            ["magic"] = "0x" + prefix.Magic.ToString("x8", CultureInfo.InvariantCulture),
            ["detourVersion"] = prefix.DetourVersion,
            ["mmapVersion"] = prefix.MmapVersion,
            ["size"] = prefix.Size,
            ["usesLiquids"] = prefix.UsesLiquids,
        };
        json["fileBytes"] = resident.File.FileBytes;
        json["linkSize"] = resident.File.LinkSize;
        json["header"] = new JsonObject
        {
            ["detour"] = new JsonArray(h.x, h.y, h.layer),
            ["userId"] = h.userId,
            ["polyCount"] = h.polyCount,
            ["vertCount"] = h.vertCount,
            ["detailMeshCount"] = h.detailMeshCount,
            ["detailVertCount"] = h.detailVertCount,
            ["detailTriCount"] = h.detailTriCount,
            ["maxLinkCount"] = h.maxLinkCount,
            ["bvNodeCount"] = h.bvNodeCount,
            ["offMeshConCount"] = h.offMeshConCount,
            ["offMeshBase"] = h.offMeshBase,
            ["walkableHeight"] = h.walkableHeight,
            ["walkableRadius"] = h.walkableRadius,
            ["walkableClimb"] = h.walkableClimb,
            ["bvQuantFactor"] = h.bvQuantFactor,
            ["boundsMin"] = Json(NavGeometry.ToWorld(h.bmin)),
            ["boundsMax"] = Json(NavGeometry.ToWorld(h.bmax)),
        };
        json["links"] = new JsonObject
        {
            ["internal"] = counts.Internal,
            ["external"] = counts.External,
            ["offMesh"] = counts.OffMesh,
            ["stale"] = counts.Stale,
            ["allocated"] = counts.Allocated,
            ["capacity"] = counts.MaxLinkCount,
        };
        json["colorKeys"] = new JsonObject([.. byColor.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))]);
        json["areas"] = new JsonObject([.. byArea.Select(kv => KeyValuePair.Create(kv.Key.ToString(CultureInfo.InvariantCulture), (JsonNode?)kv.Value))]);

        var connections = new JsonArray();
        var overlayPoints = new JsonArray();
        var overlaySegments = new JsonArray();
        string color = ReferenceColor();
        foreach (DtOffMeshConnection con in data.offMeshCons ?? [])
        {
            WorldPoint from = NavGeometry.ToWorld(con.pos[0]);
            WorldPoint to = NavGeometry.ToWorld(con.pos[1]);
            connections.Add(new JsonObject
            {
                ["poly"] = con.poly,
                ["from"] = Json(from),
                ["to"] = Json(to),
                ["radius"] = con.rad,
                ["flags"] = con.flags,
                ["bidirectional"] = (con.flags & DtDetour.DT_OFFMESH_CON_BIDIR) != 0,
                ["side"] = con.side,
                ["userId"] = con.userId,
            });
            overlaySegments.Add(new JsonObject { ["from"] = Json(from), ["to"] = Json(to), ["label"] = $"off-mesh {con.poly}", ["color"] = color });
        }

        json["offMeshConnections"] = connections;
        json["neighbours"] = Json(Summarize(view));
        (double minX, double maxX, double minY, double maxY) = WorldGrid.Bounds(tile);
        json["overlay"] = new JsonObject
        {
            ["points"] = overlayPoints,
            ["segments"] = overlaySegments,
            ["boxes"] = new JsonArray
            {
                new JsonObject
                {
                    ["min"] = Json(new WorldPoint(minX, minY, NavGeometry.ToWorld(h.bmin).Z)),
                    ["max"] = Json(new WorldPoint(maxX, maxY, NavGeometry.ToWorld(h.bmax).Z)),
                    ["label"] = $"ref navmesh tile {tile.X},{tile.Y}",
                    ["color"] = color,
                },
            },
        };
        return json;
    }

    private static string ReferenceColor() => LayerCatalog.Get(LayerIds.RefNavPoly).DefaultColor;
}
