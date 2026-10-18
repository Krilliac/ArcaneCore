using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Host;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Host;

/// <summary>Ray tests against chunks (double precision, origin-relative floats) and the tile walk a ray takes.</summary>
public sealed class ChunkPickerTests
{
    private static readonly TileKey Goldshire = Geo.TileAt(-9462.66, 16.1915);
    private static readonly WorldPoint Down = new(0, 0, -1);

    [Fact]
    public void ATriangleIsHitFromAboveAndFromBelow_AndTheHitPointIsOnTheRay()
    {
        GeometryChunk quad = Geo.Quad(LayerIds.Terrain, Goldshire, -9500, -9400, 10, 30, 60, attribute: 7);

        ChunkHit? above = ChunkPicker.Nearest(quad, new WorldPoint(-9450, 20, 100), Down, 1000);
        ChunkHit? below = ChunkPicker.Nearest(quad, new WorldPoint(-9450, 20, 0), new WorldPoint(0, 0, 1), 1000);

        Assert.NotNull(above);
        Assert.Equal(40, above.Value.Distance, 4);
        Assert.Equal("triangle", above.Value.Primitive);
        Assert.Equal(7u, above.Value.Attribute);
        Assert.Equal(new WorldPoint(-9450, 20, 60).Z, above.Value.Point.Z, 4);
        Assert.Equal(-9450, above.Value.Point.X, 9);
        Assert.NotNull(below);
        Assert.Equal(60, below.Value.Distance, 4);
        Assert.Equal("triangle", below.Value.Primitive);
    }

    [Fact]
    public void ARayThatMissesTheQuad_OrFallsShortOfIt_FindsNothing()
    {
        // Triangles only: the shared diagonal passes through (-9450, 20), and a ray that stops 0.1 yd short of an edge line
        // is within the 0.25 yd line tolerance (that case is a line hit, covered by the line tests).
        GeometryChunk quad = Geo.Quad(LayerIds.Terrain, Goldshire, -9500, -9400, 10, 30, 60, edges: false);

        Assert.Null(ChunkPicker.Nearest(quad, new WorldPoint(-9300, 20, 100), Down, 1000));
        Assert.Null(ChunkPicker.Nearest(quad, new WorldPoint(-9450, 20, 100), Down, 39.9));
        Assert.NotNull(ChunkPicker.Nearest(quad, new WorldPoint(-9450, 20, 100), Down, 40.1));
        Assert.Null(ChunkPicker.Nearest(quad, new WorldPoint(-9450, 20, 100), new WorldPoint(0, 0, 1), 1000));
    }

    [Fact]
    public void TheNearestOfSeveralTrianglesWins()
    {
        var builder = new GeometryBuilder(LayerIds.Terrain, Goldshire, WorldGrid.Origin(Goldshire));
        foreach (double z in new[] { 50.0, 80.0, 65.0 })
        {
            uint a = builder.AddVertex(-9500, 10, z);
            uint b = builder.AddVertex(-9400, 10, z);
            uint c = builder.AddVertex(-9450, 30, z);
            builder.AddTriangle(a, b, c, (uint)z, edges: false);
        }

        ChunkHit? hit = ChunkPicker.Nearest(builder.Build(), new WorldPoint(-9450, 15, 100), Down, 1000);

        Assert.NotNull(hit);
        Assert.Equal(20, hit.Value.Distance, 4);
        Assert.Equal(80u, hit.Value.Attribute);
    }

    [Fact]
    public void ALineIsPickedWithinAQuarterYard_AndNotBeyondIt()
    {
        GeometryChunk segment = Geo.Segment(LayerIds.Terrain, Goldshire, new WorldPoint(-9450, 20, 60), new WorldPoint(-9440, 20, 60), attribute: 4);

        ChunkHit? near = ChunkPicker.Nearest(segment, new WorldPoint(-9445, 20.2, 100), Down, 1000);
        ChunkHit? far = ChunkPicker.Nearest(segment, new WorldPoint(-9445, 20.3, 100), Down, 1000);

        Assert.NotNull(near);
        Assert.Equal("line", near.Value.Primitive);
        Assert.Equal(4u, near.Value.Attribute);
        Assert.Equal(40, near.Value.Distance, 2);
        Assert.Null(far);
        Assert.Equal(0.25, ChunkPicker.Tolerance);
    }

