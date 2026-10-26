using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Insight;
using ArcaneCore.Atlas.Tests.Live;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Insight;

/// <summary>insight.watch re-runs commands on live frames, bounded in rate, and every watched result survives the host's event size limit.</summary>
public sealed class InsightWatchTests
{
    private const string Highlight = "live.highlight {\"kind\":\"creature\"}";

    private static async Task<CommandResult> StartAsync(InsightRig rig, string[] commands, double? hz = null)
    {
        var args = new JsonObject
        {
            ["action"] = "start",
            ["commands"] = new JsonArray([.. commands.Select(c => (JsonNode?)c)]),
        };
        if (hz is { } rate)
        {
            args["hz"] = rate;
        }

        return await rig.RunAsync("insight.watch", args);
    }

    private static IEnumerable<JsonNode?> CommandEvents(InsightRig rig, string command)
        => rig.Events(EventHub.Topics.Command).Where(e => e!["command"]!.GetValue<string>() == command);

    [WindowsFact]
    public async Task AWatch_RunsItsCommandOnTheFirstFrame_ThenAtMostOncePerPeriod_OnTheInjectedClock()
    {
        await using InsightRig rig = await InsightRig.StartAsync();
        rig.Clock.Value = 10_000;
        CommandResult started = await StartAsync(rig, [Highlight], hz: 2);
        Assert.True(started.Ok, started.ErrorMessage);
        Assert.Equal(2, started.Data!["hz"]!.GetValue<double>());

        await rig.SendFrameAsync(1, 0, InsightRig.Entity("1", "creature", 1, 1));
        await Wait.UntilAsync(() => rig.Hub.Watch.Runs == 1, "first run");
        rig.Clock.Value = 10_100;
        await rig.SendFrameAsync(2, 0, InsightRig.Entity("1", "creature", 2, 2));
        rig.Clock.Value = 10_499;
        await rig.SendFrameAsync(3, 0, InsightRig.Entity("1", "creature", 3, 3));
        await Wait.UntilAsync(() => rig.Hub.Watch.Throttled == 2, "two throttled frames");
        Assert.Equal(1, rig.Hub.Watch.Runs);

        rig.Clock.Value = 10_500;
        await rig.SendFrameAsync(4, 0, InsightRig.Entity("1", "creature", 4, 4));
        await Wait.UntilAsync(() => rig.Hub.Watch.Runs == 2, "second run after a full period");
        Assert.Equal(2, CommandEvents(rig, "live.highlight").Count());
    }

    [WindowsFact]
    public async Task WatchedResults_ArePublishedAsCommandEvents_LabelledAsScriptRuns_WithTheirOverlay()
    {
        await using InsightRig rig = await InsightRig.StartAsync();
        await StartAsync(rig, [Highlight]);

        await rig.SendFrameAsync(1, 0, InsightRig.Entity("1", "creature", 7, 8), InsightRig.Entity("2", "bot", 1, 1));
        await Wait.UntilAsync(() => CommandEvents(rig, "live.highlight").Any(), "watched event");

        JsonNode published = CommandEvents(rig, "live.highlight").First()!;
        Assert.True(published["ok"]!.GetValue<bool>());
        Assert.Equal("script", published["origin"]!.GetValue<string>());
        Assert.Null(published["data"]!["omitted"]);
        JsonArray points = published["data"]!["overlay"]!["points"]!.AsArray();
        Assert.Single(points);
        Assert.Equal([7.0, 8.0], points[0]!["at"]!.AsArray().Take(2).Select(n => n!.GetValue<double>()));
    }

    [WindowsFact]
    public async Task Stop_EndsTheWatch_AndListShowsWhatRuns()
    {
        await using InsightRig rig = await InsightRig.StartAsync();
        CommandResult started = await StartAsync(rig, [Highlight], hz: 1);
        string id = started.Data!["id"]!.GetValue<string>();

        CommandResult listed = await rig.RunAsync("insight.watch", new JsonObject { ["action"] = "list" });
        JsonObject row = Assert.Single(listed.Data!["watches"]!.AsArray())!.AsObject();
        Assert.Equal(id, row["id"]!.GetValue<string>());
        Assert.Equal(1, row["hz"]!.GetValue<double>());
        Assert.Equal([Highlight], row["commands"]!.AsArray().Select(c => c!.GetValue<string>()));

        CommandResult stopped = await rig.RunAsync("insight.watch", new JsonObject { ["action"] = "stop", ["id"] = id });
        Assert.True(stopped.Ok, stopped.ErrorMessage);
        Assert.Equal(0, rig.Hub.Watch.Count);
        Assert.Empty((await rig.RunAsync("insight.watch", new JsonObject { ["action"] = "list" })).Data!["watches"]!.AsArray());

        long before = rig.Hub.Watch.Runs;
        rig.Clock.Value += 60_000;
        await rig.SendFrameAsync(1, 0, InsightRig.Entity("1", "creature", 1, 1));
        rig.Clock.Value += 60_000;
        await rig.SendFrameAsync(2, 0, InsightRig.Entity("1", "creature", 2, 2));
        Assert.Equal(before, rig.Hub.Watch.Runs);
        Assert.Equal(0, rig.Hub.Watch.Throttled);
        Assert.Empty(CommandEvents(rig, "live.highlight"));
        Assert.Equal(CommandException.BadArguments, (await rig.RunAsync("insight.watch", new JsonObject { ["action"] = "stop", ["id"] = "w99" })).ErrorCode);
    }

