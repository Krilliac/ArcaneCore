using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Insight;
using ArcaneCore.Atlas.Tests.Live;
using ArcaneCore.TestFixtures;
using Xunit;
using WdbcTestBuilder = ArcaneCore.Atlas.Tests.Reference.Mpq.WdbcTestBuilder;

namespace ArcaneCore.Atlas.Tests.Insight;

/// <summary>live.inspect, live.anomalies and live.heatmap against a fake daemon that answers the world's query kinds.</summary>
public sealed class InsightQueryCommandTests
{
    private static SpellNames Spells()
    {
        var builder = new WdbcTestBuilder(173);
        builder.Row([(0, 133u), .. builder.Localized(120, "Fireball")]);
        builder.Row([(0, 172u), .. builder.Localized(120, "Corruption")]);
        return SpellNames.FromBytes(builder.Build());
    }

    private static JsonObject Item(long seq, string severity, string subsystem, string code, string subject, long t, (uint Map, double X, double Y, double Z)? at = null, int count = 1)
    {
        var item = new JsonObject
        {
            ["seq"] = seq,
            ["t"] = t,
            ["key"] = subsystem + "/" + code,
            ["subsystem"] = subsystem,
            ["code"] = code,
            ["severity"] = severity,
            ["subject"] = subject,
            ["detail"] = "detail of " + seq,
            ["count"] = count,
        };
        if (at is { } where)
        {
            item["pos"] = "current";
            item["map"] = where.Map;
            item["instance"] = 0;
            item["p"] = Wire.Point(where.X, where.Y, where.Z);
        }

        return item;
    }

    private static JsonObject AnomalyReply(JsonObject request, long latest, params JsonObject[] items)
        => Wire.Result(
            request["id"]!.GetValue<long>(),
            ("kind", "anomalies"),
            ("latest", latest),
            ("lost", 0),
            ("items", new JsonArray([.. items.Select(i => (JsonNode?)i.DeepClone())])));

    private static IEnumerable<double> Numbers(JsonNode? array) => array!.AsArray().Select(n => n!.GetValue<double>());

    // ---- live.inspect ----

    [WindowsFact]
    public async Task Inspect_AsksTheWorldForTheGuidOnTheNamedMap_AndAddsSpellNamesAndARing()
    {
        await using InsightRig rig = await InsightRig.StartAsync(rev: 2, spells: Spells());

        (JsonObject request, CommandResult result) = await rig.AskAsync(
            "live.inspect",
            new JsonObject { ["guid"] = "0x1234abcd", ["map"] = 1, ["instance"] = 2 },
            req => Wire.Result(
                req["id"]!.GetValue<long>(),
                ("kind", "inspect"),
                ("guid", "1234ABCD"),
                ("entity", "creature"),
                ("name", "Boar"),
                ("p", Wire.Point(1, 2, 3)),
                ("unit", true),
                ("level", 7),
                ("auras", new JsonArray(
                    new JsonObject { ["spell"] = 133, ["stacks"] = 1, ["remainingMs"] = 1500, ["durationMs"] = 3000, ["caster"] = null, ["positive"] = false },
                    new JsonObject { ["spell"] = 99999, ["stacks"] = 2, ["remainingMs"] = -1, ["durationMs"] = -1, ["caster"] = "ABC", ["positive"] = true }))));

        Assert.True(result.Ok, result.ErrorMessage);
        Assert.Equal("query", request["op"]!.GetValue<string>());
        Assert.Equal("inspect", request["kind"]!.GetValue<string>());
        Assert.Equal("1234ABCD", request["guid"]!.GetValue<string>());
        Assert.Equal(1, request["map"]!.GetValue<int>());
        Assert.Equal(2, request["instance"]!.GetValue<int>());
        JsonObject data = result.Data!.AsObject();
        Assert.Equal("inspect", data["kind"]!.GetValue<string>());
        Assert.Equal("Boar", data["name"]!.GetValue<string>());
        Assert.Equal(7, data["level"]!.GetValue<int>());
        Assert.False(data.ContainsKey("op"));
        Assert.False(data.ContainsKey("ok"));
        JsonArray auras = data["auras"]!.AsArray();
        Assert.Equal("Fireball", auras[0]!["name"]!.GetValue<string>());
        Assert.Null(auras[1]!["name"]);
        Assert.Equal(99999, auras[1]!["spell"]!.GetValue<int>());
        JsonObject ring = Assert.Single(data["overlay"]!["points"]!.AsArray())!.AsObject();
        Assert.Equal([1.0, 2.0, 3.0], Numbers(ring["at"]));
        Assert.Equal("Boar", ring["label"]!.GetValue<string>());
    }

