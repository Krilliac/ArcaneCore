using System.Numerics;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Reference.Navmesh;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using DotRecast.Detour;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Navmesh;

public sealed class NavCommandTests : IDisposable
{
    private readonly SyntheticMap _data = new();
    private readonly List<IDisposable> _disposables = [];
    private AtlasContext? _context;

    public void Dispose()
    {
        _context?.Dispose();
        foreach (IDisposable d in _disposables)
        {
            d.Dispose();
        }

        _data.Dispose();
    }

    private AtlasContext Context => _context ??= AtlasModules.Compose(new AtlasPaths(_data.Root, null), NullLoggerFactory.Instance, headless: true, [new ReferenceNavmeshModule()]);

    private BuiltTile Add(int x, int y, int cells = 4, Func<int, int, bool>? present = null, Func<int, int, ushort>? flags = null, IReadOnlyList<OffMeshSpec>? offMesh = null, float height = 10f)
        => _data.Add(new TileSpec
        {
            TerrainX = x,
            TerrainY = y,
            Cells = cells,
            Height = height,
            Present = present ?? ((_, _) => true),
            Flags = flags ?? ((_, _) => 1),
            OffMesh = offMesh ?? [],
        });

    private static JsonArray Point(Vector3 v) => new(v.X, v.Y, v.Z);

    private static Vector3 Cell(int x, int y, int cells, int i, int j, float height = 10f) => SyntheticNav.CellCenterWorld(x, y, cells, i, j, height);

    private async Task<CommandResult> Run(string name, JsonObject args)
        => await Context.Commands.InvokeAsync(name, args, Context.Services, CommandOrigin.Script, headless: true, CancellationToken.None);

    private async Task<JsonNode> Ok(string name, JsonObject args)
    {
        CommandResult result = await Run(name, args);
        Assert.True(result.Ok, $"{name} failed: {result.ErrorCode} {result.ErrorMessage}");
        return JsonNode.Parse(result.Data!.ToJsonString())!;
    }

    private async Task<CommandResult> Fails(string name, JsonObject args, string code)
    {
        CommandResult result = await Run(name, args);
        Assert.False(result.Ok);
        Assert.Equal(code, result.ErrorCode);
        return result;
    }

    private static JsonObject PathArgs(Vector3 a, Vector3 b) => new() { ["map"] = 0, ["a"] = Point(a), ["b"] = Point(b) };

    private static Vector3[] Points(JsonNode result)
        => [.. result["points"]!.AsArray().Select(p => new Vector3(p![0]!.GetValue<float>(), p[1]!.GetValue<float>(), p[2]!.GetValue<float>()))];

    private static string Status(JsonNode result) => result["status"]!.GetValue<string>();

