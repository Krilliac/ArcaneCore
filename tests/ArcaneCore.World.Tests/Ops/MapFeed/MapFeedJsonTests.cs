using System.Buffers;
using System.Numerics;
using System.Text;
using System.Text.Json;
using ArcaneCore.Game.Creatures;
using ArcaneCore.TestFixtures;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Ops.MapFeed;
using Xunit;

namespace ArcaneCore.World.Tests.Ops.MapFeed;

/// <summary>
/// The server's lines (docs/areas/map-feed.md, "Protocol") written from hand-built frames and answers: every property name, the GUID
/// format, the optional parts, the cut flags, and that a line is one line whatever the names in it hold. Pure; no world, no pipe.
/// </summary>
public sealed class MapFeedJsonTests
{
    private const ulong CreatureGuid = 0xF130_0001_2B00_1092UL;

    private static string Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static JsonDocument Frame(FeedFrame frame, long sequence = 9, long dropped = 3)
    {
        string text = Write(w => MapFeedJson.WriteFrame(w, frame, sequence, dropped));
        Assert.DoesNotContain('\n', text);
        return JsonDocument.Parse(text);
    }

    private static FeedFrame BuildFrame(FramePool pool)
    {
        FeedFrame frame = pool.Rent() ?? throw new InvalidOperationException("the pool is empty");
        frame.MapId = 1;
        frame.InstanceId = 2;
        frame.ClockMs = 123_456;
        frame.AddGrid(31, 32);
        frame.AddGrid(31, 33);
        frame.Counters = new FeedCounters
        {
            Players = 1, Bots = 2, Creatures = 3, GameObjects = 4, Truncated = 5, NonFinite = 6, PathsOmitted = 7, DeferredPaths = 8,
            DirectFallbackPaths = 9, NoPathPaths = 10, HomePathTeleports = 11, Online = 12, DiffMs = 50,
        };

        Span<Vector3> points = frame.ReservePoints(5);
        points[0] = new Vector3(1, 2, 3);
        points[1] = new Vector3(4, 5, 6);
        points[2] = new Vector3(7, 8, 9);
        points[3] = new Vector3(10, 11, 12);
        points[4] = new Vector3(13, 14, 15);
        frame.CommitPoints(5);

        int creature = frame.AddEntity();
        frame.Entities[creature] = new FeedEntity
        {
            Guid = CreatureGuid, Kind = FeedEntityKind.Creature, Entry = 299, Name = "Young \"Wolf\" é\n", X = -8940.5f, Y = -132.25f, Z = 83.5f,
            Orientation = 1.5f, HealthPercent = 75f, Motion = (byte)MovementGeneratorType.Chase, Target = 0x1UL,
            HomeX = 10, HomeY = 20, HomeZ = 30,
            SplineId = 17, SplineStartX = -8940.5f, SplineStartY = -132.25f, SplineStartZ = 83.5f, SplineT0 = 1000, SplineDuration = 2500, SplineRun = true,
            SplineOffset = 0, SplineCount = 2,
            Parts = FeedEntityParts.Health | FeedEntityParts.Target | FeedEntityParts.Home | FeedEntityParts.Spline | FeedEntityParts.SplineCut,
        };

        int chest = frame.AddEntity();
        frame.Entities[chest] = new FeedEntity { Guid = 0xF110_0000_0000_0001UL, Kind = FeedEntityKind.GameObject, Entry = 2843, Name = "Chest", X = 1, Y = 2, Z = 3 };

        int player = frame.AddEntity();
        frame.Entities[player] = new FeedEntity
        {
            Guid = 0x1UL, Kind = FeedEntityKind.Player, Name = "Alice", X = 4, Y = 5, Z = 6, Orientation = 0.5f, HealthPercent = 100f, Parts = FeedEntityParts.Health,
        };

        int bot = frame.AddEntity();
        frame.Entities[bot] = new FeedEntity
        {
            Guid = 0x2UL, Kind = FeedEntityKind.Bot, Name = "Botty", X = 7, Y = 8, Z = 9, Orientation = 0f,
            RouteOffset = 2, RouteCount = 3, RouteNext = 1, RouteFlags = BotRouteFlags.MeshEndedShort | BotRouteFlags.Chase | BotRouteFlags.Navigated,
            Parts = FeedEntityParts.Route | FeedEntityParts.RouteCut,
        };
        return frame;
    }

