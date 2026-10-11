using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Maps.Collision;
using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>Terrain checks at the four 2026-10-11 bot QA death-loop cells (#178, #195, #200, #210).</summary>
public sealed class PlayerbotDeathLocationTerrainTests(ITestOutputHelper output)
{
    [RealTerrainBotFact]
    public void ReportedDeathSpots_AreOnDryTerrain()
    {
        var maps = new TerrainManager(Environment.GetEnvironmentVariable(RealTerrainBotFactAttribute.Variable)!);
        TerrainInfo eastern = maps.For(0);
        (int Issue, float X, float Y, float Z)[] deaths =
        [
            (178, 101.1f, -184.9f, 127.3f),
            (195, -137.2f, -4192.0f, 119.6f),
            (200, -166.8f, -4215.0f, 121.2f),
            (210, -1476.0f, -2650.0f, 47.8f),
        ];
        foreach ((int issue, float x, float y, float z) in deaths)
        {
            float ground = eastern.GetHeight(x, y, z);
            LiquidStatus liquid = eastern.GetLiquidStatus(x, y, z, LiquidTypeFlags.AllLiquids, out LiquidData water);
            output.WriteLine($"#{issue}: reported z={z:F1}, map ground={ground:F1}, liquid={liquid}, liquid level={water.Level:F1}");
            Assert.True(float.IsFinite(ground) && ground != TerrainTile.InvalidHeightValue, $"#{issue}: no terrain height");
            Assert.Equal(LiquidStatus.NoWater, liquid);
        }
    }
}

public sealed partial class PlayerbotRealTerrainNavigationTests
{
    [RealTerrainBotFact]
    public async Task AlteracExit_UsesTheModelFloorBelowTheRawMapSurface()
    {
        await using Terrain terrain = await Terrain.StartAsync();
        var exit = new Vector3(101.1f, -184.9f, 127.3f);
        await terrain.PlaceAsync(exit);
        float floor = await terrain.Host.OnWorldAsync(() => terrain.Player.Map!.Collision.GetHeight(exit.X, exit.Y, exit.Z + 3f));
        output.WriteLine($"#178: reported z={exit.Z:F1}, collision floor={floor:F1}");
        Assert.InRange(Math.Abs(floor - exit.Z), 0f, 3f);
    }
}
