using System.Numerics;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Client.Models;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Client.Models;

public sealed class WmoLayerTests
{
    private static readonly WorldPoint Spot = new(-9300, 100, 50);

    private static string Key(JsonObject attribute, string name) => attribute[name]!.GetValue<string>();

    [Fact]
    public void RefWmo_DrawsEveryGroupTriangle_InWorldSpace_SplitIntoCollideAndRenderOnly()
    {
        var world = new ModelWorld();
        var root = new SyntheticWmoRoot();
        world.AddWmo(ModelWorld.WallPath, root, ModelFixtures.WallGroup(0f, 0x20), ModelFixtures.WallGroup(2f, 0x04));
        world.Placements.Add(ModelWorld.Goldshire, ModelWorld.Wmo(7, Spot, ModelWorld.Goldshire));

        LayerBuild build = world.Build(LayerIds.RefWmo, ModelWorld.Goldshire);

        Assert.Equal(TileState.Ok, build.Status.State);
        GeometryChunk chunk = Assert.Single(build.Chunks);
        Assert.Equal(4, chunk.TriangleCount);
        int collide = 0;
        int render = 0;
        for (int t = 0; t < chunk.TriangleCount; t++)
        {
            JsonObject attribute = ModelWorld.AttributeOf(chunk, t);
            string colorKey = Key(attribute, "colorKey");
            collide += colorKey == ColorKeys.Collidable ? 1 : 0;
            render += colorKey == ColorKeys.RenderOnly ? 1 : 0;
            Assert.Equal(7u, attribute["uniqueId"]!.GetValue<uint>());
            Assert.False(attribute["extractorSkipped"]!.GetValue<bool>());
        }

        Assert.Equal(2, collide);
        Assert.Equal(2, render);

        // Model x = 0 maps to world x = the placement's x; model y/z are +-5 around the placement; the second group sits 2 yards further west (-2 in world x).
        List<WorldPoint> vertices = ModelWorld.WorldVertices(chunk);
        Assert.All(vertices.Where(v => Math.Abs(v.X - Spot.X) < 0.01), v =>
        {
            Assert.InRange(v.Y, Spot.Y - 5.01, Spot.Y + 5.01);
            Assert.InRange(v.Z, Spot.Z - 5.01, Spot.Z + 5.01);
        });
        Assert.Equal(4, vertices.Count(v => Math.Abs(v.X - Spot.X) < 0.01));
        Assert.Equal(4, vertices.Count(v => Math.Abs(v.X - (Spot.X - 2)) < 0.01));
    }

    [Fact]
    public void RefWmo_RotationFollowsTheServersEulerOrder()
    {
        // A yaw (rot.Y) of 90 degrees is Rz(90): model (0, 5, 0) goes to internal (-5, 0, 0), and the half turn to world makes that world x + 5.
        var world = new ModelWorld();
        world.AddWall();
        world.Placements.Add(ModelWorld.Goldshire, ModelWorld.Wmo(1, Spot, ModelWorld.Goldshire, yaw: 90));

        GeometryChunk chunk = Assert.Single(world.Build(LayerIds.RefWmo, ModelWorld.Goldshire).Chunks);

        List<WorldPoint> vertices = ModelWorld.WorldVertices(chunk);
        Assert.All(vertices, v => Assert.InRange(v.Y, Spot.Y - 0.01, Spot.Y + 0.01));
        Assert.Contains(vertices, v => Math.Abs(v.X - (Spot.X + 5)) < 0.01);
        Assert.Contains(vertices, v => Math.Abs(v.X - (Spot.X - 5)) < 0.01);
    }

