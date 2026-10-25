using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Insight;
using ArcaneCore.Atlas.Tests.Live;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Insight;

/// <summary>live.record, live.replay and the headless replay queries over frames a fake daemon really sent.</summary>
public sealed class InsightRecordReplayTests
{
    private const string FrameTopic = "insight.replay.frame";
    private const string StateTopic = "insight.replay.state";

    private static IEnumerable<double> Numbers(JsonNode? array) => array!.AsArray().Select(n => n!.GetValue<double>());

    /// <summary>Records three frames: guid 1 walks 100 yd east, guid 2 dies in the second frame, guid 3 appears in the third.</summary>
    private static async Task<InsightRig> RecordedAsync(int rev = 2)
    {
        InsightRig rig = await InsightRig.StartAsync(rev);
        CommandResult on = await rig.RunAsync("live.record", new JsonObject { ["on"] = true });
        Assert.True(on.Ok, on.ErrorMessage);
        await rig.SendFrameAsync(1, 0, InsightRig.Entity("1", "creature", 0, 0, d: 11), InsightRig.Entity("2", "creature", 10, 10));
        await rig.SendFrameAsync(2, 0, InsightRig.Entity("1", "creature", 50, 0, d: 11), InsightRig.Entity("2", "creature", 10, 10, hp: 0));
        await rig.SendFrameAsync(3, 0, InsightRig.Entity("1", "creature", 100, 0, d: 11), InsightRig.Entity("2", "creature", 10, 10, hp: 0), InsightRig.Entity("3", "bot", 5, 5));
        return rig;
    }

    [WindowsFact]
    public async Task Record_StartsStopsAndReportsItsBounds_AndKeepsWhatItHasForReplayAfterStopping()
    {
        await using InsightRig rig = await InsightRig.StartAsync();
        await rig.SendFrameAsync(1, 0, InsightRig.Entity("1", "creature", 0, 0));

        CommandResult idle = await rig.RunAsync("live.record");
        Assert.False(idle.Data!["recording"]!.GetValue<bool>());
        Assert.Equal(0, idle.Data["frames"]!.GetValue<int>());

        CommandResult on = await rig.RunAsync("live.record", new JsonObject { ["on"] = true, ["maxMb"] = 8, ["maxMinutes"] = 2 });
        Assert.True(on.Data!["recording"]!.GetValue<bool>());
        Assert.Equal(8L * 1024 * 1024, on.Data["maxBytes"]!.GetValue<long>());
        Assert.Equal(2, on.Data["maxMinutes"]!.GetValue<int>());

        await rig.SendFrameAsync(2, 0, InsightRig.Entity("1", "creature", 1, 1));
        await rig.SendFrameAsync(3, 0, InsightRig.Entity("1", "creature", 2, 2));
        await Wait.UntilAsync(() => rig.Hub.Record?.NewestSeq == 3, "frames recorded");
        CommandResult running = await rig.RunAsync("live.record");
        Assert.Equal(2, running.Data!["frames"]!.GetValue<int>());
        Assert.Equal(2, running.Data["oldestSeq"]!.GetValue<long>());
        Assert.Equal(3, running.Data["newestSeq"]!.GetValue<long>());
        Assert.True(running.Data["bytes"]!.GetValue<long>() > 0);

        CommandResult off = await rig.RunAsync("live.record", new JsonObject { ["on"] = false });
        Assert.False(off.Data!["recording"]!.GetValue<bool>());
        await rig.SendFrameAsync(4, 0, InsightRig.Entity("1", "creature", 3, 3));
        CommandResult after = await rig.RunAsync("live.record");
        Assert.Equal(2, after.Data!["frames"]!.GetValue<int>());
        CommandResult replay = await rig.RunAsync("live.replay", new JsonObject { ["seq"] = 2 });
        Assert.True(replay.Ok, replay.ErrorMessage);
    }