    [Fact]
    public void APointIsPickedWithinAQuarterYard_AndLinesAndPointsCanBeLeftOut()
    {
        GeometryChunk dot = Geo.Dot(LayerIds.Terrain, Goldshire, new WorldPoint(-9450, 20, 60), attribute: 9);

        ChunkHit? near = ChunkPicker.Nearest(dot, new WorldPoint(-9450.2, 20, 100), Down, 1000);
        ChunkHit? far = ChunkPicker.Nearest(dot, new WorldPoint(-9450.3, 20, 100), Down, 1000);
        ChunkHit? left = ChunkPicker.Nearest(dot, new WorldPoint(-9450.2, 20, 100), Down, 1000, includeLinesAndPoints: false);

        Assert.NotNull(near);
        Assert.Equal("point", near.Value.Primitive);
        Assert.Equal(9u, near.Value.Attribute);
        Assert.Equal(40, near.Value.Distance, 2);
        Assert.Null(far);
        Assert.Null(left);
    }

    [Fact]
    public void ATriangleBeatsALineBehindIt_AndALineBeatsATriangleBehindIt()
    {
        var builder = new GeometryBuilder(LayerIds.Terrain, Goldshire, WorldGrid.Origin(Goldshire));
        uint a = builder.AddVertex(-9500, 10, 60);
        uint b = builder.AddVertex(-9400, 10, 60);
        uint c = builder.AddVertex(-9450, 30, 60);
        builder.AddTriangle(a, b, c, 1, edges: false);
        uint p = builder.AddVertex(-9450, 15, 70);
        uint q = builder.AddVertex(-9449, 15, 70);
        builder.AddLine(p, q, 2);
        GeometryChunk chunk = builder.Build();

        ChunkHit? lineFirst = ChunkPicker.Nearest(chunk, new WorldPoint(-9449.5, 15, 100), Down, 1000);
        ChunkHit? triangleOnly = ChunkPicker.Nearest(chunk, new WorldPoint(-9449.5, 15, 100), Down, 1000, includeLinesAndPoints: false);

        Assert.Equal("line", lineFirst!.Value.Primitive);
        Assert.Equal(30, lineFirst.Value.Distance, 2);
        Assert.Equal("triangle", triangleOnly!.Value.Primitive);
        Assert.Equal(40, triangleOnly.Value.Distance, 4);
    }

    [Fact]
    public void FarFromTheWorldOrigin_PositionsStayAccurateBecauseTheyAreRelativeToTheChunkOrigin()
    {
        // Tile (0, 0) lies near x = y = 17000. An absolute float32 holds such a coordinate to about 0.002 yd; origin-relative
        // positions hold it to about 0.00001 yd, so a ray 0.00004 inside the left edge hits and one 0.00006 outside misses.
        var tile = new TileKey(0, 0, 0);
        const double edge = 16990.123456;
        GeometryChunk quad = Geo.Quad(LayerIds.Terrain, tile, edge, edge + 5, 17000.0, 17005.0, 4321.5, edges: false);

        ChunkHit? inside = ChunkPicker.Nearest(quad, new WorldPoint(edge + 0.00004, 17002.5, 5000), Down, 1000);
        ChunkHit? outside = ChunkPicker.Nearest(quad, new WorldPoint(edge - 0.00006, 17002.5, 5000), Down, 1000);

        Assert.NotNull(inside);
        Assert.Equal(5000 - 4321.5, inside.Value.Distance, 3);
        Assert.Null(outside);
    }

    [Fact]
    public void AChunkWithNoGeometryIsNeverHit()
    {
        GeometryChunk empty = new GeometryBuilder(LayerIds.Terrain, Goldshire, WorldGrid.Origin(Goldshire)).Build();

        Assert.Null(ChunkPicker.Nearest(empty, new WorldPoint(-9450, 20, 100), Down, 1000));
    }
}

public sealed class TileTraversalTests
{
    private static readonly WorldPoint North = new(1, 0, 0);

    private static TileKey[] Walk(WorldPoint origin, WorldPoint direction, double max, uint map = 0)
        => [.. TileTraversal.Along(map, origin, direction, max)];

    [Fact]
    public void AVerticalRayYieldsItsOwnTileOnly()
    {
        TileKey[] tiles = Walk(new WorldPoint(-9462.66, 16.1915, 500), new WorldPoint(0, 0, -1), 1000);

        Assert.Equal(new[] { new TileKey(0, 49, 31) }, tiles);
    }

