using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Client.Models;
using ArcaneCore.Game.Maps.Collision.VMaps;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Atlas.Tests.Reference.Client.Models;

/// <summary>
/// Real-data oracles: the client WMO/M2 readers and placement transforms against the server's own extracted vmaps, read
/// with ArcaneCore.Game's readers. Each test asserts counts that are only reachable when real files were read, and prints
/// every mismatch class it found.
/// </summary>
[Collection(RealClientModels.Collection)]
public sealed class RealModelOracleTests(ITestOutputHelper output)
{
    private const double ModelTolerance = 1e-3;
    private const double WorldTolerance = 1e-2;

    // A server spawn further than this from every client candidate of the same model counts as unmatched.
    private const double MatchRadius = 1.0;

    private static readonly TileKey GoldshireTile = new(0, 49, 31);
    private static readonly TileKey WestOfGoldshireTile = new(0, 48, 31);

    [RealClientModelsFact]
    public void ModelSpace_CollidableTrianglesEqualTheServerModels()
    {
        DirectoryClientFiles files = RealClientModels.Files;
        var reader = new AdtPlacementReader(files);
        var cache = new ClientModelCache();
        var clientByServerName = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var spawnNames = new Dictionary<string, (string Raw, bool IsM2)>(StringComparer.OrdinalIgnoreCase);
        var classes = new MismatchClasses();
        int tiles = 0;
        foreach (TileKey tile in RegionTiles())
        {
            IReadOnlyList<ClientPlacement>? placements = reader.Placements(tile);
            List<ServerTileSpawn>? spawns = ServerTileReader.Read(RealClientModels.VmapDirectory, tile);
            if (placements is null || spawns is null)
            {
                continue;
            }

            tiles++;
            TilePlan plan = ModelPlanner.Plan(tile.MapId, null, placements, PlanScope.All, files, cache, CancellationToken.None);
            if (plan.Problems.Missing > 0)
            {
                classes.AddExpected("client model file the archives do not have (the extractor finds none either)", Inv($"{tile}: {plan.Problems.ToJson().ToJsonString()}"));
            }

            if (plan.Problems.Rejected > 0)
            {
                classes.AddUnexplained("client model the readers reject", Inv($"{tile}: {plan.Problems.ToJson().ToJsonString()}"));
            }

            foreach (PlannedWmo wmo in plan.Wmos)
            {
                Add(clientByServerName, ClientModelNames.ServerModelName(wmo.Model.Path), wmo.Model.Path);
            }

            foreach (PlannedM2 m2 in plan.M2s)
            {
                Add(clientByServerName, ClientModelNames.ServerModelName(m2.ModelPath), m2.ModelPath);
            }

            foreach (ServerTileSpawn spawn in spawns)
            {
                spawnNames.TryAdd(spawn.ModelName, (spawn.Spawn.Name, spawn.Spawn.IsM2));
            }
        }

        Assert.Equal(0, reader.NameIndexDisagreements);
        var stats = new ModelStats();
        int wmos = 0;
        int m2s = 0;
        foreach ((string name, (string raw, bool isM2)) in spawnNames.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!clientByServerName.TryGetValue(name, out HashSet<string>? paths))
            {
                classes.AddUnexplained("server model with no client model of that name in the region's placements", name);
                continue;
            }

            string file = Path.Combine(RealClientModels.VmapDirectory, VMapFormat.ModelFileName(raw));
            if (!File.Exists(file))
            {
                classes.AddUnexplained("server spawn names a model file that is not in vmaps/", name);
                continue;
            }

            WorldModel server = WorldModel.Parse(File.ReadAllBytes(file));
            var reasons = new List<string>();
            bool matched = false;
            foreach (string path in paths.Order(StringComparer.Ordinal))
            {
                string? reason = isM2 ? CompareM2(cache.GetM2(files, path).Model!, server, stats) : CompareWmo(cache.GetWmo(files, path).Model!, server, stats);
                if (reason is null)
                {
                    matched = true;
                    break;
                }

                reasons.Add(path + ": " + reason);
            }

            if (!matched)
            {
                classes.AddUnexplained("model data differs", name + " -> " + string.Join("; ", reasons));
                continue;
            }

            if (paths.Count > 1)
            {
                // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/adtfile.cpp:30-37 (models are written under their plain name, so same-named models in different directories share one server file)
                classes.AddExpected("plain-name collision (the server file equals one of the same-named client models)", name);
            }

            if (isM2)
            {
                m2s++;
            }
            else
            {
                wmos++;
            }
        }

