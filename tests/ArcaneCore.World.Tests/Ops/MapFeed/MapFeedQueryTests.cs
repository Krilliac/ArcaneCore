using System.Numerics;
using System.Text;
using System.Text.Json;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.World.Ops.MapFeed;
using Microsoft.Extensions.Logging;
using Xunit;
using static ArcaneCore.World.Tests.Ops.Control.WorldControlPipeTests;

namespace ArcaneCore.World.Tests.Ops.MapFeed;

/// <summary>
/// The questions a tool asks over the feed (docs/areas/map-feed.md, "Queries"): line of sight, height, path and under-map, answered on
/// the world thread by the map's own collision, on loaded grids only. A real world with a flat synthetic tile under the human start
/// (<see cref="FeedWorld.GroundZ"/>), scripted models and a scripted pathfinder where the open defaults could not tell a right answer
/// from a wrong one, and a real named pipe.
/// </summary>
public sealed class MapFeedQueryTests
{
    private static readonly Vector3 Start = new(FeedWorld.StartX, FeedWorld.StartY, FeedWorld.StartZ);

    /// <summary>A world with a player at the human start, its grid and terrain tile loaded, and a client past hello.</summary>
    private static async Task<(FeedWorld World, FeedClient Client)> StartAsync(FeedWorldSetup? setup = null)
    {
        FeedWorld world = await FeedWorld.StartAsync(setup);
        await world.AddPlayerAsync();
        await WaitForLoadedAsync(world, Start);
        return (world, await world.ConnectAsync());
    }

    private static Task WaitForLoadedAsync(FeedWorld world, Vector3 point)
        => world.Host.WaitForWorldAsync(() => IsLoaded(world, point), "the grid and terrain tile under the player");

    /// <summary>World thread: whether the grid and the terrain tile under <paramref name="point"/> are loaded.</summary>
    private static bool IsLoaded(FeedWorld world, Vector3 point)
        => world.Host.World.FindMap(0, 0) is { } map
           && map.Grids.IsGridLoaded(GridDefines.ComputeGridCoord(point.X, point.Y))
           && TerrainTile.TileOf(point.X, point.Y) is { } tile
           && map.Terrain.IsTileLoaded(tile.X, tile.Y);

    private static void AssertOk(JsonElement result, long id, string kind)
    {
        Assert.Equal("result", result.Op());
        Assert.Equal(id, result.GetProperty("id").GetInt64());
        Assert.True(result.GetProperty("ok").GetBoolean(), result.GetRawText());
        Assert.Equal(kind, result.GetProperty("kind").GetString());
    }

    private static void AssertRefused(JsonElement result, long id, string code)
    {
        Assert.Equal("result", result.Op());
        Assert.Equal(id, result.GetProperty("id").GetInt64());
        Assert.False(result.GetProperty("ok").GetBoolean(), result.GetRawText());
        Assert.Equal(code, result.Code());
    }

    private static void AssertNear(float expected, float actual, float tolerance = 0.01f)
        => Assert.True(MathF.Abs(expected - actual) <= tolerance, $"expected {expected}, got {actual}");

    [WindowsFact]
    public async Task Height_OverTheSyntheticTile_IsTheTilesHeight_AndSaysThereAreNoModels()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        (FeedWorld world, FeedClient client) = await StartAsync();
        await using FeedWorld owned = world;

        JsonElement result = await client.QueryAsync(7, "height", 0, Start);