    [Fact]
    public void ARayHeadingNorth_VisitsTilesInDescendingXIndexOrder()
    {
        // u = 32 - x / 533.33: from 49.74 down to 45.99 after 2000 yd, so tiles 49, 48, 47, 46 and 45; v stays 31.97 (tile 31).
        TileKey[] tiles = Walk(new WorldPoint(-9462.66, 16.1915, 60), North, 2000);

        Assert.Equal(new[] { 49, 48, 47, 46, 45 }, tiles.Select(t => t.X));
        Assert.All(tiles, t => Assert.Equal(31, t.Y));
    }

    [Fact]
    public void AShortRay_StaysInItsTile()
    {
        TileKey[] tiles = Walk(new WorldPoint(-9462.66, 16.1915, 60), North, 100);

        Assert.Equal(new[] { new TileKey(0, 49, 31) }, tiles);
    }

    [Fact]
    public void ADiagonalRay_StepsAcrossTheNearerBoundaryFirst()
    {
        // Heading north-west: u reaches 49.0 after 561 yd, v reaches 31.0 after 731 yd, u reaches 48.0 after 1315 yd and v reaches 30.0 after 1486 yd.
        double s = Math.Sqrt(0.5);

        TileKey[] tiles = Walk(new WorldPoint(-9462.66, 16.1915, 60), new WorldPoint(s, s, 0), 1400);

        Assert.Equal(new[] { new TileKey(0, 49, 31), new TileKey(0, 48, 31), new TileKey(0, 48, 30), new TileKey(0, 47, 30) }, tiles);
    }

    [Fact]
    public void ARayStartingOutsideTheMap_EntersAtTheEdge()
    {
        // x = 19066 is 2000 yd south of tile 0's far edge (17066.67); heading toward -x it enters after about 2000 yd.
        TileKey[] tiles = Walk(new WorldPoint(19066.67, 0, 60), new WorldPoint(-1, 0, 0), 5000);

        Assert.Equal(new TileKey(0, 0, 32), tiles[0]);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, tiles.Select(t => t.X));
    }

    [Fact]
    public void ARayThatNeverEntersTheMap_VisitsNothing()
    {
        Assert.Empty(Walk(new WorldPoint(30000, 30000, 0), North, 5000));
        Assert.Single(Walk(new WorldPoint(-9462.66, 16.1915, 60), North, 0.001));
    }

    [Fact]
    public void TheWalkStopsAtTheEdgeOfTheGrid_AndCarriesTheMapId()
    {
        TileKey[] tiles = Walk(new WorldPoint(-16800, 16.1915, 60), North, 40000, map: 530);

        Assert.All(tiles, t => Assert.Equal(530u, t.MapId));
        Assert.Equal(63, tiles[0].X);
        Assert.Equal(0, tiles[^1].X);
        Assert.Equal(64, tiles.Length);
    }
}

/// <summary><c>query.pick</c> through the registry: rays, camera rays, layer choice and what is reported.</summary>
public sealed class PickCommandTests
{
    private static readonly TileKey Goldshire = Geo.TileAt(-9462.66, 16.1915);

    private static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    private static string Down(double x, double y, double z, string extra = "")
        => $$"""{"ray":{"origin":[{{x}},{{y}},{{z}}],"direction":[0,0,-1]},"layers":["terrain"]{{extra}}}""";

    [Fact]
    public async Task AVerticalRay_HitsTheTerrainBelow_AndReportsLayerTileAttributeAndAnOverlay()
    {
        await using HostRig rig = HostRig.Create();
        rig.Terrain.AddChunks(LayerIds.Terrain, Goldshire, Geo.Quad(LayerIds.Terrain, Goldshire, -9500, -9400, 10, 30, 60, attribute: 3, kind: "ground"));

        JsonNode hit = await rig.DataAsync("query.pick", Args(Down(-9450, 20, 100)));

        Assert.True(hit.Flag("hit"));
        Assert.Equal("terrain", hit.Str("layer"));
        Assert.Equal(40, hit.Num("distance"), 3);
        Assert.Equal(60, hit.Num("point.2"), 3);
        Assert.Equal(-9450, hit.Num("point.0"), 6);
        Assert.Equal("triangle", hit.Str("primitive"));
        Assert.Equal(3, hit.Num("attribute"));
        Assert.Equal("ground", hit.Str("details.kind"));
        Assert.Equal(new double[] { 0, Goldshire.X, Goldshire.Y }, new[] { hit.Num("tile.0"), hit.Num("tile.1"), hit.Num("tile.2") });
        Assert.Equal("terrain triangle #3", hit.Str("overlay.points.0.label"));
        Assert.Equal(60, hit.Num("overlay.points.0.at.2"), 3);
        Assert.Equal(1, hit.Num("tested.chunks"));
        Assert.Equal(2, hit.Num("tested.tiles"));
        Assert.Equal(1, hit.Num("tested.skipped"));
        Assert.Equal(1, rig.Terrain.BuildsOf(LayerIds.Terrain, Goldshire));
    }

