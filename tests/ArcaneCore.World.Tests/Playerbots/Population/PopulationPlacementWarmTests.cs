using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Tests.GridTerrain;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Combat;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Population;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Population;

/// <summary>
/// The placement safety data (spawn index, liquid status and area level per graveyard) is built on a background task and handed to the
/// world thread as one immutable snapshot; the world thread never builds it and never reads a terrain tile for the check.
/// </summary>
public sealed class PopulationPlacementWarmTests
{
    private const string WaitingReason = "waiting for the placement safety data";

    private static readonly FactionTemplateCatalog Factions = new([
        new FactionTemplateRecord(1, 1, 72, 3, 2, 12),
        new FactionTemplateRecord(14, 14, 0, 8, 0, 1),
    ]);

    [Fact]
    public async Task ColdStart_TheWorldThreadNeverBuildsThePlacementFacts_AndThePassDefers()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Feature.Provisioner.WarmGate = ct => gate.Task.WaitAsync(ct);
        world.Feature.TerrainLocator = _ => (PopulationTestHost.Elwynn, PopulationTestHost.Elwynn);

        // Locating the graveyards also runs in the background and holds creation first; wait it out, with the facts build still held.
        PopulationStatus status = await PassAsync(world);
        for (DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            status.Reason.Contains("graveyard terrain lookup", StringComparison.Ordinal) && DateTime.UtcNow < deadline;)
        {
            await Task.Delay(20);
            status = await PassAsync(world);
        }

        // The build is held open: the pass defers instead of building the data itself on the world thread.
        Assert.Contains(WaitingReason, status.Reason);
        Assert.Empty(world.Members.Snapshot);
        Assert.Empty(await world.Owners.LoadAllAsync());
        Assert.Equal(0, world.RunningCount);
        Assert.Equal((0, 0), await WorldThreadBuildsAsync(world));

        gate.SetResult();
        for (DateTime deadline = DateTime.UtcNow.AddSeconds(15); world.Members.Snapshot.Count == 0 && DateTime.UtcNow < deadline;)
        {
            await Task.Delay(20);
            await PassAsync(world);
        }