    [Fact]
    public void RefWmo_FlagsTheGroupsAndInstancesTheExtractorDrops_ButStillDrawsThem()
    {
        var world = new ModelWorld();
        var root = new SyntheticWmoRoot();
        root.GroupNames.AddRange(["lobby", "antiportal"]);
        SyntheticWmoGroup kept = ModelFixtures.WallGroup(0f);
        SyntheticWmoGroup unreachable = ModelFixtures.WallGroup(1f, groupFlags: 0x80);
        SyntheticWmoGroup flagged = ModelFixtures.WallGroup(2f, groupFlags: 0x4000000);
        SyntheticWmoGroup named = ModelFixtures.WallGroup(3f);
        named.NameOffset = (uint)"lobby\0".Length;
        world.AddWmo(ModelWorld.WallPath, root, kept, unreachable, flagged, named);
        world.Placements.Add(ModelWorld.Goldshire, ModelWorld.Wmo(1, Spot, ModelWorld.Goldshire));

        GeometryChunk chunk = Assert.Single(world.Build(LayerIds.RefWmo, ModelWorld.Goldshire).Chunks);

        Assert.Equal(8, chunk.TriangleCount);
        var reasons = new Dictionary<int, string?>();
        for (int t = 0; t < chunk.TriangleCount; t += 2)
        {
            JsonObject attribute = ModelWorld.AttributeOf(chunk, t);
            reasons[attribute["group"]!.GetValue<int>()] = attribute["skipReason"]?.GetValue<string>();
        }

        Assert.Null(reasons[0]);
        Assert.Equal("unreachable", reasons[1]);
        Assert.Equal("antiportal", reasons[2]);
        Assert.Equal("antiportal-name", reasons[3]);
        Assert.Equal(0, chunk.Stats["extractorSkippedInstances"]!.GetValue<int>());
    }

    [Fact]
    public void RefWmo_DestructibleInstance_IsDrawnAndFlagged()
    {
        var world = new ModelWorld();
        world.AddWall();
        world.Placements.Add(ModelWorld.Goldshire, ModelWorld.Wmo(5, Spot, ModelWorld.Goldshire, flags: 1));

        GeometryChunk chunk = Assert.Single(world.Build(LayerIds.RefWmo, ModelWorld.Goldshire).Chunks);

        Assert.Equal(2, chunk.TriangleCount);
        JsonObject attribute = ModelWorld.AttributeOf(chunk, 0);
        Assert.True(attribute["extractorSkipped"]!.GetValue<bool>());
        Assert.Equal("destructible", Key(attribute, "skipReason"));
    }

    [Fact]
    public void RefWmo_IsDrawnOnlyInTheTileThatContainsThePlacement_AndOncePerUniqueId()
    {
        var world = new ModelWorld();
        world.AddWall();
        ClientPlacement placement = ModelWorld.Wmo(11, Spot, ModelWorld.Goldshire);

        // The same object is listed by both ADTs (the client lists an object in every tile it touches), plus a duplicate record.
        world.Placements.Add(ModelWorld.Goldshire, placement, placement with { Rotation = new WorldPoint(0, 0, 0) });
        world.Placements.Add(ModelWorld.Neighbour, placement with { Tile = ModelWorld.Neighbour });

        LayerBuild home = world.Build(LayerIds.RefWmo, ModelWorld.Goldshire);
        LayerBuild other = world.Build(LayerIds.RefWmo, ModelWorld.Neighbour);

        Assert.Equal(TileState.Ok, home.Status.State);
        Assert.Equal(2, Assert.Single(home.Chunks).TriangleCount);
        Assert.Equal(1, home.Chunks[0].Stats["duplicateUniqueIds"]!.GetValue<int>());
        Assert.Equal(TileState.Empty, other.Status.State);
        Assert.Empty(other.Chunks);
    }

    [Fact]
    public void RefWmo_OriginQuirkPlacementLandsAtWorldOrigin_InTheTileThatContainsIt()
    {
        var world = new ModelWorld();
        world.AddWall();
        var origin = new TileKey(0, 32, 32);

        // Raw x == 0 and raw z == 0: the extractor moves the WMO to (533.33333 * 32) in both, which is world (0, 0).
        var placement = new ClientPlacement(PlacementKind.Wmo, ModelWorld.WallPath, 3, new WorldPoint(0, 10, 0), new WorldPoint(0, 0, 0), 1, 0, 0, origin);
        world.Placements.Add(origin, placement);

        GeometryChunk chunk = Assert.Single(world.Build(LayerIds.RefWmo, origin).Chunks);

        Assert.True(ModelWorld.AttributeOf(chunk, 0)["originQuirk"]!.GetValue<bool>());
        Assert.All(ModelWorld.WorldVertices(chunk), v =>
        {
            Assert.InRange(v.X, -0.01, 0.01);
            Assert.InRange(v.Y, -5.01, 5.01);
            Assert.InRange(v.Z, 4.99, 15.01);
        });
    }