    [Fact]
    public async Task AMissIsReportedAsNoHit_AndAShortRayStopsShortOfTheSurface()
    {
        await using HostRig rig = HostRig.Create();
        rig.Terrain.AddChunks(LayerIds.Terrain, Goldshire, Geo.Quad(LayerIds.Terrain, Goldshire, -9500, -9400, 10, 30, 60));

        JsonNode aside = await rig.DataAsync("query.pick", Args(Down(-9300, 20, 100)));
        JsonNode shortRay = await rig.DataAsync("query.pick", Args("""{"ray":{"origin":[-9450,20,100],"direction":[0,0,-1],"maxDistance":30},"layers":["terrain"]}"""));
        JsonNode argument = await rig.DataAsync("query.pick", Args(Down(-9450, 20, 100, ""","maxDistance":39""")));

        Assert.False(aside.Flag("hit"));
        Assert.False(shortRay.Flag("hit"));
        Assert.False(argument.Flag("hit"));
        Assert.Equal(1, aside.Num("tested.chunks"));
        Assert.Null(aside.At("layer"));
    }

    [Fact]
    public async Task ARayDirectionNeedNotBeAUnitVector()
    {
        await using HostRig rig = HostRig.Create();
        rig.Terrain.AddChunks(LayerIds.Terrain, Goldshire, Geo.Quad(LayerIds.Terrain, Goldshire, -9500, -9400, 10, 30, 60));

        JsonNode hit = await rig.DataAsync("query.pick", Args("""{"ray":{"origin":"-9450 20 100","direction":"0,0,-250"},"layers":"terrain"}"""));

        Assert.True(hit.Flag("hit"));
        Assert.Equal(40, hit.Num("distance"), 3);
    }

    [Fact]
    public async Task TheNearestChunkOfATileWins_AcrossParts()
    {
        await using HostRig rig = HostRig.Create();
        rig.Terrain.AddChunks(
            LayerIds.Terrain,
            Goldshire,
            Geo.Quad(LayerIds.Terrain, Goldshire, -9500, -9400, 10, 30, 60, attribute: 1, part: 0),
            Geo.Quad(LayerIds.Terrain, Goldshire, -9500, -9400, 10, 30, 70, attribute: 2, part: 1));

        JsonNode hit = await rig.DataAsync("query.pick", Args(Down(-9450, 20, 100)));

        Assert.Equal(30, hit.Num("distance"), 3);
        Assert.Equal(2, hit.Num("attribute"));
        Assert.Equal(2, hit.Num("tested.chunks"));
    }

    [Fact]
    public async Task TheNearestLayerWins_AndTheLayersArgumentRestrictsTheTest()
    {
        var walls = new FakeLayerSource(LayerIds.VMapWmo);
        await using HostRig rig = HostRig.Create(register: c => c.Layers.Add(walls));
        rig.Terrain.AddChunks(LayerIds.Terrain, Goldshire, Geo.Quad(LayerIds.Terrain, Goldshire, -9500, -9400, 10, 30, 60));
        walls.AddChunks(LayerIds.VMapWmo, Goldshire, Geo.Quad(LayerIds.VMapWmo, Goldshire, -9500, -9400, 10, 30, 75));

        JsonNode defaults = await rig.DataAsync("query.pick", Args("""{"ray":{"origin":[-9450,20,100],"direction":[0,0,-1]}}"""));
        JsonNode terrainOnly = await rig.DataAsync("query.pick", Args(Down(-9450, 20, 100)));
        JsonNode both = await rig.DataAsync("query.pick", Args("""{"ray":{"origin":[-9450,20,100],"direction":[0,0,-1]},"layers":"terrain,vmap.wmo"}"""));

        Assert.Equal("vmap.wmo", defaults.Str("layer"));
        Assert.Equal(25, defaults.Num("distance"), 3);
        Assert.Equal(2, defaults.At("tested.layers")!.AsArray().Count);
        Assert.Equal("terrain", terrainOnly.Str("layer"));
        Assert.Equal(40, terrainOnly.Num("distance"), 3);
        Assert.Equal("vmap.wmo", both.Str("layer"));
    }