        AssertOk(result, 7, "height");
        Assert.False(result.GetProperty("data").GetBoolean(), "no vmaps: the models are the open default");
        AssertNear(FeedWorld.GroundZ, result.GetProperty("terrain").GetSingle());
        AssertNear(FeedWorld.GroundZ, result.GetProperty("floor").GetSingle());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("model").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("outdoors").ValueKind);
    }

    [WindowsFact]
    public async Task Height_InsideAModel_FindsItsFloor_AndTheInterior()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        (FeedWorld world, FeedClient client) = await StartAsync();
        await using FeedWorld owned = world;
        await world.InstallCollisionAsync(lineOfSight: new ScriptedLineOfSight());
        float x = (ScriptedLineOfSight.BuildingMinX + ScriptedLineOfSight.BuildingMaxX) / 2f;

        JsonElement result = await client.QueryAsync(8, "height", 0, new Vector3(x, FeedWorld.StartY, ScriptedLineOfSight.BuildingFloor + 1f));

        AssertOk(result, 8, "height");
        Assert.True(result.GetProperty("data").GetBoolean());
        AssertNear(FeedWorld.GroundZ, result.GetProperty("terrain").GetSingle());
        AssertNear(ScriptedLineOfSight.BuildingFloor, result.GetProperty("model").GetSingle());
        AssertNear(ScriptedLineOfSight.BuildingFloor, result.GetProperty("floor").GetSingle());
        Assert.False(result.GetProperty("outdoors").GetBoolean(), "the building is an interior");
    }

    [WindowsFact]
    public async Task LineOfSight_ThroughAWall_IsBlockedWithItsFirstHit_AndADoodadBlocksOnlyWhenDoodadsCount()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        (FeedWorld world, FeedClient client) = await StartAsync();
        await using FeedWorld owned = world;
        await world.InstallCollisionAsync(lineOfSight: new ScriptedLineOfSight());
        float y = FeedWorld.StartY;
        float z = FeedWorld.StartZ + 2f;

        JsonElement wall = await client.QueryAsync(1, "los", 0, new Vector3(-8949f, y, z), new Vector3(-8930f, y, z));
        AssertOk(wall, 1, "los");
        Assert.True(wall.GetProperty("data").GetBoolean());
        Assert.False(wall.GetProperty("clear").GetBoolean());
        Assert.False(wall.GetProperty("clearDoodads").GetBoolean());
        AssertNear(ScriptedLineOfSight.WallX, wall.GetProperty("hit").ToVector().X);

        JsonElement doodad = await client.QueryAsync(2, "los", 0, new Vector3(-8930f, y, z), new Vector3(-8910f, y, z));
        AssertOk(doodad, 2, "los");
        Assert.True(doodad.GetProperty("clear").GetBoolean(), "the server's own line of sight ignores doodads");
        Assert.False(doodad.GetProperty("clearDoodads").GetBoolean());
        AssertNear(ScriptedLineOfSight.DoodadX, doodad.GetProperty("hit").ToVector().X);

        JsonElement open = await client.QueryAsync(3, "los", 0, new Vector3(-8935f, y, z), new Vector3(-8925f, y, z));
        AssertOk(open, 3, "los");
        Assert.True(open.GetProperty("clear").GetBoolean());
        Assert.True(open.GetProperty("clearDoodads").GetBoolean());
        Assert.False(open.TryGetProperty("hit", out _), "nothing was hit");
    }

    [WindowsFact]
    public async Task Path_IsTheMapsPathfindersAnswer_AskedWithoutLoadingTiles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        (FeedWorld world, FeedClient client) = await StartAsync();
        await using FeedWorld owned = world;
        Vector3 end = new(-8930f, -140f, FeedWorld.StartZ);
        Vector3 bend = new(-8940f, -136f, FeedWorld.StartZ);
        var finder = new ScriptedPathfinder((a, b) => new PathResult(PathType.Normal, [a, bend, b]));
        await world.InstallCollisionAsync(pathfinder: finder);

        JsonElement result = await client.QueryAsync(4, "path", 0, Start, end);

        AssertOk(result, 4, "path");
        Assert.True(result.GetProperty("data").GetBoolean());
        Assert.True(result.GetProperty("hasPath").GetBoolean());
        Assert.Equal("normal", Assert.Single(result.GetProperty("type").Strings()));
        Vector3[] points = result.GetProperty("pts").ToVectors();
        Assert.Equal(3, points.Length);
        Assert.True(Vector3.Distance(Start, points[0]) < 0.01f);
        Assert.True(Vector3.Distance(bend, points[1]) < 0.01f);
        Assert.True(Vector3.Distance(end, points[2]) < 0.01f);
        AssertNear(Vector3.Distance(Start, bend) + Vector3.Distance(bend, end), result.GetProperty("length").GetSingle(), 0.05f);
        Assert.False(result.TryGetProperty("cut", out _));

        int calls = await world.Host.OnWorldAsync(() => finder.Calls);
        bool loadsTiles = await world.Host.OnWorldAsync(() => finder.LastOptions is { LoadTiles: true } or { NonBlockingTiles: true });
        Assert.Equal(1, calls);
        Assert.False(loadsTiles, "a feed path never asks the pathfinder to read tiles");
    }

    [WindowsFact]
    public async Task UnderMap_BelowTheTerrain_HasItsDepth_AndAboveItIsOk()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        (FeedWorld world, FeedClient client) = await StartAsync();
        await using FeedWorld owned = world;

        JsonElement under = await client.QueryAsync(5, "undermap", 0, Start with { Z = FeedWorld.GroundZ - 50f });
        AssertOk(under, 5, "undermap");
        Assert.Equal("undermap", under.GetProperty("verdict").GetString());
        AssertNear(50f, under.GetProperty("depth").GetSingle());

        JsonElement above = await client.QueryAsync(6, "undermap", 0, Start with { Z = FeedWorld.GroundZ + 1f });
        AssertOk(above, 6, "undermap");
        Assert.Equal("ok", above.GetProperty("verdict").GetString());
        Assert.False(above.TryGetProperty("depth", out _));
    }

    [WindowsFact]
    public async Task AQueryOutsideTheLoadedGrids_IsRefused_AndLoadsNothing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        (FeedWorld world, FeedClient client) = await StartAsync();
        await using FeedWorld owned = world;
        var finder = new ScriptedPathfinder((a, b) => PathResult.StraightLine(a, b, PathType.Normal));
        await world.InstallCollisionAsync(pathfinder: finder);
        Vector3 far = new(0f, 0f, 0f);
        Assert.False(await world.Host.OnWorldAsync(() => IsLoaded(world, far)), "the test needs a point whose grid is not loaded");

        JsonElement height = await client.QueryAsync(10, "height", 0, far);
        AssertRefused(height, 10, MapFeedProtocol.NotLoaded);
        Assert.Contains("never loads", height.GetProperty("message").GetString(), StringComparison.Ordinal);
        AssertRefused(await client.QueryAsync(11, "undermap", 0, far), 11, MapFeedProtocol.NotLoaded);
        AssertRefused(await client.QueryAsync(12, "los", 0, Start, far), 12, MapFeedProtocol.NotLoaded);
        AssertRefused(await client.QueryAsync(13, "path", 0, Start, far), 13, MapFeedProtocol.NotLoaded);

        await world.StepAsync(2);
        Assert.False(await world.Host.OnWorldAsync(() => IsLoaded(world, far)), "a query loaded the grid or the tile it was refused for");
        Assert.Equal(0, await world.Host.OnWorldAsync(() => finder.Calls));
        Assert.DoesNotContain(world.Logs.Entries, e => e.Level >= LogLevel.Error);
    }

    [WindowsFact]
    public async Task AQueryOfAMapThatIsNotRunning_IsRefused()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        (FeedWorld world, FeedClient client) = await StartAsync();
        await using FeedWorld owned = world;

        JsonElement result = await client.QueryAsync(20, "height", 4242, Start);

        AssertRefused(result, 20, MapFeedProtocol.MapNotLoaded);
        Assert.Contains("4242", result.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [WindowsFact]
    public async Task CoordinatesThatAreNotFiniteFloats_NeverReachCollision_AndAreLoggedBelowError()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        (FeedWorld world, FeedClient client) = await StartAsync();
        await using FeedWorld owned = world;
        var finder = new ScriptedPathfinder((a, b) => PathResult.StraightLine(a, b, PathType.Normal));
        await world.InstallCollisionAsync(pathfinder: finder);

        await client.SendRawAsync(Encoding.UTF8.GetBytes("{\"op\":\"query\",\"id\":30,\"kind\":\"path\",\"map\":0,\"a\":[1e39,-132,83],\"b\":[-8930,-132,83]}\n"));
        AssertRefused(await client.ReadAsync(), 30, MapFeedProtocol.BadArgs);
        await client.SendRawAsync(Encoding.UTF8.GetBytes("{\"op\":\"query\",\"id\":31,\"kind\":\"path\",\"map\":0,\"a\":[-8949,-132,83],\"b\":[-8930,\"x\",83]}\n"));
        AssertRefused(await client.ReadAsync(), 31, MapFeedProtocol.BadArgs);

        // The connection is still served: the next good question is answered.
        AssertOk(await client.QueryAsync(32, "path", 0, Start, new Vector3(-8930f, -132f, FeedWorld.StartZ)), 32, "path");
        Assert.Equal(1, await world.Host.OnWorldAsync(() => finder.Calls));
        Assert.DoesNotContain(world.Logs.Entries, e => e.Level >= LogLevel.Error);
    }
}
