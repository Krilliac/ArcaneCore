using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Client.Models;
using ArcaneCore.Game.Maps.Collision.VMaps;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Atlas.Tests.Reference.Client.Models;

/// <summary>
/// Real-data checks of the ref.wmo / ref.m2.* layers and ref.model.info over the extracted 1.12.1 client: every instance is
/// drawn by exactly one tile, the drawn triangle counts equal the resolved placements, and the model description agrees
/// with the server's own model file.
/// </summary>
public sealed class RealModelLayerTests(ITestOutputHelper output)
{
    private static readonly TileKey GoldshireTile = new(0, 49, 31);

    [RealClientModelsFact]
    public void Layers_DrawEveryWmoOnceAcrossTheGoldshireBlock()
    {
        DirectoryClientFiles files = RealClientModels.Files;
        var reader = new AdtPlacementReader(files);
        ClientModelLayerSource source = Source(files, reader, new ClientModelCache());
        var listedBy = new Dictionary<uint, int>();
        var drawnBy = new Dictionary<uint, List<TileKey>>();
        var block = new HashSet<TileKey>();
        var positions = new Dictionary<uint, WorldPoint>();
        for (int x = GoldshireTile.X - 1; x <= GoldshireTile.X + 1; x++)
        {
            for (int y = GoldshireTile.Y - 1; y <= GoldshireTile.Y + 1; y++)
            {
                var tile = new TileKey(GoldshireTile.MapId, x, y);
                block.Add(tile);
                foreach (ClientPlacement placement in reader.Placements(tile) ?? throw new InvalidOperationException($"no ADT for {tile}"))
                {
                    if (placement.Kind == PlacementKind.Wmo)
                    {
                        listedBy[placement.UniqueId] = listedBy.GetValueOrDefault(placement.UniqueId) + 1;
                        positions[placement.UniqueId] = PlacementTransform.InternalToWorld(PlacementTransform.ForWmo(placement, out _).Position);
                    }
                }

                long started = Stopwatch.GetTimestamp();
                LayerBuild build = source.Build(LayerIds.RefWmo, tile, CancellationToken.None);
                output.WriteLine(Inv($"{tile}: {build.Status.State}, {build.Chunks.Sum(c => (long)c.TriangleCount)} triangles in {build.Chunks.Count} part(s), {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms"));
                Assert.True(build.Status.State is TileState.Ok or TileState.Empty, Inv($"{tile}: {build.Status.State} {build.Status.Detail}"));
                foreach (uint id in DrawnIds(build))
                {
                    if (!drawnBy.TryGetValue(id, out List<TileKey>? tiles))
                    {
                        drawnBy[id] = tiles = [];
                    }

                    tiles.Add(tile);
                }
            }
        }

        // A WMO listed by several ADTs of the block is drawn once, by the tile that contains its position.
        int multiListed = listedBy.Count(p => p.Value > 1);
        Assert.True(multiListed > 0, "no WMO of the block is listed by two ADTs, so the de-duplication was not exercised");
        Assert.All(drawnBy, p => Assert.Single(p.Value));
        Assert.All(drawnBy, p => Assert.Equal(WorldGrid.TileOf(GoldshireTile.MapId, positions[p.Key].X, positions[p.Key].Y), p.Value[0]));

        // Every WMO whose position lies in the block is drawn (the model files resolve for all of them).
        List<uint> ownedInBlock = [.. positions.Where(p => WorldGrid.TileOf(GoldshireTile.MapId, p.Value.X, p.Value.Y) is { } owner && block.Contains(owner)).Select(p => p.Key)];
        Assert.Equal(ownedInBlock.Order(), drawnBy.Keys.Order());
        output.WriteLine(Inv($"block WMOs listed {listedBy.Count}, listed by 2+ ADTs {multiListed}, drawn {drawnBy.Count} (each by one tile)"));
    }

