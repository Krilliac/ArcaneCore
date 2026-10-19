using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Live;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Live;

/// <summary>
/// Lines laid out exactly the way the world daemon's feed writer (lane L1) emits them: field order, the fields it leaves out for some
/// kinds (no <c>e</c> for players and bots, no <c>mt</c> outside creatures), its 16-digit upper-case guids, its full counter set and
/// the extra <c>cut</c> flags on splines and routes. The client must take every entity of such a frame without rejecting any.
/// </summary>
public sealed class ServerWireShapeTests
{
    // ref: claude/atlas-l1@5891e4773:src/ArcaneCore.World/Ops/MapFeed/MapFeedJson.cs:137-158 (WriteHello: op, protocol, sha, pid, maps[{map, instance, players, creatures}])
    private const string HelloLine =
        """{"op":"hello","protocol":1,"sha":"5891e4773","pid":31337,"maps":[{"map":0,"instance":0,"players":1,"creatures":812},{"map":1,"instance":0,"players":0,"creatures":95}]}""";

    // ref: claude/atlas-l1@5891e4773:src/ArcaneCore.World/Ops/MapFeed/MapFeedJson.cs:304-331 (WriteFrame), :333-353 (WriteCounters), :355-407 (WriteEntity),
    // :409-430 (WriteSpline, cut only when set), :432-459 (WriteRoute, flags by name, cut only when set), :478-489 (WriteGuid: 16 upper-case hex digits)
    // One line on the wire; written here over several lines and joined.
    private static readonly string FrameLine =
        """
        {"op":"frame","seq":17,"clockMs":123456,"map":0,"instance":0,"grids":[[32,48],[31,48]],
        "counters":{"players":1,"bots":1,"creatures":1,"gameobjects":1,"emitted":4,"truncated":0,"nonFinite":0,"pathsOmitted":0,"dropped":0,
        "online":2,"diffMs":50,"deferredPaths":0,"directFallbackPaths":0,"noPathPaths":0,"homePathTeleports":0},"entities":[
        {"g":"0000000000000002","k":"player","n":"Arthas","x":-8949.95,"y":-132.493,"z":83.5312,"o":0.0,"hp":100},
        {"g":"0000000000000003","k":"bot","n":"Botty","x":-8950.5,"y":-130.25,"z":83.5,"o":1.5,"hp":87,
        "route":{"pts":[[-8950.5,-130.25,83.5],[-8940,-120,84]],"next":1,"flags":["meshEndedShort"],"cut":true}},
        {"g":"F130000C4D00A1B2","k":"creature","e":3100,"n":"Wolf","x":-8960,"y":-140,"z":82,"o":3.14159,"hp":42,"mt":"chase",
        "tg":"0000000000000002","home":[-8970,-150,81],
        "sp":{"id":9,"start":[-8961,-141,82],"pts":[[-8955,-136,82.5],[-8950,-132,83.5]],"t0":123000,"dur":1500,"run":true,"cut":true}},
        {"g":"F110000B1500C0DE","k":"gameobject","e":1731,"n":"","x":-8930,"y":-110,"z":85,"o":0.5}]}
        """.ReplaceLineEndings(string.Empty);

    [Fact]
    public void TheServersHelloLine_IsTakenWhole()
    {
        Assert.True(FrameParser.TryParseHello(JsonNode.Parse(HelloLine)!.AsObject(), out HelloInfo? hello, out string? problem), problem);

        Assert.Equal(1, hello.Protocol);
        Assert.Equal(31337, hello.Pid);
        Assert.Equal("5891e4773", hello.Sha);
        Assert.Equal(new[] { new ServerMap(0, 0, 1, 812), new ServerMap(1, 0, 0, 95) }, hello.Maps);
        Assert.Equal(0, hello.MapsRejected);
    }

    [Fact]
    public void ACutRouteLine_WithNextPastTheShippedPoints_KeepsTheBot()
    {
        // MapFeedCapture ships min(total, room, 512) corners and "cut":true but writes next unchanged, so next can index past pts.
        string line = """{"op":"frame","seq":1,"clockMs":1,"map":0,"instance":0,"grids":[],"entities":[{"g":"0000000000000003","k":"bot","n":"Botty","x":1,"y":2,"z":3,"o":0.0,"hp":50,"route":{"pts":[[1,2,3],[4,5,6]],"next":517,"flags":[],"cut":true}}]}""";

        Assert.True(FrameParser.TryParseFrame(JsonNode.Parse(line)!.AsObject(), 1, out ParsedFrame? parsed, out string? problem), problem);

        Assert.Equal(0, parsed.Rejected);
        LiveEntity bot = Assert.Single(parsed.Frame.Entities);
        Assert.Equal(2, bot.Route!.Next);
        Assert.True(bot.Route.Cut);
    }

    [Fact]
    public void TheServersFrameLine_HasEveryEntityAccepted_WithTheFieldsItLeftOutDefaulted()
    {
        Assert.True(FrameParser.TryParseFrame(JsonNode.Parse(FrameLine)!.AsObject(), 1, out ParsedFrame? parsed, out string? problem), problem);

        Assert.Equal(0, parsed.Rejected);
        Assert.Null(parsed.FirstProblem);
        LiveFrame frame = parsed.Frame;
        Assert.Equal(17, frame.Seq);
        Assert.Equal(new[] { (32, 48), (31, 48) }, frame.Grids);
        Assert.Equal(4, frame.Counters!["emitted"]!.GetValue<int>());
        Assert.Equal(new[] { LiveKind.Player, LiveKind.Bot, LiveKind.Creature, LiveKind.GameObject }, frame.Entities.Select(e => e.Kind));

        LiveEntity player = frame.Entities[0];
        Assert.Equal(2UL, player.Id);
        Assert.Equal(0u, player.Entry);
        Assert.Null(player.Motion);
        Assert.Equal(new WorldPoint(-8949.95, -132.493, 83.5312), player.Position);

        LiveEntity bot = frame.Entities[1];
        Assert.NotNull(bot.Route);
        Assert.Equal(1, bot.Route.Next);
        Assert.Equal(new[] { "meshEndedShort" }, bot.Route.Flags);
        Assert.Equal(2, bot.Route.Points.Count);

        LiveEntity wolf = frame.Entities[2];
        Assert.Equal(0xF130000C4D00A1B2UL, wolf.Id);
        Assert.Equal(3100u, wolf.Entry);
        Assert.Equal("chase", wolf.Motion);
        Assert.Equal(2UL, wolf.Target);
        Assert.Equal(new WorldPoint(-8970, -150, 81), wolf.Home);
        Assert.NotNull(wolf.Spline);
        Assert.Equal(9u, wolf.Spline.Id);
        Assert.Equal(1500, wolf.Spline.DurationMs);
        Assert.True(wolf.Spline.Run);

        LiveEntity chest = frame.Entities[3];
        Assert.Equal(0xF110000B1500C0DEUL, chest.Id);
        Assert.Equal(1731u, chest.Entry);
    }
}
