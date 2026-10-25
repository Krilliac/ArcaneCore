using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots.Population;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Population;

/// <summary>
/// The indoor hop of the zone capacity survey on the real vmaps (<c>ARCANECORE_TEST_TERRAIN_DIR</c>; reported Skipped without it): it
/// loads the model tiles under city and crypt spawns, and must leave the vmap model cache as it found it.
/// </summary>
public sealed class PopulationSurveyRealTerrainTests(ITestOutputHelper output) : RealTerrainNavigationTestBase(output)
{
    private static CreatureSpawn Spawn(uint guid, float x, float y, float z) => new() { Guid = guid, Entry = 1, MapId = 0, X = x, Y = y, Z = z };

    [RealTerrainBotFact]
    public async Task TheIndoorHops_LeaveTheVmapModelCacheAsTheyFoundIt()
    {
        await using Terrain terrain = await Terrain.StartAsync();
        // Spawns on tiles of Stormwind, Ironforge, the Deathknell crypt and Goldshire (each a tile of many WMOs), a few per tile.
        (float X, float Y, float Z)[] places = [(-8913f, 554f, 94f), (-8830f, 620f, 94f), (-4918f, -967f, 502f), (-4850f, -1000f, 502f),
            (1666f, 1666f, 135f), (1645f, 1665f, 132f), (-9460f, 32f, 57f), (-9440f, 60f, 57f)];
        CreatureSpawn[] spawns = [.. places.Select((p, i) => Spawn((uint)i + 1, p.X, p.Y, p.Z))];

        var probe = new PlayerbotPopulationFeature.SurfaceSurveyProbe();
        int hop = 0;
        bool[] outdoors = [];
        await Task.Run(() =>
        {
            var answers = new List<bool>();
            foreach (CreatureSpawn spawn in spawns) // one spawn per batch: each is a hop on its own tile
            {
                ++probe.Hops;
                answers.AddRange(terrain.Host.World.InvokeAsync(() => PlayerbotPopulationFeature.IndoorSurface(terrain.Host.World, [spawn], ++hop % 4 == 0, probe)).GetAwaiter().GetResult());
            }

            terrain.Host.World.InvokeAsync(() => PlayerbotPopulationFeature.IndoorSurface(terrain.Host.World, [], true, probe)).GetAwaiter().GetResult();
            outdoors = [.. answers];
        });

        output.WriteLine($"hops {probe.Hops}; vmap models cached {probe.ModelsBefore} before, {probe.ModelsAfter} after; {probe.Trimmed} dropped; outdoors {string.Join(",", outdoors)}");
        Assert.True(probe.ModelsBefore >= 0, "the world has vmaps");
        Assert.True(probe.Trimmed > 0, "the survey brought models in and dropped them");
        Assert.True(probe.ModelsAfter <= probe.ModelsBefore, $"models cached {probe.ModelsBefore} before, {probe.ModelsAfter} after");
    }
}
