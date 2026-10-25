using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Live;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Live;

/// <summary>The action log and undo: an inverse exists only where the opposite command is known and its inputs were recorded.</summary>
public sealed class ActionLogTests
{
    private const ulong Wolf = 0xF130_0000_0000_0001;
    private const ulong Bot = 0x1000_0000_0000_0002;
    private const ulong Placed = 0xF130_0000_0000_0777;

    private static string Hex(ulong guid) => guid.ToString("X", System.Globalization.CultureInfo.InvariantCulture);

    private static DevUiRig Rig()
    {
        var rig = new DevUiRig();
        rig.Directory.Entities[Wolf] = FakeDirectory.Entity(Wolf, LiveKind.Creature, "Wolf", 100, 200, 30, map: 1);
        rig.Directory.Entities[Bot] = FakeDirectory.Entity(Bot, LiveKind.Bot, "Alphabot", 1, 2, 3, map: 0);
        rig.Requester.ServerCatalog = true;
        rig.Requester.Commands = [.. DevUiCatalog.All.Where(a => a.Path is not null).Select(a => a.Path!)];
        return rig;
    }

    [Fact]
    public async Task PlacedCreature_IsUndoneByDeletingTheGuidFromTheReply()
    {
        DevUiRig rig = Rig();
        rig.Requester.OnAction = _ => new JsonObject
        {
            ["ok"] = true,
            ["lines"] = new JsonArray($"Placed Wolf (Creature:{Placed:X16}) at 1 2 3 on map 0."),
            ["settled"] = true,
        };
        JsonObject placed = await rig.DataAsync("action.at", """{"template":"place.creature","at":[1,2,3],"args":{"entry":299}}""");
        Assert.True(placed["undoable"]!.GetValue<bool>());
        rig.Requester.OnAction = _ => new JsonObject { ["ok"] = true, ["lines"] = new JsonArray("Removed."), ["settled"] = true };

        JsonObject undone = await rig.DataAsync("action.undo", $$"""{"id":{{placed["id"]}}}""");

        JsonObject undoLine = rig.Requester.ActionLines.Last();
        Assert.True(undone["ok"]!.GetValue<bool>());
        Assert.Equal("creature delete", undoLine["command"]!.GetValue<string>());
        Assert.Equal(Hex(Placed), undoLine["targets"]![0]!.GetValue<string>());
        Assert.Equal(placed["id"]!.GetValue<long>(), rig.Actions.Log.Recent(5).Last().UndoOf);
        Assert.Equal(undone["id"]!.GetValue<long>(), rig.Actions.Log.Find(placed["id"]!.GetValue<long>())!.UndoneBy);
        Assert.False(undone["undoable"]!.GetValue<bool>());
        CommandResult twice = await rig.RunAsync("action.undo", $$"""{"id":{{placed["id"]}}}""");
        Assert.False(twice.Ok);
        Assert.Contains("already undone", twice.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Teleport_IsUndoneByGoingBackToThePositionTheLiveDataShowedBefore()
    {
        DevUiRig rig = Rig();
        JsonObject moved = await rig.DataAsync("action.at", $$"""{"template":"tele.selected","at":[500,600,70],"map":530,"targets":["{{Hex(Bot)}}"]}""");

        await rig.DataAsync("action.undo", $$"""{"id":{{moved["id"]}}}""");

        JsonObject back = rig.Requester.ActionLines.Last();
        Assert.Equal("tele", back["template"]!.GetValue<string>());
        Assert.Equal(Hex(Bot), back["targets"]![0]!.GetValue<string>());
        Assert.Equal(0u, back["point"]!["map"]!.GetValue<uint>());
        Assert.Equal(1, back["point"]!["x"]!.GetValue<double>());
        Assert.Equal(3, back["point"]!["z"]!.GetValue<double>());
        Assert.Equal(1.5, back["point"]!["o"]!.GetValue<double>());
    }

    [Fact]
    public async Task Walk_AcrossMaps_HasNoInverse_AndSaysWhy()
    {
        DevUiRig rig = Rig();

        JsonObject moved = await rig.DataAsync("action.at", $$"""{"template":"moveto.selected","at":[500,600,70],"map":530,"targets":["{{Hex(Bot)}}"]}""");

        Assert.False(moved["undoable"]!.GetValue<bool>());
        Assert.Contains("across maps", moved["noUndoReason"]!.GetValue<string>(), StringComparison.Ordinal);
        CommandResult undo = await rig.RunAsync("action.undo", $$"""{"id":{{moved["id"]}}}""");
        Assert.False(undo.Ok);
    }

    [Fact]
    public async Task Kill_IsUndoneByRespawn()
    {
        DevUiRig rig = Rig();
        JsonObject killed = await rig.DataAsync("action.on", $$"""{"template":"kill","guid":"{{Hex(Wolf)}}"}""");

        await rig.DataAsync("action.undo", $$"""{"id":{{killed["id"]}}}""");

        JsonObject undo = rig.Requester.ActionLines.Last();
        Assert.Equal("creature respawn", undo["command"]!.GetValue<string>());
        Assert.Equal(Hex(Wolf), undo["targets"]![0]!.GetValue<string>());
    }

    [Theory]
    [InlineData("bot.take", "botctl release")]
    [InlineData("bot.release", "botctl take")]
    public async Task BotOrders_UndoEachOther(string action, string inverse)
    {
        DevUiRig rig = Rig();
        JsonObject first = await rig.DataAsync("action.on", $$"""{"template":"{{action}}","guid":"{{Hex(Bot)}}"}""");

        await rig.DataAsync("action.undo", $$"""{"id":{{first["id"]}}}""");

        Assert.Equal($"{inverse} 0x{Bot:X16}", rig.Requester.ActionLines.Last()["command"]!.GetValue<string>());
    }

    [Fact]
    public async Task AiPause_IsUndoneByResume()
    {
        DevUiRig rig = Rig();
        JsonObject paused = await rig.DataAsync("action.on", $$"""{"template":"ai.pause","guid":"{{Hex(Wolf)}}"}""");

        await rig.DataAsync("action.undo", $$"""{"id":{{paused["id"]}}}""");

        Assert.Equal("creature ai resume", rig.Requester.ActionLines.Last()["command"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("setlevel", """{"level":10}""")]
    [InlineData("setfaction", """{"faction":14}""")]
    [InlineData("setspeed", """{"rate":2}""")]
    public async Task SetEdits_HaveNoUndo_BecauseTheDaemonSendsNoOldValue(string action, string args)
    {
        DevUiRig rig = Rig();

        JsonObject edited = await rig.DataAsync("action.on", $$"""{"template":"{{action}}","guid":"{{Hex(Wolf)}}","args":{{args}}}""");
        CommandResult undo = await rig.RunAsync("action.undo", $$"""{"id":{{edited["id"]}}}""");

        Assert.False(edited["undoable"]!.GetValue<bool>());
        Assert.Contains("no inverse", edited["noUndoReason"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(undo.Ok);
        Assert.Equal(CommandException.BadArguments, undo.ErrorCode);
        Assert.Single(rig.Requester.ActionLines);
    }

    [Fact]
    public async Task ARawCommand_AndAFailedAction_AreNotUndoable()
    {
        DevUiRig rig = Rig();
        JsonObject raw = await rig.DataAsync("action.run", """{"command":"creature kill"}""");
        rig.Requester.OnAction = _ => new JsonObject { ["ok"] = false, ["code"] = "target-gone", ["message"] = "gone" };
        JsonObject failed = await rig.DataAsync("action.on", $$"""{"template":"kill","guid":"{{Hex(Wolf)}}"}""");

        CommandResult rawUndo = await rig.RunAsync("action.undo", $$"""{"id":{{raw["id"]}}}""");
        CommandResult failedUndo = await rig.RunAsync("action.undo", $$"""{"id":{{failed["id"]}}}""");
        CommandResult unknown = await rig.RunAsync("action.undo", """{"id":999}""");

        Assert.False(rawUndo.Ok);
        Assert.False(failedUndo.Ok);
        Assert.Contains("failed", failedUndo.ErrorMessage, StringComparison.Ordinal);
        Assert.False(unknown.Ok);
    }

    [Fact]
    public async Task APartiallyFailedBulkAction_UndoesOnlyWhatWorked()
    {
        DevUiRig rig = Rig();
        const ulong second = 0xF130_0000_0000_0002;
        rig.Directory.Entities[second] = FakeDirectory.Entity(second, LiveKind.Creature, "Boar");
        rig.Requester.OnAction = _ => new JsonObject
        {
            ["ok"] = false,
            ["code"] = "target-gone",
            ["lines"] = new JsonArray(),
            ["targets"] = new JsonArray(
                new JsonObject { ["guid"] = Hex(Wolf), ["ok"] = true },
                new JsonObject { ["guid"] = Hex(second), ["ok"] = false, ["failure"] = "target-gone" }),
        };

        JsonObject result = await rig.DataAsync("action.on", $$"""{"template":"kill","targets":["{{Hex(Wolf)}}","{{Hex(second)}}"]}""");

        // A failed action is not undoable as a whole (the UI shows the failure); the log still records who succeeded.
        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Equal("target-gone", result["code"]!.GetValue<string>());
        Assert.True(result["targets"]![0]!["ok"]!.GetValue<bool>());
        Assert.False(result["targets"]![1]!["ok"]!.GetValue<bool>());
        Assert.False(result["undoable"]!.GetValue<bool>());
    }

    [Fact]
    public void TheLog_KeepsTheLastFiveHundred_AndMarksUndoneEntriesOnce()
    {
        var log = new ActionLog();
        for (int i = 0; i < ActionLog.Capacity + 25; i++)
        {
            log.Add(new ActionEntry { Id = log.NextId(), Ok = true, Inverse = [new ActionStep(null, "creature respawn", [1], null, null)] });
        }

        IReadOnlyList<ActionEntry> all = log.Recent(10_000);

        Assert.Equal(ActionLog.Capacity, all.Count);
        Assert.Equal(26, all[0].Id);
        Assert.Null(log.Find(25));
        Assert.True(log.MarkUndone(100, 600));
        Assert.False(log.MarkUndone(100, 601));
        Assert.False(log.MarkUndone(1, 602));
        Assert.Equal(all[^1].Id, log.LastUndoable()!.Id);
        Assert.False(log.Find(100)!.Undoable);
    }

    [Fact]
    public void EntryJson_CarriesWhatThePanelShows()
    {
        var entry = new ActionEntry
        {
            Id = 7,
            At = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero),
            Actor = "atlas-local:daemon#1",
            Action = "kill",
            Lines = ["template kill 0x1"],
            Targets = [1],
            Ok = true,
            Replies = ["done"],
            Writes = [new JsonObject { ["field"] = "health", ["old"] = 10, ["new"] = 0 }],
            Inverse = [new ActionStep(null, "creature respawn", [1], null, null)],
        };

        JsonObject json = entry.ToJson();

        Assert.Equal("2026-10-24T12:00:00.0000000+00:00", json["at"]!.GetValue<string>());
        Assert.Equal("health", json["writes"]![0]!["field"]!.GetValue<string>());
        Assert.True(json["undoable"]!.GetValue<bool>());
        Assert.Equal("1", json["targets"]![0]!.GetValue<string>());
    }
}
