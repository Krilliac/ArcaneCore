using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Tests.Locomotion;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// <see cref="MapCollision.ClassifyUnderMap"/> and the observer that uses it. The decision table runs on a real one-tile terrain (ground at
/// <see cref="HazardKit.Ground"/>) with a scripted model service; the same query on a real extraction is checked at the bottom (gated on
/// <c>ARCANECORE_TEST_TERRAIN_DIR</c>).
/// </summary>
public sealed class UnderMapClassificationTests(ITestOutputHelper output)
{
    private const float X = HazardKit.X;
    private const float Y = HazardKit.Y;
    private const float Ground = HazardKit.Ground;

    /// <summary>A model service with scripted answers (floor below, surface above, WMO area) and a scripted tile coverage.</summary>
    private sealed class ScriptedModels(ModelCoverage coverage) : ILineOfSight, IModelCoverage
    {
        public bool Area { get; init; }

        public float? FloorBelow { get; init; }

        public float? SurfaceAbove { get; init; }

        public List<float> SearchDistances { get; } = [];

        public bool Enabled => true;

        public ModelCoverage CoverageAt(uint mapId, int tileX, int tileY) => coverage;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            return false;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance)
        {
            SearchDistances.Add(maxSearchDistance);
            return maxSearchDistance >= 0 ? FloorBelow : SurfaceAbove;
        }

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        {
            info = default;
            return Area;
        }
    }

    private static UnderMapVerdict Classify(ILineOfSight? models, float z, float minDepth = 0f)
    {
        using var kit = new HazardKit(liquid: null);
        if (models is not null)
        {
            WorldCollision.Of(kit.Kit.World).Install(lineOfSight: models);
        }

        return kit.Map.Collision.ClassifyUnderMap(X, Y, z, minDepth);
    }

    [Fact]
    public void NoModelService_TheTerrainComparisonDecides()
    {
        UnderMapVerdict under = Classify(null, Ground - 30f, minDepth: 15f);
        Assert.Equal(UnderMapKind.UnderMap, under.Kind);
        Assert.Equal(30f, under.DepthYards, 0.01f);

        Assert.Equal(UnderMapKind.Ok, Classify(null, Ground - 15f, minDepth: 15f).Kind); // exactly at the margin
        Assert.Equal(UnderMapKind.Ok, Classify(null, Ground + 5f).Kind);                 // above the ground
    }

    [Fact]
    public void NoTerrainTile_IsUnknown_AndNonFinitePointsAreUnknown()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        MapCollision collision = world.GetMap(0).Collision;

        Assert.Equal(UnderMapKind.Unknown, collision.ClassifyUnderMap(X, Y, -100f).Kind);
        Assert.Equal(UnderMapKind.Unknown, collision.ClassifyUnderMap(float.NaN, Y, 0f).Kind);
        Assert.Equal(UnderMapKind.Unknown, collision.ClassifyUnderMap(X, Y, float.PositiveInfinity).Kind);
    }

    [Fact]
    public void ATiledModelTreeWhoseTileIsNotLoaded_IsUnknown()
    {
        Assert.Equal(UnderMapKind.Unknown, Classify(new ScriptedModels(ModelCoverage.TileNotLoaded), Ground - 100f).Kind);
    }

    [Fact]
    public void AMapWithoutAModelTree_FallsBackToTheTerrain()
    {
        UnderMapVerdict verdict = Classify(new ScriptedModels(ModelCoverage.None) { Area = true, FloorBelow = 0f }, Ground - 100f);
        Assert.Equal(UnderMapKind.UnderMap, verdict.Kind);
    }

    [Fact]
    public void InsideAWmoGroup_IsOk()
        => Assert.Equal(UnderMapKind.Ok, Classify(new ScriptedModels(ModelCoverage.Loaded) { Area = true }, Ground - 100f).Kind);

    [Fact]
    public void AModelFloorBelow_IsOk_SearchedDownSixtyYards()
    {
        var models = new ScriptedModels(ModelCoverage.Loaded) { FloorBelow = Ground - 105f };
        Assert.Equal(UnderMapKind.Ok, Classify(models, Ground - 100f).Kind);
        Assert.Contains(MapCollision.UnderMapFloorSearch, models.SearchDistances);
    }

    [Fact]
    public void AModelSurfaceJustAbove_IsOk_SearchedUpwardFiveYards()
    {
        var models = new ScriptedModels(ModelCoverage.Loaded) { SurfaceAbove = Ground - 98f };
        Assert.Equal(UnderMapKind.Ok, Classify(models, Ground - 100f).Kind);
        Assert.Contains(-MapCollision.UnderMapCeilingSearch, models.SearchDistances);
    }

    [Fact]
    public void LoadedModelsThatExplainNothing_ConvictWithTheRawTerrainDepth()
    {
        UnderMapVerdict verdict = Classify(new ScriptedModels(ModelCoverage.Loaded), Ground - 100f);
        Assert.Equal(UnderMapKind.UnderMap, verdict.Kind);
        Assert.Equal(100f, verdict.DepthYards, 0.01f);
    }

    [Fact]
    public void TheMarginShortCircuits_BeforeAnyModelIsAsked()
    {
        var models = new ScriptedModels(ModelCoverage.Loaded);
        Assert.Equal(UnderMapKind.Ok, Classify(models, Ground - 10f, minDepth: 15f).Kind);
        Assert.Empty(models.SearchDistances);
    }

    // ---- the observer ---------------------------------------------------------------------------------------------------------

    private static int ObserverReports(ILineOfSight models, float z)
    {
        using var kit = new HazardKit(liquid: null);
        WorldCollision.Of(kit.Kit.World).Install(lineOfSight: models);
        Player player = kit.AddPlayer(4_105_050, 20f, out _);
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == "move/under-map" && n.Subject.Contains("(guid 4105050)", StringComparison.Ordinal));

        MovementInfo previous = player.Movement;
        var incoming = new MovementInfo { Flags = MovementFlags.None, Time = 1, X = X, Y = Y, Z = z };
        var context = new MovementObserverContext(player, kit.Kit.World, WorldOpcode.MsgMoveHeartbeat);
        MovementObservers.Before(context, in previous, ref incoming);
        player.ApplyClientMovement(incoming, 1);
        MovementObservers.After(context, in previous);
        return capture.Notices.Count;
    }

    [Fact]
    public void TheObserver_ReportsOnlyWhenTheModelsDoNotExplainThePosition()
    {
        float deep = Ground - UndermapObserver.UnderTerrainThresholdYards - 10f;
        Assert.Equal(1, ObserverReports(new ScriptedModels(ModelCoverage.Loaded), deep));
        Assert.Equal(0, ObserverReports(new ScriptedModels(ModelCoverage.Loaded) { Area = true }, deep));
        Assert.Equal(0, ObserverReports(new ScriptedModels(ModelCoverage.Loaded) { FloorBelow = deep - 1f }, deep));
        Assert.Equal(0, ObserverReports(new ScriptedModels(ModelCoverage.TileNotLoaded), deep));
        Assert.Equal(0, ObserverReports(new ScriptedModels(ModelCoverage.Loaded), Ground - UndermapObserver.UnderTerrainThresholdYards + 1f));
    }

    // ---- real data ------------------------------------------------------------------------------------------------------------

    private static string Root => Environment.GetEnvironmentVariable(RealTerrainFactAttribute.Variable)!;

    private static MapCollision RealMap(uint mapId, float x, float y, bool loadVmapTile)
    {
        var vmaps = new VMapManager(Path.Combine(Root, "vmaps"));
        WorldRuntime world = TestWorld.CreateRuntime();
        world.Options.Maps.DataDirectory = Root;
        WorldCollision.Of(world).Install(lineOfSight: vmaps);
        if (loadVmapTile)
        {
            (int tx, int ty) = TerrainTile.TileOf(x, y)!.Value;
            Assert.True(vmaps.LoadTile(mapId, tx, ty));
        }
        else
        {
            Assert.NotNull(vmaps.GetTree(mapId));
        }

        return world.GetMap(mapId).Collision;
    }

    [RealTerrainTheory]
    [MemberData(nameof(UnderMapRealTerrainTests.NotUnderTheMap), MemberType = typeof(UnderMapRealTerrainTests))]
    public void CavesAndCities_ClassifyAsOk(string name, uint mapId, float x, float y, float z)
    {
        MapCollision collision = RealMap(mapId, x, y, loadVmapTile: true);
        float terrain = collision.Map.GetHeight(x, y, z);
        UnderMapVerdict verdict = collision.ClassifyUnderMap(x, y, z);
        output.WriteLine($"{name}: z {z}, terrain {terrain}, verdict {verdict}");

        Assert.True(terrain - z > 5f, "the point lies under the terrain surface, so only the models can explain it");
        Assert.Equal(UnderMapKind.Ok, verdict.Kind);
    }

    [RealTerrainFact]
    public void FarBelowAnOpenField_IsUnderTheMap_WithTheTerrainDepth()
    {
        // Northshire start (classic-db playercreateinfo): open ground, 100 yd below it nothing is built.
        const float x = -8949.95f, y = -132.493f, z = 83.5312f;
        MapCollision collision = RealMap(0, x, y, loadVmapTile: true);
        float terrain = collision.Map.GetHeight(x, y, z);

        UnderMapVerdict verdict = collision.ClassifyUnderMap(x, y, z - 100f);
        output.WriteLine($"terrain {terrain}, verdict {verdict}");

        Assert.Equal(UnderMapKind.UnderMap, verdict.Kind);
        Assert.Equal(terrain - (z - 100f), verdict.DepthYards, 0.01f);
        Assert.Equal(UnderMapKind.Ok, collision.ClassifyUnderMap(x, y, z).Kind);
    }

    [RealTerrainFact]
    public void AnUnloadedVmapTile_IsUnknown()
    {
        const float x = -8949.95f, y = -132.493f, z = 83.5312f;
        MapCollision collision = RealMap(0, x, y, loadVmapTile: false);

        Assert.Equal(UnderMapKind.Unknown, collision.ClassifyUnderMap(x, y, z - 100f).Kind);
    }
}