    [WindowsFact]
    public async Task StopWithoutAnId_EndsEveryWatch()
    {
        await using InsightRig rig = await InsightRig.StartAsync();
        await StartAsync(rig, [Highlight]);
        await StartAsync(rig, ["live.highlight {\"kind\":\"bot\"}"]);
        Assert.Equal(2, rig.Hub.Watch.Count);

        CommandResult stopped = await rig.RunAsync("insight.watch", new JsonObject { ["action"] = "stop" });

        Assert.True(stopped.Ok, stopped.ErrorMessage);
        Assert.Equal(0, rig.Hub.Watch.Count);
        Assert.Equal(2, stopped.Data!["stopped"]!.GetValue<int>());
    }

    [WindowsFact]
    public async Task Start_RefusesWhatCannotBeWatched_AndRatesOutsideTheLimit()
    {
        await using InsightRig rig = await InsightRig.StartAsync();

        foreach ((string[] commands, double? hz) in new (string[], double?)[]
        {
            ([], null),
            (["live.status"], null),
            (["no.such.command"], null),
            (["live.highlight {not json"], null),
            (["live.highlight [1]"], null),
            ([Highlight], 0),
            ([Highlight], -1),
            ([Highlight], 2.01),
            ([Highlight, Highlight, Highlight, Highlight, Highlight, Highlight, Highlight, Highlight, Highlight], null),
        })
        {
            CommandResult refused = await StartAsync(rig, commands, hz);
            Assert.False(refused.Ok);
            Assert.Equal(CommandException.BadArguments, refused.ErrorCode);
        }

        Assert.Equal(0, rig.Hub.Watch.Count);
        Assert.True((await StartAsync(rig, [Highlight], hz: 2)).Ok);
        Assert.True((await StartAsync(rig, ["live.highlight {\"kind\":\"bot\"}"], hz: 0.1)).Ok);
    }

    [WindowsFact]
    public async Task AWatchedCommandThatFails_PublishesItsFailure_AndKeepsWatching()
    {
        await using InsightRig rig = await InsightRig.StartAsync(rev: 1);
        rig.Clock.Value = 5_000;
        await StartAsync(rig, ["live.heatmap {\"metric\":\"combat\"}"], hz: 2);

        await rig.SendFrameAsync(1, 0, InsightRig.Entity("1", "creature", 1, 1));
        await Wait.UntilAsync(() => rig.Hub.Watch.Runs == 1, "first run");
        rig.Clock.Value = 6_000;
        await rig.SendFrameAsync(2, 0, InsightRig.Entity("1", "creature", 1, 1));
        await Wait.UntilAsync(() => rig.Hub.Watch.Runs == 2, "second run");

        JsonNode failure = CommandEvents(rig, "live.heatmap").First()!;
        Assert.False(failure["ok"]!.GetValue<bool>());
        Assert.Equal(CommandException.Unavailable, failure["error"]!["code"]!.GetValue<string>());
    }

    // ---- the event size limit ----

    private static JsonObject Anomaly(long seq, int cell)
        => new()
        {
            ["seq"] = seq,
            ["t"] = 1_700_000_000_000 + seq,
            ["key"] = "bot-elevator/board-missed",
            ["subsystem"] = "bot-elevator",
            ["code"] = "board-missed",
            ["severity"] = seq % 3 == 0 ? "error" : seq % 3 == 1 ? "warning" : "info",
            ["subject"] = $"bot Thrall{seq} (guid {seq})",
            ["detail"] = "waited at the lift and never boarded",
            ["count"] = 2,
            ["pos"] = "current",
            ["map"] = 0,
            ["instance"] = 0,
            ["p"] = Wire.Point((cell % 100 * 200) - 10_000, (cell / 100 * 200) - 5_000, 1),
        };

    private static async Task<InsightRig> WorstCaseAsync()
    {
        InsightRig rig = await InsightRig.StartAsync(rev: 2);
        rig.Clock.Value = 1_700_000_100_000;
        rig.Respond(request =>
        {
            Assert.Equal("anomalies", request["kind"]!.GetValue<string>());
            int max = request["max"]!.GetValue<int>();
            return Wire.Result(
                request["id"]!.GetValue<long>(),
                ("kind", "anomalies"),
                ("latest", 4096),
                ("lost", 0),
                ("items", new JsonArray([.. Enumerable.Range(1, Math.Min(max, 4096)).Select(i => (JsonNode?)Anomaly(i, i))])));
        });

        await rig.SendFrameAsync(1, 0, WorstEntities());
        return rig;
    }