    [Fact]
    public void RefWmo_SplitsIntoPartsUnderASmallLimit_WithEveryUsedAttributeDescribedInItsPart()
    {
        var world = new ModelWorld();
        world.AddWall();
        for (uint i = 0; i < 4; i++)
        {
            world.Placements.Add(ModelWorld.Goldshire, ModelWorld.Wmo(100 + i, new WorldPoint(-9300 - (i * 20), 100, 50), ModelWorld.Goldshire));
        }

        TilePlan plan = ModelPlanner.Plan(0, ModelWorld.Goldshire, world.Placements.Placements(ModelWorld.Goldshire)!, PlanScope.Wmo, world.Files, world.Cache, CancellationToken.None);
        var geometry = new PartedGeometry(LayerIds.RefWmo, ModelWorld.Goldshire, WorldGrid.Origin(ModelWorld.Goldshire), partLimitBytes: 100);
        foreach (PlannedWmo wmo in plan.Wmos)
        {
            ModelGeometry.AddWmo(geometry, wmo, CancellationToken.None);
        }

        IReadOnlyList<GeometryChunk> chunks = geometry.Finish(new JsonObject());

        Assert.True(chunks.Count >= 2, $"expected several parts, got {chunks.Count}");
        Assert.Equal(8, chunks.Sum(c => c.TriangleCount));
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Part));
        foreach (GeometryChunk chunk in chunks)
        {
            Assert.All(chunk.Triangles, index => Assert.True(index < chunk.VertexCount));
            Assert.All(chunk.TriangleAttributes, id => Assert.True(chunk.Attributes.ContainsKey(id), $"attribute {id} is not described in part {chunk.Part}"));
            Assert.NotEmpty(GeometryWire.Write(chunk));
        }

        var ids = chunks.SelectMany(c => c.Attributes.Keys).ToList();
        Assert.Equal(4, ids.Distinct().Count());
    }

    [Fact]
    public void RefWmo_ASingleHugeGroupIsSplitAcrossParts_WithVerticesCopiedIntoEachPart()
    {
        var world = new ModelWorld();
        var group = new SyntheticWmoGroup();
        group.Vertices.AddRange([new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1)]);
        for (int i = 0; i < 9000; i++)
        {
            group.Triangles.Add(new SyntheticTriangle(0, 1, 2));
        }

        world.AddWmo(ModelWorld.WallPath, new SyntheticWmoRoot(), group);
        world.Placements.Add(ModelWorld.Goldshire, ModelWorld.Wmo(1, Spot, ModelWorld.Goldshire));
        TilePlan plan = ModelPlanner.Plan(0, ModelWorld.Goldshire, world.Placements.Placements(ModelWorld.Goldshire)!, PlanScope.Wmo, world.Files, world.Cache, CancellationToken.None);
        var geometry = new PartedGeometry(LayerIds.RefWmo, ModelWorld.Goldshire, WorldGrid.Origin(ModelWorld.Goldshire), partLimitBytes: 40_000);

        int added = ModelGeometry.AddWmo(geometry, Assert.Single(plan.Wmos), CancellationToken.None);
        IReadOnlyList<GeometryChunk> chunks = geometry.Finish(new JsonObject());

        Assert.Equal(9000, added);
        Assert.True(chunks.Count >= 2);
        Assert.Equal(9000, chunks.Sum(c => c.TriangleCount));
        Assert.All(chunks, c => Assert.All(c.Triangles, index => Assert.True(index < c.VertexCount)));
        Assert.All(chunks, c => Assert.All(c.TriangleAttributes, id => Assert.True(c.Attributes.ContainsKey(id))));
    }
}

public sealed class M2LayerTests
{
    private static readonly WorldPoint Spot = new(-9250, 100, 50);

    [Fact]
    public void RefM2Collision_DrawsTheBoundingHull_ScaledAndPlacedInWorldSpace()
    {
        var world = new ModelWorld();
        world.AddCube();
        world.Placements.Add(ModelWorld.Goldshire, ModelWorld.M2(9, Spot, ModelWorld.Goldshire, scale: 2048));

        LayerBuild build = world.Build(LayerIds.RefM2Collision, ModelWorld.Goldshire);

        Assert.Equal(TileState.Ok, build.Status.State);
        GeometryChunk chunk = Assert.Single(build.Chunks);
        Assert.Equal(12, chunk.TriangleCount);
        List<WorldPoint> vertices = ModelWorld.WorldVertices(chunk);
        Assert.Equal(8, vertices.Count);
        Assert.Equal(Spot.X - 2, vertices.Min(v => v.X), 3);
        Assert.Equal(Spot.X + 2, vertices.Max(v => v.X), 3);
        Assert.Equal(Spot.Y - 2, vertices.Min(v => v.Y), 3);
        Assert.Equal(Spot.Z + 2, vertices.Max(v => v.Z), 3);
        JsonObject attribute = ModelWorld.AttributeOf(chunk, 0);
        Assert.Equal("mddf", attribute["source"]!.GetValue<string>());
        Assert.Equal(2.0, attribute["scale"]!.GetValue<double>());
        Assert.False(attribute["extractorSkipped"]!.GetValue<bool>());
    }