    [RealClientModelsFact]
    public void Layers_DrawTheResolvedPlacementsOfTheGoldshireTile()
    {
        DirectoryClientFiles files = RealClientModels.Files;
        var reader = new AdtPlacementReader(files);
        var cache = new ClientModelCache();
        ClientModelLayerSource source = Source(files, reader, cache);
        IReadOnlyList<ClientPlacement> placements = reader.Placements(GoldshireTile)!;
        TilePlan wmoPlan = ModelPlanner.Plan(GoldshireTile.MapId, GoldshireTile, placements, PlanScope.Wmo, files, cache, CancellationToken.None);
        TilePlan m2Plan = ModelPlanner.Plan(GoldshireTile.MapId, GoldshireTile, placements, PlanScope.M2 | PlanScope.Doodads, files, cache, CancellationToken.None);

        LayerBuild wmo = source.Build(LayerIds.RefWmo, GoldshireTile, CancellationToken.None);
        LayerBuild collision = source.Build(LayerIds.RefM2Collision, GoldshireTile, CancellationToken.None);
        LayerBuild render = source.Build(LayerIds.RefM2Render, GoldshireTile, CancellationToken.None);
        Assert.All(new[] { wmo, collision, render }, b => Assert.Equal(TileState.Ok, b.Status.State));

        long wmoTriangles = wmoPlan.Wmos.Sum(w => w.Model.Groups.Sum(g => (long)g.TriangleCount));
        long skippedTriangles = wmoPlan.Wmos.Sum(w => Enumerable.Range(0, w.Model.Groups.Count)
            .Where(g => w.SkipReason is not null || w.Model.SkipReason(g) is not null)
            .Sum(g => (long)w.Model.Groups[g].TriangleCount));
        long collisionTriangles = m2Plan.M2s.Where(m => m.Model.HasCollisionHull).Sum(m => (long)m.Model.BoundingTriangleCount);
        long renderTriangles = m2Plan.M2s.Sum(m => (long)m.Model.RenderTriangleCount);
        Assert.Equal(wmoTriangles, wmo.Chunks.Sum(c => (long)c.TriangleCount));
        Assert.Equal(skippedTriangles, CountTriangles(wmo, a => a["extractorSkipped"]!.GetValue<bool>()));
        Assert.Equal(collisionTriangles, collision.Chunks.Sum(c => (long)c.TriangleCount));
        Assert.Equal(renderTriangles, render.Chunks.Sum(c => (long)c.TriangleCount));
        Assert.True(wmoPlan.Wmos.Count >= 3 && m2Plan.M2s.Count >= 100, Inv($"only {wmoPlan.Wmos.Count} WMOs and {m2Plan.M2s.Count} M2s resolved"));
        Assert.True(renderTriangles > collisionTriangles, "render meshes should outweigh the bounding hulls");
        output.WriteLine(Inv($"{GoldshireTile}: ref.wmo {wmoTriangles} triangles ({skippedTriangles} flagged extractor-skipped) over {wmoPlan.Wmos.Count} WMOs in {wmo.Chunks.Count} chunk(s); ref.m2.collision {collisionTriangles}; ref.m2.render {renderTriangles} over {m2Plan.M2s.Count} M2 instances"));
    }

    [RealClientModelsFact]
    public void ModelInfo_AgreesWithTheServerModelFile()
    {
        DirectoryClientFiles files = RealClientModels.Files;
        var reader = new AdtPlacementReader(files);
        var cache = new ClientModelCache();
        var services = new AtlasServices();
        services.Add<IClientFileSource>(files);
        var queries = new ReferenceModelQueries(services, cache);
        TilePlan plan = ModelPlanner.Plan(GoldshireTile.MapId, null, reader.Placements(GoldshireTile)!, PlanScope.Wmo, files, cache, CancellationToken.None);
        int described = 0;
        foreach (WmoModel model in plan.Wmos.Where(w => w.SkipReason is null).Select(w => w.Model).DistinctBy(m => m.Path, StringComparer.OrdinalIgnoreCase))
        {
            JsonObject info = queries.ModelInfo(model.Path);
            string serverName = ClientModelNames.ServerModelName(model.Path);
            WorldModel server = WorldModel.Parse(File.ReadAllBytes(Path.Combine(RealClientModels.VmapDirectory, VMapFormat.ModelFileName(serverName))));
            JsonArray groups = info["groups"]!.AsArray();
            Assert.Equal(model.Root.Header.GroupCount, (uint)groups.Count);
            Assert.Equal(server.Groups.Count, info["extractor"]!["keptGroups"]!.GetValue<int>());
            Assert.Equal(server.Groups.Sum(g => g.Triangles.Count), info["extractor"]!["triangles"]!.GetValue<int>());
            Assert.Equal(server.RootWmoId, info["header"]!["wmoId"]!.GetValue<uint>());
            Assert.Equal(model.Root.DoodadSets.Count, info["doodadSets"]!.AsArray().Count);
            described++;
        }

        Assert.True(described >= 3, Inv($"only {described} WMOs described"));
        output.WriteLine(Inv($"ref.model.info matched the server model file for {described} WMOs of {GoldshireTile}"));
    }

    private static ClientModelLayerSource Source(IClientFileSource files, IClientPlacementSource placements, ClientModelCache cache)
    {
        var services = new AtlasServices();
        services.Add(files);
        services.Add(placements);
        services.Add<IMapDirectory>(new FakeMapDirectory(new ClientMap(0, "Azeroth", "Eastern Kingdoms", 0)));
        return new ClientModelLayerSource(services, cache, NullLogger.Instance);
    }

    private static HashSet<uint> DrawnIds(LayerBuild build)
    {
        var ids = new HashSet<uint>();
        foreach (GeometryChunk chunk in build.Chunks)
        {
            foreach (JsonObject attribute in chunk.Attributes.Values)
            {
                ids.Add(attribute["uniqueId"]!.GetValue<uint>());
            }
        }

        return ids;
    }

    private static long CountTriangles(LayerBuild build, Func<JsonObject, bool> predicate)
    {
        long count = 0;
        foreach (GeometryChunk chunk in build.Chunks)
        {
            for (int t = 0; t < chunk.TriangleCount; t++)
            {
                count += predicate(ModelWorld.AttributeOf(chunk, t)) ? 1 : 0;
            }
        }

        return count;
    }

    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