    /// <summary>5,000 entities, all below 50 % hp and in combat, each in its own 16-yard heatmap cell.</summary>
    private static JsonObject[] WorstEntities()
        => [.. Enumerable.Range(0, 5000).Select(i => InsightRig.Entity(
            (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "creature", (i % 100 * 200) - 10_000, (i / 100 * 200) - 5_000, hp: 10, mt: "chase", cb: true))];

    [WindowsFact]
    public async Task EveryWatchableCommand_AtItsWorstCase_PublishesItsRealResult_NotTheOmittedPlaceholder_UnderSixteenThousandNodes()
    {
        await using InsightRig rig = await WorstCaseAsync();
        string[] commands =
        [
            "live.anomalies {\"limit\":4096}",
            "live.highlight {\"hpBelow\":50,\"limit\":5000}",
            "live.heatmap {\"metric\":\"density\",\"cellSize\":16,\"map\":0}",
            "live.heatmap {\"metric\":\"combat\",\"cellSize\":16,\"map\":0}",
            "live.heatmap {\"metric\":\"anomalies\",\"cellSize\":16,\"map\":0,\"windowSec\":3600}",
        ];
        CommandResult started = await StartAsync(rig, commands, hz: 2);
        Assert.True(started.Ok, started.ErrorMessage);

        rig.Clock.Value += 1_000;
        await rig.SendFrameAsync(2, 0, WorstEntities());
        await Wait.UntilAsync(() => rig.Hub.Watch.Runs >= 1, "worst-case run");

        foreach (string command in new[] { "live.anomalies", "live.highlight", "live.heatmap" })
        {
            JsonNode[] events = [.. CommandEvents(rig, command).Select(e => e!)];
            Assert.NotEmpty(events);
            JsonNode last = events[^1];
            Assert.True(last["ok"]!.GetValue<bool>(), last.ToJsonString());
            Assert.Equal("script", last["origin"]!.GetValue<string>());
            JsonNode data = last["data"]!;
            Assert.Null(data["omitted"]);
            Assert.NotNull(data["overlay"]);
            Assert.True(InsightRig.NodeCount(data) < 16_000, $"{command}: {InsightRig.NodeCount(data)} nodes");
        }

        JsonNode anomalies = CommandEvents(rig, "live.anomalies").Last()!["data"]!;
        Assert.True(anomalies["truncated"]!.GetValue<bool>());
        Assert.Equal(4096, anomalies["total"]!.GetValue<int>());
        Assert.True(anomalies["items"]!.AsArray().Count <= 1500);
        Assert.True(anomalies["items"]!.AsArray().Count > 0);

        // A capped answer keeps the newest notices, oldest first, with no gap between them.
        long[] seqs = [.. anomalies["items"]!.AsArray().Select(i => i!["seq"]!.GetValue<long>())];
        Assert.Equal(4096L, seqs[^1]);
        Assert.Equal(seqs.Order(), seqs);
        Assert.Equal(seqs[^1] - seqs[0] + 1, seqs.Length);

        JsonNode highlight = CommandEvents(rig, "live.highlight").Last()!["data"]!;
        Assert.True(highlight["truncated"]!.GetValue<bool>());
        Assert.Equal(5000, highlight["total"]!.GetValue<int>());
        Assert.True(highlight["entities"]!.AsArray().Count <= 1500);

        foreach (JsonNode? heat in CommandEvents(rig, "live.heatmap").TakeLast(3))
        {
            JsonNode data = heat!["data"]!;
            Assert.True(data["overlay"]!["boxes"]!.AsArray().Count <= Heatmap.MaxBoxes);
        }
    }

    [WindowsFact]
    public async Task Control_TheSameCommandsUnwatchedAtFullLimit_AreTooBigForAnEvent_ProvingTheTestAboveReachesTheLimit()
    {
        await using InsightRig rig = await WorstCaseAsync();

        CommandResult anomalies = await rig.RunAsync("live.anomalies", new JsonObject { ["limit"] = 4096 });
        CommandResult highlight = await rig.RunAsync("live.highlight", new JsonObject { ["hpBelow"] = 50, ["limit"] = 5000 });

        Assert.True(anomalies.Ok, anomalies.ErrorMessage);
        Assert.Equal(4096, anomalies.Data!["items"]!.AsArray().Count);
        Assert.False(anomalies.Data["truncated"]!.GetValue<bool>());
        Assert.True(highlight.Ok, highlight.ErrorMessage);
        Assert.Equal(5000, highlight.Data!["entities"]!.AsArray().Count);
        foreach (string command in new[] { "live.anomalies", "live.highlight" })
        {
            JsonNode published = CommandEvents(rig, command).Single()!;
            Assert.NotNull(published["data"]!["omitted"]);
            Assert.Null(published["data"]!["items"]);
        }
    }
}