    [Fact]
    public async Task ARayCrossingTileBorders_FindsASurfaceInALaterTile()
    {
        await using HostRig rig = HostRig.Create();
        var next = new TileKey(0, Goldshire.X - 1, Goldshire.Y);
        rig.Terrain.AddChunks(LayerIds.Terrain, Goldshire, Geo.Quad(LayerIds.Terrain, Goldshire, -9500, -9400, 10, 30, 0));
        rig.Terrain.AddChunks(LayerIds.Terrain, next, Geo.Wall(LayerIds.Terrain, next, -8950, 0, 40, 40, 80));

        JsonNode hit = await rig.DataAsync("query.pick", Args("""{"ray":{"origin":[-9462,20,55],"direction":[1,0,0]},"layers":["terrain"]}"""));

        Assert.True(hit.Flag("hit"));
        Assert.Equal(512, hit.Num("distance"), 3);
        Assert.Equal(next.X, hit.Num("tile.1"));
        Assert.Equal(-8950, hit.Num("point.0"), 3);
        Assert.Equal(1, rig.Terrain.BuildsOf(LayerIds.Terrain, next));
        Assert.True(hit.Num("tested.tiles") >= 5);
    }

    [Fact]
    public async Task TheMapsGlobalTileIsAlwaysTested()
    {
        await using HostRig rig = HostRig.Create();
        TileKey global = TileKey.Global(0);
        rig.Terrain.AddChunks(LayerIds.Terrain, Goldshire, Geo.Quad(LayerIds.Terrain, Goldshire, -9500, -9400, 10, 30, 60));
        rig.Terrain.AddChunks(LayerIds.Terrain, global, Geo.Quad(LayerIds.Terrain, global, -9500, -9400, 10, 30, 80));

        JsonNode hit = await rig.DataAsync("query.pick", Args(Down(-9450, 20, 100)));

        Assert.Equal(20, hit.Num("distance"), 3);
        Assert.Equal(-1, hit.Num("tile.1"));
        Assert.Equal(-1, hit.Num("tile.2"));
    }

    [Fact]
    public async Task ATileThatCannotBeRead_IsListedAsAProblem_AndDoesNotStopThePick()
    {
        await using HostRig rig = HostRig.Create();
        var broken = new TileKey(0, Goldshire.X - 1, Goldshire.Y);
        var refused = new TileKey(0, Goldshire.X - 2, Goldshire.Y);
        rig.Terrain.AddFailing(LayerIds.Terrain, broken, new InvalidDataException("corrupt height block"));
        rig.Terrain.AddRejected(LayerIds.Terrain, refused, "bad magic");
        rig.Terrain.AddChunks(LayerIds.Terrain, Goldshire, Geo.Quad(LayerIds.Terrain, Goldshire, -9500, -9400, 10, 30, 60));
        string request = """{"ray":{"origin":[-9450,20,100],"direction":[1,0,-1]},"layers":["terrain"]}""";

        JsonNode hit = await rig.DataAsync("query.pick", Args(request));

        Assert.True(hit.Flag("hit"));
        Assert.Equal(Goldshire.X, hit.Num("tile.1"));
        JsonNode problem = Assert.Single(hit.At("tested.problems")!.AsArray())!;
        Assert.Equal("terrain", problem.Str("layer"));
        Assert.Equal(broken.X, problem.Num("tile.1"));
        Assert.Contains("corrupt height block", problem.Str("message"), StringComparison.Ordinal);
        Assert.True(hit.Num("tested.skipped") >= 1);
    }