    [Fact]
    public void RefM2Render_DrawsTheViewMesh_AndFlagsAModelWithoutAHull()
    {
        var world = new ModelWorld();
        world.AddCube(withHull: false);
        world.Placements.Add(ModelWorld.Goldshire, ModelWorld.M2(9, Spot, ModelWorld.Goldshire));

        LayerBuild render = world.Build(LayerIds.RefM2Render, ModelWorld.Goldshire);
        LayerBuild collision = world.Build(LayerIds.RefM2Collision, ModelWorld.Goldshire);

        GeometryChunk chunk = Assert.Single(render.Chunks);
        Assert.Equal(12, chunk.TriangleCount);
        JsonObject attribute = ModelWorld.AttributeOf(chunk, 0);
        Assert.True(attribute["extractorSkipped"]!.GetValue<bool>());
        Assert.Equal("no-bounding-hull", attribute["skipReason"]!.GetValue<string>());

        // A model with no hull has no collision: an empty tile, not a failure.
        Assert.Equal(TileState.Empty, collision.Status.State);
    }

    [Fact]
    public void RefM2_DoodadsOfAWmoComeFromTheSelectedSetOnly_AndAreFlaggedWhenNoKeptGroupReferencesThem()
    {
        var world = new ModelWorld();
        world.AddCube();
        var root = new SyntheticWmoRoot();
        root.DoodadSets.Add(("set0", 0, 2));
        root.DoodadSets.Add(("set1", 2, 1));
        root.Doodads.Add((ModelWorld.CubeStoredPath, new Vector3(1, 0, 0), Quaternion.Identity, 1f));
        root.Doodads.Add((ModelWorld.CubeStoredPath, new Vector3(0, 0, 3), Quaternion.Identity, 1f));
        root.Doodads.Add((ModelWorld.CubeStoredPath, new Vector3(0, 9, 0), Quaternion.Identity, 1f));
        SyntheticWmoGroup group = ModelFixtures.WallGroup(0f);
        group.DoodadReferences.AddRange([0, 2]);
        world.AddWmo(ModelWorld.WallPath, root, group);
        ClientPlacement set0 = ModelWorld.Wmo(1, Spot, ModelWorld.Goldshire, doodadSet: 0);
        world.Placements.Add(ModelWorld.Goldshire, set0);

        GeometryChunk chunk = Assert.Single(world.Build(LayerIds.RefM2Collision, ModelWorld.Goldshire).Chunks);

        Assert.Equal(24, chunk.TriangleCount);
        var skipped = new Dictionary<int, bool>();
        for (int t = 0; t < chunk.TriangleCount; t += 12)
        {
            JsonObject attribute = ModelWorld.AttributeOf(chunk, t);
            Assert.Equal("wmo-doodad", attribute["source"]!.GetValue<string>());
            skipped[attribute["doodadIndex"]!.GetValue<int>()] = attribute["extractorSkipped"]!.GetValue<bool>();
        }

        Assert.False(skipped[0]);
        Assert.True(skipped[1]);
        Assert.DoesNotContain(2, skipped.Keys);

        // doodad 1 is at WMO-local z + 3, so its cube spans z 52..54 in world.
        List<WorldPoint> vertices = ModelWorld.WorldVertices(chunk);
        Assert.Equal(Spot.Z + 4, vertices.Max(v => v.Z), 3);
    }

