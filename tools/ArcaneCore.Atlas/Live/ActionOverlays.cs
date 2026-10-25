using System.Text.Json;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Live;

/// <summary>
/// Turns the data the <c>.atlas debug</c> and <c>.atlas spawns</c> leaves print (one JSON line each) into the <c>overlay</c> the web UI already
/// draws for any command result (points, segments, polylines). No new draw path: rings are polylines, threat is segments, labels ride on
/// points. Pure; any thread.
/// </summary>
public static class ActionOverlays
{
    private const int RingSegments = 48;
    private const string AggroColor = "#E5534B";
    private const string LeashColor = "#E0A030";
    private const string HomeColor = "#3CCB9A";
    private const string ThreatColor = "#D05CE3";
    private const string RouteColor = "#4DA3FF";
    private const string CreatureSpawnColor = "#FFD34D";
    private const string ObjectSpawnColor = "#4DA3FF";

    /// <summary>The first reply line that is a JSON object, or null.</summary>
    public static JsonObject? FirstJson(IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 1 && trimmed[0] == '{' && trimmed[^1] == '}')
            {
                try
                {
                    return JsonNode.Parse(trimmed) as JsonObject;
                }
                catch (JsonException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The overlay for one <c>.atlas debug</c> reply: an aggro ring around <paramref name="at"/> (the unit's live position, else its home),
    /// a leash ring around home, home and spawn points, the waypoint path, a line to every threat holder found in the live data, and the AI
    /// name as a label on the unit. Null when the reply is not a debug object.
    /// </summary>
    /// <param name="debug">The parsed reply line.</param>
    /// <param name="at">The unit's position from the live data, or null.</param>
    /// <param name="threatPosition">Looks up a threat holder's position by guid.</param>
    public static JsonObject? FromDebug(JsonObject debug, WorldPoint? at, Func<ulong, WorldPoint?> threatPosition)
    {
        ArgumentNullException.ThrowIfNull(debug);
        ArgumentNullException.ThrowIfNull(threatPosition);
        WorldPoint? home = ReadPoint(debug["home"]);
        WorldPoint? center = at ?? home;
        if (center is null)
        {
            return null;
        }

        var points = new JsonArray();
        var segments = new JsonArray();
        var polylines = new JsonArray();
        string ai = debug["ai"]?.GetValue<string>() ?? string.Empty;
        points.Add(Point(center.Value, ai.Length > 0 ? $"AI {ai}" : "AI (none)", AggroColor));

        if (Number(debug["aggroRadius"]) is { } aggro and > 0)
        {
            polylines.Add(Ring(center.Value, aggro, "aggro " + F(aggro), AggroColor, dashed: false));
        }

        if (home is { } homePoint)
        {
            points.Add(Point(homePoint, "home", HomeColor));
            if (Number(debug["leash"]) is { } leash and > 0)
            {
                polylines.Add(Ring(homePoint, leash, "leash " + F(leash), LeashColor, dashed: true));
            }
        }

        if (ReadPoint(debug["spawn"]) is { } spawn)
        {
            points.Add(Point(spawn, "spawn point", HomeColor));
        }

        if (debug["waypoints"] is JsonArray waypoints)
        {
            WorldPoint[] path = [.. waypoints.Select(ReadPoint).Where(p => p is not null).Select(p => p!.Value)];
            if (path.Length >= 2)
            {
                polylines.Add(new JsonObject
                {
                    ["pts"] = new JsonArray([.. path.Select(p => (JsonNode)LiveJson.Point(p))]),
                    ["label"] = "route",
                    ["color"] = RouteColor,
                });
            }
            else if (path.Length == 1)
            {
                points.Add(Point(path[0], "route", RouteColor));
            }
        }

        if (debug["threat"] is JsonArray threat)
        {
            foreach (JsonNode? entry in threat)
            {
                if (entry is JsonObject holder
                    && holder["guid"]?.GetValue<string>() is { } text && LiveGuid.TryParse(text, out ulong guid)
                    && threatPosition(guid) is { } holderAt)
                {
                    segments.Add(new JsonObject
                    {
                        ["from"] = LiveJson.Point(center.Value),
                        ["to"] = LiveJson.Point(holderAt),
                        ["label"] = "threat " + F(Number(holder["threat"]) ?? 0),
                        ["color"] = ThreatColor,
                    });
                }
            }
        }

        return new JsonObject { ["points"] = points, ["segments"] = segments, ["polylines"] = polylines };
    }

    /// <summary>The overlay for one <c>.atlas spawns</c> reply: a point per spawn row (creatures amber, game objects blue), labelled with the entry.</summary>
    public static JsonObject? FromSpawns(JsonObject spawns)
    {
        ArgumentNullException.ThrowIfNull(spawns);
        if (spawns["rows"] is not JsonArray rows)
        {
            return null;
        }

        var points = new JsonArray();
        foreach (JsonNode? node in rows)
        {
            if (node is JsonObject row && ReadPoint(row) is { } at)
            {
                bool creature = row["kind"]?.GetValue<string>() == "creature";
                points.Add(Point(at, $"{(creature ? "creature" : "object")} {row["entry"]?.ToJsonString()}", creature ? CreatureSpawnColor : ObjectSpawnColor));
            }
        }

        return new JsonObject { ["points"] = points };
    }

    private static JsonObject Point(WorldPoint at, string label, string color)
        => new() { ["at"] = LiveJson.Point(at), ["label"] = label, ["color"] = color };

    private static JsonObject Ring(WorldPoint center, double radius, string label, string color, bool dashed)
    {
        var pts = new JsonArray();
        for (int i = 0; i <= RingSegments; i++)
        {
            double angle = 2 * Math.PI * i / RingSegments;
            pts.Add(LiveJson.Point(new WorldPoint(center.X + (radius * Math.Cos(angle)), center.Y + (radius * Math.Sin(angle)), center.Z)));
        }

        return new JsonObject { ["pts"] = pts, ["label"] = label, ["color"] = color, ["dashed"] = dashed };
    }

    private static WorldPoint? ReadPoint(JsonNode? node)
    {
        if (node is JsonObject o && Number(o["x"]) is { } x && Number(o["y"]) is { } y && Number(o["z"]) is { } z)
        {
            return new WorldPoint(x, y, z);
        }

        return null;
    }

    private static string F(double value) => value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    private static double? Number(JsonNode? node)
        => node is JsonValue v && v.TryGetValue(out double d) && double.IsFinite(d) ? d : null;
}
