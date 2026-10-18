using System.Numerics;
using System.Text.Json;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Terrain;
using Xunit;

namespace ArcaneCore.World.Tests.Ops.MapFeed;

/// <summary>
/// Skips (visibly) unless <c>ARCANECORE_TEST_TERRAIN_DIR</c> names a server terrain data root (<c>maps/</c>, <c>vmaps/</c>, <c>mmaps/</c>
/// from the vmangos extractors) and the system is Windows (the feed is a Windows named pipe). The data is read only, never written.
/// </summary>
public sealed class MapFeedRealTerrainFactAttribute : FactAttribute
{
    /// <summary>The variable naming the data root.</summary>
    public const string Variable = "ARCANECORE_TEST_TERRAIN_DIR";

    /// <summary>Skip unless the data root and Windows are there.</summary>
    public MapFeedRealTerrainFactAttribute()
    {
        if (MapFeedRealTerrain.SkipReason() is { } reason)
        {
            Skip = reason;
        }
    }
}

internal static class MapFeedRealTerrain
{
    public static string Root => Environment.GetEnvironmentVariable(MapFeedRealTerrainFactAttribute.Variable)!;

    public static string? SkipReason()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "the map feed is a Windows named pipe";
        }

        string? dir = Environment.GetEnvironmentVariable(MapFeedRealTerrainFactAttribute.Variable);
        return string.IsNullOrWhiteSpace(dir) || !Directory.Exists(Path.Combine(dir, "maps"))
            ? $"{MapFeedRealTerrainFactAttribute.Variable} is not set to a terrain data root (maps/, vmaps/, mmaps/ from the 1.12.1 client)."
            : null;
    }
}

/// <summary>
/// The feed's queries over the real extracted terrain at the human start in Northshire (map 0): the answers can only be what they are when
/// the server really read the <c>.map</c>, <c>vmaps</c> and <c>mmaps</c> files there.
/// </summary>
public sealed class MapFeedRealTerrainTests
{
    private static readonly Vector3 Start = new(FeedWorld.StartX, FeedWorld.StartY, FeedWorld.StartZ);

    /// <summary>A point a few yards east of the start, on the open ground in front of Northshire Abbey.</summary>
    private static readonly Vector3 East = new(FeedWorld.StartX + 8f, FeedWorld.StartY, FeedWorld.StartZ);

    private static async Task<(FeedWorld World, FeedClient Client)> StartAsync()
    {
        FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { DataRoot = MapFeedRealTerrain.Root, Terrain = false });
        await world.AddPlayerAsync();
        await world.Host.WaitForWorldAsync(
            () => world.Host.World.FindMap(0, 0) is { } map
                  && map.Grids.IsGridLoaded(GridDefines.ComputeGridCoord(Start.X, Start.Y))
                  && TerrainTile.TileOf(Start.X, Start.Y) is { } tile
                  && map.Terrain.IsTileLoaded(tile.X, tile.Y),
            "the grid and terrain tile under the human start");
        return (world, await world.ConnectAsync());
    }

    private static void AssertOk(JsonElement result, long id)
    {
        Assert.Equal("result", result.Op());
        Assert.Equal(id, result.GetProperty("id").GetInt64());
        Assert.True(result.GetProperty("ok").GetBoolean(), result.GetRawText());
    }

    [MapFeedRealTerrainFact]
    public async Task Height_AtTheHumanStart_IsTheExtractedTilesHeight_JustBelowTheStartPosition()
    {
        (FeedWorld world, FeedClient client) = await StartAsync();
        await using FeedWorld owned = world;

        JsonElement result = await client.QueryAsync(1, "height", 0, Start);

        AssertOk(result, 1);
        float terrain = result.GetProperty("terrain").GetSingle();

        // Independent of the feed: the tile file parsed here, and the start position (playercreateinfo, race 1) standing on the ground.
        (int tx, int ty) = TerrainTile.TileOf(Start.X, Start.Y)!.Value;
        string file = Path.Combine(MapFeedRealTerrain.Root, "maps", TerrainTile.FileName(0, tx, ty));
        float fromFile = TerrainTile.Parse(File.ReadAllBytes(file)).GetHeight(Start.X, Start.Y);
        Assert.True(MathF.Abs(fromFile - terrain) <= 0.001f, $"feed {terrain}, file {fromFile}");
        Assert.InRange(terrain, FeedWorld.StartZ - 3f, FeedWorld.StartZ + 0.5f);
        Assert.True(result.GetProperty("data").GetBoolean(), "the vmaps of map 0 were found under the data root");
        Assert.InRange(result.GetProperty("floor").GetSingle(), FeedWorld.StartZ - 3f, FeedWorld.StartZ + 0.5f);
    }

    [MapFeedRealTerrainFact]
    public async Task LineOfSightPathAndUnderMap_AtTheHumanStart_AreAnsweredFromTheRealData()
    {
        (FeedWorld world, FeedClient client) = await StartAsync();
        await using FeedWorld owned = world;
        Vector3 eye = new(0f, 0f, 2f);

        JsonElement los = await client.QueryAsync(2, "los", 0, Start + eye, East + eye);
        AssertOk(los, 2);
        Assert.True(los.GetProperty("data").GetBoolean(), "model data for map 0 is loaded");
        Assert.True(los.GetProperty("clear").GetBoolean(), "eight yards of open ground in front of the abbey");

        JsonElement path = await client.QueryAsync(3, "path", 0, Start, East);
        AssertOk(path, 3);
        Assert.True(path.GetProperty("data").GetBoolean(), "the navigation mesh of map 0 is loaded");
        Assert.True(path.GetProperty("hasPath").GetBoolean(), path.GetRawText());
        Vector3[] points = path.GetProperty("pts").ToVectors();
        Assert.True(points.Length >= 2);
        Assert.True(Vector2.Distance(new Vector2(East.X, East.Y), new Vector2(points[^1].X, points[^1].Y)) < 2f, path.GetRawText());
        Assert.True(path.GetProperty("length").GetSingle() >= Vector2.Distance(new Vector2(Start.X, Start.Y), new Vector2(East.X, East.Y)) - 2f);

        JsonElement under = await client.QueryAsync(4, "undermap", 0, Start);
        AssertOk(under, 4);
        Assert.Equal("ok", under.GetProperty("verdict").GetString());
    }
}
