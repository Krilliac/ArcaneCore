using System.Numerics;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Tests.GridTerrain;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.City;

/// <summary>
/// <see cref="PlayerbotCapitalLocator.Locate"/> over a synthetic terrain tile (map 0, tile 32/32: flat ground at 20, every cell flagged 1000,
/// so the terrain answer is zone 100, area 5000) and a fake model service that finds one WMO group everywhere. Unproven until the integration gate.
/// </summary>
public sealed class PlayerbotCapitalLocatorTests : IDisposable
{
    private const uint TerrainZone = 100;
    private const uint TerrainArea = 5000;
    private const uint CityArea = 1537;
    private const float X = -100f;
    private const float Y = -100f;

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "arcanecore-capital-locator-" + Guid.NewGuid().ToString("N"));
    private readonly TerrainManager _terrain;
    private readonly AreaTable _areas;

    public PlayerbotCapitalLocatorTests()
    {
        string maps = Directory.CreateDirectory(Path.Combine(_dataDir, "maps")).FullName;
        File.WriteAllBytes(Path.Combine(maps, TerrainTile.FileName(0, 32, 32)), new MapFileBuilder
        {
            GridHeight = 20f,
            AreaFlags = [.. Enumerable.Repeat((ushort)1000, 256)],
        }.Build());
        var registry = new MapRegistry([new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", "")]);
        _areas = new AreaTable(
        [
            new AreaTemplate(TerrainZone, 0, 0, 0, 0, 1, "Terrain Zone", 0, 0),
            new AreaTemplate(TerrainArea, 0, TerrainZone, 1000, 0, 1, "Terrain Area", 0, 0),
            new AreaTemplate(CityArea, 0, 0, 2000, (uint)AreaFlags.Capital, 1, "Ironforge", 0, 0),
        ], registry);
        _terrain = new TerrainManager(_dataDir) { Areas = _areas };
    }

    public void Dispose() => Directory.Delete(_dataDir, recursive: true);

    /// <summary>A model service that finds <see cref="Group"/> at every position and records the heights it was asked from.</summary>
    private sealed class FakeModels(ModelAreaInfo group) : ILineOfSight
    {
        public List<float> AskedZ { get; } = [];

        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            return false;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => null;

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        {
            AskedZ.Add(z);
            info = group;
            return true;
        }
    }

    // root 100, name set 0, group 5; the group floor is at 0, the ground at 20 above it.
    private static ModelAreaInfo Group(int groupId = 5) => new(0, 0, 100, groupId, 0f);

    private static WmoAreaTable Rows(uint areaId = CityArea) => new([new WmoAreaTemplate(1, 100, 0, 5, areaId, 0)]);

    private PlayerbotCapitalLocator Locator(WmoAreaTable rows, ILineOfSight? models, List<(uint, int, int)>? loaded = null, List<(uint, int, int)>? unloaded = null)
        => new(_terrain, _areas, rows, models,
            loaded is null ? null : (map, x, y) => { loaded.Add((map, x, y)); return true; },
            unloaded is null ? null : (map, x, y) => unloaded.Add((map, x, y)));

    [Fact]
    public void Locate_WithoutModels_IsTheTerrainsArea()
    {
        var locator = Locator(Rows(), null);
        Assert.False(locator.HasWmoAreas);
        Assert.Equal((TerrainZone, TerrainArea), locator.Locate(0, X, Y, 5f));
    }

    [Fact]
    public void Locate_AMissingTile_IsUnknown_NotTheMapsLinkedZone()
    {
        var locator = Locator(WmoAreaTable.Empty, null);
        Assert.Equal((0u, 0u), locator.Locate(0, -600f, -600f, 5f)); // tile (33, 33) has no file
    }

    [Fact]
    public void Locate_AGroupWithAnAreaRow_WinsOverTheTerrain_FoundFromOneYardAbove()
    {
        var models = new FakeModels(Group());
        var locator = Locator(Rows(), models);
        Assert.True(locator.HasWmoAreas);
        Assert.Equal((CityArea, CityArea), locator.Locate(0, X, Y, 5f)); // the city is a zone: it is its own zone
        Assert.Equal([6f], models.AskedZ);
    }

    [Fact]
    public void Locate_AGroupUnderTerrainThatRisesToThePoint_IsRejected_TheSpawnIsOnTheGroundOverTheBuilding()
    {
        var models = new FakeModels(Group());
        var locator = Locator(Rows(), models);
        // On the ground (20) over a group whose floor is at 0: raised 21, 21 + 2 > 20 and 20 > 0, so the group does not count.
        Assert.Equal((TerrainZone, TerrainArea), locator.Locate(0, X, Y, 20f));
        // Below the ground (the Undercity case): raised 6, 6 + 2 > 20 is false, so the group counts.
        Assert.Equal((CityArea, CityArea), locator.Locate(0, X, Y, 5f));
    }

    [Fact]
    public void Locate_WmoRowsAreExact_NoFallbackToAnotherGroupOrNameSet()
    {
        var wrongGroup = Locator(Rows(), new FakeModels(Group(groupId: 6)));
        Assert.Equal((TerrainZone, TerrainArea), wrongGroup.Locate(0, X, Y, 5f));
        var wrongNameSet = Locator(Rows(), new FakeModels(new ModelAreaInfo(0, 2, 100, 5, 0f)));
        Assert.Equal((TerrainZone, TerrainArea), wrongNameSet.Locate(0, X, Y, 5f));
    }

    [Fact]
    public void Locate_ARowWithNoArea_OrAnAreaNotInTheTable_FallsToTheTerrain()
    {
        Assert.Equal((TerrainZone, TerrainArea), Locator(Rows(areaId: 0), new FakeModels(Group())).Locate(0, X, Y, 5f));
        Assert.Equal((TerrainZone, TerrainArea), Locator(Rows(areaId: 9999), new FakeModels(Group())).Locate(0, X, Y, 5f));
    }

    [Fact]
    public void Locate_AFirstRowWithNoAreaStillShadowsTheSecond()
    {
        var rows = new WmoAreaTable([new WmoAreaTemplate(1, 100, 0, 5, 0, 0), new WmoAreaTemplate(2, 100, 0, 5, CityArea, 0)]);
        Assert.Equal((TerrainZone, TerrainArea), Locator(rows, new FakeModels(Group())).Locate(0, X, Y, 5f));
    }

    [Fact]
    public void Locate_ANonFiniteHeight_SkipsTheModels()
    {
        var models = new FakeModels(Group());
        Assert.Equal((TerrainZone, TerrainArea), Locator(Rows(), models).Locate(0, X, Y, float.NaN));
        Assert.Empty(models.AskedZ);
    }

    [Fact]
    public void Locate_LoadsEachModelTileOnce_AndKeepsAWindowOfSix()
    {
        var loaded = new List<(uint, int, int)>();
        var unloaded = new List<(uint, int, int)>();
        var locator = Locator(Rows(), new FakeModels(Group()), loaded, unloaded);

        locator.Locate(0, X, Y, 5f);
        locator.Locate(0, X - 1f, Y, 5f); // the same tile
        Assert.Equal([(0u, 32, 32)], loaded);

        for (int k = 1; k < 8; k++)
        {
            locator.Locate(0, X - (533.3333f * k), Y, 5f);
        }

        Assert.Equal(8, loaded.Select(l => l.Item2).Distinct().Count());
        Assert.Equal([(0u, 32, 32), (0u, 33, 32)], unloaded); // eight loaded, six kept: the two oldest left
    }
}
