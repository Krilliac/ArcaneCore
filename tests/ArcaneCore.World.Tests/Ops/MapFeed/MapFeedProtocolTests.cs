using System.Text;
using ArcaneCore.World.Ops.MapFeed;
using Xunit;

namespace ArcaneCore.World.Tests.Ops.MapFeed;

/// <summary>
/// The parser of client lines (docs/areas/map-feed.md, "Protocol"): what it accepts, and that everything else is a refusal with a stable
/// code before any of it can reach the world. Pure; no world, no pipe.
/// </summary>
public sealed class MapFeedProtocolTests
{
    private static bool Parse(string line, out FeedRequest request, out FeedError error)
        => MapFeedProtocol.TryParse(Encoding.UTF8.GetBytes(line), out request, out error);

    private static FeedError Refused(string line)
    {
        Assert.False(Parse(line, out _, out FeedError error), $"expected a refusal: {line}");
        return error;
    }

    private static FeedRequest Accepted(string line)
    {
        Assert.True(Parse(line, out FeedRequest request, out FeedError error), $"expected the line to parse: {line} ({error.Code}: {error.Message})");
        return request;
    }

    [Fact]
    public void Hello_ReadsTheProtocol_AndIgnoresUnknownProperties()
    {
        FeedRequest hello = Accepted("""{"op":"hello","protocol":1,"client":"atlas","future":{"a":[1,2]}}""");
        Assert.Equal(FeedOp.Hello, hello.Op);
        Assert.Equal(1, hello.Protocol);

        // Any integer is parsed; the version check is the connection's (it answers a mismatch with a protocol error and closes).
        Assert.Equal(7, Accepted("""{"op":"hello","protocol":7}""").Protocol);
    }

