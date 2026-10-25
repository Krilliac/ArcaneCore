using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots.Population;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Population;

/// <summary>
/// The indoor hop of the zone capacity survey on the real vmaps (<c>ARCANECORE_TEST_TERRAIN_DIR</c>; reported Skipped without it): it
/// loads the model tiles under city and crypt spawns, answers indoors for a spawn inside a building, and releases the tiles it
/// loaded and the models it brought in, without touching a tile a grid holds.
/// </summary>
public sealed class PopulationSurveyRealTerrainTests(ITestOutputHelper output) : RealTerrainNavigationTestBase(output)
{
    private static CreatureSpawn Spawn(uint guid, float x, float y, float z) => new() { Guid = guid, Entry = 1, MapId = 0, X = x, Y = y, Z = z };

    // Known places (the bot navigation tests' live positions): William Pestle stands inside the Lion's Pride Inn (Goldshire), Undertaker
    // Mordo outside the Deathknell crypt, and the crypt stairs are inside it.
    private static readonly Vector3 Pestle = new(-9460.3f, 31.94f, 57.05f);
    private static readonly Vector3 Mordo = new(1678.99f, 1667.86f, 135.85f);
    private static readonly Vector3 CryptStairs = new(1645.4f, 1665.9f, 132.6f);

    [RealTerrainBotFact]
    public async Task TheIndoorHops_AnswerIndoors_AndLeaveTilesAndModelsAsTheyFoundThem()
    {
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(Mordo); // a grid holds the Deathknell tile now
        (float X, float Y, float Z)[] places =
        [
            (Pestle.X, Pestle.Y, Pestle.Z), (Mordo.X, Mordo.Y, Mordo.Z), (CryptStairs.X, CryptStairs.Y, CryptStairs.Z),
            (-8913f, 554f, 94f), (-8830f, 620f, 94f), (-4918f, -967f, 502f), (-4850f, -1000f, 502f),
        ];
        CreatureSpawn[] spawns = [.. places.Select((p, i) => Spawn((uint)i + 1, p.X, p.Y, p.Z))];
        var world = terrain.Host.World;
        VMapManager vmaps = await terrain.Host.OnWorldAsync(() => (VMapManager)world.GetMap(0).Collision.LineOfSight);

        (bool[] HeldBefore, int OnDemandBefore, HashSet<string> UsedBefore) before = await terrain.Host.OnWorldAsync(() =>
            (spawns.Select(s => vmaps.HoldsTile(0, s.X, s.Y)).ToArray(), vmaps.OnDemandTileCount, UsedModels(vmaps)));
        Assert.True(before.HeldBefore[1], "a grid holds the tile the bot stands on (the test would prove nothing otherwise)");
        Assert.True(before.HeldBefore.Count(h => !h) >= 3, "several surveyed tiles were not held before");

        var probe = new PlayerbotPopulationFeature.SurfaceSurveyProbe();
        int hop = 0;
        bool[] outdoors = await Task.Run(() =>
        {
            var answers = new List<bool>();
            foreach (CreatureSpawn spawn in spawns) // one spawn per batch: each is a hop of its own tile
            {
                ++probe.Hops;
                answers.AddRange(world.InvokeAsync(() => PlayerbotPopulationFeature.IndoorSurface(world, [spawn], ++hop % 4 == 0, probe)).GetAwaiter().GetResult());
            }

            world.InvokeAsync(() => PlayerbotPopulationFeature.IndoorSurface(world, [], true, probe)).GetAwaiter().GetResult();
            return answers.ToArray();
        });

        (bool[] HeldAfter, int OnDemandAfter, HashSet<string> UsedAfter) after = await terrain.Host.OnWorldAsync(() =>
            (spawns.Select(s => vmaps.HoldsTile(0, s.X, s.Y)).ToArray(), vmaps.OnDemandTileCount, UsedModels(vmaps)));
        output.WriteLine($"hops {probe.Hops}; vmap models cached {probe.ModelsBefore} before, {probe.ModelsAfter} after, {probe.Trimmed} dropped; "
            + $"on-demand tiles {before.OnDemandBefore} before, {after.OnDemandAfter} after; models in use {before.UsedBefore.Count} before, {after.UsedAfter.Count} after; "
            + $"outdoors {string.Join(",", outdoors)}");

        // Known answers: inside the inn and the crypt reads indoors, outside the crypt reads outdoors.
        Assert.False(outdoors[0], "William Pestle stands inside the Lion's Pride Inn");
        Assert.True(outdoors[1], "Undertaker Mordo stands outside the crypt");
        Assert.False(outdoors[2], "the crypt stairs are inside the crypt");

        // Tile state: every tile is held exactly as before (the held one still, the others released again), no on-demand tile is left over.
        Assert.Equal(before.HeldBefore, after.HeldAfter);
        Assert.Equal(before.OnDemandBefore, after.OnDemandAfter);
        Assert.True(after.HeldAfter[1]);

        // Models: the set a loaded tile uses is unchanged, and the cache holds nothing more than before.
        Assert.True(probe.ModelsBefore >= 0);
        Assert.True(probe.Trimmed > 0, "the survey brought models in and dropped them");
        Assert.True(before.UsedBefore.SetEquals(after.UsedAfter), "the models in use changed");
        Assert.True(probe.ModelsAfter <= probe.ModelsBefore, $"models cached {probe.ModelsBefore} before, {probe.ModelsAfter} after");
    }

    private static HashSet<string> UsedModels(VMapManager vmaps)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        vmaps.GetTree(0)!.CollectModelNames(used);
        return used;
    }
}
