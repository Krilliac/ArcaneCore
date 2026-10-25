using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Live;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Live;

public sealed class FrameParserTests
{
    /// <summary>Round-trips through text, as a line off the wire is: parsed nodes convert to any numeric type, code-built ones do not.</summary>
    private static JsonObject OnTheWire(JsonObject built) => JsonNode.Parse(built.ToJsonString())!.AsObject();

    private static ParsedFrame Parse(JsonObject frame)
    {
        Assert.True(FrameParser.TryParseFrame(OnTheWire(frame), 123, out ParsedFrame? parsed, out string? problem), problem);
        return parsed;
    }

    private static JsonObject Raw(string json) => JsonNode.Parse(json)!.AsObject();

    [Theory]
    [InlineData(4, 11, (byte)4, (byte)11)]
    [InlineData(0, 0, (byte)0, (byte)0)]
    [InlineData(-3, 300, (byte)0, (byte)0)]
    public void APlayerEntity_ReadsRaceAndClass_AndAnythingOutOfRangeIsUnknown(int race, int playerClass, byte expectedRace, byte expectedClass)
    {
        JsonObject entity = Wire.Entity("0x1A2B", "player", 1, 2, 3, "Alice", 0);
        entity["r"] = race;
        entity["cl"] = playerClass;

        LiveEntity e = Assert.Single(Parse(Wire.Frame(1, 0, 9, entity)).Frame.Entities);

        Assert.Equal(expectedRace, e.Race);
        Assert.Equal(expectedClass, e.Class);
    }

    [Theory]
    [InlineData("\"r\":4,\"cl\":11", (byte)4, (byte)11)]
    [InlineData("\"r\":\"4\",\"cl\":\"11\"", (byte)0, (byte)0)]
    [InlineData("\"r\":4.5,\"cl\":11.5", (byte)0, (byte)0)]
    [InlineData("\"r\":null,\"cl\":true", (byte)0, (byte)0)]
    [InlineData("\"r\":256,\"cl\":-1", (byte)0, (byte)0)]
    public void APlayerEntityParsedFromJsonText_ReadsRaceAndClass_AndAWrongTypeIsUnknown(string fields, byte expectedRace, byte expectedClass)
    {
        JsonObject entity = Raw("{\"g\":\"0x1A2B\",\"k\":\"player\",\"n\":\"Alice\",\"x\":1,\"y\":2,\"z\":3," + fields + "}");

        LiveEntity e = Assert.Single(Parse(Wire.Frame(1, 0, 9, entity)).Frame.Entities);

        Assert.Equal(expectedRace, e.Race);
        Assert.Equal(expectedClass, e.Class);
    }

    [Fact]
    public void AnEntityWithoutRaceAndClass_ReadsThemAsUnknown()
    {
        LiveEntity e = Assert.Single(Parse(Wire.Frame(1, 0, 9, Wire.Entity("0x1A2B", "player", 1, 2, 3, "Alice", 0))).Frame.Entities);

        Assert.Equal(0, e.Race);
        Assert.Equal(0, e.Class);
    }