        output.WriteLine(Inv($"tiles {tiles}; server models {spawnNames.Count}; WMOs equal {wmos} ({stats.WmoGroups} kept groups, {stats.WmoTriangles} triangles, {stats.SkippedGroups} skipped groups, {stats.LiquidGroups} liquid grids equal); M2s equal {m2s} ({stats.M2Triangles} bounding triangles); max vertex delta {stats.MaxDelta:G4}"));
        output.WriteLine(Inv($"expected: {stats.RenderOnlyKept} kept-group triangles the MOPY rule calls render-only are in the server models (precision mode keeps every MOVI triangle)"));
        classes.Write(output);
        Assert.True(tiles >= 25, Inv($"only {tiles} region tiles had both an ADT and a vmtile"));
        Assert.True(wmos >= 50, Inv($"only {wmos} WMO models compared"));
        Assert.True(m2s >= 200, Inv($"only {m2s} M2 models compared"));
        Assert.True(stats.MaxDelta <= ModelTolerance, Inv($"max vertex delta {stats.MaxDelta}"));
        Assert.True(stats.LiquidGroups > 0, "no WMO group liquid (MLIQ) was compared");
        Assert.Equal(0, classes.UnexplainedCount);
    }

    [RealClientModelsFact]
    public void WorldSpace_PlacementsMatchTheServerSpawns()
    {
        DirectoryClientFiles files = RealClientModels.Files;
        var reader = new AdtPlacementReader(files);
        var cache = new ClientModelCache();
        var classes = new MismatchClasses();
        var serverToClient = new Dictionary<uint, (PlacementKind Kind, uint UniqueId, int Doodad)>();
        var serverModels = new Dictionary<string, WorldModel>(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        double maxPosition = 0;
        double maxRotation = 0;
        double maxScale = 0;
        double maxTriangle = 0;
        double maxGeometric = 0;
        int tilted = 0;
        long vertices = 0;
        foreach (TileKey tile in new[] { GoldshireTile, WestOfGoldshireTile })
        {
            IReadOnlyList<ClientPlacement> placements = reader.Placements(tile) ?? throw new InvalidOperationException($"no ADT for {tile}");
            List<ServerTileSpawn> spawns = ServerTileReader.Read(RealClientModels.VmapDirectory, tile) ?? throw new InvalidOperationException($"no vmtile for {tile}");

            // The test reader agrees with the server's own loader on how many instances the tile holds.
            var manager = new VMapManager(RealClientModels.VmapDirectory);
            Assert.True(manager.LoadTile(tile.MapId, tile.X, tile.Y));
            Assert.Equal(spawns.Select(s => s.Slot).Distinct().Count(), manager.GetTree(tile.MapId)!.LoadedInstanceCount);

            TilePlan plan = ModelPlanner.Plan(tile.MapId, null, placements, PlanScope.All, files, cache, CancellationToken.None);
            List<Expected> expected = ExpectedSpawns(plan, files, cache, classes);
            var used = new bool[expected.Count];
            var seenIds = new HashSet<uint>();
            foreach (ServerTileSpawn entry in spawns)
            {
                ModelSpawn spawn = entry.Spawn;
                if (!seenIds.Add(spawn.Id))
                {
                    // ref: vmangos@0e3ff01/src/game/vmap/TileAssembler.cpp:245-246 (TileEntries is a multimap: an id an ADT lists twice is written twice)
                    classes.AddExpected("the tile lists one spawn id twice", Inv($"{tile} id {spawn.Id} {entry.ModelName}"));
                    continue;
                }

                // The server id is not the client's unique id: the extractor numbers each (client unique id, doodad id) pair
                // in the order it first meets it across the whole extraction, so it cannot be recomputed per tile. A spawn
                // is paired with the nearest unused client instance of the same model and kind, and the pairing is then
                // required to be one to one by server id across both tiles (serverToClient below).
                // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/vmapexport.cpp:89-92 (GenerateUniqueObjectId: map.emplace(pair, size + 1))
                int best = -1;
                double bestDistance = double.MaxValue;
                var position = new Vec3d(spawn.Position.X, spawn.Position.Y, spawn.Position.Z);
                for (int i = 0; i < expected.Count; i++)
                {
                    Expected candidate = expected[i];
                    if (used[i] || candidate.IsM2 != spawn.IsM2 || !string.Equals(candidate.ServerName, entry.ModelName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    double distance = (candidate.Position - position).Length;
                    if (distance < bestDistance)
                    {
                        best = i;
                        bestDistance = distance;
                    }
                }

                if (best < 0 || bestDistance > MatchRadius)
                {
                    classes.AddUnexplained(
                        "server spawn with no client placement",
                        Inv($"{tile} id {spawn.Id} flags {spawn.Flags} {entry.ModelName} at {spawn.Position} (nearest same-name candidate {(best < 0 ? "none" : bestDistance.ToString("G4", CultureInfo.InvariantCulture) + " yd")})"));
                    continue;
                }

                used[best] = true;
                Expected match = expected[best];
                Count(counts, match.Kind);
                if (!string.Equals(match.ServerName, entry.ModelName, StringComparison.Ordinal))
                {
                    classes.AddUnexplained("model name differs only in case", match.ServerName + " vs " + entry.ModelName);
                }

                (PlacementKind, uint, int) clientKey = (match.IsM2 && match.Doodad < 0 ? PlacementKind.M2 : PlacementKind.Wmo, match.UniqueId, match.Doodad);
                if (serverToClient.TryGetValue(spawn.Id, out (PlacementKind, uint, int) known) && known != clientKey)
                {
                    classes.AddUnexplained("one server id stands for two client placements", Inv($"id {spawn.Id}: {known} and {clientKey}"));
                }

                serverToClient[spawn.Id] = clientKey;
                Mat3d serverRotation = PlacementTransform.ServerRotation(spawn.Rotation.X, spawn.Rotation.Y, spawn.Rotation.Z);
                maxPosition = Math.Max(maxPosition, bestDistance);
                maxRotation = Math.Max(maxRotation, CornerDelta(match.Corners, match.Scale, serverRotation, match.ServerRotation));
                maxScale = Math.Max(maxScale, Math.Abs(spawn.Scale - match.Scale) * Radius(match.Corners));

                // Every model vertex the instance carries, placed both ways.
                WorldModel model = ServerModel(serverModels, entry.Spawn.Name);
                var instance = new ModelInstance(spawn, model);
                var serverEquivalent = new InstanceTransform(match.ServerRotation, match.Position, match.Scale);
                double geometric = 0;
                foreach ((Vector3 clientVertex, Vector3 serverVertex) in match.Vertices(model))
                {
                    Vector3 world = VMapFormat.ToWorld(instance.FromModel(serverVertex));
                    var serverWorld = new WorldPoint(world.X, world.Y, world.Z);
                    maxTriangle = Math.Max(maxTriangle, Distance(serverEquivalent.ModelToWorld(Vec3d.From(clientVertex)), serverWorld));
                    geometric = Math.Max(geometric, Distance(match.Transform.ModelToWorld(Vec3d.From(clientVertex)), serverWorld));
                    vertices++;
                }

                maxGeometric = Math.Max(maxGeometric, geometric);
                if (geometric > WorldTolerance)
                {
                    if (match.Doodad >= 0)
                    {
                        tilted++;
                    }
                    else
                    {
                        classes.AddUnexplained("layer geometry differs from the server instance", Inv($"{match.Kind} {match.UniqueId} {match.ServerName}: {geometric:G4} yd"));
                    }
                }
            }

            for (int i = 0; i < expected.Count; i++)
            {
                if (!used[i])
                {
                    Expected missing = expected[i];
                    classes.AddUnexplained("client placement the server does not have", Inv($"{tile} {missing.Kind} {missing.UniqueId}/{missing.Doodad} {missing.ServerName}"));
                }
            }
        }

        output.WriteLine("matched: " + string.Join(", ", counts.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => Inv($"{p.Key} {p.Value}"))));
        output.WriteLine(Inv($"max deltas (yd): position {maxPosition:G4}, rotation {maxRotation:G4}, scale {maxScale:G4}, placed vertices {maxTriangle:G4} over {vertices} vertices"));
        output.WriteLine(Inv($"expected: {tilted} WMO doodads drawn with the client's composition (Rwmo * Rdoodad) differ from the server's stored Euler triple by up to {maxGeometric:G4} yd"));
        classes.Write(output);
        Assert.True(counts.GetValueOrDefault("wmo") >= 5 && counts.GetValueOrDefault("m2") >= 50 && counts.GetValueOrDefault("doodad") >= 50, "too few matched instances: " + string.Join(", ", counts));
        Assert.True(maxPosition <= WorldTolerance, Inv($"position delta {maxPosition}"));
        Assert.True(maxRotation <= WorldTolerance, Inv($"rotation delta {maxRotation}"));
        Assert.True(maxScale <= WorldTolerance, Inv($"scale delta {maxScale}"));
        Assert.True(maxTriangle <= WorldTolerance, Inv($"placed vertex delta {maxTriangle}"));
        Assert.Equal(0, classes.UnexplainedCount);
    }

    [RealClientModelsFact]
    public void LineOfSight_AgreesWithTheServerAroundGoldshire()
    {
        DirectoryClientFiles files = RealClientModels.Files;
        var services = new AtlasServices();
        services.Add<IClientFileSource>(files);
        services.Add<IClientPlacementSource>(new AdtPlacementReader(files));
        var cache = new ClientModelCache();
        var queries = new ReferenceModelQueries(services, cache);
        var manager = new VMapManager(RealClientModels.VmapDirectory);
        for (int x = GoldshireTile.X - 1; x <= GoldshireTile.X + 1; x++)
        {
            for (int y = GoldshireTile.Y - 1; y <= GoldshireTile.Y + 1; y++)
            {
                _ = manager.LoadTile(GoldshireTile.MapId, x, y);
            }
        }

        // Segments around the WMOs the Goldshire tile owns (centres from the data, not invented coordinates).
        IReadOnlyList<ClientPlacement> placements = new AdtPlacementReader(files).Placements(GoldshireTile)!;
        TilePlan plan = ModelPlanner.Plan(GoldshireTile.MapId, GoldshireTile, placements, PlanScope.Wmo, files, cache, CancellationToken.None);
        List<WorldPoint> centres = [.. plan.Wmos.Where(w => w.SkipReason is null).Select(w => w.Position).Take(12)];
        Assert.True(centres.Count >= 3, Inv($"only {centres.Count} WMOs owned by {GoldshireTile}"));

        var random = new SplitMix(5875);
        int segments = 0;
        int spellBlocked = 0;
        int movementBlocked = 0;
        var disagreements = new List<string>();
        foreach (WorldPoint centre in centres)
        {
            for (int i = 0; i < 30; i++)
            {
                WorldPoint a = Around(random, centre);
                WorldPoint b = Around(random, centre);
                JsonObject result = queries.LineOfSight(GoldshireTile.MapId, a, b, false, CancellationToken.None);
                bool refSpell = result["spell"]!["clear"]!.GetValue<bool>();
                bool refMovement = result["movement"]!["clear"]!.GetValue<bool>();
                bool serverSpell = manager.IsInLineOfSight(GoldshireTile.MapId, ToVector(a), ToVector(b), ignoreM2: true);
                bool serverMovement = manager.IsInLineOfSight(GoldshireTile.MapId, ToVector(a), ToVector(b), ignoreM2: false);
                segments++;
                spellBlocked += serverSpell ? 0 : 1;
                movementBlocked += serverMovement ? 0 : 1;
                if (refSpell != serverSpell || refMovement != serverMovement)
                {
                    disagreements.Add(Inv($"{a} -> {b}: spell ref {refSpell} server {serverSpell}, movement ref {refMovement} server {serverMovement}; ref hit {result["movement"]!["hit"]?.ToJsonString()}"));
                }
            }
        }

        output.WriteLine(Inv($"segments {segments}; server blocked: spell {spellBlocked}, movement {movementBlocked}; disagreements {disagreements.Count}"));
        foreach (string line in disagreements.Take(20))
        {
            output.WriteLine("  " + line);
        }

        Assert.True(spellBlocked >= 30 && segments - spellBlocked >= 30, Inv($"not a meaningful sample: {spellBlocked} of {segments} spell segments blocked"));
        Assert.True(disagreements.Count * 100 <= segments, Inv($"{disagreements.Count} of {segments} segments disagree with the server"));
    }

    // Goldshire and Stormwind, Ironforge (map 0) and Orgrimmar (map 1): 3x3 tile blocks (map, tx, ty).
    private static IEnumerable<TileKey> RegionTiles()
    {
        (uint Map, int X, int Y)[] centres = [(0, 49, 31), (0, 48, 30), (0, 41, 33), (1, 28, 40)];
        var seen = new HashSet<TileKey>();
        foreach ((uint map, int cx, int cy) in centres)
        {
            for (int x = cx - 1; x <= cx + 1; x++)
            {
                for (int y = cy - 1; y <= cy + 1; y++)
                {
                    var tile = new TileKey(map, x, y);
                    if (seen.Add(tile))
                    {
                        yield return tile;
                    }
                }
            }
        }
    }

    private static string? CompareWmo(WmoModel client, WorldModel server, ModelStats stats)
    {
        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/vmapexport.cpp:205-218 (the kept groups, in file order, are the model's groups)
        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.cpp:274-353 (precision mode: mogp flags, group id, then every MOVI index and MOVT vertex unchanged)
        List<int> kept = [.. Enumerable.Range(0, client.Groups.Count).Where(i => client.SkipReason(i) is null)];
        if (server.RootWmoId != client.Root.Header.WmoId)
        {
            return Inv($"root id {client.Root.Header.WmoId}, server {server.RootWmoId}");
        }

        if (kept.Count != server.Groups.Count)
        {
            return Inv($"{kept.Count} kept groups, server {server.Groups.Count}");
        }

        double delta = 0;
        for (int k = 0; k < kept.Count; k++)
        {
            WmoGroupFile group = client.Groups[kept[k]];
            GroupModel model = server.Groups[k];
            if (group.Flags != model.MogpFlags || group.GroupId != model.GroupWmoId)
            {
                return Inv($"group {kept[k]} flags/id {group.Flags:X}/{group.GroupId}, server {model.MogpFlags:X}/{model.GroupWmoId}");
            }

            if (group.Vertices.Length != model.Vertices.Count || group.TriangleCount != model.Triangles.Count)
            {
                return Inv($"group {kept[k]} {group.Vertices.Length} vertices / {group.TriangleCount} triangles, server {model.Vertices.Count} / {model.Triangles.Count}");
            }

            for (int v = 0; v < group.Vertices.Length; v++)
            {
                delta = Math.Max(delta, (group.Vertices[v] - model.Vertices[v]).Length());
            }

            for (int t = 0; t < group.TriangleCount; t++)
            {
                MeshTriangle triangle = model.Triangles[t];
                if (triangle.Index0 != group.Indices[3 * t] || triangle.Index1 != group.Indices[(3 * t) + 1] || triangle.Index2 != group.Indices[(3 * t) + 2])
                {
                    return Inv($"group {kept[k]} triangle {t} differs");
                }

                if (!WmoMaterialFlags.IsCollidable(group.TriangleFlags[t]))
                {
                    stats.RenderOnlyKept++;
                }
            }

            if (CompareLiquid(group.Liquid, model.Liquid, stats) is { } liquidReason)
            {
                return Inv($"group {kept[k]} liquid: {liquidReason}");
            }

            stats.WmoTriangles += group.TriangleCount;
        }

        if (delta > ModelTolerance)
        {
            return Inv($"vertex delta {delta}");
        }

        stats.MaxDelta = Math.Max(stats.MaxDelta, delta);
        stats.WmoGroups += kept.Count;
        stats.SkippedGroups += client.Groups.Count - kept.Count;
        return null;
    }

    // The extractor copies the MLIQ grid unchanged except the liquid type, which it recomputes from the tile flags and the
    // root flags; the type is therefore not compared.
    // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.cpp:249-259 (MLIQ read: header, xverts * yverts vertices, xtiles * ytiles flag bytes)
    // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.cpp:432-506 (LIQU: type rewritten, then the header, every vertex height, every flag byte)
    // ref: src/ArcaneCore.Game/Maps/Collision/VMaps/WorldModel.cs:251-297 (WmoLiquid: tiles x/y, corner, type, heights, flags)
    private static string? CompareLiquid(WmoLiquidGrid? client, WmoLiquid? server, ModelStats stats)
    {
        if (client is null || server is null)
        {
            return client is null && server is null ? null : Inv($"client {(client is null ? "none" : "MLIQ")}, server {(server is null ? "none" : "LIQU")}");
        }

        if (client.TilesX != server.TilesX || client.TilesY != server.TilesY || client.Heights.Length != server.Heights.Length)
        {
            return Inv($"{client.TilesX}x{client.TilesY} tiles / {client.Heights.Length} heights, server {server.TilesX}x{server.TilesY} / {server.Heights.Length}");
        }

        double delta = (client.Corner - server.Corner).Length();
        for (int i = 0; i < client.Heights.Length; i++)
        {
            delta = Math.Max(delta, Math.Abs(client.Heights[i] - server.Heights[i]));
        }

        if (delta > ModelTolerance || !client.TileFlags.AsSpan().SequenceEqual(server.Flags))
        {
            return Inv($"corner/height delta {delta} or tile flags differ");
        }

        stats.LiquidGroups++;
        return null;
    }

    private static string? CompareM2(M2ModelFile client, WorldModel server, ModelStats stats)
    {
        if (!client.HasCollisionHull)
        {
            return "the client model has no bounding hull";
        }

        if (server.Groups.Count != 1)
        {
            return Inv($"server model has {server.Groups.Count} groups");
        }

        GroupModel model = server.Groups[0];
        if (client.BoundingVertices.Length != model.Vertices.Count || client.BoundingTriangleCount != model.Triangles.Count)
        {
            return Inv($"{client.BoundingVertices.Length} vertices / {client.BoundingTriangleCount} triangles, server {model.Vertices.Count} / {model.Triangles.Count}");
        }

        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/model.cpp:56 and :142-145 (load: fixCoordSystem (x, z, -y))
        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/model.cpp:124-131 (write: y = -z, z = old y), so the two swaps compose to the identity
        double delta = 0;
        for (int v = 0; v < client.BoundingVertices.Length; v++)
        {
            delta = Math.Max(delta, (client.BoundingVertices[v] - model.Vertices[v]).Length());
        }

        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/model.cpp:108-117 (the second and third index of each triangle are swapped)
        for (int t = 0; t < model.Triangles.Count; t++)
        {
            MeshTriangle triangle = model.Triangles[t];
            if (triangle.Index0 != client.BoundingIndices[3 * t] || triangle.Index1 != client.BoundingIndices[(3 * t) + 2] || triangle.Index2 != client.BoundingIndices[(3 * t) + 1])
            {
                return Inv($"triangle {t} differs (after the winding swap)");
            }
        }

        if (delta > ModelTolerance)
        {
            return Inv($"vertex delta {delta}");
        }

        stats.MaxDelta = Math.Max(stats.MaxDelta, delta);
        stats.M2Triangles += model.Triangles.Count;
        return null;
    }

    private static List<Expected> ExpectedSpawns(TilePlan plan, IClientFileSource files, ClientModelCache cache, MismatchClasses classes)
    {
        var expected = new List<Expected>();
        foreach (PlannedWmo wmo in plan.Wmos)
        {
            if (wmo.SkipReason is not null)
            {
                classes.AddExpected("WMO the extractor drops: " + wmo.SkipReason, Inv($"{wmo.Placement.UniqueId} {wmo.Model.Path}"));
                continue;
            }

            WmoModel model = wmo.Model;
            List<int> kept = [.. Enumerable.Range(0, model.Groups.Count).Where(i => model.SkipReason(i) is null)];
            expected.Add(new Expected(
                "wmo",
                false,
                wmo.Placement.UniqueId,
                -1,
                ClientModelNames.ServerModelName(model.Path),
                wmo.Transform,
                wmo.Transform.Rotation,
                Corners(model.Root.Header.BoundsMin, model.Root.Header.BoundsMax),
                server => kept.SelectMany((g, k) => model.Groups[g].Vertices.Select((v, i) => (v, server.Groups[k].Vertices[i])))));
        }

        foreach (PlannedM2 m2 in plan.M2s)
        {
            if (m2.SkipReason is not null)
            {
                classes.AddExpected((m2.IsWmoDoodad ? "WMO doodad" : "MDDF doodad") + " the extractor drops: " + m2.SkipReason, Inv($"{m2.UniqueId}/{m2.DoodadIndex} {m2.ModelPath}"));
                continue;
            }

            M2ModelFile model = m2.Model;
            Mat3d serverRotation = m2.Transform.Rotation;
            if (m2.IsWmoDoodad)
            {
                WmoModel parent = cache.GetWmo(files, ClientModelNames.ClientFilePath(m2.Source.ModelPath)).Model!;
                WmoDoodad doodad = parent.Root.Doodads[m2.DoodadIndex];
                (float x, float y, float z) = PlacementTransform.ExtractorDoodadRotation(doodad.Rotation, m2.Source.Rotation);
                serverRotation = PlacementTransform.ServerRotation(x, y, z);
            }

            (Vector3 min, Vector3 max) = model.BoundingBox;
            expected.Add(new Expected(
                m2.IsWmoDoodad ? "doodad" : "m2",
                true,
                m2.UniqueId,
                m2.IsWmoDoodad ? m2.DoodadIndex : -1,
                ClientModelNames.ServerModelName(m2.ModelPath),
                m2.Transform,
                serverRotation,
                Corners(min, max),
                server => model.BoundingVertices.Select((v, i) => (v, server.Groups[0].Vertices[i]))));
        }

        return expected;
    }

    private static WorldModel ServerModel(Dictionary<string, WorldModel> models, string spawnName)
    {
        string file = VMapFormat.ModelFileName(spawnName);
        if (!models.TryGetValue(file, out WorldModel? model))
        {
            model = WorldModel.Parse(File.ReadAllBytes(Path.Combine(RealClientModels.VmapDirectory, file)));
            models[file] = model;
        }

        return model;
    }

    private static Vec3d[] Corners(Vector3 min, Vector3 max)
    {
        var corners = new Vec3d[8];
        for (int i = 0; i < 8; i++)
        {
            corners[i] = new Vec3d((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
        }

        return corners;
    }

    // How far the model's bounding corners move between two rotations, in yards.
    private static double CornerDelta(Vec3d[] corners, double scale, Mat3d a, Mat3d b)
    {
        double delta = 0;
        foreach (Vec3d corner in corners)
        {
            delta = Math.Max(delta, (a.Transform(corner * scale) - b.Transform(corner * scale)).Length);
        }

        return delta;
    }

    private static double Radius(Vec3d[] corners) => corners.Max(c => c.Length);

    private static double Distance(WorldPoint a, WorldPoint b)
        => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)) + ((a.Z - b.Z) * (a.Z - b.Z)));

    private static WorldPoint Around(SplitMix random, WorldPoint centre)
        => new(centre.X + random.Range(-40, 40), centre.Y + random.Range(-40, 40), centre.Z + random.Range(0, 12));

    private static Vector3 ToVector(WorldPoint p) => new((float)p.X, (float)p.Y, (float)p.Z);

    private static void Add(Dictionary<string, HashSet<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out HashSet<string>? set))
        {
            map[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        set.Add(value);
    }

    private static void Count(Dictionary<string, int> counts, string key) => counts[key] = counts.GetValueOrDefault(key) + 1;

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);

    private sealed class ModelStats
    {
        public long WmoGroups { get; set; }

        public long SkippedGroups { get; set; }

        public long WmoTriangles { get; set; }

        public long M2Triangles { get; set; }

        public long RenderOnlyKept { get; set; }

        public long LiquidGroups { get; set; }

        public double MaxDelta { get; set; }
    }

    /// <summary>One spawn the server should have for a kept client instance.</summary>
    private sealed record Expected(
        string Kind,
        bool IsM2,
        uint UniqueId,
        int Doodad,
        string ServerName,
        InstanceTransform Transform,
        Mat3d ServerRotation,
        Vec3d[] Corners,
        Func<WorldModel, IEnumerable<(Vector3 Client, Vector3 Server)>> Vertices)
    {
        public Vec3d Position => Transform.Position;

        public double Scale => Transform.Scale;
    }

    /// <summary>Mismatches grouped by class: expected ones follow a documented extractor rule, the rest fail the test.</summary>
    private sealed class MismatchClasses
    {
        private readonly SortedDictionary<string, List<string>> _expected = new(StringComparer.Ordinal);
        private readonly SortedDictionary<string, List<string>> _unexplained = new(StringComparer.Ordinal);

        public int UnexplainedCount => _unexplained.Values.Sum(v => v.Count);

        public void AddExpected(string kind, string detail) => Add(_expected, kind, detail);

        public void AddUnexplained(string kind, string detail) => Add(_unexplained, kind, detail);

        public void Write(ITestOutputHelper output)
        {
            foreach ((string kind, List<string> details) in _expected)
            {
                output.WriteLine(Inv($"expected [{details.Count}] {kind}: {string.Join(" | ", details.Take(3))}"));
            }

            foreach ((string kind, List<string> details) in _unexplained)
            {
                output.WriteLine(Inv($"UNEXPLAINED [{details.Count}] {kind}"));
                foreach (string detail in details.Take(15))
                {
                    output.WriteLine("  " + detail);
                }
            }
        }

        private static void Add(SortedDictionary<string, List<string>> map, string kind, string detail)
        {
            if (!map.TryGetValue(kind, out List<string>? list))
            {
                map[kind] = list = [];
            }

            list.Add(detail);
        }
    }
}
