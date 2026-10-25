using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Playerbots.Population;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Population;

/// <summary>
/// The world-thread hop of the zone capacity survey (<c>PlayerbotPopulationFeature.IndoorSurface</c>) on a world whose vmaps are the
/// synthetic fixture: a tile held by a grid stays loaded, a tile nobody held is released again, the models the hop brought in are
/// trimmed, and a spawn inside a model reads indoors.
/// </summary>
public sealed class PopulationSurveyVmapTests : IDisposable
{
    private const int TileRow = 30;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arcanecore-survey-vmaps-" + Guid.NewGuid().ToString("N"));

    public PopulationSurveyVmapTests()
    {
        using var fixture = new VMapFixture();
        fixture.Place("wall", VMapFixture.Box(new Vector3(-1, -10, 0), new Vector3(1, 10, 10)), On(0));
        fixture.Place("box", VMapFixture.Box(new Vector3(-1, -10, 0), new Vector3(1, 10, 10)), On(1));
        fixture.Write(0);
        string vmaps = Directory.CreateDirectory(Path.Combine(_root, "vmaps")).FullName;
        Directory.CreateDirectory(Path.Combine(_root, "maps"));
        foreach (string file in Directory.GetFiles(fixture.Directory)) File.Copy(file, Path.Combine(vmaps, Path.GetFileName(file)));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static Vector3 On(int tileX) => new((32f - tileX - 0.5f) * 533.33333f, (32f - TileRow - 0.5f) * 533.33333f, 0f);

    private static CreatureSpawn Spawn(uint guid, Vector3 at) => new() { Guid = guid, Entry = 1, MapId = 0, X = at.X, Y = at.Y, Z = at.Z };

    [Fact]
    public async Task TheHop_ReleasesWhatItLoaded_KeepsAGridsTile_TrimsItsModels_AndReadsInsideAModelAsIndoors()
    {
        await using WorldTestHost host = WorldTestHost.Start(configure: o => o.Maps.DataDirectory = _root);
        var world = host.World;
        VMapManager vmaps = await host.OnWorldAsync(() => (VMapManager)world.GetMap(0).Collision.LineOfSight);
        await host.OnWorldAsync(() => ((ICollisionTileLifecycle)vmaps).OnTileLoaded(0, 0, TileRow)); // a grid holds the wall's tile

        CreatureSpawn insideWall = Spawn(1, On(0) + new Vector3(0, 0, 5)), besideWall = Spawn(2, On(0) + new Vector3(30, 0, 1));
        CreatureSpawn insideBox = Spawn(3, On(1) + new Vector3(0, 0, 5));
        var probe = new PlayerbotPopulationFeature.SurfaceSurveyProbe();

        bool[] wall = await host.OnWorldAsync(() => PlayerbotPopulationFeature.IndoorSurface(world, [insideWall, besideWall], false, probe));
        Assert.Equal([false, true], wall);
        Assert.True(await host.OnWorldAsync(() => vmaps.HoldsTile(0, insideWall.X, insideWall.Y)), "the grid's tile is still loaded");
        Assert.Equal(0, await host.OnWorldAsync(() => vmaps.OnDemandTileCount));

        bool[] box = await host.OnWorldAsync(() => PlayerbotPopulationFeature.IndoorSurface(world, [insideBox], true, probe));
        Assert.Equal([false], box);
        Assert.False(await host.OnWorldAsync(() => vmaps.HoldsTile(0, insideBox.X, insideBox.Y)), "the tile nobody held was released again");
        Assert.Equal(0, await host.OnWorldAsync(() => vmaps.OnDemandTileCount));
        Assert.Equal(1, probe.Trimmed); // the box; the wall is still used by the grid's tile
        Assert.Equal(1, await host.OnWorldAsync(() => vmaps.CachedModelCount));
        Assert.Equal(1, probe.ModelsAfter);
    }
}
