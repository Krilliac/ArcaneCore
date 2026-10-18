using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Live;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Live;

/// <summary>The <c>live.*</c> commands: argument validation before anything touches the pipe, and the entity listing.</summary>
public sealed class LiveCommandsTests
{
    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    private static async Task<CommandResult> RefusedAsync(LiveRig rig, string command, string args)
    {
        CommandResult result = await rig.RunAsync(command, Obj(args));
        Assert.False(result.Ok, $"{command} {args} should have been refused");
        return result;
    }

    [Theory]
    [InlineData("""{}""", "'map' is required")]
    [InlineData("""{"map":-1}""", "map id")]
    [InlineData("""{"map":"x"}""", "integer")]
    [InlineData("""{"map":0,"instance":-1}""", "'instance'")]
    [InlineData("""{"map":0,"hz":0}""", "'hz'")]
    [InlineData("""{"map":0,"hz":-2}""", "'hz'")]
    [InlineData("""{"map":0,"hz":61}""", "'hz'")]
    [InlineData("""{"map":0,"kinds":["wolf"]}""", "unknown kind 'wolf'")]
    [InlineData("""{"map":0,"kinds":"player,dragon"}""", "unknown kind 'dragon'")]
    [InlineData("""{"map":0,"colour":"red"}""", "unknown argument 'colour'")]
    [InlineData("""{"map":0,"paths":"maybe"}""", "true or false")]
    public async Task Subscribe_RefusesBadArguments_BeforeAnythingIsSent(string args, string message)
    {
        await using var rig = new LiveRig();
        await rig.ConnectAsync();

        CommandResult result = await RefusedAsync(rig, "live.subscribe", args);

        Assert.Equal(CommandException.BadArguments, result.ErrorCode);
        Assert.Contains(message, result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Subscribe_AcceptsKindsAsACommaListFromTheCommandLine_WithoutDuplicates()
    {
        await using var rig = new LiveRig();
        FakeFeedServer server = await rig.ConnectAsync();

        CommandResult result = await rig.RunAsync("live.subscribe", Obj("""{"map":"0","hz":"1.5","kinds":"bot, creature,bot","paths":"false"}"""));
        JsonObject sent = await server.ReadAsync();

        Assert.True(result.Ok, result.ErrorMessage);
        Assert.Equal("""["bot","creature"]""", sent["kinds"]!.ToJsonString());
        Assert.Equal(1.5, sent["hz"]!.GetValue<double>());
        Assert.False(sent["paths"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Subscribe_WithoutAConnection_IsUnavailable()
    {
        await using var rig = new LiveRig();

        CommandResult result = await RefusedAsync(rig, "live.subscribe", """{"map":0}""");

        Assert.Equal(CommandException.Unavailable, result.ErrorCode);
        Assert.Contains("live.connect", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"a":[1,2,3]}""", "'kind' is required")]
    [InlineData("""{"kind":"los"}""", "'a' is required")]
    [InlineData("""{"kind":"teleport","a":[1,2,3]}""", "one of los, height, path, undermap")]
    [InlineData("""{"kind":"los","a":[1,2,3]}""", "needs argument 'b'")]
    [InlineData("""{"kind":"path","a":[1,2,3]}""", "needs argument 'b'")]
    [InlineData("""{"kind":"height","a":[1,2,3],"b":[4,5,6]}""", "takes one point")]
    [InlineData("""{"kind":"undermap","a":[1,2,3],"b":[4,5,6]}""", "takes one point")]
    [InlineData("""{"kind":"height","a":[1,2]}""", "three finite numbers")]
    [InlineData("""{"kind":"height","a":[1,2,"x"]}""", "three finite numbers")]
    [InlineData("""{"kind":"height","a":"1,2"}""", "three finite numbers")]
    [InlineData("""{"kind":"height","a":[1,2,3],"map":-4}""", "'map'")]
    [InlineData("""{"kind":"height","a":[1,2,3],"instance":-4,"map":0}""", "'instance'")]
    public async Task Query_RefusesBadArguments_BeforeAnythingIsSent(string args, string message)
    {
        await using var rig = new LiveRig();
        await rig.ConnectAsync();

        CommandResult result = await RefusedAsync(rig, "live.query", args);

        Assert.Equal(CommandException.BadArguments, result.ErrorCode);
        Assert.Contains(message, result.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"guid":"xyz"}""")]
    [InlineData("""{"guid":"0"}""")]
    [InlineData("""{"guid":true}""")]
    [InlineData("""{"guid":1.5}""")]
    [InlineData("""{"guid":"12345678901234567"}""")]
    [InlineData("""{"guid":["1"]}""")]
    public async Task Follow_RefusesAGuidThatIsNotNonZeroHex(string args)
    {
        await using var rig = new LiveRig();

        CommandResult result = await RefusedAsync(rig, "live.follow", args);

        Assert.Equal(CommandException.BadArguments, result.ErrorCode);
        Assert.Contains("guid", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Null(rig.View.Current.FollowGuid);
    }

    [Theory]
    [InlineData("""{"guid":1234}""")]
    [InlineData("""{"guid":1e3}""")]
    public async Task Follow_RefusesABareNumber_WithAHintToQuoteIt(string args)
    {
        await using var rig = new LiveRig();

        CommandResult result = await RefusedAsync(rig, "live.follow", args);

        Assert.Equal(CommandException.BadArguments, result.ErrorCode);
        Assert.Contains("quote", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Null(rig.View.Current.FollowGuid);
    }

    [Theory]
    [InlineData("""{"radius":50}""", "needs 'near'")]
    [InlineData("""{"near":[0,0,0],"radius":0}""", "'radius'")]
    [InlineData("""{"near":[0,0,0],"radius":40001}""", "'radius'")]
    [InlineData("""{"limit":0}""", "'limit'")]
    [InlineData("""{"limit":5001}""", "'limit'")]
    [InlineData("""{"kind":"dragon"}""", "must be one of player, bot, creature, gameobject")]
    [InlineData("""{"near":[0,0]}""", "three finite numbers")]
    [InlineData("""{"guid":"zz"}""", "guid")]
    [InlineData("""{"map":-1}""", "'map'")]
    [InlineData("""{"trails":"sometimes"}""", "true or false")]
    public async Task Entities_RefuseBadArguments(string args, string message)
    {
        await using var rig = new LiveRig();

        CommandResult result = await RefusedAsync(rig, "live.entities", args);

        Assert.Equal(CommandException.BadArguments, result.ErrorCode);
        Assert.Contains(message, result.ErrorMessage, StringComparison.Ordinal);
    }

    private static async Task<(LiveRig Rig, FakeFeedServer Server)> SeededAsync()
    {
        var rig = new LiveRig();
        FakeFeedServer server = await rig.ConnectAsync();
        JsonObject chest = Wire.Entity("5", "gameobject", 10, 10, 0, "Chest");
        JsonObject wolf = Wire.Entity("3", "creature", 100, 0, 0, "Young Wolf");
        wolf["sp"] = new JsonObject { ["id"] = 9, ["start"] = Wire.Point(100, 0, 0), ["pts"] = new JsonArray(Wire.Point(110, 0, 0)), ["t0"] = 100, ["dur"] = 800, ["run"] = false };
        JsonObject bot = Wire.Entity("2", "bot", 30, 0, 0, "Bobby");
        bot["route"] = new JsonObject { ["pts"] = new JsonArray(Wire.Point(31, 0, 0)), ["next"] = 0, ["flags"] = new JsonArray("meshEndedShort") };
        await server.WriteAsync(Wire.Frame(
            0, 0, 1, Wire.Entity("1", "player", 0, 0, 0, "Alice"), bot, wolf, Wire.Entity("4", "creature", -200, 0, 0, "Old Wolf"), chest));
        await rig.Log.NextAsync(EventHub.Topics.LiveFrame);
        await server.WriteAsync(Wire.Frame(1, 0, 1, Wire.Entity("6", "creature", 0, 0, 0, "Far Wolf")));
        await rig.Log.NextAsync(EventHub.Topics.LiveFrame);
        return (rig, server);
    }

    private static IEnumerable<string> Guids(CommandResult result)
        => result.Data!["entities"]!.AsArray().Select(e => e!["guid"]!.GetValue<string>());

    [Fact]
    public async Task Entities_ListsEverythingByGuid_WithTheFieldsThatWereSent()
    {
        (LiveRig rig, _) = await SeededAsync();
        await using (rig)
        {
            CommandResult result = await rig.RunAsync("live.entities");

            Assert.True(result.Ok, result.ErrorMessage);
            Assert.Equal(new[] { "1", "2", "3", "4", "5", "6" }, Guids(result));
            Assert.Equal(6, result.Data!["matched"]!.GetValue<int>());
            Assert.False(result.Data["truncated"]!.GetValue<bool>());
            Assert.True(result.Data["connected"]!.GetValue<bool>());

            JsonObject wolf = result.Data["entities"]![2]!.AsObject();
            Assert.Equal("creature", wolf["kind"]!.GetValue<string>());
            Assert.Equal("Young Wolf", wolf["name"]!.GetValue<string>());
            Assert.Equal(1234, wolf["entry"]!.GetValue<int>());
            Assert.Equal(0, wolf["map"]!.GetValue<int>());
            Assert.Equal("[100,0,0]", wolf["at"]!.ToJsonString());
            Assert.Equal(100.0, wolf["hp"]!.GetValue<double>());
            Assert.Equal("idle", wolf["motion"]!.GetValue<string>());
            Assert.Equal(9, wolf["spline"]!["id"]!.GetValue<int>());
            Assert.Equal("[[110,0,0]]", wolf["spline"]!["pts"]!.ToJsonString());
            Assert.Null(wolf["route"]);
            Assert.Null(wolf["trail"]);

            JsonObject bot = result.Data["entities"]![1]!.AsObject();
            Assert.Equal("""["meshEndedShort"]""", bot["route"]!["flags"]!.ToJsonString());
        }
    }

    [Fact]
    public async Task Entities_FilterByKindNameMapInstanceAndGuid()
    {
        (LiveRig rig, _) = await SeededAsync();
        await using (rig)
        {
            Assert.Equal(new[] { "3", "4", "6" }, Guids(await rig.RunAsync("live.entities", Obj("""{"kind":"creature"}"""))));
            Assert.Equal(new[] { "3", "4", "6" }, Guids(await rig.RunAsync("live.entities", Obj("""{"name":"WOLF"}"""))));
            Assert.Equal(new[] { "3" }, Guids(await rig.RunAsync("live.entities", Obj("""{"name":"young"}"""))));
            Assert.Equal(new[] { "6" }, Guids(await rig.RunAsync("live.entities", Obj("""{"map":1}"""))));
            Assert.Empty(Guids(await rig.RunAsync("live.entities", Obj("""{"map":1,"instance":5}"""))));
            Assert.Equal(new[] { "3" }, Guids(await rig.RunAsync("live.entities", Obj("""{"guid":"0x3"}"""))));
            Assert.False((await rig.RunAsync("live.entities", Obj("""{"guid":3}"""))).Ok);
            Assert.Equal(new[] { "3", "4" }, Guids(await rig.RunAsync("live.entities", Obj("""{"kind":"creature","map":0}"""))));
            CommandResult none = await rig.RunAsync("live.entities", Obj("""{"name":"dragon"}"""));
            Assert.True(none.Ok);
            Assert.Equal(0, none.Data!["matched"]!.GetValue<int>());
        }
    }

    [Fact]
    public async Task Entities_Near_ReturnsNearestFirstWithDistance_OnTheViewsMapUnlessAMapIsGiven()
    {
        (LiveRig rig, _) = await SeededAsync();
        await using (rig)
        {
            CommandResult result = await rig.RunAsync("live.entities", Obj("""{"near":[0,0,0],"radius":150}"""));
            CommandResult otherMap = await rig.RunAsync("live.entities", Obj("""{"near":[0,0,0],"radius":10,"map":1}"""));
            CommandResult defaultRadius = await rig.RunAsync("live.entities", Obj("""{"near":[0,0,0]}"""));

            // 1 at 0, 5 at 14.14, 2 at 30, 3 at 100; 4 is 200 away and 6 is on another map.
            Assert.Equal(new[] { "1", "5", "2", "3" }, Guids(result));
            Assert.Equal(0.0, result.Data!["entities"]![0]!["distance"]!.GetValue<double>());
            Assert.Equal(14.142, result.Data["entities"]![1]!["distance"]!.GetValue<double>());
            Assert.Equal(100.0, result.Data["entities"]![3]!["distance"]!.GetValue<double>());
            Assert.Equal(new[] { "6" }, Guids(otherMap));
            Assert.Equal(new[] { "1", "5", "2", "3" }, Guids(defaultRadius));
        }
    }

    [Fact]
    public async Task Entities_Limit_TruncatesAndSaysSo()
    {
        (LiveRig rig, _) = await SeededAsync();
        await using (rig)
        {
            CommandResult result = await rig.RunAsync("live.entities", Obj("""{"limit":2}"""));

            Assert.Equal(new[] { "1", "2" }, Guids(result));
            Assert.Equal(6, result.Data!["matched"]!.GetValue<int>());
            Assert.Equal(2, result.Data["returned"]!.GetValue<int>());
            Assert.True(result.Data["truncated"]!.GetValue<bool>());
        }
    }

    [Fact]
    public async Task Entities_Trails_ListTheRecentPositionsOldestFirst()
    {
        (LiveRig rig, FakeFeedServer server) = await SeededAsync();
        await using (rig)
        {
            for (int step = 1; step <= 3; step++)
            {
                await server.WriteAsync(Wire.Frame(0, 0, 1 + step, Wire.Entity("2", "bot", 30 + step, 0, 0, "Bobby")));
                await rig.Log.NextAsync(EventHub.Topics.LiveFrame);
            }

            CommandResult with = await rig.RunAsync("live.entities", Obj("""{"guid":"2","trails":true}"""));
            CommandResult without = await rig.RunAsync("live.entities", Obj("""{"guid":"2"}"""));

            Assert.Equal("[[30,0,0],[31,0,0],[32,0,0],[33,0,0]]", with.Data!["entities"]![0]!["trail"]!.ToJsonString());
            Assert.Null(without.Data!["entities"]![0]!["trail"]);
        }
    }

    [Fact]
    public async Task Status_WithoutAConnection_SaysSoAndKeepsWorking()
    {
        await using var rig = new LiveRig();

        CommandResult result = await rig.RunAsync("live.status");

        Assert.True(result.Ok, result.ErrorMessage);
        Assert.False(result.Data!["connected"]!.GetValue<bool>());
        Assert.Equal("not connected", result.Data["detail"]!.GetValue<string>());
        Assert.Null(result.Data["pid"]);
        Assert.Equal(1, result.Data["protocol"]!.GetValue<int>());
        Assert.Empty(result.Data["frames"]!.AsArray());
        Assert.Equal(0, result.Data["entities"]!["total"]!.GetValue<int>());
        Assert.Equal(0, result.Data["stats"]!["badMessages"]!.GetValue<long>());
    }

    [Fact]
    public async Task Status_ShowsTheFollowTheViewHolds()
    {
        await using var rig = new LiveRig();
        rig.View.Patch(Obj("""{"follow":"ab"}"""), "ui");

        CommandResult result = await rig.RunAsync("live.status");

        Assert.Equal("AB", result.Data!["following"]!.GetValue<string>());
    }
}