    [Fact]
    public void AFrame_CarriesTheDocumentedEnvelope()
    {
        var pool = new FramePool(2);
        using JsonDocument doc = Frame(BuildFrame(pool));
        JsonElement root = doc.RootElement;

        Assert.Equal("frame", root.GetProperty("op").GetString());
        Assert.Equal(9, root.GetProperty("seq").GetInt64());
        Assert.Equal(123_456, root.GetProperty("clockMs").GetInt64());
        Assert.Equal(1, root.GetProperty("map").GetInt32());
        Assert.Equal(2, root.GetProperty("instance").GetInt32());
        int[] firstGrid = [.. root.GetProperty("grids")[0].EnumerateArray().Select(e => e.GetInt32())];
        Assert.Equal([31, 32], firstGrid);
        Assert.Equal(2, root.GetProperty("grids").GetArrayLength());

        JsonElement counters = root.GetProperty("counters");
        Assert.Equal(1, counters.GetProperty("players").GetInt32());
        Assert.Equal(2, counters.GetProperty("bots").GetInt32());
        Assert.Equal(3, counters.GetProperty("creatures").GetInt32());
        Assert.Equal(4, counters.GetProperty("gameobjects").GetInt32());
        Assert.Equal(4, counters.GetProperty("emitted").GetInt32());
        Assert.Equal(5, counters.GetProperty("truncated").GetInt32());
        Assert.Equal(6, counters.GetProperty("nonFinite").GetInt32());
        Assert.Equal(7, counters.GetProperty("pathsOmitted").GetInt32());
        Assert.Equal(3, counters.GetProperty("dropped").GetInt64());
        Assert.Equal(12, counters.GetProperty("online").GetInt32());
        Assert.Equal(50, counters.GetProperty("diffMs").GetInt32());
        Assert.Equal(8, counters.GetProperty("deferredPaths").GetInt64());
        Assert.Equal(9, counters.GetProperty("directFallbackPaths").GetInt64());
        Assert.Equal(10, counters.GetProperty("noPathPaths").GetInt64());
        Assert.Equal(11, counters.GetProperty("homePathTeleports").GetInt64());
        Assert.Equal(4, root.GetProperty("entities").GetArrayLength());
    }

    [Fact]
    public void ACreature_IsWrittenWithItsMotionTargetHomeAndSpline()
    {
        var pool = new FramePool(2);
        using JsonDocument doc = Frame(BuildFrame(pool));
        JsonElement creature = doc.RootElement.GetProperty("entities")[0];

        Assert.Equal("F13000012B001092", creature.GetProperty("g").GetString());
        Assert.Equal("creature", creature.GetProperty("k").GetString());
        Assert.Equal(299, creature.GetProperty("e").GetInt32());
        Assert.Equal("Young \"Wolf\" é\n", creature.GetProperty("n").GetString()); // escaped on the wire, whole after parsing
        Assert.Equal(-8940.5f, creature.GetProperty("x").GetSingle());
        Assert.Equal(-132.25f, creature.GetProperty("y").GetSingle());
        Assert.Equal(83.5f, creature.GetProperty("z").GetSingle());
        Assert.Equal(1.5f, creature.GetProperty("o").GetSingle());
        Assert.Equal(75f, creature.GetProperty("hp").GetSingle());
        Assert.Equal("chase", creature.GetProperty("mt").GetString());
        Assert.Equal("0000000000000001", creature.GetProperty("tg").GetString());
        Assert.Equal(new Vector3(10, 20, 30), creature.GetProperty("home").ToVector());
        Assert.False(creature.TryGetProperty("route", out _));

        JsonElement spline = creature.GetProperty("sp");
        Assert.Equal(17, spline.GetProperty("id").GetInt32());
        Assert.Equal(new Vector3(-8940.5f, -132.25f, 83.5f), spline.GetProperty("start").ToVector());
        Assert.Equal([new Vector3(1, 2, 3), new Vector3(4, 5, 6)], spline.GetProperty("pts").ToVectors());
        Assert.Equal(1000, spline.GetProperty("t0").GetInt64());
        Assert.Equal(2500, spline.GetProperty("dur").GetInt32());
        Assert.True(spline.GetProperty("run").GetBoolean());
        Assert.True(spline.GetProperty("cut").GetBoolean());
    }

