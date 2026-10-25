using System.Globalization;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Live;

namespace ArcaneCore.Atlas.Insight;

/// <summary><c>live.anomalies</c> (the server's anomaly ledger as a list and coloured markers) and <c>live.heatmap</c> (cell counts of anomalies, deaths, density or combat).</summary>
internal static class AnomalyCommands
{
    public const int DefaultLimit = 256;
    public const int MaxLimit = 4096;
    public const double DefaultCellSize = 120;
    public const double MinCellSize = 16;
    public const double MaxCellSize = 2000;
    public const int DefaultWindowSec = 300;
    public const int MaxWindowSec = 86_400;

    /// <summary>The feed revision that carries the per-entity combat flag.</summary>
    public const int CombatRevision = 2;

    private static readonly string[] Severities = ["info", "warning", "error"];
    private static readonly string[] Metrics = ["anomalies", "deaths", "density", "combat"];

    public static void Register(CommandRegistry registry, InsightHub hub)
    {
        registry.Register(new CommandDefinition(
            "live.anomalies",
            $"List the world daemon's recent anomalies (silent failures such as a bot that never boarded a lift) and mark them on the map, coloured by severity. Positions are the subject's position now (pos 'current'), so a subject that no longer exists has none. Watched or run from a script it is capped at {InsightArgs.MaxWatchedRows} rows; run directly it returns up to {MaxLimit}.",
            [
                new CommandArg("since", ArgType.Integer, "Only anomalies with a sequence number above this (default 0).", Default: JsonValue.Create(0)),
                new CommandArg("map", ArgType.Integer, "Only anomalies whose subject is on this map (rows without a position are left out)."),
                new CommandArg("subsystem", ArgType.String, "Only this subsystem, for example bot-elevator."),
                new CommandArg("severity", ArgType.String, "Only this severity or worse.", Choices: Severities),
                new CommandArg("limit", ArgType.Integer, $"Most rows to ask for (1..{MaxLimit}, default {DefaultLimit}).", Default: JsonValue.Create(DefaultLimit)),
            ],
            (call, ct) => AnomaliesAsync(hub, call, ct)));

        registry.Register(new CommandDefinition(
            "live.heatmap",
            $"Count something per square map cell and draw the cells as coloured boxes (fixed blue-to-red ramp, label = count). Metrics: anomalies (the server's anomaly ledger), deaths (entities whose hp fell to zero in the window), density (entities now), combat (entities in combat now; needs feed rev {CombatRevision}). At most {Heatmap.MaxBoxes} boxes: the busiest cells are drawn and the rest are merged into 'other'.",
            [
                new CommandArg("metric", ArgType.String, "What to count.", Required: true, Choices: Metrics),
                new CommandArg("cellSize", ArgType.Number, $"Cell side in yards ({MinCellSize:0}..{MaxCellSize:0}, default {DefaultCellSize:0}).", Default: JsonValue.Create(DefaultCellSize)),
                new CommandArg("windowSec", ArgType.Integer, $"For anomalies and deaths: how far back, in seconds (1..{MaxWindowSec}, default {DefaultWindowSec}).", Default: JsonValue.Create(DefaultWindowSec)),
                new CommandArg("map", ArgType.Integer, "Map id. Default: the subscribed map, else the view's map."),
            ],
            (call, ct) => HeatmapAsync(hub, call, ct)));
    }

