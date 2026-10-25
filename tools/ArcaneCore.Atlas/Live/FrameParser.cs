using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Game.Maps.Grid;

namespace ArcaneCore.Atlas.Live;

/// <summary>A frame that passed validation, with the JSON that is pushed to the UI (invalid entities removed, guids canonical).</summary>
/// <param name="Frame">The typed frame.</param>
/// <param name="Json">The frame object as pushed on <see cref="EventHub.Topics.LiveFrame"/>.</param>
/// <param name="Rejected">Entities and grid pairs dropped from an otherwise good frame.</param>
/// <param name="FirstProblem">Why the first of them was dropped.</param>
internal sealed record ParsedFrame(LiveFrame Frame, JsonObject Json, int Rejected, string? FirstProblem);

/// <summary>The server's answer to <c>hello</c>.</summary>
/// <param name="Protocol">The protocol number the server speaks.</param>
/// <param name="Sha">The server build, when it reported one.</param>
/// <param name="Pid">The server process id, when it reported one.</param>
/// <param name="Maps">The maps and instances it has loaded.</param>
/// <param name="MapsRejected">Entries of <c>maps</c> that were malformed and dropped.</param>
/// <param name="FirstMapProblem">Why the first of them was dropped.</param>
/// <param name="Tools">The daemon offers the packet tools (its hello says <c>"tools":true</c>; docs/areas/map-feed.md, "Packet tools").</param>
internal sealed record HelloInfo(int Protocol, string? Sha, int? Pid, IReadOnlyList<ServerMap> Maps, int MapsRejected = 0, string? FirstMapProblem = null, bool Tools = false);

/// <summary>
/// Validates map-feed messages (design section 9). A message whose structure is wrong is refused whole; inside an accepted frame, an
/// entity or grid pair that is wrong is dropped and counted, so one bad creature never blinds the viewer.
/// <para>
/// Every JSON number is read from nodes that <c>JsonNode.Parse</c> made from wire text, which convert to any numeric type; the
/// code-built-node pitfall that <see cref="JsonScalars"/> guards against cannot occur here. Pure; any thread.
/// </para>
/// </summary>
internal static class FrameParser
{
    // ref: D:\ArcaneCore-lanes\_wf\ATLAS-2026-10-18.md:216-219 (frame and entity field list)

    /// <summary>
    /// Largest accepted absolute coordinate. A map spans +-17066.666 yd (Core/WorldGrid.cs, vmap MapMid) and the server's no-height
    /// sentinel is -100000 (src/ArcaneCore.Game/Maps/Grid/GridDefines.cs, <c>MaxHeight</c> 100000), so one million is generous and still
    /// refuses values that would wreck a camera.
    /// </summary>
    public const double MaxCoordinate = 1_000_000;

    /// <summary>Longest spline or route accepted.</summary>
    public const int MaxPathPoints = 16_384;

    /// <summary>Deepest JSON nesting accepted; real frames nest to about six levels.</summary>
    public const int MaxJsonDepth = 32;

    /// <summary>Parser options for one wire line.</summary>
    public static JsonDocumentOptions DocumentOptions { get; } = new() { MaxDepth = MaxJsonDepth };

    public static bool TryParseHello(JsonObject root, [NotNullWhen(true)] out HelloInfo? hello, [NotNullWhen(false)] out string? problem)
    {
        try
        {
            return TryParseHelloCore(root, out hello, out problem);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            hello = null;
            problem = $"hello contains invalid JSON text or duplicate keys: {ex.Message}";
            return false;
        }
    }