    [Fact]
    public void RefM2_ADoodadSetBeyondTheModelsSetsPlacesNothing_AndIsCounted()
    {
        var world = new ModelWorld();
        world.AddCube();
        var root = new SyntheticWmoRoot();
        root.DoodadSets.Add(("set0", 0, 1));
        root.Doodads.Add((ModelWorld.CubeStoredPath, Vector3.Zero, Quaternion.Identity, 1f));
        world.AddWmo(ModelWorld.WallPath, root, ModelFixtures.WallGroup(0f));
        world.Placements.Add(ModelWorld.Goldshire, ModelWorld.Wmo(1, Spot, ModelWorld.Goldshire, doodadSet: 3));

        LayerBuild build = world.Build(LayerIds.RefM2Collision, ModelWorld.Goldshire);

        Assert.Equal(TileState.Empty, build.Status.State);
        TilePlan plan = ModelPlanner.Plan(0, ModelWorld.Goldshire, world.Placements.Placements(ModelWorld.Goldshire)!, PlanScope.All, world.Files, world.Cache, CancellationToken.None);
        Assert.Equal(1, plan.DoodadSetOutOfRange);
        Assert.Empty(plan.M2s);
    }

    [Fact]
    public void ADoodadOfASkippedWmo_IsFlaggedWithTheWmosReason()
    {
        var world = new ModelWorld();
        world.AddCube();
        var root = new SyntheticWmoRoot();
        root.DoodadSets.Add(("set0", 0, 1));
        root.Doodads.Add((ModelWorld.CubeStoredPath, Vector3.Zero, Quaternion.Identity, 1f));
        SyntheticWmoGroup group = ModelFixtures.WallGroup(0f);
        group.DoodadReferences.Add(0);
        world.AddWmo(ModelWorld.WallPath, root, group);
        world.Placements.Add(ModelWorld.Goldshire, ModelWorld.Wmo(1, Spot, ModelWorld.Goldshire, flags: 1));

        GeometryChunk chunk = Assert.Single(world.Build(LayerIds.RefM2Render, ModelWorld.Goldshire).Chunks);

        JsonObject attribute = ModelWorld.AttributeOf(chunk, 0);
        Assert.True(attribute["extractorSkipped"]!.GetValue<bool>());
        Assert.Equal("wmo-destructible", attribute["skipReason"]!.GetValue<string>());
    }

    [Fact]
    public void ADoodadWithAPureYawWmo_HasNoServerRotationDeviation_AndATiltedOneOnARolledWmoDoes()
    {
        var world = new ModelWorld();
        world.AddCube();
        var root = new SyntheticWmoRoot();
        root.DoodadSets.Add(("set0", 0, 1));
        root.Doodads.Add((ModelWorld.CubeStoredPath, Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), 1f));
        SyntheticWmoGroup group = ModelFixtures.WallGroup(0f);
        group.DoodadReferences.Add(0);
        world.AddWmo(ModelWorld.WallPath, root, group);
        ClientPlacement yawed = ModelWorld.Wmo(1, Spot, ModelWorld.Goldshire, yaw: 33);
        ClientPlacement rolled = yawed with { UniqueId = 2, Rotation = new WorldPoint(0, 0, 30) };

        PlannedM2 pure = Assert.Single(ModelPlanner.Plan(0, null, [yawed], PlanScope.All, world.Files, world.Cache, CancellationToken.None).M2s);
        PlannedM2 roll = Assert.Single(ModelPlanner.Plan(0, null, [rolled], PlanScope.All, world.Files, world.Cache, CancellationToken.None).M2s);

        Assert.InRange(pure.ServerRotationDeviationDegrees!.Value, 0, 0.01);
        Assert.InRange(roll.ServerRotationDeviationDegrees!.Value, 59.9, 60.1);
    }
}

public sealed class LayerStateTests
{
    private static readonly WorldPoint Spot = new(-9300, 100, 50);

    [Fact]
    public void WithoutClientFilesOrPlacements_EveryLayerReportsMissingWithTheReason()
    {
        var bare = new ModelWorld(withFiles: false, withPlacements: false, withMaps: false);
        var filesOnly = new ModelWorld(withFiles: true, withPlacements: false, withMaps: false);

        foreach (string layer in new[] { LayerIds.RefWmo, LayerIds.RefM2Collision, LayerIds.RefM2Render })
        {
            TileStatus peek = bare.Source.PeekStatus(layer, ModelWorld.Goldshire);
            LayerBuild none = bare.Build(layer, ModelWorld.Goldshire);
            LayerBuild noPlacements = filesOnly.Build(layer, ModelWorld.Goldshire);

            Assert.Equal(TileState.Missing, peek.State);
            Assert.Equal(TileState.Missing, none.Status.State);
            Assert.Contains("client file", none.Status.Detail, StringComparison.Ordinal);
            Assert.Equal(TileState.Missing, noPlacements.Status.State);
            Assert.Contains("placement", noPlacements.Status.Detail, StringComparison.Ordinal);
            Assert.Empty(bare.Source.AvailableTiles(layer, 0));
        }
    }

