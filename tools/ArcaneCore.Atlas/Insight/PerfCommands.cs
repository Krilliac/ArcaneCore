using System.Diagnostics;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;

namespace ArcaneCore.Atlas.Insight;

/// <summary><c>perf.report</c> (the UI posts its numbers) and <c>perf.stats</c> (host process numbers plus the latest UI report, for CLI and MCP).</summary>
internal static class PerfCommands
{
    /// <summary>The numbers a UI report may carry (the perf HUD's fields); anything else is refused.</summary>
    public static readonly IReadOnlyList<string> ReportFields =
    [
        "fps", "frameMsP50", "frameMsP95", "drawCalls", "triangles", "gpuMb", "textureMb", "liveHz", "feedLagMs", "entities", "dropped", "truncated", "diffMs", "online",
    ];

    private const double Mb = 1024.0 * 1024.0;

    public static void Register(CommandRegistry registry, InsightHub hub)
    {
        registry.Register(new CommandDefinition(
            "perf.report",
            $"The UI posts its latest frame and render numbers here every 2 seconds (perf.stats returns them). stats is an object of non-negative numbers; allowed keys: {string.Join(", ", ReportFields)}.",
            [new CommandArg("stats", ArgType.Object, "The numbers to store, replacing the previous report.", Required: true)],
            (call, _) => Task.FromResult<JsonNode?>(Report(hub, call))));

        registry.Register(new CommandDefinition(
            "perf.stats",
            "Host process numbers (working set, GC heap, layer cache), live feed counters, the recorder state and the latest UI report with its age.",
            [],
            (_, _) => Task.FromResult<JsonNode?>(Stats(hub))));
    }

    private static JsonObject Report(InsightHub hub, CommandInvocation call)
    {
        JsonObject stats = call.OptionalObject("stats") ?? throw InsightArgs.Bad(call, "argument 'stats' must be an object.");
        if (stats.Count == 0)
        {
            throw InsightArgs.Bad(call, "stats is empty.");
        }

        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach ((string key, JsonNode? node) in stats)
        {
            if (!ReportFields.Contains(key, StringComparer.Ordinal))
            {
                throw InsightArgs.Bad(call, $"unknown stat '{key}'; allowed: {string.Join(", ", ReportFields)}.");
            }

            if (!JsonScalars.TryGetDouble(node, out double number) || !double.IsFinite(number) || number < 0)
            {
                throw InsightArgs.Bad(call, $"stat '{key}' must be a non-negative number.");
            }

            values[key] = number;
        }

        hub.SetUi(new UiReport(values, hub.Clock()));
        return new JsonObject { ["accepted"] = values.Count };
    }

    private static JsonObject Stats(InsightHub hub)
    {
        using Process process = Process.GetCurrentProcess();
        var result = new JsonObject
        {
            ["process"] = new JsonObject
            {
                ["workingSetMb"] = Math.Round(process.WorkingSet64 / Mb, 1),
                ["gcHeapMb"] = Math.Round(GC.GetTotalMemory(forceFullCollection: false) / Mb, 1),
                ["layerCacheMb"] = Math.Round((hub.Services.GetService(typeof(LayerCache)) is LayerCache cache ? cache.Bytes : 0) / Mb, 1),
                ["threads"] = process.Threads.Count,
            },
            ["live"] = hub.Feed.Status(),
            ["recorder"] = ReplayCommands.RecordState(hub),
        };

        if (hub.LatestUi is { } ui)
        {
            var block = new JsonObject();
            foreach ((string key, double value) in ui.Values.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                block[key] = value;
            }

            block["ageMs"] = Math.Max(0, hub.Clock() - ui.AtMs);
            result["ui"] = block;
        }
        else
        {
            result["ui"] = null;
        }

        return result;
    }
}