    [Fact]
    public void AFullEntity_KeepsEveryField()
    {
        JsonObject entity = Wire.Entity("0x1A2B", "creature", -9462.66, 16.19, 57.05, "Marshal Dughan", 1.25);
        entity["tg"] = "0x0000000000000042";
        entity["home"] = Wire.Point(-9460, 16, 57);
        entity["sp"] = new JsonObject
        {
            ["id"] = 77,
            ["start"] = Wire.Point(1, 2, 3),
            ["pts"] = new JsonArray(Wire.Point(4, 5, 6), Wire.Point(7, 8, 9)),
            ["t0"] = 5000,
            ["dur"] = 1500,
            ["run"] = true,
        };
        entity["route"] = new JsonObject
        {
            ["pts"] = new JsonArray(Wire.Point(1, 1, 1), Wire.Point(2, 2, 2), Wire.Point(3, 3, 3)),
            ["next"] = 2,
            ["flags"] = new JsonArray("meshEndedShort"),
        };

        ParsedFrame parsed = Parse(Wire.Frame(1, 0, 9, entity));

        LiveEntity e = Assert.Single(parsed.Frame.Entities);
        Assert.Equal(0x1A2BUL, e.Id);
        Assert.Equal(LiveKind.Creature, e.Kind);
        Assert.Equal(1234u, e.Entry);
        Assert.Equal("Marshal Dughan", e.Name);
        Assert.Equal(new WorldPoint(-9462.66, 16.19, 57.05), e.Position);
        Assert.Equal(1.25, e.Orientation);
        Assert.Equal(100.0, e.HealthPercent);
        Assert.Equal("idle", e.Motion);
        Assert.Equal(0x42UL, e.Target);
        Assert.Equal(new WorldPoint(-9460, 16, 57), e.Home);
        Assert.NotNull(e.Spline);
        Assert.Equal(77u, e.Spline.Id);
        Assert.Equal(new WorldPoint(1, 2, 3), e.Spline.Start);
        Assert.Equal(2, e.Spline.Points.Count);
        Assert.Equal(5000, e.Spline.StartClockMs);
        Assert.Equal(1500, e.Spline.DurationMs);
        Assert.True(e.Spline.Run);
        Assert.NotNull(e.Route);
        Assert.Equal(2, e.Route.Next);
        Assert.Equal(new[] { "meshEndedShort" }, e.Route.Flags);
        Assert.Equal(9, parsed.Frame.Seq);
        Assert.Equal(2250, parsed.Frame.ClockMs);
        Assert.Equal(2, parsed.Frame.Grids.Count);
        Assert.Equal(7, parsed.Frame.Counters!["deferredPathSearches"]!.GetValue<int>());
        Assert.Equal(0, parsed.Rejected);
    }

    [Fact]
    public void GuidsArePushedInTheOneCanonicalSpelling()
    {
        JsonObject entity = Wire.Entity("0x00000000000000ab", "player", 1, 2, 3);
        entity["tg"] = "00ff";

        ParsedFrame parsed = Parse(Wire.Frame(0, 0, 1, entity));

        var pushed = parsed.Json["entities"]!.AsArray()[0]!.AsObject();
        Assert.Equal("AB", pushed["g"]!.GetValue<string>());
        Assert.Equal("FF", pushed["tg"]!.GetValue<string>());
        Assert.Equal(0xABUL, parsed.Frame.Entities[0].Id);
    }

    [Theory]
    [InlineData("1A2B", true, 0x1A2BUL)]
    [InlineData("0x1a2b", true, 0x1A2BUL)]
    [InlineData("0XFFFFFFFFFFFFFFFF", true, ulong.MaxValue)]
    [InlineData("  7  ", true, 7UL)]
    [InlineData("", false, 0UL)]
    [InlineData("0x", false, 0UL)]
    [InlineData("12345678901234567", false, 0UL)]
    [InlineData("-1", false, 0UL)]
    [InlineData("1.5", false, 0UL)]
    [InlineData("xyz", false, 0UL)]
    public void Guids_ParseHexWithOrWithoutPrefix(string text, bool ok, ulong expected)
    {
        Assert.Equal(ok, LiveGuid.TryParse(text, out ulong guid));
        Assert.Equal(expected, guid);
    }