    [Fact]
    public async Task ACameraRay_ThroughTheViewCentre_HitsTheSurfaceUnderTheTarget()
    {
        await using HostRig rig = HostRig.Create();
        CameraState camera = ViewState.Default.Camera;
        rig.Terrain.AddChunks(LayerIds.Terrain, Goldshire, Geo.Quad(LayerIds.Terrain, Goldshire, -9500, -9400, 10, 30, camera.Target.Z));

        JsonNode hit = await rig.DataAsync("query.pick", Args("""{"ndc":"0,0"}"""));

        Assert.True(hit.Flag("hit"));
        Assert.Equal(camera.Distance, hit.Num("distance"), 2);
        Assert.Equal(camera.Target.X, hit.Num("point.0"), 2);
        Assert.Equal(camera.Target.Y, hit.Num("point.1"), 2);
        Assert.True(hit.Num("tested.tiles") >= 3);
    }

    [Fact]
    public async Task ACameraRay_InTopMode_StartsHighAboveTheTarget_AndStillReachesTheGround()
    {
        await using HostRig rig = HostRig.Create();
        double ground = ViewState.Default.Camera.Target.Z - 200;
        rig.Terrain.AddChunks(LayerIds.Terrain, Goldshire, Geo.Quad(LayerIds.Terrain, Goldshire, -9500, -9400, 10, 30, ground));
        await rig.DataAsync("view.set", Args("""{"camera":{"mode":"top"}}"""));

        // The ray starts TopEyeHeight above the target, so maxDistance is measured from the target plane: the ground
        // 200 yd below it is reached with 300 (and the default 2000) but not with 100.
        JsonNode byDefault = await rig.DataAsync("query.pick", Args("""{"ndc":"0,0"}"""));
        JsonNode enough = await rig.DataAsync("query.pick", Args("""{"ndc":"0,0","maxDistance":300}"""));
        JsonNode capped = await rig.DataAsync("query.pick", Args("""{"ndc":"0,0","maxDistance":100}"""));

        Assert.True(byDefault.Flag("hit"));
        Assert.Equal(CameraMath.TopEyeHeight + 200, byDefault.Num("distance"), 2);
        Assert.Equal(ground, byDefault.Num("point.2"), 3);
        Assert.True(enough.Flag("hit"));
        Assert.False(capped.Flag("hit"));
    }

    [Fact]
    public async Task AHiddenLayerIsNotPickedByDefault()
    {
        await using HostRig rig = HostRig.Create();
        rig.Terrain.AddChunks(LayerIds.Terrain, Goldshire, Geo.Quad(LayerIds.Terrain, Goldshire, -9500, -9400, 10, 30, 60));
        await rig.DataAsync("layer.set", Args("""{"id":"terrain","visible":false}"""));

        JsonNode hit = await rig.DataAsync("query.pick", Args("""{"ray":{"origin":[-9450,20,100],"direction":[0,0,-1]}}"""));

        Assert.False(hit.Flag("hit"));
        Assert.Empty(hit.At("tested.layers")!.AsArray());
        Assert.Equal(0, rig.Terrain.Builds);
    }

    [Fact]
    public async Task ARayOnAnotherMap_FindsNothingOnThisOne()
    {
        await using HostRig rig = HostRig.Create();
        rig.Terrain.AddChunks(LayerIds.Terrain, Goldshire, Geo.Quad(LayerIds.Terrain, Goldshire, -9500, -9400, 10, 30, 60));

        JsonNode hit = await rig.DataAsync("query.pick", Args("""{"ray":{"origin":[-9450,20,100],"direction":[0,0,-1],"map":1},"layers":["terrain"]}"""));

        Assert.False(hit.Flag("hit"));
        Assert.Equal(0, rig.Terrain.Builds);
    }

