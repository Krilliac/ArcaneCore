using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Tests.Locomotion;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// <see cref="MapCollision.ClassifyUnderMap"/> and the observer that uses it. The decision table runs on a real one-tile terrain (ground at
/// <see cref="HazardTestKit.Ground"/>) with a scripted model service; the same query on a real extraction is checked at the bottom (gated on
/// <c>ARCANECORE_TEST_TERRAIN_DIR</c>).
/// </summary>
public sealed class UnderMapClassificationTests(ITestOutputHelper output)
{
    private const float X = HazardTestKit.X;
    private const float Y = HazardTestKit.Y;
    private const float Ground = HazardTestKit.Ground;

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
        using var kit = new HazardTestKit(liquid: null);
        if (models is not null)
        {
            WorldCollision.Of(kit.Kit.World).Install(lineOfSight: models);
        }

        return kit.Map.Collision.ClassifyUnderMap(X, Y, z, minDepth);
    }

    // Report grade (anomaly reports, creature and bot checks): terrain alone convicts when there are no models. The recovery grade that
    // moves a player is asserted below (RecoveryGrade_*) and never convicts on terrain alone.
    [Fact]
    public void NoModelService_TheTerrainComparisonDecides()
    {
        UnderMapVerdict under = Classify(null, Ground - 30f, minDepth: 15f);
        Assert.Equal(UnderMapKind.UnderMap, under.Kind);
        Assert.Equal(30f, under.DepthYards, 0.01f);

        Assert.Equal(UnderMapKind.Ok, Classify(null, Ground - 15f, minDepth: 15f).Kind); // exactly at the margin
        Assert.Equal(UnderMapKind.Ok, Classify(null, Ground + 5f).Kind);                 // above the ground
    }

    private static UnderMapVerdict ClassifyForRecovery(ILineOfSight? models, float z, float minDepth = 0f)
    {
        using var kit = new HazardTestKit(liquid: null);
        if (models is not null)
        {
            WorldCollision.Of(kit.Kit.World).Install(lineOfSight: models);
        }

        return kit.Map.Collision.ClassifyUnderMapForRecovery(X, Y, z, minDepth);
    }

    [Fact]
    public void RecoveryGrade_WithNoModelService_IsUnknown_NotATerrainOnlyConviction()
    {
        Assert.Equal(UnderMapKind.Unknown, ClassifyForRecovery(null, Ground - 120f, minDepth: 100f).Kind);
        Assert.Equal(UnderMapKind.Ok, ClassifyForRecovery(null, Ground + 5f).Kind); // above the ground is still Ok
    }

    [Theory]
    [InlineData(ModelCoverage.Unavailable)]
    [InlineData(ModelCoverage.TileNotLoaded)]
    public void RecoveryGrade_WithUnavailableOrUnloadedModels_IsUnknown(ModelCoverage coverage)
        => Assert.Equal(UnderMapKind.Unknown, ClassifyForRecovery(new ScriptedModels(coverage), Ground - 120f, minDepth: 100f).Kind);

    [Fact]
    public void RecoveryGrade_WithCoverageNone_IsUnknown_AndLoadedCoverageStillConvicts()
    {
        Assert.Equal(UnderMapKind.Unknown, ClassifyForRecovery(new ScriptedModels(ModelCoverage.None), Ground - 120f, minDepth: 100f).Kind);

        UnderMapVerdict loaded = ClassifyForRecovery(new ScriptedModels(ModelCoverage.Loaded), Ground - 120f, minDepth: 100f);
        Assert.Equal(UnderMapKind.UnderMap, loaded.Kind);
        Assert.Equal(120f, loaded.DepthYards, 0.01f);
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
    public void AModelSurfaceAboveAUnitInOpenSpace_DoesNotExcuseIt()
    {
        // Triangle hits are double-sided: the underside of a floor the unit fell through must not count as an explanation.
        var models = new ScriptedModels(ModelCoverage.Loaded) { SurfaceAbove = Ground - 96f };
        UnderMapVerdict verdict = Classify(models, Ground - 100f);

        Assert.Equal(UnderMapKind.UnderMap, verdict.Kind);
        Assert.DoesNotContain(models.SearchDistances, d => d < 0f); // no upward search at all
    }

    [Fact]
    public void ModelHeightsSwitchedOff_IsUnknown_NotATerrainOnlyConviction()
    {
        using var fixture = new VMapFixture();
        fixture.Place("box", VMapFixture.Box(new Vector3(-5, -5, 0), new Vector3(5, 5, 5)), new Vector3(1000f, 1000f, 50f));
        fixture.Write(0);
        var vmaps = new VMapManager(fixture.Directory, enableLineOfSight: true, enableHeight: false);

        Assert.Equal(UnderMapKind.Unknown, Classify(vmaps, Ground - 100f).Kind);
    }

    [Fact]
    public void ATileWithNoVmtileFile_IsLoadedAndEmptyOnceAttempted_SoTheTerrainDecides()
    {
        // The tree knows a box in another tile; the tile under (X, Y) has no vmtile file (ocean, coast, empty land).
        using var fixture = new VMapFixture();
        fixture.Place("box", VMapFixture.Box(new Vector3(-5, -5, 0), new Vector3(5, 5, 5)), new Vector3(1000f, 1000f, 50f));
        fixture.Write(0);
        var vmaps = new VMapManager(fixture.Directory);
        (int tx, int ty) = TerrainTile.TileOf(X, Y)!.Value;
        Assert.False(File.Exists(Path.Combine(fixture.Directory, VMapFormat.TileFileName(0, tx, ty))));

        using var kit = new HazardTestKit(liquid: null);
        WorldCollision.Of(kit.Kit.World).Install(lineOfSight: vmaps);
        MapCollision collision = kit.Map.Collision;

        Assert.Equal(ModelCoverage.TileNotLoaded, vmaps.CoverageAt(0, tx, ty));
        Assert.Equal(UnderMapKind.Unknown, collision.ClassifyUnderMap(X, Y, Ground - 100f).Kind); // never attempted

        Assert.False(vmaps.LoadTile(0, tx, ty)); // nothing to read
        Assert.False(vmaps.LoadTile(0, tx, ty)); // the same answer every time
        // Anti-cheat MapAntiCheatTerrain.HasModels asks exactly this: "a model tile was read". An empty tile is not one.
        Assert.False(vmaps.GetTree(0)!.IsTileLoaded(tx, ty));
        Assert.Equal(ModelCoverage.Loaded, vmaps.CoverageAt(0, tx, ty));
        UnderMapVerdict verdict = collision.ClassifyUnderMap(X, Y, Ground - 100f);
        Assert.Equal(UnderMapKind.UnderMap, verdict.Kind);
        Assert.Equal(100f, verdict.DepthYards, 0.01f);

        // An unload forgets the empty marker, so a reload tries the file again.
        vmaps.UnloadTile(0, tx, ty);
        Assert.Equal(ModelCoverage.TileNotLoaded, vmaps.CoverageAt(0, tx, ty));
    }

    // ---- the creature check with the real classifier (no stub) --------------------------------------------------------------

    private static int CreatureReports(ILineOfSight models, uint entry)
    {
        using var kit = new HazardTestKit(liquid: null);
        WorldCollision.Of(kit.Kit.World).Install(lineOfSight: models);
        CreatureContent content = CreatureTestKit.Content(
            [CreatureTestKit.Template(entry)], [CreatureTestKit.Spawn(1, entry, X, Y, Ground - 30f)]);
        var system = new CreatureMapSystem(kit.Map, content, random: new Random(1));
        kit.Map.AddUpdater(system);
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(
            n => n.Key == "creature/under-map" && n.Subject.StartsWith($"creature entry {entry} ", StringComparison.Ordinal));

        kit.AddPlayer(entry, 20f, out _);
        for (int i = 0; i < 30; i++)
        {
            kit.Tick(100);
        }

        Assert.Single(system.Creatures);
        return capture.Notices.Count;
    }

    [Fact]
    public void TheCreatureCheck_UsesTheRealClassifier_OverRealTerrainAndScriptedModels()
    {
        Assert.Equal(1, CreatureReports(new ScriptedModels(ModelCoverage.Loaded), 949201));                    // 30 yd under the ground, no model
        Assert.Equal(0, CreatureReports(new ScriptedModels(ModelCoverage.Loaded) { Area = true }, 949202));    // inside a WMO group
        Assert.Equal(0, CreatureReports(new ScriptedModels(ModelCoverage.Loaded) { FloorBelow = Ground - 40f }, 949203)); // a model floor below
        Assert.Equal(0, CreatureReports(new ScriptedModels(ModelCoverage.TileNotLoaded), 949204));             // unloaded tile: unknown
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
        using var kit = new HazardTestKit(liquid: null);
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