    [Theory]
    [InlineData("""{"g":"1","k":"creature","x":1,"y":2}""", "x, y and z")]
    [InlineData("""{"g":"1","k":"creature","x":"1","y":2,"z":3}""", "x, y and z")]
    [InlineData("""{"g":"1","k":"creature","x":1e999,"y":2,"z":3}""", "x, y and z")]
    [InlineData("""{"g":"1","k":"creature","x":5000000,"y":2,"z":3}""", "x, y and z")]
    [InlineData("""{"g":"1","k":"creature","x":1,"y":2,"z":null}""", "x, y and z")]
    [InlineData("""{"g":"1","k":"vehicle","x":1,"y":2,"z":3}""", "not one of")]
    [InlineData("""{"g":"1","x":1,"y":2,"z":3}""", "not one of")]
    [InlineData("""{"g":"Creature","k":"creature","x":1,"y":2,"z":3}""", "g is not")]
    [InlineData("""{"g":"0","k":"creature","x":1,"y":2,"z":3}""", "g is not")]
    [InlineData("""{"g":7,"k":"creature","x":1,"y":2,"z":3}""", "g is not")]
    [InlineData("""{"k":"creature","x":1,"y":2,"z":3}""", "g is not")]
    [InlineData("""{"g":"1","k":"creature","x":1,"y":2,"z":3,"e":-4}""", "e is not")]
    [InlineData("""{"g":"1","k":"creature","x":1,"y":2,"z":3,"n":5}""", "n is not")]
    [InlineData("""{"g":"1","k":"creature","x":1,"y":2,"z":3,"o":"north"}""", "o is not")]
    [InlineData("""{"g":"1","k":"creature","x":1,"y":2,"z":3,"hp":"full"}""", "hp is not")]
    [InlineData("""{"g":"1","k":"creature","x":1,"y":2,"z":3,"tg":"nobody"}""", "tg is not")]
    [InlineData("""{"g":"1","k":"creature","x":1,"y":2,"z":3,"home":[1,2]}""", "home is not")]
    [InlineData("""{"g":"1","k":"creature","x":1,"y":2,"z":3,"sp":{"start":[0,0,0]}}""", "sp.pts")]
    [InlineData("""{"g":"1","k":"creature","x":1,"y":2,"z":3,"sp":{"pts":[]}}""", "sp.start")]
    [InlineData("""{"g":"1","k":"creature","x":1,"y":2,"z":3,"sp":{"start":[0,0,0],"pts":[[1,2]]}}""", "sp.pts")]
    [InlineData("""{"g":"1","k":"creature","x":1,"y":2,"z":3,"sp":{"start":[0,0,0],"pts":[],"run":"yes"}}""", "sp has")]
    [InlineData("""{"g":"1","k":"creature","x":1,"y":2,"z":3,"route":{"pts":[[1,1,1]],"next":3}}""", "route.next")]
    [InlineData("""{"g":"1","k":"creature","x":1,"y":2,"z":3,"route":{"pts":[[1,1,1]],"next":3,"cut":false}}""", "route.next")]
    [InlineData("""{"g":"1","k":"creature","x":1,"y":2,"z":3,"route":{"pts":[[1,1,1]],"next":-1,"cut":true}}""", "route.next")]
    [InlineData("""{"g":"1","k":"creature","x":1,"y":2,"z":3,"route":{"pts":[[1,1,1]],"cut":"yes"}}""", "route.cut")]
    [InlineData("""{"g":"1","k":"creature","x":1,"y":2,"z":3,"route":{"pts":[[1,1,1]],"flags":[1]}}""", "route.flags")]
    [InlineData("""{"g":"1","k":"creature","x":1,"y":2,"z":3,"route":"none"}""", "route is not")]
    public void ABadEntity_IsDroppedAndCounted_TheFrameStillLands(string entityJson, string reason)
    {
        JsonObject good = Wire.Entity("2", "player", 1, 2, 3);
        JsonObject frame = Wire.Frame(0, 0, 1, good);
        frame["entities"]!.AsArray().Add(Raw(entityJson));
        frame["entities"]!.AsArray().Add(Wire.Entity("3", "bot", 4, 5, 6));

        ParsedFrame parsed = Parse(frame);

        Assert.Equal(new ulong[] { 2, 3 }, parsed.Frame.Entities.Select(e => e.Id));
        Assert.Equal(1, parsed.Rejected);
        Assert.Contains(reason, parsed.FirstProblem, StringComparison.Ordinal);
        Assert.Equal(2, parsed.Json["entities"]!.AsArray().Count);
    }

