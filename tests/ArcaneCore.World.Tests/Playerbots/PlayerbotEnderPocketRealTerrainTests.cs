using System.Numerics;
using ArcaneCore.World.Playerbots;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotEnderPocketRealTerrainTests : RealTerrainNavigationTestBase
{
    public PlayerbotEnderPocketRealTerrainTests(ITestOutputHelper output) : base(output) { }

    /// <summary>Bot QA #251/#261: prove Gaerolas's mesh is joined to the repeated Starbreeze stall point.</summary>
    [RealTerrainBotFact]
    public async Task FromStarbreezeStall_TheMeshRoutesToGaerolas()
    {
        Vector3 stall = new(9897.6f, 532.3f, 1304.2f);
        Vector3 ender = new(9845.15f, 440.93f, 1317.27f);
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(stall, 1);
        (bool planned, PlayerbotRoute? route) = await terrain.Host.OnWorldAsync(() =>
        {
            bool found = PlayerbotNavigation.TryPlanToward(terrain.Player, ender, new PlayerbotOptions(),
                out PlayerbotRoute? path, completedQuestReturn: true);
            return (found, path);
        });
        Assert.True(planned && route is not null, "no mesh route from the Starbreeze stall to Gaerolas");
        output.WriteLine($"mesh route {route!.Distance:F1} yards, {route.Points.Count} corners, end {route.Points[^1]}");
        Assert.True(Vector3.Distance(route.Points[^1], ender) <= 5f, "the mesh route ends outside hand-in range");
    }
}