    [WindowsFact]
    public async Task Record_RefusesSizesOutsideItsLimits()
    {
        await using InsightRig rig = await InsightRig.StartAsync();

        foreach (JsonObject bad in new[]
        {
            new JsonObject { ["on"] = true, ["maxMb"] = 0 },
            new JsonObject { ["on"] = true, ["maxMb"] = 1025 },
            new JsonObject { ["on"] = true, ["maxMinutes"] = 0 },
            new JsonObject { ["on"] = true, ["maxMinutes"] = 121 },
        })
        {
            CommandResult refused = await rig.RunAsync("live.record", bad);
            Assert.Equal(CommandException.BadArguments, refused.ErrorCode);
        }

        Assert.False((await rig.RunAsync("live.record")).Data!["recording"]!.GetValue<bool>());
    }

    [WindowsFact]
    public async Task ReplayBySeq_PublishesTheRecordedFrame_WithItsPositionsAndD_AndSaysWhatItCannotShow()
    {
        await using InsightRig rig = await RecordedAsync();

        CommandResult result = await rig.RunAsync("live.replay", new JsonObject { ["seq"] = 2 });

        Assert.True(result.Ok, result.ErrorMessage);
        Assert.Equal(2, result.Data!["seq"]!.GetValue<long>());
        Assert.False(result.Data["playing"]!.GetValue<bool>());
        Assert.Equal(["guid", "kind", "x", "y", "z", "hp", "motion", "d"], result.Data["replay"]!["fields"]!.AsArray().Select(f => f!.GetValue<string>()));
        Assert.Contains("positions", result.Data["replay"]!["note"]!.GetValue<string>(), StringComparison.Ordinal);
        JsonNode published = Assert.Single(rig.Events(FrameTopic))!;
        Assert.Equal(2, published["seq"]!.GetValue<long>());
        Assert.Equal(0, published["map"]!.GetValue<int>());
        Assert.Equal(["fields", "note"], published["replay"]!.AsObject().Select(p => p.Key).Order());
        JsonArray entities = published["entities"]!.AsArray();
        Assert.Equal(2, entities.Count);
        JsonNode walker = entities.Single(e => e!["g"]!.GetValue<string>() == "1")!;
        Assert.Equal(50.0, walker["x"]!.GetValue<double>());
        Assert.Equal(11, walker["d"]!.GetValue<int>());
        Assert.Equal("creature", walker["k"]!.GetValue<string>());
        JsonNode dead = entities.Single(e => e!["g"]!.GetValue<string>() == "2")!;
        Assert.Equal(0, dead["hp"]!.GetValue<int>());
        Assert.Null(dead["d"]);
        Assert.Null(walker["sp"]);
        Assert.Null(walker["route"]);
        Assert.True(rig.Events(StateTopic).Count >= 1);
        Assert.True(rig.Events(StateTopic)[^1]!["replaying"]!.GetValue<bool>());
    }

    [WindowsFact]
    public async Task ReplayAtSecondsAgo_InterpolatesBetweenTheRecordedFrames()
    {
        await using InsightRig rig = await RecordedAsync();

        // Frame n is at n * 250 ms, so 0.125 s before the newest (750 ms) is 625 ms: halfway between frames 2 (500) and 3 (750) is 625.
        CommandResult result = await rig.RunAsync("live.replay", new JsonObject { ["at"] = 0.125 });

        Assert.True(result.Ok, result.ErrorMessage);
        Assert.Equal(625, result.Data!["clockMs"]!.GetValue<long>());
        JsonNode walker = rig.Events(FrameTopic).Single()!["entities"]!.AsArray().Single(e => e!["g"]!.GetValue<string>() == "1")!;
        Assert.Equal(75.0, walker["x"]!.GetValue<double>());
    }