        Assert.Single(world.Members.Snapshot);
        await world.WaitForSettledAsync(1);
        Assert.Equal(1, world.Feature.Provisioner.FactsBuilds);
        Assert.Equal((0, 0), await WorldThreadBuildsAsync(world)); // creating and placing on the world thread built nothing
    }

    [Fact]
    public async Task Warm_PlacementDecisionsMatchTheOldInlineCheck()
    {
        var content = new CreatureContent(
            [new CreatureTemplate { Entry = 990001, Name = "test hostile", Faction = 14, MinLevel = 50, MaxLevel = 50,
                MinLevelHealth = 300, MaxLevelHealth = 300, DisplayIds = [49] }],
            [new CreatureSpawn { Guid = 990001, Entry = 990001, MapId = 0, X = -8880f, Y = -133f, Z = 81f }],
            [], [], []);
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 0,
            configureServices: services => services.AddSingleton(Factions));
        var near = new WorldSafeLoc(1, 0, -8913.23f, -133f, 81f, 0f, "near");
        var far = new WorldSafeLoc(9901, 0, -8200f, -700f, 90f, 0f, "far");
        var elsewhere = new WorldSafeLoc(9902, 1, 1000f, 1000f, 10f, 0f, "other map");
        WorldSafeLoc[] sites = [near, far, elsewhere];
        AreaTable areas = await world.Host.World.InvokeAsync(() => WorldMaps.Of(world.Host.World).Terrain.Areas);

        PopulationPlacementFacts facts = PopulationPlacementFacts.Build(content, areas, null, sites, CancellationToken.None);
        var provisioner = new PopulationProvisioner(world.Host.WorldServices);
        var oldIndex = new PopulationSpawnIndex(content);

        // No terrain data: every site is dry at area level 0, which is what the old inline check saw without terrain.
        foreach (WorldSafeLoc site in sites)
            foreach (byte level in new byte[] { 1, 10, 60 })
                foreach (uint faction in new uint[] { 1, 14 })
                    Assert.Equal(PopulationPlacementSafety.IsSafe(site, level, faction, 0, oldIndex, Factions),
                        provisioner.IsSafe(facts, site, level, faction));

        // The world-thread check is lookups only: it builds no index and reads no tile there (the probes are per thread, read on the world thread).
        (int Indexes, int Reads) before = await WorldThreadBuildsAsync(world);
        await world.Host.World.InvokeAsync(() =>
        {
            foreach (WorldSafeLoc site in sites) provisioner.IsSafe(facts, site, 5, 1);
            return true;
        });
        Assert.Equal(before, await WorldThreadBuildsAsync(world));
        // The comparison is not vacuous: the hostile spawn makes the near site unsafe for a level one and the far one safe.
        Assert.False(provisioner.IsSafe(facts, near, 1, 1));
        Assert.True(provisioner.IsSafe(facts, far, 1, 1));
        Assert.True(facts.IsCurrentFor(content, areas));
    }

    [Fact]
    public async Task FactsBuiltForOtherContent_AreStale_AndProvisionAndRelocateRefuseWithoutAnAnomaly()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 0);
        PlayerbotOperationResult made = await world.Bots.CreateAsync("Staleone", 1, 1);
        Guid id = Assert.IsType<Guid>(made.BotId);
        Assert.True((await world.Bots.StartAsync(id.ToString())).Success);
        var spot = new WorldSafeLoc(9901, 0, -8200f, -700f, 90f, 0f, "far");
        AreaTable areas = await world.Host.World.InvokeAsync(() => WorldMaps.Of(world.Host.World).Terrain.Areas);

        // A distinct content object, as after a creature content reload between the plan and the provision.
        var reloaded = new CreatureContent([], [], [], [], []);
        PopulationPlacementFacts stale = PopulationPlacementFacts.Build(reloaded, areas, null, [spot], CancellationToken.None);
        Assert.False(stale.IsCurrentFor(CurrentContent(world), areas));
        Assert.False(stale.IsCurrentFor(reloaded, new AreaTable([], WorldMaps.Of(world.Host.World).Registry)));

        await world.Host.World.InvokeAsync(() =>
        {
            var provisioner = new PopulationProvisioner(world.Host.WorldServices);
            Assert.Equal("placement-data-stale", provisioner.Provision(id, 5, spot, stale));
            Assert.Equal("placement-data-stale", provisioner.Relocate(id, spot, stale));
            Assert.Equal((byte)1, world.Bots.FindSession(id)!.Player!.Level);
            return true;
        });
    }

    [Fact]
    public async Task WorldThreadProbes_CountABuildOnTheWorldThread_SoTheNoBuildAssertionsCanFail()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 0);
        (int Indexes, int Reads) before = await WorldThreadBuildsAsync(world);

        await world.Host.World.InvokeAsync(() =>
        {
            _ = new PopulationSpawnIndex(CreatureContent.Empty);
            _ = new TerrainManager(null).ReadTileDetached(0, 32, 32);
            return true;
        });

        Assert.Equal((before.Indexes + 1, before.Reads + 1), await WorldThreadBuildsAsync(world));
    }

    [Fact]
    public async Task Warm_WithTerrain_GivesTheLiquidStatusAndAreaLevelTheLiveLookupsGive()
    {
        string dataDir = Path.Combine(Path.GetTempPath(), "arcanecore-placement-" + Guid.NewGuid().ToString("N"));
        string maps = Directory.CreateDirectory(Path.Combine(dataDir, "maps")).FullName;
        try
        {
            // One tile (map 0, 32/32) with a water surface at 50, the ground at 20 and a different area flag in each of its 256 cells.
            File.WriteAllBytes(Path.Combine(maps, TerrainTile.FileName(0, 32, 32)), new MapFileBuilder
            {
                GridHeight = 20f,
                HasLiquid = true,
                LiquidGlobalFlags = (byte)LiquidTypeFlags.Water,
                LiquidGlobalEntry = 1,
                LiquidLevel = 50f,
                AreaFlags = [.. Enumerable.Range(0, 256).Select(i => (ushort)(1000 + i))],
            }.Build());
            var registry = new MapRegistry([new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", "")]);
            // Every fifth flag has no area row: level 0 there.
            var areas = new AreaTable(Enumerable.Range(0, 256).Where(i => i % 5 != 0).Select(i =>
                new AreaTemplate((uint)(5000 + i), 0, 12, (uint)(1000 + i), 0, (i % 9) + 1, "area " + i, 0, 0)), registry);
            var terrain = new TerrainManager(dataDir) { Areas = areas };
            Assert.True(terrain.Enabled);

            var sites = new List<WorldSafeLoc>();
            uint id = 9000;
            foreach ((float x, float y) in new[] { (-110f, -130f), (-300f, -200f), (-50f, -450f), (-420f, -20f), (-200f, -330f) })
                foreach (float z in new[] { 30f, 49f, 60f })
                    sites.Add(new WorldSafeLoc(id++, 0, x, y, z, 0f, "site " + id));
            sites.Add(new WorldSafeLoc(id++, 0, -700f, -130f, 40f, 0f, "no tile"));   // a tile that has no file
            sites.Add(new WorldSafeLoc(id++, 1, -110f, -130f, 40f, 0f, "other map")); // a map with no terrain at all

            int readsBefore = TerrainManager.DetachedReadsOnThisThread;
            PopulationPlacementFacts facts = PopulationPlacementFacts.Build(CreatureContent.Empty, areas, terrain, sites, CancellationToken.None);
            Assert.Equal(readsBefore + 3, TerrainManager.DetachedReadsOnThisThread); // one read per distinct tile: the file, the missing file, the other map

            var liquids = new HashSet<LiquidStatus>();
            var levels = new HashSet<int>();
            foreach (WorldSafeLoc site in sites)
            {
                PopulationPlacementSite got = facts.Sites[site];
                LiquidStatus liquid = terrain.For(site.MapId).GetLiquidStatus(site.X, site.Y, site.Z, LiquidTypeFlags.AllLiquids, out _);
                int level = PlayerbotAreaLevelLookup.FromTerrain(terrain, site.MapId).LevelAt(site.X, site.Y, site.Z);
                Assert.True((liquid, level) == (got.Liquid, got.AreaLevel), $"{site.Name} ({site.X}, {site.Y}, {site.Z}): live {liquid}/{level}, facts {got.Liquid}/{got.AreaLevel}");
                liquids.Add(liquid);
                levels.Add(level);
            }

            // Not vacuous: the sites span several liquid statuses and several area levels, including the unknown one.
            Assert.True(liquids.Count >= 3, string.Join(",", liquids));
            Assert.True(levels.Count >= 4, string.Join(",", levels));
            Assert.Contains(0, levels);
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    private static Task<(int Indexes, int Reads)> WorldThreadBuildsAsync(PopulationTestHost world)
        => world.Host.World.InvokeAsync(() => (PopulationSpawnIndex.BuiltOnThisThread, TerrainManager.DetachedReadsOnThisThread));

    private static CreatureContent CurrentContent(PopulationTestHost world)
        => world.Host.WorldServices.GetService<CreatureWorldFeature>()?.Content ?? CreatureContent.Empty;

    private static Task<PopulationStatus> PassAsync(PopulationTestHost world)
    {
        world.Ticks.Set(200, 10); // a pass needs a fresh tick window
        return world.PassAsync();
    }
}