    [Fact]
    public void ATileWithoutAnAdt_IsMissing_NotEmpty()
    {
        var world = new ModelWorld();

        LayerBuild build = world.Build(LayerIds.RefWmo, ModelWorld.Goldshire);
        TileStatus peek = world.Source.PeekStatus(LayerIds.RefWmo, ModelWorld.Goldshire);

        Assert.Equal(TileState.Missing, build.Status.State);
        Assert.Equal(TileState.Missing, peek.State);
        Assert.Contains("Azeroth_31_49.adt", peek.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ATileWithAnAdt_PeeksUnknown_AndAnAdtWithNoModelsBuildsEmpty()
    {
        var world = new ModelWorld();
        world.AddAdt(ModelWorld.Goldshire);
        world.Placements.Add(ModelWorld.Goldshire);

        TileStatus peek = world.Source.PeekStatus(LayerIds.RefWmo, ModelWorld.Goldshire);
        LayerBuild build = world.Build(LayerIds.RefWmo, ModelWorld.Goldshire);

        Assert.Equal(TileState.Unknown, peek.State);
        Assert.Equal(TileState.Empty, build.Status.State);
    }

    [Fact]
    public void CorruptPlacements_AreRejectedWithTheMessage()
    {
        var world = new ModelWorld();
        world.Placements.Corrupt[ModelWorld.Goldshire] = "MDDF chunk is truncated";

        LayerBuild build = world.Build(LayerIds.RefM2Collision, ModelWorld.Goldshire);

        Assert.Equal(TileState.Rejected, build.Status.State);
        Assert.Contains("MDDF chunk is truncated", build.Status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingModelFile_IsRejectedWithItsPath_NeverEmpty()
    {
        var world = new ModelWorld();
        world.Placements.Add(ModelWorld.Goldshire, ModelWorld.Wmo(1, Spot, ModelWorld.Goldshire, path: "World\\wmo\\Test\\Gone.wmo"));

        LayerBuild build = world.Build(LayerIds.RefWmo, ModelWorld.Goldshire);

        Assert.Equal(TileState.Rejected, build.Status.State);
        Assert.Contains("Gone.wmo", build.Status.Detail, StringComparison.Ordinal);
        Assert.Contains("1 missing", build.Status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AZeroByteWmoRoot_IsRejected_NotEmpty()
    {
        var world = new ModelWorld();
        world.Files.Add(ModelWorld.WallPath, []);
        world.Placements.Add(ModelWorld.Goldshire, ModelWorld.Wmo(1, Spot, ModelWorld.Goldshire));

        LayerBuild build = world.Build(LayerIds.RefWmo, ModelWorld.Goldshire);

        Assert.Equal(TileState.Rejected, build.Status.State);
        Assert.Contains("1 rejected", build.Status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ACorruptGroupFile_RejectsTheWmoAndNamesTheGroup()
    {
        var world = new ModelWorld();
        world.AddWall();
        world.Files.Add(WmoRootFile.GroupFilePath(ModelWorld.WallPath, 0), [1, 2, 3, 4]);
        world.Placements.Add(ModelWorld.Goldshire, ModelWorld.Wmo(1, Spot, ModelWorld.Goldshire));

        LayerBuild build = world.Build(LayerIds.RefWmo, ModelWorld.Goldshire);

        Assert.Equal(TileState.Rejected, build.Status.State);
        Assert.Contains("Wall_000.wmo", build.Status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingGroupFile_RejectsTheWmo()
    {
        var world = new ModelWorld();
        var root = new SyntheticWmoRoot { GroupCount = 2 };
        world.Files.Add(ModelWorld.WallPath, ModelFixtures.WmoRoot(root));
        world.Files.Add(WmoRootFile.GroupFilePath(ModelWorld.WallPath, 0), ModelFixtures.WmoGroup(ModelFixtures.WallGroup(0f)));
        world.Placements.Add(ModelWorld.Goldshire, ModelWorld.Wmo(1, Spot, ModelWorld.Goldshire));

        LayerBuild build = world.Build(LayerIds.RefWmo, ModelWorld.Goldshire);

        Assert.Equal(TileState.Rejected, build.Status.State);
        Assert.Contains("Wall_001.wmo", build.Status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenSomeModelsFail_TheRestStillDraw_AndTheStatusSaysWhatFailed()
    {
        var world = new ModelWorld();
        world.AddWall();
        world.Placements.Add(
            ModelWorld.Goldshire,
            ModelWorld.Wmo(1, Spot, ModelWorld.Goldshire),
            ModelWorld.Wmo(2, Spot, ModelWorld.Goldshire, path: "World\\wmo\\Test\\Gone.wmo"));

        LayerBuild build = world.Build(LayerIds.RefWmo, ModelWorld.Goldshire);

        Assert.Equal(TileState.Ok, build.Status.State);
        Assert.Equal(2, Assert.Single(build.Chunks).TriangleCount);
        Assert.Contains("1 model file(s) unavailable", build.Status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AvailableTiles_ComeFromTheAdtFileNames_InServerTileOrder_AndIncludeTheGlobalTile()
    {
        var world = new ModelWorld();
        world.AddAdt(ModelWorld.Goldshire);
        world.AddAdt(ModelWorld.Neighbour);
        world.Files.Add("World\\Maps\\Azeroth\\Azeroth.wdt", [0]);
        world.Files.Add("World\\Maps\\Azeroth\\Azeroth_xx_yy.adt", [0]);
        world.Files.Add("World\\Maps\\Azeroth\\Azeroth_99_99.adt", [0]);
        world.Files.Add("World\\Maps\\Azeroth\\Azeroth_5_6.adt.bak", [0]);
        world.Placements.Add(TileKey.Global(0), ModelWorld.Wmo(1, Spot, TileKey.Global(0)));

        IReadOnlyCollection<TileKey> tiles = world.Source.AvailableTiles(LayerIds.RefWmo, 0);

        Assert.Equal(
            new[] { ModelWorld.Goldshire, ModelWorld.Neighbour, TileKey.Global(0) }.OrderBy(t => t.X).ThenBy(t => t.Y),
            tiles.OrderBy(t => t.X).ThenBy(t => t.Y));
        Assert.Empty(world.Source.AvailableTiles(LayerIds.RefWmo, 1));
    }

    [Fact]
    public void TheGlobalTile_BuildsTheWdtWmo_AndEveryTileRefusesAnUnknownLayer()
    {
        var world = new ModelWorld();
        world.AddWall();
        TileKey global = TileKey.Global(0);
        world.Placements.Add(global, ModelWorld.Wmo(1, Spot, global));

        LayerBuild build = world.Build(LayerIds.RefWmo, global);

        Assert.Equal(TileState.Ok, build.Status.State);
        Assert.Equal(2, Assert.Single(build.Chunks).TriangleCount);
        Assert.Throws<ArgumentException>(() => world.Source.Build(LayerIds.Terrain, ModelWorld.Goldshire, CancellationToken.None));
        Assert.Throws<ArgumentException>(() => world.Source.Build(LayerIds.RefWmo, new TileKey(0, 99, 99), CancellationToken.None));
    }

    [Fact]
    public void ACancelledBuild_Throws_AndLeavesNothingCached()
    {
        var world = new ModelWorld();
        world.AddWall();
        world.Placements.Add(ModelWorld.Goldshire, ModelWorld.Wmo(1, Spot, ModelWorld.Goldshire));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => world.Source.Build(LayerIds.RefWmo, ModelWorld.Goldshire, cts.Token));

        Assert.Equal(TileState.Ok, world.Build(LayerIds.RefWmo, ModelWorld.Goldshire).Status.State);
    }

    [Fact]
    public void TheLayerCache_BuildsATileOnce_AndTheWireRoundTrips()
    {
        var world = new ModelWorld();
        world.AddWall();
        world.Placements.Add(ModelWorld.Goldshire, ModelWorld.Wmo(1, Spot, ModelWorld.Goldshire));
        var registry = new LayerRegistry();
        registry.Add(world.Source);
        var cache = new LayerCache(registry);

        LayerBuild first = cache.Get(LayerIds.RefWmo, ModelWorld.Goldshire, CancellationToken.None);
        LayerBuild second = cache.Get(LayerIds.RefWmo, ModelWorld.Goldshire, CancellationToken.None);
        IReadOnlyList<byte[]> wire = cache.GetWire(LayerIds.RefWmo, ModelWorld.Goldshire, CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(1, world.Placements.Calls);
        Assert.Single(wire);
        Assert.True(wire[0].Length > 0);
        Assert.Equal(new[] { LayerIds.RefWmo, LayerIds.RefM2Collision, LayerIds.RefM2Render }, registry.Available);
    }
}

public sealed class ModelCacheTests
{
    [Fact]
    public void AMissingOrCorruptModel_IsLoadedOnce_AndReportedEveryTime()
    {
        var world = new ModelWorld();
        world.Files.Add("World\\Doodads\\Test\\Bad.m2", [1, 2, 3]);
        var counting = new CountingFiles(world.Files);
        var cache = new ClientModelCache();

        ModelLoad<M2ModelFile> missing1 = cache.GetM2(counting, "World\\Doodads\\Test\\Gone.m2");
        ModelLoad<M2ModelFile> missing2 = cache.GetM2(counting, "world/doodads/test/GONE.M2");
        ModelLoad<M2ModelFile> bad1 = cache.GetM2(counting, "World\\Doodads\\Test\\Bad.m2");
        ModelLoad<M2ModelFile> bad2 = cache.GetM2(counting, "World\\Doodads\\Test\\Bad.m2");

        Assert.Equal(ModelProblemKind.Missing, missing1.Problem!.Kind);
        Assert.Equal(ModelProblemKind.Missing, missing2.Problem!.Kind);
        Assert.Equal(ModelProblemKind.Rejected, bad1.Problem!.Kind);
        Assert.Same(bad1.Problem, bad2.Problem);
        Assert.Equal(2, counting.Reads);
    }

    [Fact]
    public void AMdxName_LoadsTheM2File()
    {
        var world = new ModelWorld();
        world.AddCube();

        ModelLoad<M2ModelFile> load = new ClientModelCache().GetM2(world.Files, ModelWorld.CubeStoredPath);

        Assert.True(load.IsOk);
        Assert.True(load.Model!.HasCollisionHull);
    }

    [Fact]
    public void ConcurrentRequestsForOneModel_ShareOneLoad()
    {
        var world = new ModelWorld();
        world.AddCube();
        var counting = new CountingFiles(world.Files);
        var cache = new ClientModelCache();

        Parallel.For(0, 64, _ => Assert.True(cache.GetM2(counting, ModelWorld.CubePath).IsOk));

        Assert.Equal(1, counting.Reads);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void TheLeastRecentlyUsedModelIsDropped_WhenTheCacheIsOverItsLimit()
    {
        var world = new ModelWorld();
        world.AddCube("World\\Doodads\\Test\\A.m2");
        world.AddCube("World\\Doodads\\Test\\B.m2");
        var counting = new CountingFiles(world.Files);
        var cache = new ClientModelCache(maxBytes: 1);

        Assert.True(cache.GetM2(counting, "World\\Doodads\\Test\\A.m2").IsOk);
        Assert.True(cache.GetM2(counting, "World\\Doodads\\Test\\B.m2").IsOk);
        Assert.Equal(1, cache.Count);
        Assert.True(cache.GetM2(counting, "World\\Doodads\\Test\\A.m2").IsOk);

        Assert.Equal(3, counting.Reads);
    }

    [Fact]
    public void Clear_ForgetsEverything()
    {
        var world = new ModelWorld();
        world.AddCube();
        var cache = new ClientModelCache();
        Assert.True(cache.GetM2(world.Files, ModelWorld.CubePath).IsOk);

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.Bytes);
    }

    [Fact]
    public void AWmoWithMoreGroupsThanTheLimit_IsRejectedBeforeAnyGroupIsRead()
    {
        var world = new ModelWorld();
        world.Files.Add(ModelWorld.WallPath, ModelFixtures.WmoRoot(new SyntheticWmoRoot { GroupCount = 5000 }));
        var counting = new CountingFiles(world.Files);

        ModelLoad<WmoModel> load = new ClientModelCache().GetWmo(counting, ModelWorld.WallPath);

        Assert.Equal(ModelProblemKind.Rejected, load.Problem!.Kind);
        Assert.Equal(1, counting.Reads);
    }
}