    [Fact]
    public void AGameObjectAndAPlayer_CarryOnlyWhatTheyHave()
    {
        var pool = new FramePool(2);
        using JsonDocument doc = Frame(BuildFrame(pool));
        JsonElement chest = doc.RootElement.GetProperty("entities")[1];
        Assert.Equal("gameobject", chest.GetProperty("k").GetString());
        Assert.Equal(2843, chest.GetProperty("e").GetInt32());
        Assert.Equal("Chest", chest.GetProperty("n").GetString());
        foreach (string absent in new[] { "hp", "mt", "tg", "home", "sp", "route" })
        {
            Assert.False(chest.TryGetProperty(absent, out _), absent);
        }

        JsonElement player = doc.RootElement.GetProperty("entities")[2];
        Assert.Equal("player", player.GetProperty("k").GetString());
        Assert.Equal("0000000000000001", player.GetProperty("g").GetString());
        Assert.False(player.TryGetProperty("e", out _)); // a player has no entry
        Assert.False(player.TryGetProperty("mt", out _)); // and no motion generator
        Assert.Equal(100f, player.GetProperty("hp").GetSingle());
    }

    [Fact]
    public void ABotsRoute_IsWrittenWithItsNextPointFlagsAndCut()
    {
        var pool = new FramePool(2);
        using JsonDocument doc = Frame(BuildFrame(pool));
        JsonElement bot = doc.RootElement.GetProperty("entities")[3];

        Assert.Equal("bot", bot.GetProperty("k").GetString());
        JsonElement route = bot.GetProperty("route");
        Assert.Equal([new Vector3(7, 8, 9), new Vector3(10, 11, 12), new Vector3(13, 14, 15)], route.GetProperty("pts").ToVectors());
        Assert.Equal(1, route.GetProperty("next").GetInt32());
        Assert.Equal(["meshEndedShort", "chase", "navigated"], route.GetProperty("flags").Strings());
        Assert.True(route.GetProperty("cut").GetBoolean());
        Assert.False(bot.TryGetProperty("sp", out _));
    }

    [Theory]
    [InlineData(BotRouteFlags.None, new string[0])]
    [InlineData(BotRouteFlags.PartialToGoal, new[] { "partialToGoal" })]
    [InlineData(BotRouteFlags.QuestReturn, new[] { "questReturn" })]
    [InlineData(BotRouteFlags.MeshEndedShort | BotRouteFlags.PartialToGoal | BotRouteFlags.Chase | BotRouteFlags.Navigated | BotRouteFlags.QuestReturn,
        new[] { "meshEndedShort", "partialToGoal", "chase", "navigated", "questReturn" })]
    public void EveryRouteFlagBit_HasItsName(BotRouteFlags flags, string[] names)
    {
        var pool = new FramePool(1);
        FeedFrame frame = pool.Rent()!;
        int index = frame.AddEntity();
        frame.Entities[index] = new FeedEntity { Guid = 3, Kind = FeedEntityKind.Bot, Name = "b", RouteFlags = flags, Parts = FeedEntityParts.Route };

        using JsonDocument doc = Frame(frame);

        Assert.Equal(names, doc.RootElement.GetProperty("entities")[0].GetProperty("route").GetProperty("flags").Strings());
    }

    [Fact]
    public void AnEmptyFrame_IsValid()
    {
        var pool = new FramePool(1);
        using JsonDocument doc = Frame(pool.Rent()!);
        Assert.Equal(0, doc.RootElement.GetProperty("entities").GetArrayLength());
        Assert.Equal(0, doc.RootElement.GetProperty("grids").GetArrayLength());
    }