    private static void AssertSamePath(Vector3[] expected, Vector3[] actual, float tolerance = 0.5f)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(Vector3.Distance(expected[i], actual[i]) <= tolerance, $"point {i}: {expected[i]} vs {actual[i]}");
        }
    }

    /// <summary>The terrain tiles the path command loads for two points: their box plus one tile around it.</summary>
    private static IEnumerable<(int X, int Y)> Box(Vector3 a, Vector3 b)
    {
        TileKey ta = WorldGrid.TileOf(0, a.X, a.Y)!.Value;
        TileKey tb = WorldGrid.TileOf(0, b.X, b.Y)!.Value;
        for (int x = Math.Max(0, Math.Min(ta.X, tb.X) - 1); x <= Math.Min(63, Math.Max(ta.X, tb.X) + 1); x++)
        {
            for (int y = Math.Max(0, Math.Min(ta.Y, tb.Y) - 1); y <= Math.Min(63, Math.Max(ta.Y, tb.Y) + 1); y++)
            {
                yield return (x, y);
            }
        }
    }

    private PathResult ServerPath(Vector3 a, Vector3 b)
    {
        var server = new NavMeshPathfinder(_data.Mmaps);
        foreach ((int x, int y) in Box(a, b))
        {
            server.LoadTile(0, x, y);
        }

        return server.FindPath(0, a, b);
    }

    [Fact]
    public async Task Path_WithinOneTile_IsAStraightNormalPath()
    {
        Add(32, 32);
        Vector3 a = Cell(32, 32, 4, 0, 0);
        Vector3 b = Cell(32, 32, 4, 3, 3);

        JsonNode result = await Ok("ref.query.path", PathArgs(a, b));

        Assert.Equal("Normal", Status(result));
        Assert.Equal((int)PathType.Normal, result["type"]!.GetValue<int>());
        Assert.Equal(["Normal"], result["flags"]!.AsArray().Select(n => n!.GetValue<string>()));
        Vector3[] points = Points(result);
        Assert.Equal(2, points.Length);
        Assert.Equal(a, points[0]);
        Assert.True(Vector3.Distance(points[1], b) < 0.01f);
        Assert.Equal(Vector3.Distance(a, b), result["length"]!.GetValue<float>(), 0.01f);
        Assert.True(result["reference"]!["complete"]!.GetValue<bool>());
        Assert.False(result["reference"]!["endFar"]!.GetValue<bool>());
        Assert.Equal("path", result["reference"]!["outcome"]!.GetValue<string>());

        // Cells on the diagonal are (0,0), (0,1)/(1,0) ... (3,3): the corridor starts and ends on the right polygons.
        JsonArray polys = result["polys"]!.AsArray();
        Assert.Equal(0, polys[0]!["poly"]!.GetValue<int>());
        Assert.Equal(15, polys[^1]!["poly"]!.GetValue<int>());
        Assert.All(polys, p => Assert.Equal(new[] { 32, 32 }, p!["tile"]!.AsArray().Select(n => n!.GetValue<int>())));
        Assert.True(result["reference"]!["nodes"]!.GetValue<int>() > 0);
        Assert.False(result["reference"]!["overServerNodeBudget"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Path_AcrossATileBorder_ThroughTheExternalLinks()
    {
        Add(32, 32);
        Add(32, 31);
        Vector3 a = Cell(32, 32, 4, 0, 0);
        Vector3 b = Cell(32, 31, 4, 3, 3);

        JsonNode result = await Ok("ref.query.path", PathArgs(a, b));

        Assert.Equal("Normal", Status(result));
        TileKey[] tiles = [.. result["polys"]!.AsArray().Select(p => new TileKey(0, p!["tile"]![0]!.GetValue<int>(), p["tile"]![1]!.GetValue<int>())).Distinct()];
        Assert.Equal([new TileKey(0, 32, 32), new TileKey(0, 32, 31)], tiles);
        Assert.Equal(12, result["reference"]!["tiles"]!["requested"]!.GetValue<int>());
        Assert.Equal(2, result["reference"]!["tiles"]!["ok"]!.GetValue<int>());
        Assert.Equal(10, result["reference"]!["tiles"]!["missing"]!.GetValue<int>());
        Assert.True(Vector3.Distance(Points(result)[^1], b) < 0.01f);
    }

    [Fact]
    public async Task Path_ToAnIsland_IsIncomplete_AndNoPathWhenPartialIsRefused()
    {
        Add(32, 32, cells: 5, present: (i, _) => i != 2);
        Vector3 a = Cell(32, 32, 5, 0, 2);
        Vector3 b = Cell(32, 32, 5, 4, 2);

        JsonNode partial = await Ok("ref.query.path", PathArgs(a, b));
        JsonObject strict = PathArgs(a, b);
        strict["allowPartial"] = false;
        JsonNode refused = await Ok("ref.query.path", strict);

        Assert.Equal("Incomplete", Status(partial));
        Assert.False(partial["reference"]!["complete"]!.GetValue<bool>());
        Vector3 end = Points(partial)[^1];
        Assert.True(end.X is > -533.34f and < 0f);

        // The closest reachable point is on the polygon next to the gap, in the end point's row.
        Vector3 gapSide = Cell(32, 32, 5, 1, 2);
        Assert.True(Math.Abs(end.Y - gapSide.Y) < 100f * 1.01f && end.Z == 10f);

        Assert.Equal("NoPath", Status(refused));
        Assert.Equal("partial-refused", refused["reference"]!["outcome"]!.GetValue<string>());
        Assert.Equal(2, Points(refused).Length);
        Assert.Equal((int)PathType.NoPath, refused["type"]!.GetValue<int>());
    }

    [Fact]
    public async Task Path_Over_AnOffMeshConnection_OnlyWhenAllowed()
    {
        Vector3 from = SyntheticNav.CellCenterRecast(32, 32, 5, 1, 2, 10f);
        Vector3 to = SyntheticNav.CellCenterRecast(32, 32, 5, 3, 2, 10f);
        Add(32, 32, cells: 5, present: (i, _) => i != 2, offMesh: [new OffMeshSpec(from, to, 3f, Bidirectional: true)]);
        Vector3 a = Cell(32, 32, 5, 0, 2);
        Vector3 b = Cell(32, 32, 5, 4, 2);

        JsonNode allowed = await Ok("ref.query.path", PathArgs(a, b));
        JsonObject strict = PathArgs(a, b);
        strict["offMesh"] = false;
        JsonNode refused = await Ok("ref.query.path", strict);

        Assert.Equal("Normal", Status(allowed));
        Assert.Equal(1, allowed["reference"]!["offMeshPoints"]!.GetValue<int>());
        Assert.Contains(allowed["polys"]!.AsArray(), p => p!["offMesh"]!.GetValue<bool>());
        Vector3 start = NavMeshFormat.ToWorld(from);
        Assert.Contains(Points(allowed), p => Vector3.Distance(p, start) < 1.5f);

        // The server's reader skips off-mesh polygons, and so does offMesh: false.
        Assert.Equal("Incomplete", Status(refused));
        Assert.Equal(0, refused["reference"]!["offMeshPoints"]!.GetValue<int>());
        Assert.False(refused["reference"]!["offMesh"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Path_ToAPointFarAboveTheMesh_IsIncomplete_UnlessTheVmangosToleranceCoversIt()
    {
        Add(32, 32);
        Vector3 a = Cell(32, 32, 4, 0, 0);
        Vector3 high = Cell(32, 32, 4, 3, 3, height: 30f);
        Vector3 close = Cell(32, 32, 4, 3, 3, height: 16f);

        JsonNode far = await Ok("ref.query.path", PathArgs(a, high));
        JsonObject withTolerance = PathArgs(a, close);
        withTolerance["endTolerance"] = PathOptions.VmangosEndTolerance;
        JsonNode tolerated = await Ok("ref.query.path", withTolerance);
        JsonObject tooFar = PathArgs(a, high);
        tooFar["endTolerance"] = PathOptions.VmangosEndTolerance;
        CommandResult lost = await Run("ref.query.path", tooFar);

        // 20 yd above: the 3 x 5 x 3 box finds nothing, the 3 x 200 x 3 column does, and the end counts as far from its polygon.
        Assert.Equal("Incomplete", Status(far));
        Assert.True(far["reference"]!["complete"]!.GetValue<bool>());
        Assert.True(far["reference"]!["endFar"]!.GetValue<bool>());
        Assert.Equal("far", far["reference"]!["end"]!["box"]!.GetValue<string>());
        Assert.Equal("near", far["reference"]!["start"]!["box"]!.GetValue<string>());

        // 6 yd above with vmangos' 7 yd tolerance: a whole path ending at the mesh point below.
        Assert.Equal("Normal", Status(tolerated));
        Assert.Equal("end-tolerance", tolerated["reference"]!["end"]!["box"]!.GetValue<string>());

        // 20 yd is outside vmangos' 5 x 10 x 5 box: nothing to path to.
        Assert.True(lost.Ok);
        Assert.Equal("NoPath", lost.Data!["status"]!.GetValue<string>());
        Assert.Equal("locate-failed", lost.Data["reference"]!["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task Path_ToATileThatIsNotLoaded_IsAStraightLine_LikeTheServer()
    {
        Add(32, 32);
        Vector3 a = Cell(32, 32, 4, 0, 0);
        Vector3 b = Cell(32, 29, 4, 1, 1);

        JsonNode result = await Ok("ref.query.path", PathArgs(a, b));

        Assert.Equal("Shortcut", Status(result));
        Assert.Equal((int)(PathType.Normal | PathType.NotUsingPath), result["type"]!.GetValue<int>());
        Assert.Equal("tile-not-loaded", result["reference"]!["outcome"]!.GetValue<string>());
        AssertSamePath([a, b], Points(result));
        Assert.Equal(PathResult.StraightLine(a, b, PathType.Normal).Length, result["length"]!.GetValue<float>(), 0.01f);
    }

    [Theory]
    [InlineData("same tile")]
    [InlineData("across the border")]
    [InlineData("to an island")]
    [InlineData("far above")]
    [InlineData("unloaded tile")]
    [InlineData("water only")]
    public async Task Path_AgreesWithTheServersPathfinder_OnTheSameTiles(string scenario)
    {
        Vector3 a, b;
        switch (scenario)
        {
            case "same tile":
                Add(32, 32);
                (a, b) = (Cell(32, 32, 4, 0, 3), Cell(32, 32, 4, 3, 0));
                break;
            case "across the border":
                Add(32, 32);
                Add(32, 31);
                Add(33, 32);
                (a, b) = (Cell(32, 32, 4, 1, 1), Cell(32, 31, 4, 2, 3));
                break;
            case "to an island":
                Add(32, 32, cells: 5, present: (i, _) => i != 2);
                (a, b) = (Cell(32, 32, 5, 0, 2), Cell(32, 32, 5, 4, 2));
                break;
            case "far above":
                Add(32, 32);
                (a, b) = (Cell(32, 32, 4, 0, 0), Cell(32, 32, 4, 3, 3, height: 40f));
                break;
            case "unloaded tile":
                Add(32, 32);
                (a, b) = (Cell(32, 32, 4, 0, 0), Cell(32, 29, 4, 1, 1));
                break;
            default:
                Add(32, 32, flags: (i, _) => i < 2 ? (ushort)NavTerrain.Water : (ushort)NavTerrain.Ground);
                (a, b) = (Cell(32, 32, 4, 0, 0), Cell(32, 32, 4, 3, 3));
                break;
        }

        JsonNode reference = await Ok("ref.query.path", PathArgs(a, b));
        PathResult server = ServerPath(a, b);

        Assert.Equal((int)server.Type, reference["type"]!.GetValue<int>());
        AssertSamePath([.. server.Points], Points(reference));
    }

    [Fact]
    public async Task Path_HonoursTheIncludeAndExcludeTerrain()
    {
        Add(32, 32, flags: (i, _) => i == 1 ? (ushort)NavTerrain.Water : (ushort)NavTerrain.Ground);
        Vector3 a = Cell(32, 32, 4, 0, 0);
        Vector3 b = Cell(32, 32, 4, 3, 0);

        JsonNode open = await Ok("ref.query.path", PathArgs(a, b));
        JsonObject groundOnly = PathArgs(a, b);
        groundOnly["include"] = new JsonArray("ground");
        JsonNode blocked = await Ok("ref.query.path", groundOnly);
        JsonObject noWater = PathArgs(a, b);
        noWater["exclude"] = new JsonArray("water");
        JsonNode excluded = await Ok("ref.query.path", noWater);

        Assert.Equal("Normal", Status(open));
        Assert.Equal("Incomplete", Status(blocked));
        Assert.Equal("Incomplete", Status(excluded));
        Assert.Equal(["ground"], blocked["reference"]!["include"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["water"], excluded["reference"]!["exclude"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.All(blocked["polys"]!.AsArray(), p => Assert.Equal(1, p!["flags"]!.GetValue<int>()));
    }

    [Fact]
    public async Task Path_CutsALongPathToMaxPoints_AndFlagsIt()
    {
        // A zig-zag corridor of cells forces many corners.
        Add(32, 32, cells: 7, present: (i, j) => i % 2 == 0 || (i % 4 == 1 && j == 6) || (i % 4 == 3 && j == 0));
        Vector3 a = Cell(32, 32, 7, 0, 0);
        Vector3 b = Cell(32, 32, 7, 6, 0);
        JsonObject full = PathArgs(a, b);
        JsonObject cut = PathArgs(a, b);
        cut["maxPoints"] = 3;

        JsonNode whole = await Ok("ref.query.path", full);
        JsonNode short3 = await Ok("ref.query.path", cut);

        Assert.True(Points(whole).Length > 3);
        Assert.Equal(3, Points(short3).Length);
        Assert.Contains("Short", short3["flags"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(PathType.Short, (PathType)short3["type"]!.GetValue<int>() & PathType.Short);
    }

    [Fact]
    public async Task Path_OverlayHasThePathAndThePointsItStartedFrom()
    {
        Add(32, 32);
        Vector3 a = Cell(32, 32, 4, 0, 0);
        Vector3 b = Cell(32, 32, 4, 3, 3);

        JsonNode result = await Ok("ref.query.path", PathArgs(a, b));

        JsonNode overlay = result["overlay"]!;
        Assert.Equal(4, overlay["points"]!.AsArray().Count);
        Assert.Equal(2, overlay["segments"]!.AsArray().Count);
        JsonArray polylines = overlay["polylines"]!.AsArray();
        Assert.Equal(2, polylines.Count);
        Assert.Equal(2, polylines[0]!["pts"]!.AsArray().Count);
        Assert.Equal(LayerCatalog.Get(LayerIds.RefNavPoly).DefaultColor, polylines[0]!["color"]!.GetValue<string>());
        Assert.StartsWith("ref path Normal", polylines[0]!["label"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.All(polylines[1]!["pts"]!.AsArray(), p => Assert.Equal(10.0, p![2]!.GetValue<double>(), 3));
    }

    [Fact]
    public async Task Path_RefusesBadArguments_WithoutThrowing()
    {
        Add(32, 32);
        Vector3 a = Cell(32, 32, 4, 0, 0);
        Vector3 b = Cell(32, 32, 4, 3, 3);

        JsonObject unknownTerrain = PathArgs(a, b);
        unknownTerrain["include"] = new JsonArray("lava");
        JsonObject tooFewPoints = PathArgs(a, b);
        tooFewPoints["maxPoints"] = 1;
        JsonObject outside = PathArgs(a, new Vector3(1.0e6f, 0, 0));
        JsonObject far = PathArgs(a, Cell(5, 5, 4, 0, 0));

        CommandResult terrain = await Fails("ref.query.path", unknownTerrain, CommandException.BadArguments);
        await Fails("ref.query.path", tooFewPoints, CommandException.BadArguments);
        CommandResult off = await Fails("ref.query.path", outside, CommandException.BadArguments);
        CommandResult tiles = await Fails("ref.query.path", far, CommandException.BadArguments);
        await Fails("ref.query.path", new JsonObject { ["map"] = 0, ["a"] = Point(a) }, CommandException.BadArguments);
        await Fails("ref.query.path", new JsonObject { ["map"] = 0, ["a"] = new JsonArray(1, 2), ["b"] = Point(b) }, CommandException.BadArguments);
        await Fails("ref.query.path", new JsonObject { ["map"] = 3, ["a"] = Point(a), ["b"] = Point(b) }, CommandException.Unavailable);

        Assert.Contains("lava", terrain.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("outside the map grid", off.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("limit 64", tiles.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commands_WithoutAnMmapsDirectory_AreUnavailable()
    {
        using var bare = new SyntheticMap(writeParams: false);
        Directory.Delete(bare.Mmaps);
        using AtlasContext context = AtlasModules.Compose(new AtlasPaths(bare.Root, null), NullLoggerFactory.Instance, headless: true, [new ReferenceNavmeshModule()]);
        Vector3 p = Cell(32, 32, 4, 0, 0);

        foreach ((string name, JsonObject args) in new (string, JsonObject)[]
        {
            ("ref.query.path", PathArgs(p, p)),
            ("ref.query.nearest", new JsonObject { ["map"] = 0, ["at"] = Point(p) }),
            ("ref.nav.tile", new JsonObject { ["map"] = 0, ["x"] = 32, ["y"] = 32 }),
        })
        {
            CommandResult result = await context.Commands.InvokeAsync(name, args, context.Services, CommandOrigin.Script, true, CancellationToken.None);
            Assert.False(result.Ok);
            Assert.Equal(CommandException.Unavailable, result.ErrorCode);
            Assert.Contains("mmaps", result.ErrorMessage, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Nearest_OnTheMesh_FindsThePolygonUnderThePoint()
    {
        Add(32, 32);
        Vector3 at = Cell(32, 32, 4, 2, 1);

        JsonNode result = await Ok("ref.query.nearest", new JsonObject { ["map"] = 0, ["at"] = Point(at) });

        Assert.True(result["found"]!.GetValue<bool>());
        Assert.Equal(["near"], result["tried"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("near", result["box"]!.GetValue<string>());
        Assert.False(result["far"]!.GetValue<bool>());
        Assert.True(result["overPoly"]!.GetValue<bool>());
        Assert.Equal((2 * 4) + 1, result["poly"]!["poly"]!.GetValue<int>());
        Assert.Equal("ground", result["poly"]!["colorKey"]!.GetValue<string>());
        Assert.Equal(0.0, result["distance"]!.GetValue<double>(), 3);
        Assert.Equal(new[] { 32, 32 }, result["poly"]!["tile"]!.AsArray().Select(n => n!.GetValue<int>()));
        JsonArray centre = result["poly"]!["center"]!.AsArray();
        Assert.Equal(at.X, centre[0]!.GetValue<float>(), 0.01f);
        Assert.Equal(at.Y, centre[1]!.GetValue<float>(), 0.01f);
        Assert.Equal(2, result["overlay"]!["points"]!.AsArray().Count);
    }

    [Fact]
    public async Task Nearest_WalksTheServersBoxes_UntilOneHoldsAPolygon()
    {
        Add(32, 32);
        // 13 yd above the mesh (at height 10): the near box's 5-yd and the wide box's 10-yd half heights both miss it even after
        // Detour rounds the query box's low corner down to an even BV quantum (vmangos@0e3ff01 dep/recastnavigation/Detour/
        // Source\DetourNavMesh.cpp:829-831; the synthetic tile quantizes at 1 per yard), which a point only 11 yd above would still
        // reach with the wide box; only the far column (200 yd) holds it.
        Vector3 above = Cell(32, 32, 4, 1, 1, height: 23f);
        Vector3 beside = SyntheticNav.CellCenterWorld(32, 32, 4, 0, 0, 10f) + new Vector3(-250f, 0f, 0f);
        Vector3 nowhere = Cell(32, 32, 4, 1, 1, height: 400f);

        JsonNode high = await Ok("ref.query.nearest", new JsonObject { ["map"] = 0, ["at"] = Point(above) });
        JsonNode wide = await Ok("ref.query.nearest", new JsonObject { ["map"] = 0, ["at"] = Point(above), ["box"] = "wide" });
        JsonNode none = await Ok("ref.query.nearest", new JsonObject { ["map"] = 0, ["at"] = Point(nowhere) });
        JsonNode outsideTile = await Ok("ref.query.nearest", new JsonObject { ["map"] = 0, ["at"] = Point(beside) });

        Assert.Equal(["near", "far"], high["tried"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("far", high["box"]!.GetValue<string>());
        Assert.True(high["far"]!.GetValue<bool>());
        Assert.Equal(13.0, high["distance"]!.GetValue<double>(), 3);
        Assert.Equal(-13.0, high["deltaZ"]!.GetValue<double>(), 3);

        Assert.False(wide["found"]!.GetValue<bool>());
        Assert.Equal(["wide"], wide["tried"]!.AsArray().Select(n => n!.GetValue<string>()));

        Assert.False(none["found"]!.GetValue<bool>());
        Assert.Equal(["near", "far", "wide"], none["tried"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Null(none["poly"]);
        Assert.Single(none["overlay"]!["points"]!.AsArray());

        Assert.False(outsideTile["found"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Nearest_UsesCustomExtents_AndTheTerrainFilter()
    {
        Add(32, 32, flags: (_, _) => (ushort)NavTerrain.Water);
        Vector3 at = Cell(32, 32, 4, 1, 1);
        Vector3 off = at + new Vector3(0, 0, 30);

        JsonNode water = await Ok("ref.query.nearest", new JsonObject { ["map"] = 0, ["at"] = Point(at) });
        JsonNode groundOnly = await Ok("ref.query.nearest", new JsonObject { ["map"] = 0, ["at"] = Point(at), ["include"] = new JsonArray("ground") });
        JsonNode excluded = await Ok("ref.query.nearest", new JsonObject { ["map"] = 0, ["at"] = Point(at), ["exclude"] = new JsonArray("water") });
        JsonNode narrow = await Ok("ref.query.nearest", new JsonObject { ["map"] = 0, ["at"] = Point(off), ["extents"] = Point(new Vector3(3, 3, 5)) });
        JsonNode tall = await Ok("ref.query.nearest", new JsonObject { ["map"] = 0, ["at"] = Point(off), ["extents"] = Point(new Vector3(3, 3, 50)) });

        Assert.True(water["found"]!.GetValue<bool>());
        Assert.Equal("water", water["poly"]!["colorKey"]!.GetValue<string>());
        Assert.False(groundOnly["found"]!.GetValue<bool>());
        Assert.False(excluded["found"]!.GetValue<bool>());
        Assert.False(narrow["found"]!.GetValue<bool>());
        Assert.Equal(["custom"], narrow["tried"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.True(tall["found"]!.GetValue<bool>());
        Assert.Equal(30.0, tall["distance"]!.GetValue<double>(), 3);
    }

    [Fact]
    public async Task Nearest_RefusesBadArguments()
    {
        Add(32, 32);
        Vector3 at = Cell(32, 32, 4, 1, 1);

        await Fails("ref.query.nearest", new JsonObject { ["map"] = 0, ["at"] = Point(at), ["extents"] = Point(new Vector3(-1, 1, 1)) }, CommandException.BadArguments);
        await Fails("ref.query.nearest", new JsonObject { ["map"] = 0, ["at"] = Point(at), ["box"] = "huge" }, CommandException.BadArguments);
        await Fails("ref.query.nearest", new JsonObject { ["map"] = 0, ["at"] = Point(new Vector3(2.0e6f, 0, 0)) }, CommandException.BadArguments);
        await Fails("ref.query.nearest", new JsonObject { ["map"] = 0, ["at"] = Point(at), ["exclude"] = new JsonArray("ice") }, CommandException.BadArguments);
        await Fails("ref.query.nearest", new JsonObject { ["map"] = 0 }, CommandException.BadArguments);
        await Fails("ref.query.nearest", new JsonObject { ["map"] = 0, ["at"] = Point(at), ["colour"] = "red" }, CommandException.BadArguments);
    }

    [Fact]
    public async Task Tile_ReportsTheHeaderCountsAndTheLinkInventory()
    {
        BuiltTile built = Add(32, 32, flags: (i, j) => (i + j) % 2 == 0 ? (ushort)NavTerrain.Ground : (ushort)NavTerrain.Water);
        Add(32, 31);

        JsonNode result = await Ok("ref.nav.tile", new JsonObject { ["map"] = 0, ["x"] = 32, ["y"] = 32 });

        Assert.Equal("ok", result["state"]!.GetValue<string>());
        Assert.Equal("0003232.mmtile", result["file"]!.GetValue<string>());
        JsonNode header = result["header"]!;
        Assert.Equal(16, header["polyCount"]!.GetValue<int>());
        Assert.Equal(25, header["vertCount"]!.GetValue<int>());
        Assert.Equal(16, header["detailMeshCount"]!.GetValue<int>());
        Assert.Equal(0, header["detailVertCount"]!.GetValue<int>());
        Assert.Equal(32, header["detailTriCount"]!.GetValue<int>());
        Assert.Equal(built.MaxLinkCount, header["maxLinkCount"]!.GetValue<int>());
        Assert.Equal(32, header["bvNodeCount"]!.GetValue<int>());
        Assert.Equal(0, header["offMeshConCount"]!.GetValue<int>());
        Assert.Equal(new[] { built.DetourX, built.DetourY, 0 }, header["detour"]!.AsArray().Select(n => n!.GetValue<int>()));
        Assert.Equal(0.9, header["walkableClimb"]!.GetValue<double>(), 3);
        Assert.Equal(7, result["prefix"]!["detourVersion"]!.GetValue<int>());
        Assert.Equal(6, result["prefix"]!["mmapVersion"]!.GetValue<int>());
        Assert.Equal(built.File.Length - 20, result["prefix"]!["size"]!.GetValue<int>());
        Assert.Equal(built.File.Length, result["fileBytes"]!.GetValue<int>());
        Assert.Equal(16, result["linkSize"]!.GetValue<int>());
        Assert.Equal(48, result["links"]!["internal"]!.GetValue<int>());
        Assert.Equal(4, result["links"]!["external"]!.GetValue<int>());
        Assert.Equal(0, result["links"]!["stale"]!.GetValue<int>());
        Assert.Equal(8, result["colorKeys"]!["ground"]!.GetValue<int>());
        Assert.Equal(8, result["colorKeys"]!["water"]!.GetValue<int>());
        Assert.Equal(2, result["neighbours"]!["ok"]!.GetValue<int>());
        Assert.Single(result["overlay"]!["boxes"]!.AsArray());

        // Four areas cycle across the grid (1 + (i + j) % 4); 16 cells hold 4 of each... except the diagonal sums.
        int areas = result["areas"]!.AsObject().Sum(a => a.Value!.GetValue<int>());
        Assert.Equal(16, areas);
    }

    [Fact]
    public async Task Tile_ListsOffMeshConnections()
    {
        Vector3 from = SyntheticNav.CellCenterRecast(32, 32, 3, 0, 1, 10f);
        Vector3 to = SyntheticNav.CellCenterRecast(32, 32, 3, 2, 1, 10f);
        Add(32, 32, cells: 3, present: (i, _) => i != 1, offMesh: [new OffMeshSpec(from, to, 2.5f, Bidirectional: true, UserId: 9)]);

        JsonNode result = await Ok("ref.nav.tile", new JsonObject { ["map"] = 0, ["x"] = 32, ["y"] = 32 });

        Assert.Equal(1, result["header"]!["offMeshConCount"]!.GetValue<int>());
        JsonNode con = Assert.Single(result["offMeshConnections"]!.AsArray())!;
        Assert.Equal(6, con["poly"]!.GetValue<int>());
        Assert.Equal(2.5, con["radius"]!.GetValue<double>(), 3);
        Assert.True(con["bidirectional"]!.GetValue<bool>());
        Assert.Equal(9, con["userId"]!.GetValue<int>());
        Assert.Equal(255, con["side"]!.GetValue<int>());
        Assert.Equal(NavMeshFormat.ToWorld(from).X, con["from"]![0]!.GetValue<float>(), 0.01f);
        Assert.Equal(4, result["links"]!["offMesh"]!.GetValue<int>());
        Assert.Single(result["overlay"]!["segments"]!.AsArray());
    }

    [Fact]
    public async Task Tile_ReportsMissingAndRefusedTiles_AsStatesNotErrors()
    {
        Add(32, 32);
        _data.WriteRaw(31, 32, new byte[300]);

        JsonNode missing = await Ok("ref.nav.tile", new JsonObject { ["map"] = 0, ["x"] = 5, ["y"] = 5 });
        JsonNode refused = await Ok("ref.nav.tile", new JsonObject { ["map"] = 0, ["x"] = 31, ["y"] = 32 });
        JsonNode noMap = await Ok("ref.nav.tile", new JsonObject { ["map"] = 9, ["x"] = 5, ["y"] = 5 });

        Assert.Equal("missing", missing["state"]!.GetValue<string>());
        Assert.Null(missing["header"]);
        Assert.Equal("rejected", refused["state"]!.GetValue<string>());
        Assert.Contains("bad mmap magic", refused["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(refused["header"]);
        Assert.Equal("missing", noMap["state"]!.GetValue<string>());
        Assert.Contains("009.mmap", noMap["detail"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tile_RefusesCoordinatesOutsideTheGrid()
    {
        Add(32, 32);

        await Fails("ref.nav.tile", new JsonObject { ["map"] = 0, ["x"] = 64, ["y"] = 0 }, CommandException.BadArguments);
        await Fails("ref.nav.tile", new JsonObject { ["map"] = 0, ["x"] = -1, ["y"] = 0 }, CommandException.BadArguments);
        await Fails("ref.nav.tile", new JsonObject { ["map"] = 0, ["x"] = 1 }, CommandException.BadArguments);
    }

    [Fact]
    public async Task Commands_StopWhenCancelled()
    {
        Add(32, 32);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Vector3 a = Cell(32, 32, 4, 0, 0);
        Vector3 b = Cell(32, 32, 4, 3, 3);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Context.Commands.InvokeAsync("ref.query.path", PathArgs(a, b), Context.Services, CommandOrigin.Script, true, cts.Token));
    }

    [Fact]
    public async Task Commands_RunFromManyThreadsAtOnce()
    {
        for (int x = 31; x <= 33; x++)
        {
            for (int y = 31; y <= 33; y++)
            {
                Add(x, y, cells: 3);
            }
        }

        Vector3 a = Cell(31, 31, 3, 0, 0);
        Vector3 b = Cell(33, 33, 3, 2, 2);
        JsonNode single = await Ok("ref.query.path", PathArgs(a, b));

        JsonNode[] results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => i % 3 == 0
            ? Ok("ref.query.path", PathArgs(a, b))
            : i % 3 == 1
                ? Ok("ref.nav.tile", new JsonObject { ["map"] = 0, ["x"] = 32, ["y"] = 32 })
                : Ok("ref.query.nearest", new JsonObject { ["map"] = 0, ["at"] = Point(a) })));

        for (int i = 0; i < results.Length; i += 3)
        {
            AssertSamePath(Points(single), Points(results[i]), 0.001f);
            Assert.Equal(single["polys"]!.ToJsonString(), results[i]["polys"]!.ToJsonString());
        }
    }

    [Fact]
    public void Filter_PassesByIncludeAndExcludeFlags_AndRefusesOffMeshPolygonsWhenAsked()
    {
        var ground = new DtPoly(0, 6) { flags = (int)NavTerrain.Ground };
        var water = new DtPoly(1, 6) { flags = (int)(NavTerrain.Water | NavTerrain.SteepSlopes) };
        var none = new DtPoly(2, 6) { flags = 0 };
        var offMesh = new DtPoly(3, 6) { flags = (int)NavTerrain.Ground };
        offMesh.SetPolyType(DtPolyTypes.DT_POLYTYPE_OFFMESH_CONNECTION);
        var tile = new DtMeshTile(0);
        var open = new NavQueryFilter(NavTerrain.Ground | NavTerrain.Water, NavTerrain.SteepSlopes, allowOffMesh: true);
        var strict = new NavQueryFilter(NavTerrain.Ground | NavTerrain.Water, NavTerrain.Empty, allowOffMesh: false);

        Assert.True(open.PassFilter(1, tile, ground));
        Assert.False(open.PassFilter(2, tile, water));
        Assert.False(open.PassFilter(3, tile, none));
        Assert.True(open.PassFilter(4, tile, offMesh));
        Assert.True(strict.PassFilter(1, tile, ground));
        Assert.True(strict.PassFilter(2, tile, water));
        Assert.False(strict.PassFilter(4, tile, offMesh));
        Assert.Equal(5f, open.GetCost(new DotRecast.Core.Numerics.RcVec3f(0, 0, 0), new DotRecast.Core.Numerics.RcVec3f(3, 0, 4), 0, tile, ground, 0, tile, ground, 0, tile, ground));
    }

    [Fact]
    public void Definitions_AreRegisteredOnce_WithDocumentedArguments()
    {
        string[] names = [.. NavCommands.Definitions.Select(d => d.Name)];

        Assert.Equal(["ref.query.path", "ref.query.nearest", "ref.nav.tile"], names);
        Assert.All(NavCommands.Definitions, d =>
        {
            Assert.All(d.Args, a => Assert.False(string.IsNullOrWhiteSpace(a.Description)));
            Assert.Equal(CommandSurfaces.All, d.Surfaces);
            Assert.NotEmpty(CommandSchema.InputSchema(d)["properties"]!.AsObject());
        });
        Assert.Contains(NavCommands.Definitions.Single(d => d.Name == "ref.query.nearest").Args, a => a.Name == "box" && a.Choices!.Contains("end-tolerance"));
    }
}