    [WindowsFact]
    public async Task Inspect_RefusesABadGuidBeforeAnythingIsSent()
    {
        await using InsightRig rig = await InsightRig.StartAsync();

        foreach (string bad in new[] { "zz", "0", "", "12345678901234567" })
        {
            CommandResult refused = await rig.RunAsync("live.inspect", new JsonObject { ["guid"] = bad });
            Assert.False(refused.Ok);
            Assert.Equal(CommandException.BadArguments, refused.ErrorCode);
        }

        CommandResult missing = await rig.RunAsync("live.inspect", new JsonObject());
        Assert.Equal(CommandException.BadArguments, missing.ErrorCode);

        // None of the refusals reached the wire: the first query the daemon sees is the first id.
        (JsonObject request, CommandResult ok) = await rig.AskAsync("live.inspect", new JsonObject { ["guid"] = "1" }, r => Wire.Result(r["id"]!.GetValue<long>(), ("kind", "inspect")));
        Assert.True(ok.Ok, ok.ErrorMessage);
        Assert.Equal(1, request["id"]!.GetValue<long>());
    }

    [WindowsFact]
    public async Task Inspect_WhenNotConnected_IsUnavailable_NotAnEmptyAnswer()
    {
        await using InsightRig rig = await InsightRig.StartAsync(connect: false);

        CommandResult result = await rig.RunAsync("live.inspect", new JsonObject { ["guid"] = "1" });

        Assert.False(result.Ok);
        Assert.Equal(CommandException.Unavailable, result.ErrorCode);
        Assert.Contains("live.connect", result.ErrorMessage, StringComparison.Ordinal);
    }

