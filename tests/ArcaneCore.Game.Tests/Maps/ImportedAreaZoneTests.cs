using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Game.Tests.GridTerrain;
using ArcaneCore.Kernel.WorldData;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Game.Tests.GridTerrain;

/// <summary>World-map content and extracted terrain flags drive the default zone locator.</summary>
public sealed class ImportedAreaZoneTests : IDisposable
{
    private const float AreaPositionX = -110f;
    private const float AreaPositionY = -130f;
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "arcanecore-area-zone-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void WorldMaps_LoadsAreaContent_AndLocatorUsesMapSpecificTerrainFlags()
    {
        WriteTile(0, 32, 32, 125);
        WriteTile(1, 32, 32, 125); // no map-1 row: this is the documented cross-map fallback.
        WriteTile(1, 31, 32, 113);
        WriteTile(2, 32, 32, 999); // unknown flag on a map whose linked zone is zero.

        using var world = new WorldRuntime(
            new WorldRuntimeOptions { Maps = new MapOptions { DataDirectory = _dataDirectory }, AutosaveIntervalMs = 0 },
            new RecordingSaveQueue(), NullLogger<WorldRuntime>.Instance);
        WorldMaps maps = WorldMaps.Of(world);
        maps.Load(new MapContent(
            [
                new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
                new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", ""),
                new MapTemplate(2, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Unknown", ""),
            ],
            [
                new AreaTemplate(12, 0, 0, 126, 64, 1, "Synthetic zone", 0, 0),
                new AreaTemplate(9, 0, 12, 125, 64, 1, "Synthetic starting area", 0, 0),
                new AreaTemplate(113, 1, 0, 113, 0, 1, "Kalimdor Zone", 0, 0),
            ], [], [], []));

        var map0 = world.GetMap(0);
        var map1 = world.GetMap(1);
        var map2 = world.GetMap(2);
        Assert.Equal((12u, 9u), map0.GetZoneAndAreaId(AreaPositionX, AreaPositionY, 0));
        Assert.Equal((12u, 9u), map1.GetZoneAndAreaId(AreaPositionX, AreaPositionY, 0));
        Assert.Equal((113u, 113u), map1.GetZoneAndAreaId(100f, AreaPositionY, 0));
        Assert.Equal((0u, 0u), map2.GetZoneAndAreaId(AreaPositionX, AreaPositionY, 0));

        IZoneLocator locator = WorldStateHooks.For(world).Locator;
        Assert.Equal((12u, 9u), locator.Locate(map0, TestWorld.CreatePlayer(1, AreaPositionX, AreaPositionY, new FakeSession())));
        Assert.Equal((113u, 113u), locator.Locate(map1, TestWorld.CreatePlayer(2, 100f, AreaPositionY, new FakeSession(2), mapId: 1)));
    }

    [Fact]
    public void MapContentDefaultsRemainContinentOnly_WhenNoAreaOrTerrainMatches()
    {
        WriteTile(0, 32, 32, 777);
        using var world = new WorldRuntime(
            new WorldRuntimeOptions { Maps = new MapOptions { DataDirectory = _dataDirectory }, AutosaveIntervalMs = 0 },
            new RecordingSaveQueue(), NullLogger<WorldRuntime>.Instance);
        WorldMaps maps = WorldMaps.Of(world);
        maps.Load(MapContent.Empty);

        Assert.Contains(maps.Registry.All, map => map.Entry == 0);
        Assert.Contains(maps.Registry.All, map => map.Entry == 1);
        Assert.Equal((0u, 0u), world.GetMap(0).GetZoneAndAreaId(AreaPositionX, AreaPositionY, 0));
        Assert.Equal((0u, 0u), world.GetMap(1).GetZoneAndAreaId(AreaPositionX, AreaPositionY, 0));
    }

    [Fact]
    public void AreaParentsAreGlobalIds_WithoutAdmittingTheParentMap()
    {
        WriteTile(0, 32, 32, 125);
        using var world = new WorldRuntime(
            new WorldRuntimeOptions { Maps = new MapOptions { DataDirectory = _dataDirectory }, AutosaveIntervalMs = 0 },
            new RecordingSaveQueue(), NullLogger<WorldRuntime>.Instance);
        WorldMaps maps = WorldMaps.Of(world);
        maps.Load(new MapContent(
            [
                new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "continent", ""),
                new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "continent2", ""),
            ],
            [
                new AreaTemplate(22, 451, 0, 0, 0, 1, "global parent", 0, 0),
                new AreaTemplate(49, 0, 22, 125, 0, 1, "child area", 0, 0),
            ], [], [], []));

        Assert.Null(maps.Registry.Find(451));
        Assert.Equal(22u, maps.Areas.GetByAreaFlagAndMap(125, 0)!.ZoneId);
        Assert.Equal(22u, maps.Areas.GetById(49)!.ZoneId);
        Assert.Equal((22u, 49u), world.GetMap(0).GetZoneAndAreaId(AreaPositionX, AreaPositionY, 0));

        IZoneLocator locator = WorldStateHooks.For(world).Locator;
        Assert.Same(maps.Areas.GetById(22), locator.Find(22));
        Assert.Equal((22u, 49u), locator.Locate(world.GetMap(0),
            TestWorld.CreatePlayer(3, AreaPositionX, AreaPositionY, new FakeSession(), mapId: 0)));
    }

    private void WriteTile(uint mapId, int tileX, int tileY, ushort areaFlag)
    {
        string maps = Directory.CreateDirectory(Path.Combine(_dataDirectory, "maps")).FullName;
        File.WriteAllBytes(Path.Combine(maps, TerrainTile.FileName(mapId, tileX, tileY)), new MapFileBuilder { GridArea = areaFlag }.Build());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }
}
