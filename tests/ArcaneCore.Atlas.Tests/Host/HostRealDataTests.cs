using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Host;
using ArcaneCore.Game.Maps.Terrain;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Atlas.Tests.Host;

/// <summary>Runs only when <c>ARCANECORE_TEST_TERRAIN_DIR</c> names a server-extracted terrain root; otherwise a visible skip.</summary>
public sealed class HostRealTerrainFactAttribute : FactAttribute
{
    public HostRealTerrainFactAttribute()
    {
        if (HostRealTerrain.SkipReason() is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>The theory form of <see cref="HostRealTerrainFactAttribute"/>.</summary>
public sealed class HostRealTerrainTheoryAttribute : TheoryAttribute
{
    public HostRealTerrainTheoryAttribute()
    {
        if (HostRealTerrain.SkipReason() is { } reason)
        {
            Skip = reason;
        }
    }
}

internal static class HostRealTerrain
{
    public static string Root => Environment.GetEnvironmentVariable(AtlasPaths.TerrainEnvironmentVariable)!;

    public static string? SkipReason()
    {
        string? dir = Environment.GetEnvironmentVariable(AtlasPaths.TerrainEnvironmentVariable);
        return string.IsNullOrWhiteSpace(dir) || !Directory.Exists(Path.Combine(dir, "maps"))
            ? $"{AtlasPaths.TerrainEnvironmentVariable} is not set to a terrain data root (maps/, vmaps/, mmaps/ from the 1.12.1 client)."
            : null;
    }
}

/// <summary>
/// The built-in bookmarks against the real extracted terrain, read by the server's own reader (ArcaneCore.Game
/// <see cref="TerrainManager"/>): each one lies on a tile that has terrain, the open-ground ones stand on it, and the
/// underground ones are below it. The coordinates come from the repository files the bookmarks cite, not from these files.
/// </summary>
public sealed class HostRealDataTests(ITestOutputHelper output)
{
    /// <summary>Bookmarks taken from start positions on open ground (the source test holds them to 2.5 yd of the ground).</summary>
    public static TheoryData<string> OpenGround => new()
    {
        "northshire-start",
        "coldridge-valley",
        "valley-of-trials",
        "shadowglen",
        "camp-narache",
    };

    [HostRealTerrainFact]
    public void EveryBuiltInBookmark_LiesOnATileWithExtractedTerrain()
    {
        string maps = Path.Combine(HostRealTerrain.Root, "maps");
        foreach (Bookmark bookmark in BuiltInBookmarks.All)
        {
            (int tx, int ty) = TerrainTile.TileOf((float)bookmark.Target.X, (float)bookmark.Target.Y)!.Value;
            string file = Path.Combine(maps, TerrainTile.FileName(bookmark.Map, tx, ty));
            Assert.True(File.Exists(file), $"{bookmark.Name}: {file} is missing");
            TerrainTile tile = TerrainTile.Parse(File.ReadAllBytes(file));
            Assert.True(tile.HasData, $"{bookmark.Name}: {file} has no terrain data");
            output.WriteLine($"{bookmark.Name}: map {bookmark.Map} tile ({tx}, {ty}) {Path.GetFileName(file)}");
        }
    }

    [HostRealTerrainTheory]
    [MemberData(nameof(OpenGround))]
    public void AnOpenGroundBookmark_StandsOnTheExtractedGround(string name)
    {
        Bookmark bookmark = Assert.Single(BuiltInBookmarks.All, b => b.Name == name);
        var terrain = new TerrainManager(HostRealTerrain.Root);

        float z = (float)bookmark.Target.Z;
        float ground = terrain.For(bookmark.Map).GetHeight((float)bookmark.Target.X, (float)bookmark.Target.Y, z);
        output.WriteLine($"{name}: bookmark z {z}, ground {ground}");

        Assert.Equal(1, terrain.FilesLoaded);
        Assert.InRange(ground, z - 2.5f, z + 2.5f);
    }

    [HostRealTerrainTheory]
    [InlineData("deathknell-crypt")]
    [InlineData("undercity")]
    public void AnUndergroundBookmark_IsWellBelowTheTerrainSurface(string name)
    {
        Bookmark bookmark = Assert.Single(BuiltInBookmarks.All, b => b.Name == name);
        var terrain = new TerrainManager(HostRealTerrain.Root);

        float z = (float)bookmark.Target.Z;
        float surface = terrain.For(bookmark.Map).GetHeight((float)bookmark.Target.X, (float)bookmark.Target.Y, z);
        output.WriteLine($"{name}: bookmark z {z}, terrain surface {surface}");

        Assert.Equal(1, terrain.FilesLoaded);
        Assert.True(surface > z + 10, $"the terrain surface ({surface}) is well above {name} ({z})");
    }

    [HostRealTerrainFact]
    public async Task Status_OverTheRealDataDirectories_ReportsThemWithoutPathProblems()
    {
        AtlasPaths paths = AtlasPaths.FromEnvironment();
        await using HostRig rig = HostRig.Create(paths: paths);

        JsonNode status = await rig.DataAsync("status");

        Assert.Equal(paths.TerrainData, status.Str("paths.terrainData"));
        Assert.Empty(paths.Problems());
        Assert.DoesNotContain(status.At("problems")!.AsArray(), p => p!.GetValue<string>().Contains(paths.TerrainData!, StringComparison.Ordinal));
    }
}