    [WindowsFact]
    public async Task AnOlderWorldThatAnswersBadArgs_IsUnavailableWithAClearMessage_AndIsNotAskedAgainOnThatConnection()
    {
        await using InsightRig rig = await InsightRig.StartAsync();
        JsonObject Old(JsonObject req) => new()
        {
            ["op"] = "result",
            ["id"] = req["id"]!.GetValue<long>(),
            ["ok"] = false,
            ["code"] = "bad-args",
            ["message"] = "unknown query kind; the kinds are los, height, path and undermap",
        };

        (JsonObject first, CommandResult probe) = await rig.AskAsync("live.inspect", new JsonObject { ["guid"] = "1" }, Old);

        Assert.Equal("inspect", first["kind"]!.GetValue<string>());
        Assert.False(probe.Ok);
        Assert.Equal(CommandException.Unavailable, probe.ErrorCode);
        Assert.Contains("inspect", probe.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("does not support", probe.ErrorMessage, StringComparison.Ordinal);

        // The second attempt is refused locally; the next thing on the wire is the anomalies query, id 2, not another inspect.
        CommandResult again = await rig.RunAsync("live.inspect", new JsonObject { ["guid"] = "1" });
        Assert.Equal(CommandException.Unavailable, again.ErrorCode);
        (JsonObject next, CommandResult anomalies) = await rig.AskAsync("live.anomalies", null, r => AnomalyReply(r, 0));
        Assert.True(anomalies.Ok, anomalies.ErrorMessage);
        Assert.Equal("anomalies", next["kind"]!.GetValue<string>());
        Assert.Equal(2, next["id"]!.GetValue<long>());

        // A new connection forgets the verdict: the world may have been upgraded.
        await rig.ReconnectAsync(rev: 2);
        (JsonObject afterReconnect, CommandResult fine) = await rig.AskAsync(
            "live.inspect", new JsonObject { ["guid"] = "1" }, r => Wire.Result(r["id"]!.GetValue<long>(), ("kind", "inspect"), ("name", "Boar")));
        Assert.Equal("inspect", afterReconnect["kind"]!.GetValue<string>());
        Assert.True(fine.Ok, fine.ErrorMessage);
    }

    [WindowsFact]
    public async Task AWorldThatRefusesForAnotherReason_IsAFailure_NotAnUnsupportedVerdict()
    {
        await using InsightRig rig = await InsightRig.StartAsync();

        (_, CommandResult refused) = await rig.AskAsync(
            "live.inspect",
            new JsonObject { ["guid"] = "1" },
            r => new JsonObject { ["op"] = "result", ["id"] = r["id"]!.GetValue<long>(), ["ok"] = false, ["code"] = "not-found", ["message"] = "no such object" });
        Assert.Equal(CommandException.Failed, refused.ErrorCode);
        Assert.Contains("no such object", refused.ErrorMessage, StringComparison.Ordinal);

        (JsonObject retry, _) = await rig.AskAsync("live.inspect", new JsonObject { ["guid"] = "1" }, r => Wire.Result(r["id"]!.GetValue<long>(), ("kind", "inspect")));
        Assert.Equal(2, retry["id"]!.GetValue<long>());
    }

    // ---- live.anomalies ----

    [WindowsFact]
    public async Task Anomalies_ListsTheRowsAndDrawsOneColouredPointPerPositionedRow()
    {
        await using InsightRig rig = await InsightRig.StartAsync();
        JsonObject[] items =
        [
            Item(11, "warning", "bot-elevator", "board-missed", "bot Thrall (guid 1234)", 1000, (0, 10, 20, 30), 3),
            Item(12, "error", "movement", "under-map", "creature 3100 spawn 812", 2000, (1, -5, 6, 7)),
            Item(13, "info", "bot-elevator", "gave-up", "bot Jaina (guid 99)", 3000),
        ];

        (JsonObject request, CommandResult result) = await rig.AskAsync(
            "live.anomalies", new JsonObject { ["since"] = 5, ["limit"] = 300 }, r => AnomalyReply(r, 13, items));

        Assert.True(result.Ok, result.ErrorMessage);
        Assert.Equal("anomalies", request["kind"]!.GetValue<string>());
        Assert.Equal(5, request["since"]!.GetValue<long>());
        Assert.Equal(300, request["max"]!.GetValue<int>());
        JsonObject data = result.Data!.AsObject();
        Assert.Equal(3, data["total"]!.GetValue<int>());
        Assert.Equal(13, data["latest"]!.GetValue<long>());
        Assert.False(data["truncated"]!.GetValue<bool>());
        JsonArray rows = data["items"]!.AsArray();
        Assert.Equal([11L, 12L, 13L], rows.Select(r => r!["seq"]!.GetValue<long>()));
        Assert.Equal("bot-elevator", rows[0]!["subsystem"]!.GetValue<string>());
        Assert.Equal("board-missed", rows[0]!["code"]!.GetValue<string>());
        Assert.Equal(3, rows[0]!["count"]!.GetValue<int>());
        Assert.Equal([10.0, 20.0, 30.0], Numbers(rows[0]!["p"]));
        Assert.Null(rows[2]!["p"]);

        JsonArray points = data["overlay"]!["points"]!.AsArray();
        Assert.Equal(2, points.Count);
        Assert.Equal([10.0, 20.0, 30.0], Numbers(points[0]!["at"]));
        Assert.Equal("#FFC24B", points[0]!["color"]!.GetValue<string>());
        Assert.Equal("#FF5C5C", points[1]!["color"]!.GetValue<string>());
        Assert.Equal("bot-elevator/board-missed", points[0]!["label"]!.GetValue<string>());
    }

    [WindowsFact]
    public async Task Anomalies_FiltersBySubsystemMinimumSeverityAndMap()
    {
        await using InsightRig rig = await InsightRig.StartAsync();
        JsonObject[] items =
        [
            Item(1, "warning", "bot-elevator", "board-missed", "a", 1, (0, 1, 1, 1)),
            Item(2, "error", "movement", "under-map", "b", 2, (1, 2, 2, 2)),
            Item(3, "info", "bot-elevator", "gave-up", "c", 3),
        ];

        async Task<JsonArray> Run(JsonObject args)
        {
            (_, CommandResult r) = await rig.AskAsync("live.anomalies", args, req => AnomalyReply(req, 3, items));
            Assert.True(r.Ok, r.ErrorMessage);
            return r.Data!["items"]!.AsArray();
        }

        Assert.Equal([2L], (await Run(new JsonObject { ["subsystem"] = "movement" })).Select(r => r!["seq"]!.GetValue<long>()));
        Assert.Equal([1L, 2L], (await Run(new JsonObject { ["severity"] = "warning" })).Select(r => r!["seq"]!.GetValue<long>()));
        Assert.Equal([2L], (await Run(new JsonObject { ["severity"] = "error" })).Select(r => r!["seq"]!.GetValue<long>()));
        Assert.Equal([1L], (await Run(new JsonObject { ["map"] = 0 })).Select(r => r!["seq"]!.GetValue<long>()));
        Assert.Empty(await Run(new JsonObject { ["map"] = 5 }));
    }

    [WindowsFact]
    public async Task Anomalies_RefusesBadLimitsAndSeverities_BeforeAskingTheWorld()
    {
        await using InsightRig rig = await InsightRig.StartAsync();

        foreach (JsonObject bad in new[]
        {
            new JsonObject { ["limit"] = 0 },
            new JsonObject { ["limit"] = 4097 },
            new JsonObject { ["since"] = -1 },
            new JsonObject { ["severity"] = "fatal" },
        })
        {
            CommandResult refused = await rig.RunAsync("live.anomalies", bad);
            Assert.Equal(CommandException.BadArguments, refused.ErrorCode);
        }

        (JsonObject request, _) = await rig.AskAsync("live.anomalies", new JsonObject { ["limit"] = 4096 }, r => AnomalyReply(r, 0));
        Assert.Equal(1, request["id"]!.GetValue<long>());
        Assert.Equal(4096, request["max"]!.GetValue<int>());
    }

    [WindowsFact]
    public async Task Anomalies_OfARingLargerThanThePage_ReturnTheNewestNotices_AlsoWhenFiltered()
    {
        await using InsightRig rig = await InsightRig.StartAsync();
        const int ring = 600;
        JsonObject[] all = [.. Enumerable.Range(1, ring).Select(i => Item(i, "warning", i % 100 == 0 ? "rare" : "common", "code", "s" + i, i))];
        rig.Respond(req =>
        {
            long since = req["since"]!.GetValue<long>();
            int max = req["max"]!.GetValue<int>();
            return AnomalyReply(req, ring, [.. all.Where(a => a["seq"]!.GetValue<long>() > since).Take(max)]);
        });

        CommandResult recent = await rig.RunAsync("live.anomalies", null);
        Assert.True(recent.Ok, recent.ErrorMessage);
        long[] seqs = [.. recent.Data!["items"]!.AsArray().Select(r => r!["seq"]!.GetValue<long>())];
        Assert.Equal(256, seqs.Length);
        Assert.Equal(ring, seqs[^1]);
        Assert.Equal(ring - 255, seqs[0]);
        Assert.Equal(ring - 256, recent.Data["skipped"]!.GetValue<long>()); // the jump to the newest page passed over the older notices
        Assert.False(recent.Data["reset"]!.GetValue<bool>());

        CommandResult rare = await rig.RunAsync("live.anomalies", new JsonObject { ["subsystem"] = "rare" });
        Assert.True(rare.Ok, rare.ErrorMessage);
        Assert.Equal([100L, 200L, 300L, 400L, 500L, 600L], rare.Data!["items"]!.AsArray().Select(r => r!["seq"]!.GetValue<long>()));
        Assert.Equal(6, rare.Data["total"]!.GetValue<int>());
        Assert.Equal(0L, rare.Data["skipped"]!.GetValue<long>());
    }

    [WindowsFact]
    public async Task Anomalies_WithASinceAboveWhatTheRingIssued_AreReadFromTheStart_AndSayReset()
    {
        await using InsightRig rig = await InsightRig.StartAsync();
        JsonObject[] all = [.. Enumerable.Range(1, 3).Select(i => Item(i, "warning", "common", "code", "s" + i, i))];
        var asked = new List<long>();
        rig.Respond(req =>
        {
            long since = req["since"]!.GetValue<long>();
            lock (asked)
            {
                asked.Add(since);
            }

            if (since > 3)
            {
                JsonObject empty = AnomalyReply(req, 3);
                empty["reset"] = true;
                return empty;
            }

            return AnomalyReply(req, 3, [.. all.Where(a => a["seq"]!.GetValue<long>() > since)]);
        });

        CommandResult result = await rig.RunAsync("live.anomalies", new JsonObject { ["since"] = 9000 });

        Assert.True(result.Ok, result.ErrorMessage);
        Assert.True(result.Data!["reset"]!.GetValue<bool>());
        Assert.Equal([1L, 2L, 3L], result.Data["items"]!.AsArray().Select(i => i!["seq"]!.GetValue<long>()));
        lock (asked)
        {
            Assert.Equal([9000L, 0L], asked);
        }
    }

    // ---- live.heatmap ----

    [WindowsFact]
    public async Task Heatmap_OfAnomalies_HasExactCellCounts_AndOnlyCountsPositionedRowsInTheWindowAndMap()
    {
        await using InsightRig rig = await InsightRig.StartAsync();
        long now = 1_700_000_000_000;
        rig.Clock.Value = now;
        JsonObject[] items =
        [
            Item(1, "warning", "a", "x", "s", now - 10_000, (0, 10, 20, 5)),
            Item(2, "warning", "a", "x", "s", now - 20_000, (0, 50, 60, 5)),
            Item(3, "error", "a", "y", "s", now - 30_000, (0, 150, 20, 5)),
            Item(4, "error", "a", "y", "s", now - 40_000, (0, -10, -10, 5)),
            Item(5, "error", "a", "y", "s", now - 50_000, (1, 10, 20, 5)),
            Item(6, "error", "a", "y", "s", now - 120_000, (0, 10, 20, 5)),
            Item(7, "info", "a", "z", "s", now - 5_000),
        ];

        (JsonObject request, CommandResult result) = await rig.AskAsync(
            "live.heatmap",
            new JsonObject { ["metric"] = "anomalies", ["cellSize"] = 100, ["windowSec"] = 60, ["map"] = 0 },
            r => AnomalyReply(r, 7, items));

        Assert.True(result.Ok, result.ErrorMessage);
        Assert.Equal(0, request["since"]!.GetValue<long>());
        Assert.Equal(4096, request["max"]!.GetValue<int>());
        JsonObject data = result.Data!.AsObject();
        Assert.Equal("anomalies", data["metric"]!.GetValue<string>());
        Assert.Equal(4, data["total"]!.GetValue<int>());
        Assert.Equal(3, data["cells"]!.GetValue<int>());
        JsonArray boxes = data["overlay"]!["boxes"]!.AsArray();
        Assert.Equal(3, boxes.Count);
        JsonObject origin = boxes.Single(b => Numbers(b!["min"]).Take(2).SequenceEqual([0.0, 0.0]))!.AsObject();
        Assert.Equal("2", origin["label"]!.GetValue<string>());
        Assert.Equal("1", boxes.Single(b => Numbers(b!["min"]).Take(2).SequenceEqual([100.0, 0.0]))!["label"]!.GetValue<string>());
        Assert.Equal("1", boxes.Single(b => Numbers(b!["min"]).Take(2).SequenceEqual([-100.0, -100.0]))!["label"]!.GetValue<string>());
    }

    [WindowsFact]
    public async Task Heatmap_OfDensity_CountsEntitiesInTheStoreOnTheMap()
    {
        await using InsightRig rig = await InsightRig.StartAsync();
        await rig.SendFrameAsync(
            1,
            0,
            InsightRig.Entity("1", "creature", 10, 10),
            InsightRig.Entity("2", "creature", 90, 90),
            InsightRig.Entity("3", "bot", 110, 10),
            InsightRig.Entity("4", "player", -50, -50));

        CommandResult result = await rig.RunAsync("live.heatmap", new JsonObject { ["metric"] = "density", ["cellSize"] = 100, ["map"] = 0 });

        Assert.True(result.Ok, result.ErrorMessage);
        JsonObject data = result.Data!.AsObject();
        Assert.Equal(4, data["total"]!.GetValue<int>());
        Assert.Equal(3, data["cells"]!.GetValue<int>());
        JsonArray boxes = data["overlay"]!["boxes"]!.AsArray();
        JsonObject busiest = boxes.Single(b => b!["label"]!.GetValue<string>() == "2")!.AsObject();
        Assert.Equal([0.0, 0.0], Numbers(busiest["min"]).Take(2));
        Assert.Equal([100.0, 100.0], Numbers(busiest["max"]).Take(2));

        CommandResult otherMap = await rig.RunAsync("live.heatmap", new JsonObject { ["metric"] = "density", ["cellSize"] = 100, ["map"] = 1 });
        Assert.Equal(0, otherMap.Data!["total"]!.GetValue<int>());
    }

    [WindowsFact]
    public async Task Heatmap_OfCombat_IsUnavailableOnAFeedWithoutRevisionTwo_NeverZeroCells()
    {
        await using InsightRig rig = await InsightRig.StartAsync(rev: 1);
        await rig.SendFrameAsync(1, 0, InsightRig.Entity("1", "creature", 10, 10, cb: true));

        CommandResult result = await rig.RunAsync("live.heatmap", new JsonObject { ["metric"] = "combat", ["map"] = 0 });

        Assert.False(result.Ok);
        Assert.Equal(CommandException.Unavailable, result.ErrorCode);
        Assert.Contains("combat needs feed rev 2", result.ErrorMessage, StringComparison.Ordinal);
    }

    // The two lines are the ones tests/Shared/MapFeedWireFixture.cs holds, which ArcaneCore.World.Tests asserts the real writer produces byte for byte.
    [WindowsFact]
    public async Task Heatmap_OfCombat_OverTheLinesTheWorldsOwnWriterEmits_CountsTheCreatureInCombat()
    {
        await using InsightRig rig = await InsightRig.StartAsync(connect: false);
        await rig.ConnectWithHelloAsync(JsonNode.Parse(MapFeedWireFixture.RevisionTwoHelloLine)!.AsObject());
        await rig.SendFrameLineAsync(1, JsonNode.Parse(MapFeedWireFixture.RevisionTwoFrameLine)!.AsObject());

        CommandResult result = await rig.RunAsync("live.heatmap", new JsonObject { ["metric"] = "combat", ["cellSize"] = 100, ["map"] = 0 });

        Assert.Equal(2, rig.Feed.FeedRevision);
        Assert.True(result.Ok, result.ErrorMessage);
        JsonObject data = result.Data!.AsObject();
        Assert.Equal(1, data["total"]!.GetValue<int>());
        RecordedEntity wolf = Assert.Single(rig.Hub.Tail.Latest()!.Entities, e => (e.Flags & InsightRecorder.FlagCombat) != 0);
        Assert.Equal(1234u, wolf.D);
        Assert.Equal(55u, Assert.Single(rig.Hub.Tail.Latest()!.Entities, e => e.D == 55).D);
    }

    [WindowsFact]
    public async Task Heatmap_OfCombat_OnARevisionTwoFeed_CountsTheEntitiesFlaggedInTheLatestFrame()
    {
        await using InsightRig rig = await InsightRig.StartAsync(rev: 2);
        await rig.SendFrameAsync(
            1,
            0,
            InsightRig.Entity("1", "creature", 10, 10, cb: true),
            InsightRig.Entity("2", "creature", 20, 20, cb: true),
            InsightRig.Entity("3", "creature", 30, 30, cb: false),
            InsightRig.Entity("4", "creature", 250, 10, cb: true),
            InsightRig.Entity("5", "creature", 260, 10));

        CommandResult result = await rig.RunAsync("live.heatmap", new JsonObject { ["metric"] = "combat", ["cellSize"] = 100, ["map"] = 0 });

        Assert.True(result.Ok, result.ErrorMessage);
        JsonObject data = result.Data!.AsObject();
        Assert.Equal(3, data["total"]!.GetValue<int>());
        Assert.Equal(2, data["cells"]!.GetValue<int>());
        Assert.Equal(["1", "2"], data["overlay"]!["boxes"]!.AsArray().Select(b => b!["label"]!.GetValue<string>()).Order());
    }

    [WindowsFact]
    public async Task Heatmap_OfDeaths_CountsHpFallsToZeroInsideTheWindowAtTheDeathPosition()
    {
        await using InsightRig rig = await InsightRig.StartAsync();
        await rig.SendFrameAsync(1, 0, InsightRig.Entity("1", "creature", 10, 10), InsightRig.Entity("2", "creature", 500, 500));
        await rig.SendFrameAsync(2, 0, InsightRig.Entity("1", "creature", 20, 20, hp: 0), InsightRig.Entity("2", "creature", 500, 500));
        await rig.SendFrameAsync(3, 0, InsightRig.Entity("1", "creature", 20, 20, hp: 0), InsightRig.Entity("2", "creature", 510, 510, hp: 0));

        CommandResult result = await rig.RunAsync("live.heatmap", new JsonObject { ["metric"] = "deaths", ["cellSize"] = 100, ["windowSec"] = 60, ["map"] = 0 });

        Assert.True(result.Ok, result.ErrorMessage);
        JsonObject data = result.Data!.AsObject();
        Assert.Equal(2, data["total"]!.GetValue<int>());
        JsonArray boxes = data["overlay"]!["boxes"]!.AsArray();
        Assert.Equal(2, boxes.Count);
        Assert.Contains(boxes, b => Numbers(b!["min"]).Take(2).SequenceEqual([0.0, 0.0]));
        Assert.Contains(boxes, b => Numbers(b!["min"]).Take(2).SequenceEqual([500.0, 500.0]));
    }

    [WindowsFact]
    public async Task Heatmap_RefusesAnUnknownMetricAndOutOfRangeCellSizesAndWindows()
    {
        await using InsightRig rig = await InsightRig.StartAsync();

        foreach (JsonObject bad in new[]
        {
            new JsonObject { ["metric"] = "mana" },
            new JsonObject { ["metric"] = "density", ["cellSize"] = 15 },
            new JsonObject { ["metric"] = "density", ["cellSize"] = 2001 },
            new JsonObject { ["metric"] = "density", ["windowSec"] = 0 },
            new JsonObject { ["metric"] = "density", ["windowSec"] = 100000 },
            new JsonObject(),
        })
        {
            CommandResult refused = await rig.RunAsync("live.heatmap", bad);
            Assert.Equal(CommandException.BadArguments, refused.ErrorCode);
        }
    }

    // ---- live.highlight ----

    [WindowsFact]
    public async Task Highlight_RingsTheEntitiesThatMatchEveryFilter()
    {
        await using InsightRig rig = await InsightRig.StartAsync(rev: 2);
        JsonObject withRoute = InsightRig.Entity("3", "bot", 30, 30, hp: 50);
        withRoute["route"] = new JsonObject { ["pts"] = new JsonArray(Wire.Point(0, 0, 0), Wire.Point(1, 1, 1)), ["next"] = 1, ["flags"] = new JsonArray("stuck") };
        await rig.SendFrameAsync(
            1,
            0,
            InsightRig.Entity("1", "creature", 10, 10),
            InsightRig.Entity("2", "creature", 20, 20, hp: 20, mt: "chase", cb: true),
            withRoute,
            InsightRig.Entity("4", "bot", 40, 40, hp: 5, mt: "chase"));

        async Task<string[]> Names(JsonObject args)
        {
            CommandResult r = await rig.RunAsync("live.highlight", args);
            Assert.True(r.Ok, r.ErrorMessage);
            Assert.Equal(r.Data!["entities"]!.AsArray().Count, r.Data["overlay"]!["points"]!.AsArray().Count);
            return [.. r.Data["entities"]!.AsArray().Select(e => e!["g"]!.GetValue<string>()).Order()];
        }

        Assert.Equal(["2", "4"], await Names(new JsonObject { ["motion"] = "chase" }));
        Assert.Equal(["2", "4"], await Names(new JsonObject { ["hpBelow"] = 30 }));
        Assert.Equal(["4"], await Names(new JsonObject { ["hpBelow"] = 30, ["kind"] = "bot" }));
        Assert.Equal(["3"], await Names(new JsonObject { ["routeFlag"] = "stuck" }));
        Assert.Equal(["2"], await Names(new JsonObject { ["inCombat"] = true, ["kind"] = "creature", ["hpBelow"] = 50 }));
        Assert.Equal(["1"], await Names(new JsonObject { ["name"] = "UNIT 1" }));
        Assert.Empty(await Names(new JsonObject { ["hpBelow"] = 1 }));
    }

    [WindowsFact]
    public async Task Highlight_NeedsAtLeastOneFilter_SoItCannotRingTheWholeWorldByAccident()
    {
        await using InsightRig rig = await InsightRig.StartAsync();

        CommandResult result = await rig.RunAsync("live.highlight", new JsonObject());

        Assert.Equal(CommandException.BadArguments, result.ErrorCode);
        Assert.Contains("filter", result.ErrorMessage, StringComparison.Ordinal);
    }
}