    [WindowsFact]
    public async Task Replay_FailsClearlyWhenThereIsNoRecording_OrTheMomentIsOutsideIt_OrTheArgumentsConflict()
    {
        await using InsightRig rig = await InsightRig.StartAsync(rev: 2);

        CommandResult none = await rig.RunAsync("live.replay", new JsonObject { ["seq"] = 1 });
        Assert.Equal(CommandException.Unavailable, none.ErrorCode);
        Assert.Contains("live.record", none.ErrorMessage, StringComparison.Ordinal);

        await rig.RunAsync("live.record", new JsonObject { ["on"] = true });
        await rig.SendFrameAsync(1, 0, InsightRig.Entity("1", "creature", 0, 0));
        await rig.SendFrameAsync(2, 0, InsightRig.Entity("1", "creature", 0, 0));
        await Wait.UntilAsync(() => rig.Hub.Record?.NewestSeq == 2, "recorded");

        Assert.Equal(CommandException.Failed, (await rig.RunAsync("live.replay", new JsonObject { ["seq"] = 99 })).ErrorCode);
        Assert.Equal(CommandException.Failed, (await rig.RunAsync("live.replay", new JsonObject { ["at"] = 3600 })).ErrorCode);
        Assert.Equal(CommandException.BadArguments, (await rig.RunAsync("live.replay", new JsonObject { ["at"] = 1, ["seq"] = 1 })).ErrorCode);
        Assert.Equal(CommandException.BadArguments, (await rig.RunAsync("live.replay", new JsonObject { ["at"] = -1 })).ErrorCode);
        Assert.Equal(CommandException.BadArguments, (await rig.RunAsync("live.replay", new JsonObject())).ErrorCode);
        Assert.Equal(CommandException.BadArguments, (await rig.RunAsync("live.replay", new JsonObject { ["seq"] = 1, ["speed"] = 0 })).ErrorCode);
        Assert.Equal(CommandException.BadArguments, (await rig.RunAsync("live.replay", new JsonObject { ["seq"] = 1, ["speed"] = 61 })).ErrorCode);
        Assert.Equal(CommandException.BadArguments, (await rig.RunAsync("live.replay", new JsonObject { ["seq"] = 1, ["action"] = "rewind" })).ErrorCode);
    }

    [WindowsFact]
    public async Task Play_PublishesEveryRecordedFrameInOrderFromTheStart_ThenReportsThatReplayEnded()
    {
        await using InsightRig rig = await RecordedAsync();

        CommandResult play = await rig.RunAsync("live.replay", new JsonObject { ["seq"] = 1, ["action"] = "play", ["speed"] = 60 });

        Assert.True(play.Ok, play.ErrorMessage);
        Assert.True(play.Data!["playing"]!.GetValue<bool>());
        await Wait.UntilAsync(() => rig.Events(StateTopic).Any(s => s!["replaying"]!.GetValue<bool>() == false), "replay ended");
        Assert.Equal([1L, 2L, 3L], rig.Events(FrameTopic).Select(f => f!["seq"]!.GetValue<long>()));
        JsonNode last = rig.Events(StateTopic)[^1]!;
        Assert.False(last["replaying"]!.GetValue<bool>());
        Assert.False(last["playing"]!.GetValue<bool>());
    }

    [WindowsFact]
    public async Task PauseHoldsThePosition_AndStopEndsTheReplay()
    {
        await using InsightRig rig = await RecordedAsync();

        // Speed 0.1 puts 2.5 s between frames: the first frame is published at once, the second is far away.
        CommandResult play = await rig.RunAsync("live.replay", new JsonObject { ["seq"] = 1, ["action"] = "play", ["speed"] = 0.1 });
        Assert.True(play.Data!["playing"]!.GetValue<bool>());
        await Wait.UntilAsync(() => rig.EventCount(FrameTopic) == 1, "first replayed frame");

        CommandResult paused = await rig.RunAsync("live.replay", new JsonObject { ["action"] = "pause" });
        Assert.False(paused.Data!["playing"]!.GetValue<bool>());
        Assert.Equal(1, paused.Data["seq"]!.GetValue<long>());
        Assert.True(rig.Events(StateTopic)[^1]!["replaying"]!.GetValue<bool>());
        Assert.False(rig.Events(StateTopic)[^1]!["playing"]!.GetValue<bool>());

        CommandResult stopped = await rig.RunAsync("live.replay", new JsonObject { ["action"] = "stop" });
        Assert.True(stopped.Ok, stopped.ErrorMessage);
        Assert.False(rig.Events(StateTopic)[^1]!["replaying"]!.GetValue<bool>());
        Assert.Equal(1, rig.EventCount(FrameTopic));

        CommandResult stopAgain = await rig.RunAsync("live.replay", new JsonObject { ["action"] = "stop" });
        Assert.True(stopAgain.Ok, "stopping when nothing replays must succeed");
    }