    private static async Task<JsonNode?> AnomaliesAsync(InsightHub hub, CommandInvocation call, CancellationToken cancellationToken)
    {
        long since = call.OptionalInt("since") ?? 0;
        if (since < 0)
        {
            throw InsightArgs.Bad(call, "argument 'since' must be 0 or more.");
        }

        int limit = (int)InsightArgs.IntInRange(call, "limit", 1, MaxLimit, DefaultLimit);
        uint? map = InsightArgs.OptionalUInt(call, "map");
        string? subsystem = call.OptionalString("subsystem");
        int minSeverity = SeverityRank(call.OptionalString("severity"));
        bool capped = call.Origin == CommandOrigin.Script;

        // The ring answers oldest-first from since+1. The filters run here, so a filtered call fetches the widest page; when that page is
        // full and newer notices exist, the newest page is fetched instead, so the result is the recent notices, not the oldest ones.
        int fetch = subsystem is not null || minSeverity > 0 || map is not null ? MaxLimit : limit;
        JsonObject reply = await hub.Feed.RequestAsync(
            new JsonObject { ["kind"] = "anomalies", ["since"] = since, ["max"] = fetch },
            cancellationToken).ConfigureAwait(false);
        bool reset = reply["reset"] is JsonValue resetNode && resetNode.TryGetValue(out bool wasReset) && wasReset;
        if (reset)
        {
            // since is above anything the ring has issued (the daemon restarted): the sequence numbers started over, so read from the start.
            since = 0;
            reply = await hub.Feed.RequestAsync(
                new JsonObject { ["kind"] = "anomalies", ["since"] = 0, ["max"] = fetch },
                cancellationToken).ConfigureAwait(false);
        }

        JsonArray page = reply["items"] as JsonArray ?? [];
        long latestSeq = Long(reply["latest"]);
        long skipped = 0;
        if (page.Count >= fetch && page[^1] is JsonObject last && Long(last["seq"]) < latestSeq)
        {
            long newerSince = Math.Max(since, latestSeq - fetch);
            skipped = newerSince - since;
            reply = await hub.Feed.RequestAsync(
                new JsonObject { ["kind"] = "anomalies", ["since"] = newerSince, ["max"] = fetch },
                cancellationToken).ConfigureAwait(false);
            page = reply["items"] as JsonArray ?? [];
        }

        var matches = new List<Anomaly>();
        foreach (JsonNode? node in page)
        {
            if (node is not JsonObject item || !TryRow(item, out Anomaly anomaly))
            {
                continue;
            }

            if ((subsystem is not null && !string.Equals(anomaly.Subsystem, subsystem, StringComparison.OrdinalIgnoreCase))
                || SeverityRank(anomaly.Severity) < minSeverity
                || (map is { } wanted && (anomaly.Position is null || anomaly.Map != wanted)))
            {
                continue;
            }

            matches.Add(anomaly);
        }

        var rows = new JsonArray();
        var points = new JsonArray();
        int total = matches.Count;
        int nodes = 60;

        // The newest rows are kept: walk from the newest backwards and stop at the cap, then put the survivors back in oldest-first order.
        var keptRows = new List<JsonObject>();
        var keptPoints = new List<JsonObject>();
        for (int i = total - 1; i >= Math.Max(0, total - limit); i--)
        {
            Anomaly anomaly = matches[i];
            JsonObject row = anomaly.ToRow();
            JsonObject? point = anomaly.ToPoint();
            int cost = InsightArgs.Nodes(row) + (point is null ? 0 : InsightArgs.Nodes(point));
            if (capped && (keptRows.Count >= InsightArgs.MaxWatchedRows || nodes + cost > InsightArgs.NodeBudget))
            {
                break;
            }

            nodes += cost;
            keptRows.Add(row);
            if (point is not null)
            {
                keptPoints.Add(point);
            }
        }

        for (int i = keptRows.Count - 1; i >= 0; i--)
        {
            rows.Add(keptRows[i]);
        }

        for (int i = keptPoints.Count - 1; i >= 0; i--)
        {
            points.Add(keptPoints[i]);
        }

        return new JsonObject
        {
            ["latest"] = reply["latest"]?.DeepClone(),
            ["lost"] = reply["lost"]?.DeepClone(),
            ["skipped"] = skipped,
            ["reset"] = reset,
            ["total"] = total,
            ["returned"] = rows.Count,
            ["truncated"] = rows.Count < total,
            ["items"] = rows,
            ["overlay"] = new JsonObject { ["points"] = points },
        };
    }