    [Fact]
    public async Task BadPickRequestsAreRefused()
    {
        await using HostRig rig = HostRig.Create();
        (string Request, string Code)[] cases =
        [
            ("{}", CommandException.BadArguments),
            ("""{"ray":{"origin":[0,0,0],"direction":[0,0,-1]},"ndc":"0,0"}""", CommandException.BadArguments),
            ("""{"ray":{"origin":[0,0,0],"direction":[0,0,0]}}""", CommandException.BadArguments),
            ("""{"ray":{"origin":[0,0],"direction":[0,0,-1]}}""", CommandException.BadArguments),
            ("""{"ray":{"direction":[0,0,-1]}}""", CommandException.BadArguments),
            ("""{"ray":{"origin":[0,0,0],"direction":[0,0,-1],"maxDistance":"far"}}""", CommandException.BadArguments),
            ("""{"ray":{"origin":[0,0,0],"direction":[0,0,-1]},"maxDistance":0}""", CommandException.BadArguments),
            ("""{"ray":{"origin":[0,0,0],"direction":[0,0,-1]},"maxDistance":20001}""", CommandException.BadArguments),
            ("""{"ndc":"2,0"}""", CommandException.BadArguments),
            ("""{"ndc":"0"}""", CommandException.BadArguments),
            ("""{"ndc":"0,0","aspect":0}""", CommandException.BadArguments),
            ("""{"ray":{"origin":[0,0,0],"direction":[0,0,-1]},"layers":["no.such"]}""", CommandException.BadArguments),
            ("""{"ray":{"origin":[0,0,0],"direction":[0,0,-1]},"layers":["nav.poly"]}""", CommandException.Unavailable),
            ("""{"ray":{"origin":[0,0,0],"direction":[0,0,-1]},"layers":["live.players"]}""", CommandException.Unavailable),
            ("""{"ray":{"origin":[0,0,0],"direction":[0,0,-1]},"map":-1}""", CommandException.BadArguments),
        ];

        foreach ((string request, string code) in cases)
        {
            CommandResult result = await rig.InvokeAsync("query.pick", Args(request));
            Assert.False(result.Ok, request);
            Assert.Equal(code, result.ErrorCode);
        }
    }
}

/// <summary>PNG signature and IHDR parsing used to validate what the renderer or the UI hands back.</summary>
public sealed class PngHelperTests
{
    // A 37 x 19 image encoded by Windows GDI+ (not by this repository): the independent ground truth for the IHDR reader.
    private const string GdiPlus37By19 =
        "iVBORw0KGgoAAAANSUhEUgAAACUAAAATCAYAAAAXvcSzAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAA2SURBVEhL7c6hAQAwCMRAhmDYjshW4KsiX0SciUr1m01Tf0jgFOUU5RTlFOUU5RTlFOUUFTl1XGFiSAr9aGsAAAAASUVORK5CYII=";

    [Fact]
    public void AnIndependentlyEncodedPng_IsRecognisedAndSized()
    {
        byte[] png = Convert.FromBase64String(GdiPlus37By19);

        Assert.True(CaptureCommand.IsPng(png));
        Assert.Equal((37, 19), CaptureCommand.PngSize(png));
    }

    [Fact]
    public void ThePngBuiltByTheTestSupport_AgreesWithTheIndependentOne()
    {
        byte[] png = TestPng.Create(37, 19);

        Assert.True(CaptureCommand.IsPng(png));
        Assert.Equal((37, 19), CaptureCommand.PngSize(png));
        Assert.Equal("IEND"u8.ToArray(), png[^8..^4]);
        Assert.Equal(new byte[] { 0xAE, 0x42, 0x60, 0x82 }, png[^4..]);
    }

    [Fact]
    public void ThingsThatAreNotPngs_AreRefused()
    {
        byte[] png = TestPng.Create(8, 8);
        byte[] wrongChunk = (byte[])png.Clone();
        wrongChunk[12] = (byte)'X';
        byte[] zeroWidth = (byte[])png.Clone();
        Array.Clear(zeroWidth, 16, 4);
        byte[] shortHeader = (byte[])png.Clone();
        shortHeader[11] = 12;
        byte[] wrongSignature = (byte[])png.Clone();
        wrongSignature[1] = (byte)'X';

        Assert.False(CaptureCommand.IsPng([]));
        Assert.False(CaptureCommand.IsPng(png.AsSpan(0, 7)));
        Assert.False(CaptureCommand.IsPng(wrongSignature));
        Assert.True(CaptureCommand.IsPng(png.AsSpan(0, 8)));
        Assert.Null(CaptureCommand.PngSize(png.AsSpan(0, 8)));
        Assert.Null(CaptureCommand.PngSize(png.AsSpan(0, 23)));
        Assert.Null(CaptureCommand.PngSize(wrongChunk));
        Assert.Null(CaptureCommand.PngSize(zeroWidth));
        Assert.Null(CaptureCommand.PngSize(shortHeader));
        Assert.Null(CaptureCommand.PngSize("not a png at all, just text"u8));
    }
}