    private static bool TryParseHelloCore(JsonObject root, [NotNullWhen(true)] out HelloInfo? hello, [NotNullWhen(false)] out string? problem)
    {
        hello = null;
        if (!TryInteger(root["protocol"], 0, int.MaxValue, out long protocol))
        {
            problem = "hello has no protocol number";
            return false;
        }

        int? pid = null;
        if (root["pid"] is { } pidNode)
        {
            if (!TryInteger(pidNode, 1, int.MaxValue, out long p))
            {
                problem = "hello pid is not a process id";
                return false;
            }

            pid = (int)p;
        }

        string? sha = null;
        if (root["sha"] is { } shaNode && !TryString(shaNode, out sha))
        {
            problem = "hello sha is not a string";
            return false;
        }

        var maps = new List<ServerMap>();
        int mapsRejected = 0;
        string? firstMapProblem = null;
        if (root["maps"] is JsonArray array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonObject o
                    && TryInteger(o["map"], 0, uint.MaxValue, out long map)
                    && TryOptionalInteger(o["instance"], 0, uint.MaxValue, 0, out long instance)
                    && TryOptionalInteger(o["players"], 0, int.MaxValue, 0, out long players)
                    && TryOptionalInteger(o["creatures"], 0, int.MaxValue, 0, out long creatures))
                {
                    maps.Add(new ServerMap((uint)map, (uint)instance, (int)players, (int)creatures));
                }
                else
                {
                    mapsRejected++;
                    firstMapProblem ??= string.Create(CultureInfo.InvariantCulture, $"hello maps[{i}] is not {{map, instance, players, creatures}} with non-negative integers");
                }
            }
        }
        else if (root["maps"] is not null)
        {
            problem = "hello maps is not an array";
            return false;
        }

        bool tools = false;
        if (root["tools"] is { } toolsNode)
        {
            if (toolsNode is not JsonValue toolsValue || !toolsValue.TryGetValue(out tools))
            {
                problem = "hello tools is not true or false";
                return false;
            }
        }

        hello = new HelloInfo((int)protocol, sha, pid, maps, mapsRejected, firstMapProblem, tools);
        problem = null;
        return true;
    }

    public static bool TryParseFrame(
        JsonObject root,
        long receivedTimestamp,
        [NotNullWhen(true)] out ParsedFrame? parsed,
        [NotNullWhen(false)] out string? problem)
    {
        try
        {
            return TryParseFrameCore(root, receivedTimestamp, out parsed, out problem);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            parsed = null;
            problem = $"frame contains invalid JSON text or duplicate keys: {ex.Message}";
            return false;
        }
    }

    private static bool TryParseFrameCore(
        JsonObject root,
        long receivedTimestamp,
        [NotNullWhen(true)] out ParsedFrame? parsed,
        [NotNullWhen(false)] out string? problem)
    {
        parsed = null;
        if (!TryInteger(root["map"], 0, uint.MaxValue, out long map))
        {
            problem = "frame has no map id";
            return false;
        }

        if (!TryOptionalInteger(root["instance"], 0, uint.MaxValue, 0, out long instance))
        {
            problem = "frame instance is not an instance id";
            return false;
        }

        if (!TryOptionalInteger(root["seq"], 0, long.MaxValue, 0, out long seq))
        {
            problem = "frame seq is not a non-negative integer";
            return false;
        }

        if (!TryOptionalInteger(root["clockMs"], 0, long.MaxValue, 0, out long clock))
        {
            problem = "frame clockMs is not a non-negative integer";
            return false;
        }

        if (root["entities"] is not JsonArray entities)
        {
            problem = "frame has no entities array";
            return false;
        }

        JsonObject? counters = null;
        if (root["counters"] is { } countersNode)
        {
            if (countersNode is not JsonObject counterObject)
            {
                problem = "frame counters is not an object";
                return false;
            }

            _ = counterObject.Count;
            foreach ((_, JsonNode? val) in counterObject)
            {
                if (val is JsonValue v && v.GetValueKind() == JsonValueKind.String)
                {
                    _ = v.GetValue<string>();
                }
            }

            counters = (JsonObject)counterObject.DeepClone();
        }

        int rejected = 0;
        string? firstProblem = null;

        var grids = new List<(int X, int Y)>();
        if (root["grids"] is { } gridsNode)
        {
            if (gridsNode is not JsonArray gridArray)
            {
                problem = "frame grids is not an array";
                return false;
            }

            // ref: src/ArcaneCore.Game/Maps/Grid/GridDefines.cs:22 (MaxNumberOfGrids = 64 grids per side, indices 0..63)
            for (int i = 0; i < gridArray.Count; i++)
            {
                if (gridArray[i] is JsonArray { Count: 2 } pair
                    && TryInteger(pair[0], 0, GridDefines.MaxNumberOfGrids - 1, out long gx)
                    && TryInteger(pair[1], 0, GridDefines.MaxNumberOfGrids - 1, out long gy))
                {
                    grids.Add(((int)gx, (int)gy));
                }
                else
                {
                    rejected++;
                    firstProblem ??= string.Create(CultureInfo.InvariantCulture, $"grids[{i}] is not a [gx, gy] pair inside the 64 x 64 grid");
                }
            }
        }

        var good = new List<LiveEntity>(entities.Count);
        List<int>? dropped = null;
        for (int i = 0; i < entities.Count; i++)
        {
            if (TryEntity(entities[i], (uint)map, (uint)instance, out LiveEntity? entity, out string? entityProblem))
            {
                good.Add(entity);
            }
            else
            {
                rejected++;
                firstProblem ??= string.Create(CultureInfo.InvariantCulture, $"entities[{i}]: {entityProblem}");
                (dropped ??= []).Add(i);
            }
        }

        // The UI gets the frame without the entities that failed validation, and with canonical guid text, so what it draws is what the
        // store holds. Removal runs from the back so earlier indices stay valid.
        if (dropped is not null)
        {
            for (int i = dropped.Count - 1; i >= 0; i--)
            {
                entities.RemoveAt(dropped[i]);
            }
        }

        Canonicalize(entities);

        var frame = new LiveFrame(seq, clock, (uint)map, (uint)instance, grids, counters, good, receivedTimestamp);
        parsed = new ParsedFrame(frame, root, rejected, firstProblem);
        problem = null;
        return true;
    }

    private static void Canonicalize(JsonArray entities)
    {
        foreach (JsonNode? node in entities)
        {
            if (node is not JsonObject o)
            {
                continue;
            }

            Rewrite(o, "g");
            Rewrite(o, "tg");
        }

        static void Rewrite(JsonObject o, string key)
        {
            if (o[key] is JsonValue v && v.TryGetValue(out string? text) && LiveGuid.TryParse(text, out ulong guid))
            {
                string canonical = LiveGuid.Format(guid);
                if (!string.Equals(text, canonical, StringComparison.Ordinal))
                {
                    o[key] = canonical;
                }
            }
        }
    }

    private static bool TryEntity(JsonNode? node, uint map, uint instance, [NotNullWhen(true)] out LiveEntity? entity, [NotNullWhen(false)] out string? problem)
    {
        entity = null;
        if (node is not JsonObject o)
        {
            problem = "is not an object";
            return false;
        }

        if (!TryString(o["g"], out string? guidText) || !LiveGuid.TryParse(guidText, out ulong id) || id == 0)
        {
            problem = "g is not a non-zero hex guid";
            return false;
        }

        if (!TryString(o["k"], out string? kindText) || !LiveKinds.TryParse(kindText, out LiveKind kind))
        {
            problem = $"k '{Clip(kindText)}' is not one of {string.Join('|', LiveKinds.Names)}";
            return false;
        }

        if (!TryCoordinate(o["x"], out double x) || !TryCoordinate(o["y"], out double y) || !TryCoordinate(o["z"], out double z))
        {
            problem = "x, y and z must be finite numbers within +-1000000";
            return false;
        }

        if (!TryOptionalInteger(o["e"], 0, uint.MaxValue, 0, out long entry))
        {
            problem = "e is not an entry id";
            return false;
        }

        string? name = null;
        if (o["n"] is { } nameNode && !TryString(nameNode, out name))
        {
            problem = "n is not a string";
            return false;
        }

        double orientation = 0;
        if (o["o"] is { } orientationNode && !TryNumber(orientationNode, out orientation))
        {
            problem = "o is not a finite number";
            return false;
        }

        double? hp = null;
        if (o["hp"] is { } hpNode)
        {
            if (!TryNumber(hpNode, out double h))
            {
                problem = "hp is not a finite number";
                return false;
            }

            hp = h;
        }

        string? motion = null;
        if (o["mt"] is { } motionNode && !TryString(motionNode, out motion))
        {
            problem = "mt is not a string";
            return false;
        }

        ulong? target = null;
        if (o["tg"] is { } targetNode)
        {
            if (!TryString(targetNode, out string? targetText) || !LiveGuid.TryParse(targetText, out ulong t))
            {
                problem = "tg is not a hex guid";
                return false;
            }

            target = t == 0 ? null : t;
        }

        WorldPoint? home = null;
        if (o["home"] is { } homeNode)
        {
            if (!TryPoint(homeNode, out WorldPoint h))
            {
                problem = "home is not [x, y, z]";
                return false;
            }

            home = h;
        }

        if (!TrySpline(o["sp"], out LiveSpline? spline, out problem) || !TryRoute(o["route"], out LiveRoute? route, out problem))
        {
            return false;
        }

        // r and cl are optional and lenient: a missing, mistyped or out-of-range value reads as unknown (0) rather than refusing the entity.
        byte race = o["r"] is JsonValue raceNode && raceNode.TryGetValue(out int raceId) && raceId is > 0 and <= byte.MaxValue ? (byte)raceId : (byte)0;
        byte playerClass = o["cl"] is JsonValue classNode && classNode.TryGetValue(out int classId) && classId is > 0 and <= byte.MaxValue ? (byte)classId : (byte)0;
        entity = new LiveEntity(id, kind, (uint)entry, name, map, instance, new WorldPoint(x, y, z), orientation, hp, motion, target, home, spline, route, race, playerClass);
        problem = null;
        return true;
    }

    private static bool TrySpline(JsonNode? node, out LiveSpline? spline, [NotNullWhen(false)] out string? problem)
    {
        spline = null;
        problem = null;
        if (node is null)
        {
            return true;
        }

        if (node is not JsonObject o)
        {
            problem = "sp is not an object";
            return false;
        }

        if (!TryPoint(o["start"], out WorldPoint start))
        {
            problem = "sp.start is not [x, y, z]";
            return false;
        }

        if (!TryPoints(o["pts"], out IReadOnlyList<WorldPoint>? points))
        {
            problem = "sp.pts is not a list of [x, y, z]";
            return false;
        }

        bool run = false;
        if (!TryOptionalInteger(o["id"], 0, uint.MaxValue, 0, out long id)
            || !TryOptionalInteger(o["t0"], 0, long.MaxValue, 0, out long t0)
            || !TryOptionalInteger(o["dur"], 0, long.MaxValue, 0, out long dur)
            || (o["run"] is { } runNode && !TryBool(runNode, out run)))
        {
            problem = "sp has a mistyped id, t0, dur or run";
            return false;
        }

        spline = new LiveSpline((uint)id, start, points, t0, dur, run);
        return true;
    }

    private static bool TryRoute(JsonNode? node, out LiveRoute? route, [NotNullWhen(false)] out string? problem)
    {
        route = null;
        problem = null;
        if (node is null)
        {
            return true;
        }

        if (node is not JsonObject o)
        {
            problem = "route is not an object";
            return false;
        }

        if (!TryPoints(o["pts"], out IReadOnlyList<WorldPoint>? points))
        {
            problem = "route.pts is not a list of [x, y, z]";
            return false;
        }

        bool cut = false;
        if (o["cut"] is { } cutNode && !TryBool(cutNode, out cut))
        {
            problem = "route.cut is not a boolean";
            return false;
        }

        // ref: src/ArcaneCore.World/Ops/MapFeed/MapFeedCapture.cs (AddRoute clamps RouteNext = Math.Clamp(info.Next, 0, copy)).
        // Older or synthetic cut-route frames may still send next indexing the full uncut route (> points.Count), which means
        // "past every shipped corner"; we clamp it here for defensive compatibility, while on an uncut route it remains a corrupt index.
        if (!TryOptionalInteger(o["next"], 0, cut ? int.MaxValue : points.Count, 0, out long next))
        {
            problem = "route.next is not an index into route.pts";
            return false;
        }

        next = Math.Min(next, points.Count);

        var flags = new List<string>();
        if (o["flags"] is { } flagsNode)
        {
            if (flagsNode is not JsonArray flagArray)
            {
                problem = "route.flags is not an array";
                return false;
            }

            foreach (JsonNode? flag in flagArray)
            {
                if (!TryString(flag, out string? text))
                {
                    problem = "route.flags holds a non-string";
                    return false;
                }

                flags.Add(text);
            }
        }

        route = new LiveRoute(points, (int)next, flags, cut);
        return true;
    }

    private static bool TryPoints(JsonNode? node, [NotNullWhen(true)] out IReadOnlyList<WorldPoint>? points)
    {
        points = null;
        if (node is not JsonArray array || array.Count > MaxPathPoints)
        {
            return false;
        }

        var list = new List<WorldPoint>(array.Count);
        foreach (JsonNode? item in array)
        {
            if (!TryPoint(item, out WorldPoint p))
            {
                return false;
            }

            list.Add(p);
        }

        points = list;
        return true;
    }

    private static bool TryPoint(JsonNode? node, out WorldPoint point)
    {
        point = default;
        if (node is not JsonArray { Count: 3 } a || !TryCoordinate(a[0], out double x) || !TryCoordinate(a[1], out double y) || !TryCoordinate(a[2], out double z))
        {
            return false;
        }

        point = new WorldPoint(x, y, z);
        return true;
    }

    private static bool TryNumber(JsonNode? node, out double value)
    {
        value = 0;
        return node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue(out value) && double.IsFinite(value);
    }

    private static bool TryCoordinate(JsonNode? node, out double value)
        => TryNumber(node, out value) && Math.Abs(value) <= MaxCoordinate;

    public static bool TryInteger(JsonNode? node, long min, long max, out long value)
    {
        value = 0;
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number)
        {
            return false;
        }

        // 5.0 and 1e3 are integral JSON numbers but not Int64 text; JsonScalars accepts them like every other Atlas integer argument.
        if (!v.TryGetValue(out value) && !JsonScalars.TryGetInt64(node, out value))
        {
            return false;
        }

        return value >= min && value <= max;
    }

    private static bool TryOptionalInteger(JsonNode? node, long min, long max, long fallback, out long value)
    {
        if (node is null)
        {
            value = fallback;
            return true;
        }

        return TryInteger(node, min, max, out value);
    }

    public static bool TryString(JsonNode? node, [NotNullWhen(true)] out string? value)
    {
        value = null;
        try
        {
            return node is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.TryGetValue(out value);
        }
        catch (InvalidOperationException)
        {
            value = null;
            return false;
        }
    }

    private static bool TryBool(JsonNode? node, out bool value)
    {
        value = false;
        return node is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False && v.TryGetValue(out value);
    }

    private static string Clip(string? text)
        => text is null ? "(missing)" : text.Length <= 24 ? text : string.Concat(text.AsSpan(0, 24), "...");
}
