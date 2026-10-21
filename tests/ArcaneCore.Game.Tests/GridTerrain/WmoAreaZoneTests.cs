using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.WorldData;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Game.Tests.GridTerrain;

/// <summary>
/// A position inside a WMO group takes the area of that group (vmangos <c>TerrainInfo::GetAreaFlag</c>, GridMap.cpp:913-945, over
/// <c>WMOAreaTable.dbc</c>), not the terrain cell above it: Undercity lies under Tirisfal Glades, so its Trade Quarter read zone 85
/// and PvP enforcement, the capital rest type and the weather were Tirisfal's. Synthetic terrain and a scripted model service; the
/// real extraction is in <see cref="Collision.WmoAreaRealTerrainTests"/>. The ids are the 1.12.1 ones: Tirisfal Glades 85 (flag 179),
/// Undercity 1497 (flag 685, a Horde capital: flags 0x138, team 4).
/// </summary>
public sealed class WmoAreaZoneTests : IDisposable
{
    // The live report: an Alliance bot at Undercity's Trade Quarter (map 0).
    private const float TradeQuarterX = 1608.86f, TradeQuarterY = 236.80f, TradeQuarterZ = -51.47f;
    private const uint Tirisfal = 85, Undercity = 1497;
    private const int UndercityRoot = 1500, UndercityNameSet = 3, TradeQuarterGroup = 77;
    private const float GroupFloor = -52.5f;

    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "arcanecore-wmo-area-" + Guid.NewGuid().ToString("N"));

    private static readonly AreaTemplate[] Areas =
    [
        new(Tirisfal, 0, 0, 179, 0x40, 1, "Tirisfal Glades", AreaTeams.Horde, 0),
        new(Undercity, 0, 0, 685, 0x138, 1, "Undercity", AreaTeams.Horde, 0),
    ];

    private static readonly MapTemplate[] Maps = [new(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", "")];

    private sealed class ScriptedModels : ILineOfSight
    {
        public Func<Vector3, ModelAreaInfo?> GroupAt { get; set; } = _ => null;

        public int Queries { get; private set; }

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
            Queries++;
            ModelAreaInfo? found = GroupAt(new Vector3(x, y, z));
            info = found.GetValueOrDefault();
            return found is not null;
        }
    }

    /// <summary>The model service finds the Trade Quarter group wherever it is asked about the Undercity footprint (under z = -40).</summary>
    private static ScriptedModels UndercityModels() => new()
    {
        GroupAt = p => p.Z < -40f
            ? new ModelAreaInfo(MogpFlags: 0x2000, AdtId: UndercityNameSet, RootId: UndercityRoot, GroupId: TradeQuarterGroup, GroundZ: GroupFloor)
            : null,
    };

    private WorldRuntime World(ILineOfSight? models, IEnumerable<WmoAreaTemplate>? wmoAreas, float gridHeight = float.NaN)
    {
        string maps = Directory.CreateDirectory(Path.Combine(_dataDirectory, "maps")).FullName;
        (int tx, int ty) = TerrainTile.TileOf(TradeQuarterX, TradeQuarterY)!.Value;
        var file = new MapFileBuilder { GridArea = 179 };
        if (!float.IsNaN(gridHeight))
        {
            file.GridHeight = gridHeight;
        }

        File.WriteAllBytes(Path.Combine(maps, TerrainTile.FileName(0, tx, ty)), file.Build());

        var world = new WorldRuntime(
            new WorldRuntimeOptions { Maps = new MapOptions { DataDirectory = _dataDirectory }, AutosaveIntervalMs = 0 },
            new RecordingSaveQueue(), NullLogger<WorldRuntime>.Instance);
        if (models is not null)
        {
            WorldCollision.Of(world).Install(models);
        }

        WorldMaps.Of(world).Load(new MapContent(Maps, Areas, [], [], []) { WmoAreas = [.. wmoAreas ?? []] });
        return world;
    }

    private static WmoAreaTemplate TradeQuarterRow(uint id = 1, uint area = Undercity)
        => new(id, UndercityRoot, UndercityNameSet, TradeQuarterGroup, area, 0);

    [Fact]
    public void TradeQuarter_IsZone1497_AndACapital()
    {
        using WorldRuntime world = World(UndercityModels(), [TradeQuarterRow()]);

        (uint zone, uint area) = world.GetMap(0).GetZoneAndAreaId(TradeQuarterX, TradeQuarterY, TradeQuarterZ);

        Assert.Equal((Undercity, Undercity), (zone, area));
        AreaTemplate entry = WorldMaps.Of(world).Areas.GetById(zone)!;
        Assert.NotEqual(0u, entry.Flags & (uint)AreaFlags.Capital);
        Assert.Equal(AreaTeams.Horde, entry.Team);
    }

    [Fact]
    public void TradeQuarter_AlliancePlayer_IsInAnEnforcedArea_OfTheHordeCapital()
    {
        using WorldRuntime world = World(UndercityModels(), [TradeQuarterRow()]);
        var map = world.GetMap(0);

        (uint zone, _) = map.GetZoneAndAreaId(TradeQuarterX, TradeQuarterY, TradeQuarterZ);
        AreaTemplate entry = WorldMaps.Of(world).Areas.GetById(zone)!;

        Assert.True(PvpAreaRules.IsEnforced(entry.Team, (AreaFlags)entry.Flags, Team.Alliance, PvpRealmMode.Normal, inBattleground: false));
    }

    [Fact]
    public void TirisfalSurface_StillResolvesTo85()
    {
        using WorldRuntime world = World(UndercityModels(), [TradeQuarterRow()]);

        // Same x, y; above the ceiling of the city: no WMO group there.
        Assert.Equal((Tirisfal, Tirisfal), world.GetMap(0).GetZoneAndAreaId(TradeQuarterX, TradeQuarterY, 70f));
    }

    [Fact]
    public void TheTerrainCellIsTheAnswer_WhenTheWorldHasNoWmoAreaRows_AndTheLookupDoesNotAskTheModels()
    {
        ScriptedModels models = UndercityModels();
        using WorldRuntime world = World(models, wmoAreas: null);

        Assert.Equal((Tirisfal, Tirisfal), world.GetMap(0).GetZoneAndAreaId(TradeQuarterX, TradeQuarterY, TradeQuarterZ));
        Assert.Equal(0, models.Queries);
    }

    [Fact]
    public void TheTerrainCellIsTheAnswer_WithoutAModelService()
    {
        using WorldRuntime world = World(models: null, [TradeQuarterRow()]);

        Assert.Equal((Tirisfal, Tirisfal), world.GetMap(0).GetZoneAndAreaId(TradeQuarterX, TradeQuarterY, TradeQuarterZ));
    }

    [Theory]
    [InlineData(0u, "the row has no area (AreaTableID 0)")]
    [InlineData(99999u, "the row's area is not in the area table")]
    public void AGroupWithoutAUsableArea_ReadsTheTerrainCell(uint area, string why)
    {
        using WorldRuntime world = World(UndercityModels(), [TradeQuarterRow(area: area)]);

        Assert.True((Tirisfal, Tirisfal) == world.GetMap(0).GetZoneAndAreaId(TradeQuarterX, TradeQuarterY, TradeQuarterZ), why);
    }

    [Fact]
    public void AGroupWithNoRow_ReadsTheTerrainCell_EvenWhenTheRootHasADefaultRow()
    {
        // vmangos looks the exact (root, name set, group) up and has no (root, name set, -1) fallback (DBCStores.cpp:479-485).
        using WorldRuntime world = World(UndercityModels(), [new WmoAreaTemplate(1, UndercityRoot, UndercityNameSet, -1, Undercity, 0)]);

        Assert.Equal((Tirisfal, Tirisfal), world.GetMap(0).GetZoneAndAreaId(TradeQuarterX, TradeQuarterY, TradeQuarterZ));
    }

    [Fact]
    public void ADuplicatedKey_TakesTheLowestRowId_EvenWhenThatRowHasNoArea()
    {
        // vmangos fills a std::map in row order with insert(), which keeps the first row of a key.
        var table = new WmoAreaTable([TradeQuarterRow(id: 9, area: Tirisfal), TradeQuarterRow(id: 4, area: Undercity), TradeQuarterRow(id: 6, area: 0)]);
        Assert.True(table.TryGetAreaId(UndercityRoot, UndercityNameSet, TradeQuarterGroup, out uint area));
        Assert.Equal(Undercity, area);

        var shadowed = new WmoAreaTable([TradeQuarterRow(id: 2, area: 0), TradeQuarterRow(id: 3, area: Undercity)]);
        Assert.False(shadowed.TryGetAreaId(UndercityRoot, UndercityNameSet, TradeQuarterGroup, out _));
        Assert.Equal(1, shadowed.Count);
    }

    [Fact]
    public void GroundOverABuilding_IsNotInsideIt()
    {
        // The terrain is at z = 100, the group floor far below: a point within 2 yards under the raised point is on the ground over the
        // building (vmangos TerrainInfo::GetAreaInfo, GridMap.cpp:897-906); one deeper down is inside it.
        ScriptedModels models = new() { GroupAt = _ => new ModelAreaInfo(0x2000, UndercityNameSet, UndercityRoot, TradeQuarterGroup, GroupFloor) };
        using WorldRuntime world = World(models, [TradeQuarterRow()], gridHeight: 100f);
        var map = world.GetMap(0);

        Assert.Equal((Tirisfal, Tirisfal), map.GetZoneAndAreaId(TradeQuarterX, TradeQuarterY, 98.5f));
        Assert.Equal((Undercity, Undercity), map.GetZoneAndAreaId(TradeQuarterX, TradeQuarterY, 20f));
    }

    [Fact]
    public void ModelsReplacedAfterTheWorldIsBuilt_AreUsedAtTheNextLookup()
    {
        using WorldRuntime world = World(models: null, [TradeQuarterRow()]);
        var map = world.GetMap(0);
        Assert.Equal((Tirisfal, Tirisfal), map.GetZoneAndAreaId(TradeQuarterX, TradeQuarterY, TradeQuarterZ));

        WorldCollision.Of(world).Install(UndercityModels());

        Assert.Equal((Undercity, Undercity), map.GetZoneAndAreaId(TradeQuarterX, TradeQuarterY, TradeQuarterZ));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }
}