    private static async Task<JsonNode?> HeatmapAsync(InsightHub hub, CommandInvocation call, CancellationToken cancellationToken)
    {
        string metric = call.GetString("metric");
        double cellSize = InsightArgs.NumberInRange(call, "cellSize", MinCellSize, MaxCellSize, DefaultCellSize);
        int windowSec = (int)InsightArgs.IntInRange(call, "windowSec", 1, MaxWindowSec, DefaultWindowSec);
        if (metric == "combat")
        {
            if (!hub.Feed.IsConnected)
            {
                throw new CommandException(CommandException.Unavailable, "not connected to a map feed; run live.connect first.");
            }

            if (hub.Feed.FeedRevision < CombatRevision)
            {
                throw new CommandException(
                    CommandException.Unavailable,
                    string.Create(CultureInfo.InvariantCulture, $"combat needs feed rev {CombatRevision}; the connected world's map feed is rev {hub.Feed.FeedRevision}."));
            }
        }

        (uint map, _) = hub.Feed.QueryTarget(InsightArgs.OptionalUInt(call, "map"), null);
        IEnumerable<(double X, double Y, double Z)> points;
        switch (metric)
        {
            case "density":
                points = hub.Feed.Store.Snapshot(e => e.Map == map).Select(s => (s.Entity.Position.X, s.Entity.Position.Y, s.Entity.Position.Z));
                break;
            case "combat":
                points = hub.Tail.Latest() is { } latest && latest.Map == map
                    ? latest.Entities.Where(e => (e.Flags & InsightRecorder.FlagCombat) != 0).Select(e => ((double)e.X, (double)e.Y, (double)e.Z))
                    : [];
                break;
            case "deaths":
                InsightRecorder source = hub.Record is { FrameCount: > 0 } recorded ? recorded : hub.Tail;
                points = source.Deaths(windowSec * 1000L, map).Select(e => ((double)e.X, (double)e.Y, (double)e.Z));
                break;
            default:
                points = await AnomalyPointsAsync(hub, map, windowSec, cancellationToken).ConfigureAwait(false);
                break;
        }

        JsonObject result = Heatmap.Render(Heatmap.Bin(points, cellSize), cellSize);
        result["metric"] = metric;
        result["map"] = map;
        result["windowSec"] = windowSec;
        return result;
    }

    private static async Task<List<(double X, double Y, double Z)>> AnomalyPointsAsync(InsightHub hub, uint map, int windowSec, CancellationToken cancellationToken)
    {
        JsonObject reply = await hub.Feed.RequestAsync(
            new JsonObject { ["kind"] = "anomalies", ["since"] = 0, ["max"] = MaxLimit },
            cancellationToken).ConfigureAwait(false);
        long oldest = hub.Clock() - (windowSec * 1000L);
        var points = new List<(double, double, double)>();
        foreach (JsonNode? node in reply["items"] as JsonArray ?? [])
        {
            if (node is JsonObject item && TryRow(item, out Anomaly a) && a.Position is { } p && a.Map == map && a.AtMs >= oldest)
            {
                points.Add(p);
            }
        }

        return points;
    }

    private static int SeverityRank(string? severity)
        => severity is null ? 0 : Math.Max(0, Array.IndexOf(Severities, severity.ToLowerInvariant()));

    private static string ColorOf(string severity) => severity switch
    {
        "info" => "#6CB6FF",
        "warning" => "#FFC24B",
        "error" => "#FF5C5C",
        _ => "#FFFFFF",
    };

    private static bool TryRow(JsonObject item, out Anomaly anomaly)
    {
        anomaly = default!;
        if (item["seq"] is not JsonValue seq || !seq.TryGetValue(out long seqValue)
            || item["severity"] is not JsonValue sev || !sev.TryGetValue(out string? severity))
        {
            return false;
        }

        (double X, double Y, double Z)? position = InsightArgs.TryPoint(item["p"], out (double X, double Y, double Z) p) ? p : null;
        uint map = item["map"] is JsonValue m && m.TryGetValue(out long mapValue) && mapValue is >= 0 and <= uint.MaxValue ? (uint)mapValue : 0;
        anomaly = new Anomaly(
            seqValue,
            Long(item["t"]),
            severity.ToLowerInvariant(),
            Text(item["subsystem"]),
            Text(item["code"]),
            Text(item["subject"]),
            Text(item["detail"]),
            (int)Math.Clamp(Long(item["count"]), 0, int.MaxValue),
            position,
            map);
        return true;
    }

    private static long Long(JsonNode? node) => node is JsonValue v && v.TryGetValue(out long value) ? value : 0;

    private static string Text(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? value) ? value : string.Empty;

    private sealed record Anomaly(long Seq, long AtMs, string Severity, string Subsystem, string Code, string Subject, string Detail, int Count, (double X, double Y, double Z)? Position, uint Map)
    {
        public JsonObject ToRow()
        {
            var row = new JsonObject
            {
                ["seq"] = Seq,
                ["t"] = AtMs,
                ["severity"] = Severity,
                ["subsystem"] = Subsystem,
                ["code"] = Code,
                ["subject"] = Subject,
                ["detail"] = Detail,
                ["count"] = Count,
            };
            if (Position is { } p)
            {
                row["map"] = Map;
                row["p"] = InsightArgs.Point(p.X, p.Y, p.Z);
            }

            return row;
        }

        public JsonObject? ToPoint()
            => Position is { } p
                ? new JsonObject { ["at"] = InsightArgs.Point(p.X, p.Y, p.Z), ["label"] = Subsystem + "/" + Code, ["color"] = ColorOf(Severity) }
                : null;
    }
}