    [Theory]
    [InlineData(MovementGeneratorType.Idle, "idle")]
    [InlineData(MovementGeneratorType.Random, "random")]
    [InlineData(MovementGeneratorType.Waypoint, "waypoint")]
    [InlineData(MovementGeneratorType.Confused, "confused")]
    [InlineData(MovementGeneratorType.Chase, "chase")]
    [InlineData(MovementGeneratorType.Home, "home")]
    [InlineData(MovementGeneratorType.Point, "point")]
    [InlineData(MovementGeneratorType.Fleeing, "fleeing")]
    [InlineData(MovementGeneratorType.Distract, "distract")]
    [InlineData(MovementGeneratorType.Follow, "follow")]
    [InlineData(MovementGeneratorType.Formation, "formation")]
    public void EveryMotionType_HasItsName(MovementGeneratorType type, string name)
    {
        var pool = new FramePool(1);
        FeedFrame frame = pool.Rent()!;
        int index = frame.AddEntity();
        frame.Entities[index] = new FeedEntity { Guid = 4, Kind = FeedEntityKind.Creature, Name = "c", Motion = (byte)type };

        using JsonDocument doc = Frame(frame);

        Assert.Equal(name, doc.RootElement.GetProperty("entities")[0].GetProperty("mt").GetString());
    }

    [Fact]
    public void AMotionTypeTheFeedDoesNotKnow_IsUnknown_NotAnException()
    {
        var pool = new FramePool(1);
        FeedFrame frame = pool.Rent()!;
        int index = frame.AddEntity();
        frame.Entities[index] = new FeedEntity { Guid = 4, Kind = FeedEntityKind.Creature, Name = "c", Motion = 250 };

        using JsonDocument doc = Frame(frame);

        Assert.Equal("unknown", doc.RootElement.GetProperty("entities")[0].GetProperty("mt").GetString());
    }

    [Fact]
    public void AMissingName_IsAnEmptyString()
    {
        var pool = new FramePool(1);
        FeedFrame frame = pool.Rent()!;
        int index = frame.AddEntity();
        frame.Entities[index] = new FeedEntity { Guid = 5, Kind = FeedEntityKind.Player, Name = null };

        using JsonDocument doc = Frame(frame);

        Assert.Equal(string.Empty, doc.RootElement.GetProperty("entities")[0].GetProperty("n").GetString());
    }

    [Fact]
    public void TheHello_OffersFeedRevisionTwo()
    {
        byte[] hello = MapFeedJson.HelloLine("abc123", 4242, [new MapSummary(0, 0, 0, 3)]);

        Assert.Equal(MapFeedWireFixture.RevisionTwoHelloLine, Encoding.UTF8.GetString(hello).TrimEnd((char)10));
        using JsonDocument doc = JsonDocument.Parse(hello.AsMemory(0, hello.Length - 1));
        Assert.Equal(MapFeedProtocol.Revision, doc.RootElement.GetProperty("rev").GetInt32());
        Assert.Equal(2, MapFeedProtocol.Revision);
        Assert.Equal(1, doc.RootElement.GetProperty("protocol").GetInt32());
    }

    [Fact]
    public void TheRealWriter_EmitsTheCombatFlagAndDisplayId_OnlyWhereTheyApply()
    {
        var pool = new FramePool(1);
        FeedFrame frame = pool.Rent() ?? throw new InvalidOperationException("the pool is empty");
        frame.MapId = 0;
        frame.InstanceId = 0;
        frame.ClockMs = 1000;
        frame.AddGrid(32, 48);
        frame.Counters = new FeedCounters { Creatures = 2, GameObjects = 1 };
        int e1 = frame.AddEntity();
        frame.Entities[e1] = new FeedEntity
        {
            Guid = 0xF130_0001_2B00_0001UL, Kind = FeedEntityKind.Creature, Entry = 299, Name = "Wolf", X = 10, Y = 10, Z = 5, HealthPercent = 75f,
            Motion = (byte)MovementGeneratorType.Chase, InCombat = true, DisplayId = 1234, Parts = FeedEntityParts.Health,
        };
        int e2 = frame.AddEntity();
        frame.Entities[e2] = new FeedEntity
        {
            Guid = 0xF130_0001_2B00_0002UL, Kind = FeedEntityKind.Creature, Entry = 299, Name = "Wolf", X = 250, Y = 10, Z = 5, HealthPercent = 100f,
            Motion = (byte)MovementGeneratorType.Idle, DisplayId = 55, Parts = FeedEntityParts.Health,
        };
        int e3 = frame.AddEntity();
        frame.Entities[e3] = new FeedEntity { Guid = 0xF110_0000_0000_0001UL, Kind = FeedEntityKind.GameObject, Entry = 2843, Name = "Chest", X = 1, Y = 2, Z = 3 };

        string line = Write(w => MapFeedJson.WriteFrame(w, frame, 1, 0));
        using JsonDocument doc = JsonDocument.Parse(line);
        JsonElement entities = doc.RootElement.GetProperty("entities");

        Assert.True(entities[0].GetProperty("cb").GetBoolean());
        Assert.Equal(1234u, entities[0].GetProperty("d").GetUInt32());
        Assert.False(entities[1].TryGetProperty("cb", out _));
        Assert.Equal(55u, entities[1].GetProperty("d").GetUInt32());
        Assert.False(entities[2].TryGetProperty("cb", out _));
        Assert.False(entities[2].TryGetProperty("d", out _));
        Assert.Equal(MapFeedWireFixture.RevisionTwoFrameLine, line);
    }