    [Fact]
    public void ACutRoute_WhoseNextIsBeyondTheShippedPoints_KeepsTheEntity_WithNextClamped()
    {
        // The daemon cuts a long route to its first points but leaves next indexing the full route.
        JsonObject frame = Wire.Frame(0, 0, 1, Wire.Entity("3", "bot", 4, 5, 6));
        frame["entities"]!.AsArray()[0]!.AsObject()["route"] = Raw("""{"pts":[[1,1,1],[2,2,2]],"next":300,"flags":["meshEndedShort"],"cut":true}""");

        ParsedFrame parsed = Parse(frame);

        Assert.Equal(0, parsed.Rejected);
        LiveEntity bot = Assert.Single(parsed.Frame.Entities);
        Assert.NotNull(bot.Route);
        Assert.True(bot.Route.Cut);
        Assert.Equal(2, bot.Route.Points.Count);
        Assert.Equal(2, bot.Route.Next);
        Assert.True(bot.Route.ToJson()["cut"]!.GetValue<bool>());
    }

    [Fact]
    public void AnEntityThatIsNotAnObject_IsDropped()
    {
        JsonObject frame = Wire.Frame(0, 0, 1);
        frame["entities"]!.AsArray().Add(JsonValue.Create(5));
        frame["entities"]!.AsArray().Add(JsonValue.Create("text"));

        ParsedFrame parsed = Parse(frame);

        Assert.Empty(parsed.Frame.Entities);
        Assert.Equal(2, parsed.Rejected);
    }