    [Theory]
    [InlineData("""{"op":"hello"}""")]
    [InlineData("""{"op":"hello","protocol":"1"}""")]
    [InlineData("""{"op":"hello","protocol":1.5}""")]
    [InlineData("""{"op":"hello","protocol":null}""")]
    [InlineData("""{"op":"hello","protocol":99999999999999999999}""")]
    public void Hello_WithoutAnIntegerProtocol_IsBadArgs(string line)
        => Assert.Equal(MapFeedProtocol.BadArgs, Refused(line).Code);

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("{\"op\":\"hello\"")]
    [InlineData("""{"op":"hello",}""")]
    [InlineData("""{"op":"hello"} trailing""")]
    [InlineData("""{"op":"hello" /* comment */}""")]
    public void ALineThatIsNotJson_IsBadJson(string line)
        => Assert.Equal(MapFeedProtocol.BadJson, Refused(line).Code);

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("\"hello\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void JsonThatIsNotAnObject_IsBadJson(string line)
        => Assert.Equal(MapFeedProtocol.BadJson, Refused(line).Code);

    [Fact]
    public void JsonNestedPastTheLimit_IsBadJson_NotAStackProblem()
    {
        string deep = new string('[', MapFeedProtocol.MaxJsonDepth + 2) + new string(']', MapFeedProtocol.MaxJsonDepth + 2);
        Assert.Equal(MapFeedProtocol.BadJson, Refused("{\"op\":\"hello\",\"protocol\":1,\"x\":" + deep + "}").Code);

        // At the limit it is fine.
        string ok = new string('[', MapFeedProtocol.MaxJsonDepth - 1) + new string(']', MapFeedProtocol.MaxJsonDepth - 1);
        Accepted("{\"op\":\"hello\",\"protocol\":1,\"x\":" + ok + "}");
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"op":5}""")]
    [InlineData("""{"op":null}""")]
    [InlineData("""{"op":"frobnicate"}""")]
    [InlineData("""{"op":"HELLO","protocol":1}""")]
    [InlineData("""{"operation":"hello","protocol":1}""")]
    public void AnUnknownOrMissingOp_IsUnknownOp(string line)
        => Assert.Equal(MapFeedProtocol.UnknownOp, Refused(line).Code);

    [Fact]
    public void Subscribe_Defaults_AreEverythingWithPaths_AtTheServersRate()
    {
        FeedRequest subscribe = Accepted("""{"op":"subscribe","map":1}""");
        Assert.Equal(FeedOp.Subscribe, subscribe.Op);
        Assert.Equal(1u, subscribe.MapId);
        Assert.Equal(0u, subscribe.InstanceId);
        Assert.Equal(0d, subscribe.Hz); // not asked for: the feed substitutes its default
        Assert.Equal(FeedKinds.All, subscribe.Kinds);
        Assert.True(subscribe.Paths);
    }

    [Fact]
    public void Subscribe_ReadsInstanceHzKindsAndPaths()
    {
        FeedRequest subscribe = Accepted("""{"op":"subscribe","map":36,"instance":12,"hz":2.5,"kinds":["bot","creature"],"paths":false}""");
        Assert.Equal(36u, subscribe.MapId);
        Assert.Equal(12u, subscribe.InstanceId);
        Assert.Equal(2.5, subscribe.Hz);
        Assert.Equal(FeedKinds.Bot | FeedKinds.Creature, subscribe.Kinds);
        Assert.False(subscribe.Paths);

        Assert.Equal(FeedKinds.None, Accepted("""{"op":"subscribe","map":0,"kinds":[]}""").Kinds);
        Assert.Equal(
            FeedKinds.Player | FeedKinds.GameObject,
            Accepted("""{"op":"subscribe","map":0,"kinds":["gameobject","player","player"]}""").Kinds);
    }

    [Theory]
    [InlineData("""{"op":"subscribe"}""")]
    [InlineData("""{"op":"subscribe","map":-1}""")]
    [InlineData("""{"op":"subscribe","map":0.5}""")]
    [InlineData("""{"op":"subscribe","map":"0"}""")]
    [InlineData("""{"op":"subscribe","map":4294967296}""")]
    [InlineData("""{"op":"subscribe","map":0,"instance":-3}""")]
    [InlineData("""{"op":"subscribe","map":0,"instance":"x"}""")]
    [InlineData("""{"op":"subscribe","map":0,"hz":0}""")]
    [InlineData("""{"op":"subscribe","map":0,"hz":-4}""")]
    [InlineData("""{"op":"subscribe","map":0,"hz":"fast"}""")]
    [InlineData("""{"op":"subscribe","map":0,"kinds":"bot"}""")]
    [InlineData("""{"op":"subscribe","map":0,"kinds":["npc"]}""")]
    [InlineData("""{"op":"subscribe","map":0,"kinds":[5]}""")]
    [InlineData("""{"op":"subscribe","map":0,"kinds":["Bot"]}""")]
    [InlineData("""{"op":"subscribe","map":0,"paths":"yes"}""")]
    [InlineData("""{"op":"subscribe","map":0,"paths":1}""")]
    public void ASubscribeThatIsMalformed_IsBadArgs(string line)
        => Assert.Equal(MapFeedProtocol.BadArgs, Refused(line).Code);

    [Fact]
    public void Unsubscribe_NeedsNothingElse()
        => Assert.Equal(FeedOp.Unsubscribe, Accepted("""{"op":"unsubscribe"}""").Op);

    [Fact]
    public void Query_ReadsTheKindsAndTheirPoints()
    {
        FeedRequest los = Accepted("""{"op":"query","id":7,"kind":"los","map":0,"instance":3,"a":[1,2,3],"b":[4.5,-5.5,6]}""");
        Assert.Equal(FeedOp.Query, los.Op);
        Assert.Equal(7L, los.Id);
        Assert.True(los.HasId);
        Assert.Equal(QueryKind.LineOfSight, los.Kind);
        Assert.Equal(3u, los.InstanceId);
        Assert.Equal(new System.Numerics.Vector3(1, 2, 3), los.A);
        Assert.Equal(new System.Numerics.Vector3(4.5f, -5.5f, 6), los.B);

        Assert.Equal(QueryKind.Path, Accepted("""{"op":"query","id":1,"kind":"path","map":0,"a":[0,0,0],"b":[1,1,1]}""").Kind);

        // height and undermap take one point; a second one is ignored, not required.
        FeedRequest height = Accepted("""{"op":"query","id":2,"kind":"height","map":1,"a":[10,20,30]}""");
        Assert.Equal(QueryKind.Height, height.Kind);
        Assert.Equal(QueryKind.UnderMap, Accepted("""{"op":"query","id":3,"kind":"undermap","map":1,"a":[10,20,30],"b":[1,2,3]}""").Kind);
    }

    [Theory]
    [InlineData("""{"op":"query","kind":"los","map":0,"a":[0,0,0],"b":[1,1,1]}""", false)]
    [InlineData("""{"op":"query","id":"x","kind":"los","map":0,"a":[0,0,0],"b":[1,1,1]}""", false)]
    [InlineData("""{"op":"query","id":1.5,"kind":"los","map":0,"a":[0,0,0],"b":[1,1,1]}""", false)]
    [InlineData("""{"op":"query","id":9,"map":0,"a":[0,0,0],"b":[1,1,1]}""", true)]
    [InlineData("""{"op":"query","id":9,"kind":"teleport","map":0,"a":[0,0,0]}""", true)]
    [InlineData("""{"op":"query","id":9,"kind":"los","a":[0,0,0],"b":[1,1,1]}""", true)]
    [InlineData("""{"op":"query","id":9,"kind":"los","map":0,"b":[1,1,1]}""", true)]
    [InlineData("""{"op":"query","id":9,"kind":"los","map":0,"a":[0,0,0]}""", true)]
    [InlineData("""{"op":"query","id":9,"kind":"path","map":0,"a":[0,0,0]}""", true)]
    [InlineData("""{"op":"query","id":9,"kind":"height","map":0}""", true)]
    [InlineData("""{"op":"query","id":9,"kind":"height","map":0,"a":[1,2]}""", true)]
    [InlineData("""{"op":"query","id":9,"kind":"height","map":0,"a":[1,2,3,4]}""", true)]
    [InlineData("""{"op":"query","id":9,"kind":"height","map":0,"a":"1,2,3"}""", true)]
    [InlineData("""{"op":"query","id":9,"kind":"height","map":0,"a":[1,"2",3]}""", true)]
    [InlineData("""{"op":"query","id":9,"kind":"height","map":0,"a":[1,null,3]}""", true)]
    public void AQueryThatIsMalformed_IsBadArgs_AndKeepsItsIdWhenItHadOne(string line, bool idKept)
    {
        Assert.False(Parse(line, out FeedRequest request, out FeedError error));
        Assert.Equal(MapFeedProtocol.BadArgs, error.Code);
        Assert.Equal(FeedOp.Query, request.Op);
        Assert.Equal(idKept, request.HasId);
        if (idKept)
        {
            Assert.Equal(9L, request.Id);
        }
    }

    [Theory]
    [InlineData("1e39", "0", "0")] // beyond float: infinite once it is a float
    [InlineData("-1e39", "0", "0")]
    [InlineData("0", "1e39", "0")]
    [InlineData("0", "0", "1e39")]
    public void ANumberTooBigForAFloat_NeverBecomesAPoint(string x, string y, string z)
    {
        FeedError error = Refused($$"""{"op":"query","id":1,"kind":"height","map":0,"a":[{{x}},{{y}},{{z}}]}""");
        Assert.Equal(MapFeedProtocol.BadArgs, error.Code);
    }

    [Theory]
    [InlineData("17067", "0", "0")]
    [InlineData("-17067", "0", "0")]
    [InlineData("0", "20000", "0")]
    [InlineData("0", "-20000", "0")]
    [InlineData("0", "0", "400001")]
    [InlineData("0", "0", "-400001")]
    public void APointOutsideTheMap_IsRefused_BeforeCollisionSeesIt(string x, string y, string z)
    {
        FeedError error = Refused($$"""{"op":"query","id":1,"kind":"undermap","map":0,"a":[{{x}},{{y}},{{z}}]}""");
        Assert.Equal(MapFeedProtocol.BadArgs, error.Code);
        Assert.Contains("outside the map", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APointAtTheMapEdge_IsAccepted()
    {
        FeedRequest request = Accepted("""{"op":"query","id":1,"kind":"undermap","map":0,"a":[17066,-17066,399999]}""");
        Assert.Equal(17066f, request.A.X);
        Assert.Equal(-17066f, request.A.Y);
    }

    [Fact]
    public void TheLimitsAreTheDocumentedOnes()
    {
        Assert.Equal(1, MapFeedProtocol.Version);
        Assert.Equal(4096, MapFeedProtocol.MaxLineBytes);
        Assert.Equal(5, MapFeedProtocol.MaxStrikes);
        Assert.Equal(512, MapFeedProtocol.MaxPathPoints);
        Assert.Equal(1024, MapFeedProtocol.MaxResultPoints);
    }
}