    [Fact]
    public void HelloAndErrorLines_AreOneLineEach_WithAStableShape()
    {
        byte[] hello = MapFeedJson.HelloLine("abc123", 4242, [new MapSummary(0, 0, 3, 1500), new MapSummary(36, 7, 0, 12)]);
        Assert.Equal((byte)'\n', hello[^1]);
        Assert.Equal(1, hello.Count(b => b == (byte)'\n'));
        using JsonDocument helloDoc = JsonDocument.Parse(hello.AsMemory(0, hello.Length - 1));
        JsonElement root = helloDoc.RootElement;
        Assert.Equal("hello", root.GetProperty("op").GetString());
        Assert.Equal(1, root.GetProperty("protocol").GetInt32());
        Assert.Equal("abc123", root.GetProperty("sha").GetString());
        Assert.Equal(4242, root.GetProperty("pid").GetInt32());
        JsonElement second = root.GetProperty("maps")[1];
        Assert.Equal(36, second.GetProperty("map").GetInt32());
        Assert.Equal(7, second.GetProperty("instance").GetInt32());
        Assert.Equal(0, second.GetProperty("players").GetInt32());
        Assert.Equal(12, second.GetProperty("creatures").GetInt32());

        byte[] error = MapFeedJson.ErrorLine("bad-json", "the line is \"not\" é valid\nreally");
        Assert.Equal(1, error.Count(b => b == (byte)'\n'));
        using JsonDocument errorDoc = JsonDocument.Parse(error.AsMemory(0, error.Length - 1));
        Assert.Equal("error", errorDoc.RootElement.GetProperty("op").GetString());
        Assert.Equal("bad-json", errorDoc.RootElement.GetProperty("code").GetString());
        Assert.Equal("the line is \"not\" é valid\nreally", errorDoc.RootElement.GetProperty("message").GetString());
    }

    private static JsonDocument Result(QueryAnswer answer)
    {
        byte[] line = MapFeedJson.ResultLine(answer);
        Assert.Equal((byte)'\n', line[^1]);
        return JsonDocument.Parse(line.AsMemory(0, line.Length - 1));
    }

