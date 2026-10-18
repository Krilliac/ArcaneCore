using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Terrain;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Atlas.Server;

/// <summary>
/// The server-view commands: <c>query.height</c>, <c>query.los</c>, <c>query.path</c>, <c>query.floorstack</c> and <c>server.status</c>. They
/// validate every argument (finite, inside the map) before the engine is touched, run the query on the engine's owner thread, and return the
/// report as JSON plus an <c>overlay</c> the UI draws. A refusal (bad arguments, missing data) becomes a <see cref="CommandException"/> with a
/// precise message and is logged at Information; an unexpected failure becomes <see cref="CommandException.Failed"/>.
/// <para>Thread affinity: handlers may run on any thread; all engine access is queued to the owner thread by <see cref="ServerEngine"/>.</para>
/// </summary>
public static class ServerCommands
{
    /// <summary>The height query.</summary>
    public const string HeightName = "query.height";

    /// <summary>The line-of-sight query.</summary>
    public const string LosName = "query.los";

    /// <summary>The path query.</summary>
    public const string PathName = "query.path";

    /// <summary>The column query.</summary>
    public const string FloorStackName = "query.floorstack";

    /// <summary>The engine status.</summary>
    public const string StatusName = "server.status";

    /// <summary>Default lowest height of a column query.</summary>
    public const float DefaultZMin = -500f;

    /// <summary>Default highest height of a column query.</summary>
    public const float DefaultZMax = 2500f;

    /// <summary>Largest downward search of a height query (the world is 34 km across; beyond a few thousand yards the number is a mistake).</summary>
    public const float MaxSearch = 10000f;

    /// <summary>Most points a path query may ask for.</summary>
    public const int MaxPathPoints = 4096;

    /// <summary>Most search nodes a path query may ask for (the navmesh query's scratch grows to this, NavMeshQuery.cs:379-380).</summary>
    public const int MaxPathNodes = 32768;

    private const string Clear = "#3CCB9A";
    private const string Blocked = "#E5484D";
    private const string Neutral = "#C8D3DC";
    private const string Marker = "#FFD166";

    private static readonly string[] MoverKeys = ["canWalk", "canSwim", "canFly", "isPlayer", "inhabit", "pet"];
    private static readonly string[] OptionKeys = ["loadTiles", "allowPartial", "maxPoints", "maxSearchNodes", "endTolerance", "wideEndSearch", "includeFlags", "excludeFlags"];

