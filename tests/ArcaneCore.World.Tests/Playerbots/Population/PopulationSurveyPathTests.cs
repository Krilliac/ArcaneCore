using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Tests.GridTerrain;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots.Population;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Population;

/// <summary>
/// The production path of the zone capacity survey (<see cref="PlayerbotPopulationFeature.SurfaceZoneOf"/>,
/// <see cref="PlayerbotPopulationFeature.SurveySurface"/>, <see cref="PlayerbotPopulationFeature.IndoorSurface(IReadOnlyList{CreatureSpawn}, Action{float, float}, Func{float, float, float, bool})"/>)
/// on a synthetic tile (map 0, tile 32/32: ground at 20, a different area flag in each of its 256 cells, every fifth flag without an area row).
/// </summary>
public sealed class PopulationSurveyPathTests : IDisposable
{
    private const uint Zone = 12;
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "arcanecore-survey-" + Guid.NewGuid().ToString("N"));
    private readonly TerrainManager _terrain;
    private readonly AreaTable _areas;

    public PopulationSurveyPathTests()
    {
        string maps = Directory.CreateDirectory(Path.Combine(_dataDir, "maps")).FullName;
        File.WriteAllBytes(Path.Combine(maps, TerrainTile.FileName(0, 32, 32)), new MapFileBuilder
        {
            GridHeight = 20f,
            AreaFlags = [.. Enumerable.Range(0, 256).Select(i => (ushort)(1000 + i))],
        }.Build());
        var registry = new MapRegistry([new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", "")]);
        _areas = new AreaTable(Enumerable.Range(0, 256).Where(i => i % 5 != 0).Select(i =>
            new AreaTemplate((uint)(5000 + i), 0, Zone, (uint)(1000 + i), 0, 58, "area " + i, 0, 0)), registry);
        _terrain = new TerrainManager(_dataDir) { Areas = _areas };
    }

    public void Dispose() => Directory.Delete(_dataDir, recursive: true);

    private static CreatureSpawn Spawn(uint guid, float x, float y, float z, uint entry = 1) => new() { Guid = guid, Entry = entry, MapId = 0, X = x, Y = y, Z = z };

    private static readonly Dictionary<uint, CreatureTemplate> Templates = new()
    {
        [1] = new() { Entry = 1, Name = "Ghoul", MinLevel = 58, MaxLevel = 59, Faction = 14 },
    };

    private static FactionTemplateCatalog Factions() => new(
    [
        new FactionTemplateRecord(1, 1, 0, OwnMask: 3, FriendlyMask: 2, HostileMask: 12),
        new FactionTemplateRecord(2, 2, 0, OwnMask: 5, FriendlyMask: 4, HostileMask: 10),
        new FactionTemplateRecord(14, 7, 0, OwnMask: 8, FriendlyMask: 0, HostileMask: 1),
    ]);

    private (float X, float Y)[] Points(bool withArea)
    {
        TerrainTile tile = _terrain.ReadTileDetached(0, 32, 32);
        var found = new List<(float, float)>();
        for (float x = -250; x < 250 && found.Count < 40; x += 37)
            for (float y = -250; y < 250 && found.Count < 40; y += 41)
                if (TerrainTile.TileOf(x, y) == (32, 32) && ((tile.GetAreaFlag(x, y) - 1000) % 5 != 0) == withArea) found.Add((x, y));
        Assert.True(found.Count >= 5, $"only {found.Count} points with area {withArea}");
        return [.. found];
    }

    [Fact]
    public void TheProductionZoneOf_DropsUndergroundSpawnsAndFlagsWithoutAnArea()
    {
        TerrainTile tile = _terrain.ReadTileDetached(0, 32, 32);
        Assert.Equal(20f, tile.GetHeight(0f, 0f), 0.01f);
        foreach ((float x, float y) in Points(withArea: true).Take(8))
        {
            Assert.Equal(Zone, PlayerbotPopulationFeature.SurfaceZoneOf(Spawn(1, x, y, 21f), tile, _areas)); // on the ground
            Assert.Equal(Zone, PlayerbotPopulationFeature.SurfaceZoneOf(Spawn(1, x, y, 12.5f), tile, _areas)); // under it, but within 8 yards
            Assert.Equal(0u, PlayerbotPopulationFeature.SurfaceZoneOf(Spawn(1, x, y, 11.9f), tile, _areas)); // underground: dropped
        }

        foreach ((float x, float y) in Points(withArea: false).Take(8))
            Assert.Equal(0u, PlayerbotPopulationFeature.SurfaceZoneOf(Spawn(1, x, y, 21f), tile, _areas)); // an area flag without an area row: dropped
    }

    [Fact]
    public async Task TheSurvey_ReadsTilesOffTheWorldThread_AndOnlyTheIndoorTestHops()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 0);
        var spawns = new List<CreatureSpawn>();
        uint guid = 1;
        foreach ((float x, float y) in Points(withArea: true).Take(6)) spawns.Add(Spawn(guid++, x, y, 21f)); // 6 on the surface
        foreach ((float x, float y) in Points(withArea: true).Skip(6).Take(3)) spawns.Add(Spawn(guid++, x, y, 5f)); // 3 underground
        foreach ((float x, float y) in Points(withArea: false).Take(4)) spawns.Add(Spawn(guid++, x, y, 21f)); // 4 in cells without an area

        int worldReadsBefore = await world.Host.World.InvokeAsync(() => TerrainManager.DetachedReadsOnThisThread);
        var probe = new PlayerbotPopulationFeature.SurfaceSurveyProbe();
        (PopulationSpawnCapacity capacity, int poolReads) = await Task.Run(() =>
        {
            int before = TerrainManager.DetachedReadsOnThisThread;
            PopulationSpawnCapacity result = PlayerbotPopulationFeature.SurveySurface(world.Host.World, spawns, _terrain, _areas, e => Templates.GetValueOrDefault(e),
                Factions(), new HashSet<uint>(), probe, CancellationToken.None);
            return (result, TerrainManager.DetachedReadsOnThisThread - before);
        });
        int worldReadsAfter = await world.Host.World.InvokeAsync(() => TerrainManager.DetachedReadsOnThisThread);

        Assert.Equal(6, capacity.Fitting(Zone, Team.Alliance, 58, 58));
        Assert.Equal(6, capacity.Fitting(Zone, Team.Horde, 58, 58));
        Assert.True(poolReads >= 1, "the background thread read the tile itself"); // not vacuous: the seam counts reads on the thread that makes them
        Assert.Equal(worldReadsBefore, worldReadsAfter); // the world thread made no detached read
        Assert.True(probe.Hops >= 1);
    }

    [Fact]
    public void TheIndoorTest_LoadsTheTileOnceBeforeAskingAboutAnySpawn_AtTheSpawnsHeightPlusOne()
    {
        var calls = new List<string>();
        CreatureSpawn[] batch = [Spawn(1, 10f, 20f, 3f), Spawn(2, 11f, 21f, 4f), Spawn(3, 12f, 22f, 5f)];
        bool[] answers = PlayerbotPopulationFeature.IndoorSurface(batch,
            (x, y) => calls.Add($"load {x},{y}"),
            (x, y, z) => { calls.Add($"outdoors {x},{y},{z}"); return z > 4f; });

        Assert.Equal(["load 10,20", "outdoors 10,20,4", "outdoors 11,21,5", "outdoors 12,22,6"], calls);
        Assert.Equal([false, true, true], answers);
        Assert.Empty(PlayerbotPopulationFeature.IndoorSurface([], (_, _) => Assert.Fail("loaded for nothing"), (_, _, _) => true));
    }

    [Fact]
    public void ABatch_NeverSpansATileBoundary()
    {
        // Find the first y above 0 where the tile row changes, and put spawns a few yards either side of it (and in the same column).
        float boundary = 0f;
        while (TerrainTile.TileOf(0f, boundary) == TerrainTile.TileOf(0f, boundary + 0.25f)) boundary += 0.25f;
        boundary += 0.25f;
        var spawns = new List<CreatureSpawn>();
        for (uint i = 0; i < 3; i++) spawns.Add(Spawn(i + 1, 0f, boundary - 5f - i, 21f));
        for (uint i = 0; i < 3; i++) spawns.Add(Spawn(i + 10, 0f, boundary + 5f + i, 21f));
        Assert.NotEqual(TerrainTile.TileOf(0f, boundary - 5f), TerrainTile.TileOf(0f, boundary + 5f));

        var batches = new List<CreatureSpawn[]>();
        PopulationCreatureSurvey.CountSurface(spawns, _ => Zone,
            batch =>
            {
                batches.Add([.. batch]);
                return [.. batch.Select(_ => true)];
            },
            e => Templates.GetValueOrDefault(e), Factions(), new HashSet<uint>(), CancellationToken.None);

        Assert.Equal([3, 3], batches.Select(b => b.Length));
        foreach (CreatureSpawn[] batch in batches)
            Assert.Single(batch.Select(s => TerrainTile.TileOf(s.X, s.Y)).Distinct());
    }
}