    [Fact]
    public void ARefusedQuery_IsAResultWithOkFalse_AndItsId()
    {
        using JsonDocument doc = Result(QueryAnswer.Refused(41, "not-loaded", "grid [1,2] is not loaded"));
        Assert.Equal("result", doc.RootElement.GetProperty("op").GetString());
        Assert.Equal(41, doc.RootElement.GetProperty("id").GetInt64());
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("not-loaded", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("grid [1,2] is not loaded", doc.RootElement.GetProperty("message").GetString());
        Assert.False(doc.RootElement.TryGetProperty("kind", out _));
    }

    [Fact]
    public void ALineOfSightResult_CarriesBothVerdictsDataAndTheHit()
    {
        using JsonDocument blocked = Result(new QueryAnswer
        {
            Id = 1, Ok = true, Kind = QueryKind.LineOfSight, Clear = false, ClearWithDoodads = false, DataBacked = true, HasHit = true, Hit = new Vector3(1, 2, 3),
        });
        JsonElement los = blocked.RootElement;
        Assert.True(los.GetProperty("ok").GetBoolean());
        Assert.Equal("los", los.GetProperty("kind").GetString());
        Assert.False(los.GetProperty("clear").GetBoolean());
        Assert.False(los.GetProperty("clearDoodads").GetBoolean());
        Assert.True(los.GetProperty("data").GetBoolean());
        Assert.Equal(new Vector3(1, 2, 3), los.GetProperty("hit").ToVector());

        using JsonDocument clear = Result(new QueryAnswer { Id = 2, Ok = true, Kind = QueryKind.LineOfSight, Clear = true, ClearWithDoodads = true });
        Assert.True(clear.RootElement.GetProperty("clear").GetBoolean());
        Assert.False(clear.RootElement.GetProperty("data").GetBoolean());
        Assert.False(clear.RootElement.TryGetProperty("hit", out _));
    }

    [Fact]
    public void AHeightResult_HasNullForWhatTheDataDoesNotHold()
    {
        using JsonDocument none = Result(new QueryAnswer { Id = 3, Ok = true, Kind = QueryKind.Height });
        JsonElement root = none.RootElement;
        Assert.Equal("height", root.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("terrain").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("model").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("floor").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("outdoors").ValueKind);

        using JsonDocument full = Result(new QueryAnswer
        {
            Id = 4, Ok = true, Kind = QueryKind.Height, DataBacked = true, Terrain = 80f, Model = 82f, Floor = 82f, Outdoors = false,
        });
        Assert.Equal(80f, full.RootElement.GetProperty("terrain").GetSingle());
        Assert.Equal(82f, full.RootElement.GetProperty("model").GetSingle());
        Assert.Equal(82f, full.RootElement.GetProperty("floor").GetSingle());
        Assert.False(full.RootElement.GetProperty("outdoors").GetBoolean());
    }

    [Fact]
    public void APathResult_NamesEveryFlagItHas_AndFlagsACutPath()
    {
        using JsonDocument doc = Result(new QueryAnswer
        {
            Id = 5, Ok = true, Kind = QueryKind.Path, DataBacked = true, HasPath = true, PathType = PathType.Normal | PathType.Incomplete | PathType.Short,
            Length = 10.5f, Points = [new Vector3(0, 0, 0), new Vector3(10.5f, 0, 0)], Cut = true,
        });
        JsonElement root = doc.RootElement;
        Assert.Equal("path", root.GetProperty("kind").GetString());
        Assert.True(root.GetProperty("hasPath").GetBoolean());
        Assert.Equal(["normal", "incomplete", "short"], root.GetProperty("type").Strings());
        Assert.Equal(10.5f, root.GetProperty("length").GetSingle());
        Assert.Equal([new Vector3(0, 0, 0), new Vector3(10.5f, 0, 0)], root.GetProperty("pts").ToVectors());
        Assert.True(root.GetProperty("cut").GetBoolean());

        using JsonDocument noPath = Result(new QueryAnswer { Id = 6, Ok = true, Kind = QueryKind.Path, PathType = PathType.NoPath, Points = [new Vector3(1, 1, 1)] });
        Assert.Equal(["noPath"], noPath.RootElement.GetProperty("type").Strings());
        Assert.False(noPath.RootElement.GetProperty("hasPath").GetBoolean());
        Assert.False(noPath.RootElement.TryGetProperty("cut", out _));
    }

    [Fact]
    public void AnUnderMapResult_HasADepthOnlyWhenUnderTheMap()
    {
        using JsonDocument under = Result(new QueryAnswer { Id = 7, Ok = true, Kind = QueryKind.UnderMap, Verdict = UnderMapKind.UnderMap, Depth = 12.5f });
        Assert.Equal("undermap", under.RootElement.GetProperty("kind").GetString());
        Assert.Equal("undermap", under.RootElement.GetProperty("verdict").GetString());
        Assert.Equal(12.5f, under.RootElement.GetProperty("depth").GetSingle());

        using JsonDocument ok = Result(new QueryAnswer { Id = 8, Ok = true, Kind = QueryKind.UnderMap, Verdict = UnderMapKind.Ok });
        Assert.Equal("ok", ok.RootElement.GetProperty("verdict").GetString());
        Assert.False(ok.RootElement.TryGetProperty("depth", out _));

        using JsonDocument unknown = Result(new QueryAnswer { Id = 9, Ok = true, Kind = QueryKind.UnderMap, Verdict = UnderMapKind.Unknown });
        Assert.Equal("unknown", unknown.RootElement.GetProperty("verdict").GetString());
    }
}