    /// <summary>The command definitions over <paramref name="engine"/>.</summary>
    public static IReadOnlyList<CommandDefinition> Create(ServerEngine engine, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(log);
        return
        [
            new CommandDefinition(
                HeightName,
                "What the server's terrain, liquid, model, navmesh and combined height lookups answer at a position.",
                [
                    new CommandArg("map", ArgType.Integer, "Map id.", Required: true),
                    new CommandArg("at", ArgType.Point3, "Position [x, y, z] (world coordinates). Give this or x, y and z."),
                    new CommandArg("x", ArgType.Number, "World X (with y and z, instead of 'at')."),
                    new CommandArg("y", ArgType.Number, "World Y (with x and z, instead of 'at')."),
                    new CommandArg("z", ArgType.Number, "World Z (with x and y, instead of 'at')."),
                    new CommandArg("search", ArgType.Number, $"How far below the position (plus {ServerEngine.FloorProbeOffset:0} yards) the model and mesh floors are looked for, 0 to {MaxSearch:0}.", Default: JsonValue.Create((double)MapCollision.DefaultHeightSearch)),
                ],
                (inv, ct) => Guard(HeightName, log, () => HeightAsync(engine, inv, ct))),
            new CommandDefinition(
                LosName,
                "Line of sight between two points as the server decides it (spell and movement tests) plus a terrain sample.",
                [
                    new CommandArg("map", ArgType.Integer, "Map id.", Required: true),
                    new CommandArg("a", ArgType.Point3, "From [x, y, z].", Required: true),
                    new CommandArg("b", ArgType.Point3, "To [x, y, z].", Required: true),
                ],
                (inv, ct) => Guard(LosName, log, () => LosAsync(engine, inv, ct))),
            new CommandDefinition(
                PathName,
                "The path the server's navmesh pathfinder builds between two points, with its corridor of polygons.",
                [
                    new CommandArg("map", ArgType.Integer, "Map id.", Required: true),
                    new CommandArg("a", ArgType.Point3, "Start [x, y, z].", Required: true),
                    new CommandArg("b", ArgType.Point3, "End [x, y, z].", Required: true),
                    new CommandArg("mover", ArgType.Object, "Who moves: {canWalk, canSwim, canFly, isPlayer} (all booleans, default walk only) or {inhabit: bits 1 ground, 2 water, 4 air, pet?}. Default: a player (walks and swims)."),
                    new CommandArg("options", ArgType.Object, $"Path options: {{loadTiles (default true), allowPartial, maxPoints 2..{MaxPathPoints}, maxSearchNodes 1..{MaxPathNodes}, endTolerance 0..{PathOptions.VmangosEndTolerance * 4:0}, wideEndSearch, includeFlags, excludeFlags}}."),
                ],
                (inv, ct) => Guard(PathName, log, () => PathAsync(engine, inv, ct))),
            new CommandDefinition(
                FloorStackName,
                "Every surface on a vertical column (terrain, each model hit, liquid, navmesh polygons), lowest first.",
                [
                    new CommandArg("map", ArgType.Integer, "Map id.", Required: true),
                    new CommandArg("x", ArgType.Number, "World X.", Required: true),
                    new CommandArg("y", ArgType.Number, "World Y.", Required: true),
                    new CommandArg("zMin", ArgType.Number, "Lowest height searched.", Default: JsonValue.Create((double)DefaultZMin)),
                    new CommandArg("zMax", ArgType.Number, "Highest height searched.", Default: JsonValue.Create((double)DefaultZMax)),
                ],
                (inv, ct) => Guard(FloorStackName, log, () => FloorStackAsync(engine, inv, ct))),
            new CommandDefinition(
                StatusName,
                "The server engine's state: data directories, resident tiles, query counters and parsed-file caches.",
                [],
                (_, _) => Guard(StatusName, log, () => Task.FromResult<JsonNode?>(engine.Status().ToJson()))),
        ];
    }