    [Theory]
    [InlineData("""{"op":"frame"}""", "no map")]
    [InlineData("""{"op":"frame","map":-1,"entities":[]}""", "no map")]
    [InlineData("""{"op":"frame","map":"0","entities":[]}""", "no map")]
    [InlineData("""{"op":"frame","map":4294967296,"entities":[]}""", "no map")]
    [InlineData("""{"op":"frame","map":0}""", "no entities")]
    [InlineData("""{"op":"frame","map":0,"entities":{}}""", "no entities")]
    [InlineData("""{"op":"frame","map":0,"instance":-1,"entities":[]}""", "instance")]
    [InlineData("""{"op":"frame","map":0,"seq":"x","entities":[]}""", "seq")]
    [InlineData("""{"op":"frame","map":0,"clockMs":-5,"entities":[]}""", "clockMs")]
    [InlineData("""{"op":"frame","map":0,"entities":[],"counters":[1]}""", "counters")]
    [InlineData("""{"op":"frame","map":0,"entities":[],"grids":5}""", "grids")]
    public void AStructurallyWrongFrame_IsRefusedWhole(string json, string reason)
    {
        Assert.False(FrameParser.TryParseFrame(Raw(json), 1, out ParsedFrame? parsed, out string? problem));

        Assert.Null(parsed);
        Assert.Contains(reason, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void GridPairsOutsideTheGrid_AreDroppedAndCounted()
    {
        JsonObject frame = Wire.Frame(0, 0, 1);
        frame["grids"] = new JsonArray(new JsonArray(0, 63), new JsonArray(64, 0), new JsonArray(1), new JsonArray(-1, 5), JsonValue.Create("x"));

        ParsedFrame parsed = Parse(frame);

        Assert.Equal(new[] { (0, 63) }, parsed.Frame.Grids);
        Assert.Equal(4, parsed.Rejected);
        Assert.Contains("grids[1]", parsed.FirstProblem, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionalFieldsMayBeAbsent_AndNullMeansAbsent()
    {
        var entity = new JsonObject { ["g"] = "5", ["k"] = "gameobject", ["x"] = 1, ["y"] = 2, ["z"] = 3, ["n"] = null, ["sp"] = null, ["route"] = null, ["tg"] = null };

        ParsedFrame parsed = Parse(Wire.Frame(0, 0, 1, entity));

        LiveEntity e = Assert.Single(parsed.Frame.Entities);
        Assert.Null(e.Name);
        Assert.Null(e.Spline);
        Assert.Null(e.Route);
        Assert.Null(e.Target);
        Assert.Equal(0u, e.Entry);
        Assert.Equal(0, e.Orientation);
        Assert.Equal(LiveKind.GameObject, e.Kind);
    }

    [Fact]
    public void IntegralNumbersWrittenAsFloats_AreAcceptedAsIntegers()
    {
        JsonObject frame = Raw("""{"op":"frame","map":1.0,"instance":2e0,"seq":3.0,"entities":[]}""");

        Assert.True(FrameParser.TryParseFrame(frame, 1, out ParsedFrame? parsed, out string? problem), problem);
        Assert.Equal(1u, parsed.Frame.Map);
        Assert.Equal(2u, parsed.Frame.Instance);
        Assert.Equal(3, parsed.Frame.Seq);
    }

    [Fact]
    public void AFullFrameOf4096Entities_ParsesCompletely()
    {
        // The server's default MaxEntitiesPerFrame (design section 9).
        JsonObject[] entities = [.. Enumerable.Range(1, 4096).Select(i => Wire.Entity(i.ToString("X"), i % 2 == 0 ? "creature" : "bot", -i, i, 50))];

        ParsedFrame parsed = Parse(Wire.Frame(0, 0, 1, entities));

        Assert.Equal(4096, parsed.Frame.Entities.Count);
        Assert.Equal(0, parsed.Rejected);
        Assert.Equal(4096, parsed.Frame.Entities.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public void TheHelloReply_IsParsedAndItsOddEntriesSkippedAndCounted()
    {
        JsonObject hello = Wire.Hello(1, 99, "abc123", (0, 0, 3, 40), (1, 0, 0, 7));
        hello["maps"]!.AsArray().Add(Raw("""{"map":"x"}"""));
        hello["maps"]!.AsArray().Add(JsonValue.Create(3));

        Assert.True(FrameParser.TryParseHello(OnTheWire(hello), out HelloInfo? info, out string? problem), problem);

        Assert.Equal(1, info.Protocol);
        Assert.Equal(99, info.Pid);
        Assert.Equal("abc123", info.Sha);
        Assert.Equal(new[] { new ServerMap(0, 0, 3, 40), new ServerMap(1, 0, 0, 7) }, info.Maps);
        Assert.Equal(2, info.MapsRejected);
        Assert.Contains("maps[2]", info.FirstMapProblem, StringComparison.Ordinal);
    }

    [Fact]
    public void ACleanHelloReply_RejectsNothing()
    {
        Assert.True(FrameParser.TryParseHello(OnTheWire(Wire.Hello(1, 99, "abc123", (0, 0, 3, 40))), out HelloInfo? info, out string? problem), problem);

        Assert.Equal(0, info.MapsRejected);
        Assert.Null(info.FirstMapProblem);
    }

    [Theory]
    [InlineData("""{"op":"hello"}""", "protocol")]
    [InlineData("""{"op":"hello","protocol":"1"}""", "protocol")]
    [InlineData("""{"op":"hello","protocol":1,"pid":0}""", "pid")]
    [InlineData("""{"op":"hello","protocol":1,"sha":5}""", "sha")]
    [InlineData("""{"op":"hello","protocol":1,"maps":3}""", "maps")]
    public void AMalformedHello_IsRefused(string json, string reason)
    {
        Assert.False(FrameParser.TryParseHello(Raw(json), out HelloInfo? info, out string? problem));

        Assert.Null(info);
        Assert.Contains(reason, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePushedFrameCarriesTheServersFieldsUnchanged()
    {
        ParsedFrame parsed = Parse(Wire.Frame(2, 5, 41, Wire.Entity("A", "player", 1, 2, 3)));

        Assert.Equal("frame", parsed.Json["op"]!.GetValue<string>());
        Assert.Equal(41, parsed.Json["seq"]!.GetValue<long>());
        Assert.Equal(2, parsed.Json["map"]!.GetValue<int>());
        Assert.Equal(5, parsed.Json["instance"]!.GetValue<int>());
        Assert.Equal(2, parsed.Json["grids"]!.AsArray().Count);
        Assert.NotNull(parsed.Json["counters"]);
    }
}