    [WindowsFact]
    public async Task ReplayEntities_ListsTheEntitiesOfARecordedMoment_WithFilters()
    {
        await using InsightRig rig = await RecordedAsync();

        CommandResult all = await rig.RunAsync("live.replay.entities", new JsonObject { ["seq"] = 3 });
        Assert.Equal(3, all.Data!["matched"]!.GetValue<int>());
        Assert.Equal(3, all.Data["seq"]!.GetValue<long>());
        JsonNode one = all.Data["entities"]!.AsArray().Single(e => e!["g"]!.GetValue<string>() == "1")!;
        Assert.Equal([100.0, 0.0, 5.0], Numbers(JsonNode.Parse($"[{one["x"]},{one["y"]},{one["z"]}]")));
        Assert.Equal(11, one["d"]!.GetValue<int>());

        CommandResult bots = await rig.RunAsync("live.replay.entities", new JsonObject { ["seq"] = 3, ["kind"] = "bot" });
        Assert.Equal(["3"], bots.Data!["entities"]!.AsArray().Select(e => e!["g"]!.GetValue<string>()));

        CommandResult dead = await rig.RunAsync("live.replay.entities", new JsonObject { ["seq"] = 3, ["hpBelow"] = 1 });
        Assert.Equal(["2"], dead.Data!["entities"]!.AsArray().Select(e => e!["g"]!.GetValue<string>()));

        CommandResult limited = await rig.RunAsync("live.replay.entities", new JsonObject { ["seq"] = 3, ["limit"] = 2 });
        Assert.Equal(2, limited.Data!["returned"]!.GetValue<int>());
        Assert.True(limited.Data["truncated"]!.GetValue<bool>());

        Assert.Equal(CommandException.Failed, (await rig.RunAsync("live.replay.entities", new JsonObject { ["seq"] = 77 })).ErrorCode);
    }

    [WindowsFact]
    public async Task ReplayDiff_ReportsWhatAppearedVanishedDiedAndMovedBetweenTwoRecordedFrames()
    {
        await using InsightRig rig = await RecordedAsync();

        CommandResult diff = await rig.RunAsync("live.replay.diff", new JsonObject { ["a"] = 1, ["b"] = 3, ["moved"] = 50 });

        Assert.True(diff.Ok, diff.ErrorMessage);
        Assert.Equal(["3"], diff.Data!["appeared"]!.AsArray().Select(g => g!.GetValue<string>()));
        Assert.Empty(diff.Data["vanished"]!.AsArray());
        Assert.Equal(["2"], diff.Data["died"]!.AsArray().Select(g => g!.GetValue<string>()));
        Assert.Equal(["1"], diff.Data["moved"]!.AsArray().Select(g => g!.GetValue<string>()));

        CommandResult strict = await rig.RunAsync("live.replay.diff", new JsonObject { ["a"] = 1, ["b"] = 3, ["moved"] = 100 });
        Assert.Empty(strict.Data!["moved"]!.AsArray());

        Assert.Equal(CommandException.Failed, (await rig.RunAsync("live.replay.diff", new JsonObject { ["a"] = 1, ["b"] = 42 })).ErrorCode);
        Assert.Equal(CommandException.BadArguments, (await rig.RunAsync("live.replay.diff", new JsonObject { ["a"] = 1 })).ErrorCode);
        Assert.Equal(CommandException.BadArguments, (await rig.RunAsync("live.replay.diff", new JsonObject { ["a"] = 1, ["b"] = 3, ["moved"] = -1 })).ErrorCode);
    }
}