    /// <summary>Runs a handler body, turning engine refusals and failures into <see cref="CommandException"/>.</summary>
    private static async Task<JsonNode?> Guard(string command, ILogger log, Func<Task<JsonNode?>> body)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (CommandException)
        {
            throw;
        }
        catch (ServerEngineException e)
        {
            log.LogInformation("{Command} refused: {Message}", command, e.Message);
            string code = e.Kind == ServerErrorKind.BadRequest ? CommandException.BadArguments : CommandException.Unavailable;
            throw new CommandException(code, $"{command}: {e.Message}", e);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ObjectDisposedException e)
        {
            throw new CommandException(CommandException.Unavailable, $"{command}: the server engine has been shut down.", e);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // The engine has already logged the failure with its stack; the caller gets the type and message.
            throw new CommandException(CommandException.Failed, $"{command}: {e.GetType().Name}: {e.Message}", e);
        }
    }

    // ----- query.height -----

    private static async Task<JsonNode?> HeightAsync(ServerEngine engine, CommandInvocation inv, CancellationToken ct)
    {
        uint map = inv.GetMapId();
        Vector3 at = Position(inv);
        float search = Search(inv);
        HeightReport report = await engine.QueryHeightAsync(map, at, search, ct).ConfigureAwait(false);
        JsonObject json = report.ToJson();
        json["files"] = FilesAt(engine, map, at.X, at.Y);
        json["overlay"] = HeightOverlay(report).ToJson();
        return json;
    }

    private static ServerOverlay HeightOverlay(HeightReport report)
    {
        var overlay = new ServerOverlay();
        Vector3 at = report.At;
        overlay.Point(at, "query", Neutral);
        var heights = new List<float> { at.Z };

        void Mark(float? z, string label, string? color, string? colorKey = null)
        {
            if (z is { } h && float.IsFinite(h))
            {
                overlay.Point(new Vector3(at.X, at.Y, h), $"{label} z={Fmt(h)}", color, colorKey);
                heights.Add(h);
            }
        }

        Mark(report.Terrain.Z, "terrain", Color(LayerIds.Terrain));
        Mark(report.Models.Z, "model", Color(LayerIds.VMapWmo));
        if (report.Models.ZIgnoringM2 != report.Models.Z)
        {
            Mark(report.Models.ZIgnoringM2, "model without doodads", Color(LayerIds.VMapWmo));
        }

        Mark(report.Mesh.FloorZ, "mesh", Color(LayerIds.NavPoly));
        Mark(report.CollisionZ, "collision", Marker);
        if (report.Liquid.Level is { } level)
        {
            Mark(level, $"liquid {report.Liquid.TypeName}", null, TerrainLayerSource.LiquidColorKey(report.Liquid.Type));
        }

        if (heights.Count > 1)
        {
            overlay.Segment(new Vector3(at.X, at.Y, heights.Min()), new Vector3(at.X, at.Y, heights.Max()), "column", Neutral, dashed: true);
        }

        return overlay;
    }

    // ----- query.los -----

    private static async Task<JsonNode?> LosAsync(ServerEngine engine, CommandInvocation inv, CancellationToken ct)
    {
        uint map = inv.GetMapId();
        Vector3 a = Point(inv, "a");
        Vector3 b = Point(inv, "b");
        LosReport report = await engine.QueryLosAsync(map, a, b, ct).ConfigureAwait(false);
        JsonObject json = report.ToJson();
        json["overlay"] = LosOverlay(report).ToJson();
        return json;
    }

    private static ServerOverlay LosOverlay(LosReport report)
    {
        var overlay = new ServerOverlay();
        bool allClear = report.Spell.Clear && report.Movement.Clear && report.Terrain.Clear;
        overlay.Segment(report.From, report.To, allClear ? "clear" : "blocked", allClear ? Clear : Neutral, dashed: !allClear);
        overlay.Point(report.From, "from", Neutral);
        overlay.Point(report.To, "to", Neutral);
        if (report.Spell.Hit is { } spell)
        {
            overlay.Point(spell, "spell line of sight blocked (doodads ignored)", Blocked);
            overlay.Segment(report.From, spell, null, Blocked);
        }

        if (report.Movement.Hit is { } movement)
        {
            overlay.Point(movement, "movement blocked (doodads counted)", Blocked);
        }

        if (report.Terrain.Hit is { } terrain)
        {
            overlay.Point(terrain, "under the terrain", Marker);
        }

        return overlay;
    }

    // ----- query.path -----

    private static async Task<JsonNode?> PathAsync(ServerEngine engine, CommandInvocation inv, CancellationToken ct)
    {
        uint map = inv.GetMapId();
        Vector3 a = Point(inv, "a");
        Vector3 b = Point(inv, "b");
        PathMover mover = Mover(inv.OptionalObject("mover"));
        PathOptions options = Options(inv.OptionalObject("options"));
        PathReport report = await engine.QueryPathAsync(map, a, b, mover, options, ct).ConfigureAwait(false);
        JsonObject json = report.ToJson();
        json["overlay"] = PathOverlay(report).ToJson();
        return json;
    }

    private static ServerOverlay PathOverlay(PathReport report)
    {
        var overlay = new ServerOverlay();
        bool straight = report.Status is "straight-line" or "none";
        if (report.Points.Count >= 2)
        {
            overlay.Polyline(report.Points, $"path {report.Status}", straight ? Neutral : Color(LayerIds.NavPoly), dashed: straight);
        }

        if (report.Points.Count > 2)
        {
            overlay.Segment(report.Start, report.End, "straight line", Neutral, dashed: true);
        }

        overlay.Point(report.Start, "start", Clear);
        overlay.Point(report.End, "end", report.Status is "complete" ? Clear : Blocked);
        return overlay;
    }

    private static PathMover Mover(JsonObject? mover)
    {
        if (mover is null)
        {
            return PathMover.Player;
        }

        RejectUnknown(PathName, "mover", mover, MoverKeys);
        if (mover.ContainsKey("inhabit"))
        {
            if (mover.ContainsKey("canWalk") || mover.ContainsKey("canSwim") || mover.ContainsKey("canFly") || mover.ContainsKey("isPlayer"))
            {
                throw Bad($"{PathName}: mover takes either 'inhabit' or canWalk/canSwim/canFly/isPlayer, not both.");
            }

            long bits = ReadInt(mover, "mover", "inhabit", 0, 7);
            return PathMover.FromInhabit((uint)bits, ReadBool(mover, "mover", "pet", false));
        }

        if (mover.ContainsKey("pet"))
        {
            throw Bad($"{PathName}: mover.pet only goes with mover.inhabit.");
        }

        return new PathMover(
            ReadBool(mover, "mover", "canWalk", true),
            ReadBool(mover, "mover", "canSwim", false),
            ReadBool(mover, "mover", "canFly", false),
            ReadBool(mover, "mover", "isPlayer", false));
    }

    private static PathOptions Options(JsonObject? options)
    {
        var defaults = PathOptions.Default;
        if (options is null)
        {
            return new PathOptions { LoadTiles = true };
        }

        RejectUnknown(PathName, "options", options, OptionKeys);
        return new PathOptions
        {
            LoadTiles = ReadBool(options, "options", "loadTiles", true),
            AllowPartial = ReadBool(options, "options", "allowPartial", defaults.AllowPartial),
            MaxPoints = (int)ReadInt(options, "options", "maxPoints", 2, MaxPathPoints, defaults.MaxPoints),
            MaxSearchNodes = (int)ReadInt(options, "options", "maxSearchNodes", 1, MaxPathNodes, defaults.MaxSearchNodes),
            EndTolerance = (float)ReadNumber(options, "options", "endTolerance", 0, PathOptions.VmangosEndTolerance * 4, defaults.EndTolerance),
            WideEndSearch = ReadBool(options, "options", "wideEndSearch", defaults.WideEndSearch),
            IncludeFlags = (NavTerrain)ReadInt(options, "options", "includeFlags", 0, ushort.MaxValue, (long)defaults.IncludeFlags),
            ExcludeFlags = (NavTerrain)ReadInt(options, "options", "excludeFlags", 0, ushort.MaxValue, (long)defaults.ExcludeFlags),
        };
    }

    // ----- query.floorstack -----

    private static async Task<JsonNode?> FloorStackAsync(ServerEngine engine, CommandInvocation inv, CancellationToken ct)
    {
        uint map = inv.GetMapId();
        float x = Coordinate(inv, "x");
        float y = Coordinate(inv, "y");
        float zMin = HeightArg(inv, "zMin", DefaultZMin);
        float zMax = HeightArg(inv, "zMax", DefaultZMax);
        if (zMin > zMax)
        {
            throw Bad($"{FloorStackName}: zMin ({Fmt(zMin)}) is above zMax ({Fmt(zMax)}).");
        }

        FloorStackReport report = await engine.QueryFloorStackAsync(map, x, y, zMin, zMax, ct).ConfigureAwait(false);
        JsonObject json = report.ToJson();
        json["files"] = FilesAt(engine, map, x, y);
        json["overlay"] = FloorStackOverlay(report).ToJson();
        return json;
    }

    private static ServerOverlay FloorStackOverlay(FloorStackReport report)
    {
        var overlay = new ServerOverlay();
        overlay.Segment(new Vector3(report.X, report.Y, report.ZMin), new Vector3(report.X, report.Y, report.ZMax), "column", Neutral, dashed: true);
        foreach (FloorSurface surface in report.Surfaces)
        {
            string label = $"{surface.Kind} z={Fmt(surface.Z)}";
            var at = new Vector3(report.X, report.Y, surface.Z);
            switch (surface.Kind)
            {
                case "terrain":
                    overlay.Point(at, label, Color(LayerIds.Terrain));
                    break;
                case "model":
                    overlay.Point(at, label, Color(LayerIds.VMapWmo));
                    break;
                case "mesh":
                    overlay.Point(at, label, Color(LayerIds.NavPoly));
                    break;
                default:
                    byte type = JsonScalars.TryGetInt64(surface.Detail["type"], out long bits) && bits is >= 0 and <= byte.MaxValue ? (byte)bits : (byte)0;
                    overlay.Point(at, label, null, TerrainLayerSource.LiquidColorKey(type));
                    break;
            }
        }

        return overlay;
    }

    // ----- argument validation -----

    /// <summary>The position of <c>at</c> or of <c>x</c>, <c>y</c>, <c>z</c>, validated finite and inside the map.</summary>
    private static Vector3 Position(CommandInvocation inv)
    {
        bool hasAt = inv.Has("at");
        bool hasParts = inv.Has("x") || inv.Has("y") || inv.Has("z");
        if (hasAt == hasParts)
        {
            throw Bad($"{inv.Definition.Name}: give the position as 'at' [x, y, z] or as x, y and z, not both and not neither.");
        }

        if (hasAt)
        {
            return Point(inv, "at");
        }

        if (!(inv.Has("x") && inv.Has("y") && inv.Has("z")))
        {
            throw Bad($"{inv.Definition.Name}: x, y and z must all be given.");
        }

        return Checked(inv.Definition.Name, "position", inv.GetNumber("x"), inv.GetNumber("y"), inv.GetNumber("z"));
    }

    private static Vector3 Point(CommandInvocation inv, string name)
    {
        WorldPoint p = inv.GetPoint(name);
        return Checked(inv.Definition.Name, name, p.X, p.Y, p.Z);
    }

    private static Vector3 Checked(string command, string name, double x, double y, double z)
    {
        float fx = (float)x;
        float fy = (float)y;
        float fz = (float)z;
        if (!GridDefines.IsValidMapCoord(fx) || !GridDefines.IsValidMapCoord(fy))
        {
            throw Bad($"{command}: {name} ({Fmt(x)}, {Fmt(y)}, {Fmt(z)}) is outside the map; x and y must be within +-{GridDefines.MapHalfSize - 0.5f:0.#}.");
        }

        if (!GridDefines.IsValidZCoord(fz))
        {
            throw Bad($"{command}: {name} has z {Fmt(z)}, outside +-400000.");
        }

        return new Vector3(fx, fy, fz);
    }

    private static float Coordinate(CommandInvocation inv, string name)
    {
        double value = inv.GetNumber(name);
        float f = (float)value;
        return GridDefines.IsValidMapCoord(f)
            ? f
            : throw Bad($"{inv.Definition.Name}: {name} {Fmt(value)} is outside the map; it must be within +-{GridDefines.MapHalfSize - 0.5f:0.#}.");
    }

    private static float HeightArg(CommandInvocation inv, string name, float fallback)
    {
        double? value = inv.OptionalNumber(name);
        if (value is null)
        {
            return fallback;
        }

        float f = (float)value.Value;
        return GridDefines.IsValidZCoord(f) ? f : throw Bad($"{inv.Definition.Name}: {name} {Fmt(value.Value)} is outside +-400000.");
    }

    private static float Search(CommandInvocation inv)
    {
        double? value = inv.OptionalNumber("search");
        if (value is null)
        {
            return MapCollision.DefaultHeightSearch;
        }

        return value.Value is >= 0 and <= MaxSearch
            ? (float)value.Value
            : throw Bad($"{inv.Definition.Name}: search {Fmt(value.Value)} must be between 0 and {MaxSearch:0}.");
    }

    private static void RejectUnknown(string command, string argument, JsonObject value, string[] allowed)
    {
        foreach ((string key, _) in value)
        {
            if (!allowed.Contains(key, StringComparer.Ordinal))
            {
                throw Bad($"{command}: unknown {argument} key '{key}' (expected {string.Join(", ", allowed)}).");
            }
        }
    }

    private static bool ReadBool(JsonObject o, string argument, string key, bool fallback)
    {
        if (!o.TryGetPropertyValue(key, out JsonNode? node) || node is null)
        {
            return fallback;
        }

        return node is JsonValue v && v.TryGetValue(out bool b) ? b : throw Bad($"{PathName}: {argument}.{key} must be true or false.");
    }

    private static long ReadInt(JsonObject o, string argument, string key, long min, long max, long fallback = 0)
    {
        if (!o.TryGetPropertyValue(key, out JsonNode? node) || node is null)
        {
            return fallback;
        }

        return JsonScalars.TryGetInt64(node, out long value) && value >= min && value <= max
            ? value
            : throw Bad($"{PathName}: {argument}.{key} must be an integer from {min} to {max}.");
    }

    private static double ReadNumber(JsonObject o, string argument, string key, double min, double max, double fallback)
    {
        if (!o.TryGetPropertyValue(key, out JsonNode? node) || node is null)
        {
            return fallback;
        }

        return JsonScalars.TryGetDouble(node, out double value) && value >= min && value <= max
            ? value
            : throw Bad($"{PathName}: {argument}.{key} must be a number from {Fmt(min)} to {Fmt(max)}.");
    }

    private static JsonObject FilesAt(ServerEngine engine, uint map, float x, float y)
        => TerrainTile.TileOf(x, y) is { } tile ? engine.DescribeFiles(new TileKey(map, tile.X, tile.Y)) : [];

    private static string Color(string layerId) => LayerCatalog.Get(layerId).DefaultColor;

    private static string Fmt(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static CommandException Bad(string message) => new(CommandException.BadArguments, message);
}
