using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Client.Models;
using ArcaneCore.Atlas.Server;
using ArcaneCore.Game.Maps.Collision.VMaps;

namespace ArcaneCore.Atlas.Diff;

/// <summary>
/// Compares the server's parsed <c>.vmtile</c> / <c>.vmtree</c> spawns and <c>.vmo</c> models (<see cref="WorldModel"/>)
/// against the 1.12.1 client ADT/WDT placements and WMO/M2 files (<see cref="ModelPlanner"/>), classifying known
/// <c>vmap_extractor</c> and <c>vmap_assembler</c> differences as <see cref="ColorKeys.Expected"/> and flagging any
/// unexplained placement or geometry differences as <see cref="ColorKeys.OnlyReference"/> or <see cref="ColorKeys.OnlyServer"/>.
/// </summary>
internal static class ModelDiff
{
    /// <summary>Model-space vertex tolerance in yards.</summary>
    public const double ModelTolerance = 1e-3;

    /// <summary>World-space placed vertex tolerance in yards.</summary>
    public const double WorldTolerance = 1e-2;

    /// <summary>Maximum distance in yards between a client placement and a matching server spawn of the same model.</summary>
    public const double MatchRadius = 1.0;

    private const int MaxExamples = 20;
    private const long BytesPerTriangle = 48;

    /// <summary>Builds the <see cref="LayerIds.DiffModels"/> geometry layer for <paramref name="tile"/>.</summary>
    public static LayerBuild BuildLayer(AtlasServices services, TileKey tile, CancellationToken cancellationToken)
    {
        ServerData? serverData = services.Get<ServerData>();
        if (serverData is null)
        {
            return LayerBuild.Missing(LayerIds.DiffModels, tile, "No ServerData service is registered.");
        }

        IClientFileSource? files = services.Get<IClientFileSource>();
        if (files is null)
        {
            return LayerBuild.Missing(LayerIds.DiffModels, tile, "No client file source (MPQ or directory) is registered.");
        }

        IClientPlacementSource? placements = services.Get<IClientPlacementSource>();
        if (placements is null)
        {
            return LayerBuild.Missing(LayerIds.DiffModels, tile, "No client placement source is registered.");
        }

        ClientModelCache cache = services.Get<ClientModelCache>() ?? new ClientModelCache();

        ParsedFile<VMapTreeData> treeFile = serverData.VMapTreeFile(tile.MapId);
        if (!treeFile.IsOk)
        {
            return treeFile.State == TileState.Rejected
                ? LayerBuild.Rejected(LayerIds.DiffModels, tile, $"Server .vmtree rejected: {treeFile.Detail}")
                : LayerBuild.Missing(LayerIds.DiffModels, tile, $"Server .vmtree missing: {treeFile.Detail}");
        }

        VMapTreeData tree = treeFile.Value;
        if (tile.IsGlobal && tree.IsTiled)
        {
            return new LayerBuild(new TileStatus(LayerIds.DiffModels, tile, TileState.NotApplicable, "Map uses tiled vmaps; the global pseudo-tile has no models."), []);
        }

        if (!tile.IsGlobal && !tree.IsTiled)
        {
            return new LayerBuild(new TileStatus(LayerIds.DiffModels, tile, TileState.NotApplicable, "Map uses a global vmtree; grid tiles have no .vmtile files."), []);
        }

        IReadOnlyList<VMapSpawn> serverSpawns;
        if (tile.IsGlobal)
        {
            serverSpawns = tree.GlobalSpawns;
        }
        else
        {
            ParsedFile<VMapTileData> vmtile = serverData.VMapTileFile(tile);
            if (!vmtile.IsOk)
            {
                return vmtile.State == TileState.Rejected
                    ? LayerBuild.Rejected(LayerIds.DiffModels, tile, $"Server .vmtile rejected: {vmtile.Detail}")
                    : LayerBuild.Missing(LayerIds.DiffModels, tile, $"Server .vmtile missing: {vmtile.Detail}");
            }

            serverSpawns = vmtile.Value.Spawns;
        }

        PlacementLoad clientPlacements = ReadPlacements(placements, tile);
        if (clientPlacements.State == TileState.Rejected)
        {
            return LayerBuild.Rejected(LayerIds.DiffModels, tile, $"Client placements rejected: {clientPlacements.Detail}");
        }

        if (clientPlacements.State != TileState.Ok && clientPlacements.State != TileState.Empty)
        {
            return LayerBuild.Missing(LayerIds.DiffModels, tile, $"Client placements missing: {clientPlacements.Detail}");
        }

        var geometry = new PartedGeometry(LayerIds.DiffModels, tile, WorldGrid.Origin(tile));
        TileReport report = CompareTile(tile, tree, serverSpawns, serverData, clientPlacements.Placements, files, cache, geometry, cancellationToken);
        var stats = new JsonObject();
        WriteStats(stats, report);
        IReadOnlyList<GeometryChunk> chunks = geometry.Finish(stats);

        if (chunks.Count == 0)
        {
            return LayerBuild.Empty(
                LayerIds.DiffModels,
                tile,
                FormattableString.Invariant($"All {report.MatchedSpawns} matched spawns and collision triangles agreed within {WorldTolerance:G} yd."));
        }

        string detail = FormattableString.Invariant(
            $"{report.UnexplainedCount} unexplained, {report.ExpectedCount} expected model difference(s) ({geometry.TriangleCount:N0} triangles drawn)");
        return LayerBuild.Of(LayerIds.DiffModels, tile, chunks, detail);
    }

    /// <summary>Runs the model diff over a tile neighbourhood and returns a structured JSON report.</summary>
    public static JsonObject RunCommand(AtlasServices services, uint mapId, int centerX, int centerY, int radius, CancellationToken cancellationToken)
    {
        ServerData? serverData = services.Get<ServerData>();
        IClientFileSource? files = services.Get<IClientFileSource>();
        IClientPlacementSource? placements = services.Get<IClientPlacementSource>();
        ClientModelCache cache = services.Get<ClientModelCache>() ?? new ClientModelCache();

        var expectedClasses = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var unexplainedClasses = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var examples = new List<string>();
        var tileSummaries = new JsonArray();

        long totalServerSpawns = 0;
        long totalMatchedSpawns = 0;
        long totalDuplicateSpawnIds = 0;
        long totalSkippedWmos = 0;
        long totalSkippedWmoGroups = 0;
        long totalRenderOnlyTrianglesKept = 0;
        long totalSkippedM2s = 0;
        long totalTiltedDoodads = 0;
        long totalUnmatchedClient = 0;
        long totalUnmatchedServer = 0;
        long totalGeometryMismatches = 0;
        double maxPositionDelta = 0;
        double maxVertexDelta = 0;
        int comparedTiles = 0;
        int missingTiles = 0;
        int rejectedTiles = 0;

        int minX = Math.Max(0, centerX - radius);
        int maxX = Math.Min(WorldGrid.TilesPerSide - 1, centerX + radius);
        int minY = Math.Max(0, centerY - radius);
        int maxY = Math.Min(WorldGrid.TilesPerSide - 1, centerY + radius);

        ParsedFile<VMapTreeData>? treeFile = serverData?.VMapTreeFile(mapId);

        for (int tx = minX; tx <= maxX; tx++)
        {
            for (int ty = minY; ty <= maxY; ty++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tile = new TileKey(mapId, tx, ty);
                if (serverData is null || files is null || placements is null || treeFile is null)
                {
                    missingTiles++;
                    tileSummaries.Add(new JsonObject
                    {
                        ["tile"] = new JsonArray(mapId, tx, ty),
                        ["state"] = TileState.Missing.ToString(),
                        ["detail"] = "Required server or client model services are not registered.",
                    });
                    continue;
                }

                if (!treeFile.Value.IsOk)
                {
                    if (treeFile.Value.State == TileState.Rejected)
                    {
                        rejectedTiles++;
                    }
                    else
                    {
                        missingTiles++;
                    }

                    tileSummaries.Add(new JsonObject
                    {
                        ["tile"] = new JsonArray(mapId, tx, ty),
                        ["state"] = treeFile.Value.State.ToString(),
                        ["detail"] = treeFile.Value.Detail,
                    });
                    continue;
                }

                VMapTreeData tree = treeFile.Value.Value;
                ParsedFile<VMapTileData> vmtile = serverData.VMapTileFile(tile);
                PlacementLoad clientLoad = ReadPlacements(placements, tile);

                if (vmtile.State == TileState.Rejected || clientLoad.State == TileState.Rejected)
                {
                    rejectedTiles++;
                    string reason = vmtile.State == TileState.Rejected ? $"Server .vmtile rejected: {vmtile.Detail}" : $"Client placements rejected: {clientLoad.Detail}";
                    AddCount(unexplainedClasses, "rejected-tile", 1);
                    AddExample(examples, $"{tile}: {reason}");
                    tileSummaries.Add(new JsonObject
                    {
                        ["tile"] = new JsonArray(mapId, tx, ty),
                        ["state"] = TileState.Rejected.ToString(),
                        ["detail"] = reason,
                    });
                    continue;
                }

                if (!vmtile.IsOk || (clientLoad.State != TileState.Ok && clientLoad.State != TileState.Empty))
                {
                    missingTiles++;
                    tileSummaries.Add(new JsonObject
                    {
                        ["tile"] = new JsonArray(mapId, tx, ty),
                        ["state"] = TileState.Missing.ToString(),
                        ["detail"] = !vmtile.IsOk ? vmtile.Detail : clientLoad.Detail,
                    });
                    continue;
                }

                comparedTiles++;
                TileReport report = CompareTile(tile, tree, vmtile.Value.Spawns, serverData, clientLoad.Placements, files, cache, geometry: null, cancellationToken);
                totalServerSpawns += report.ServerSpawns;
                totalMatchedSpawns += report.MatchedSpawns;
                totalDuplicateSpawnIds += report.DuplicateSpawnIds;
                totalSkippedWmos += report.SkippedWmos;
                totalSkippedWmoGroups += report.SkippedWmoGroups;
                totalRenderOnlyTrianglesKept += report.RenderOnlyTrianglesKept;
                totalSkippedM2s += report.SkippedM2s;
                totalTiltedDoodads += report.TiltedDoodads;
                totalUnmatchedClient += report.UnmatchedClientInstances;
                totalUnmatchedServer += report.UnmatchedServerSpawns;
                totalGeometryMismatches += report.GeometryMismatches;
                maxPositionDelta = Math.Max(maxPositionDelta, report.MaxPositionDelta);
                maxVertexDelta = Math.Max(maxVertexDelta, report.MaxVertexDelta);

                foreach ((string k, long v) in report.ExpectedClasses)
                {
                    AddCount(expectedClasses, k, v);
                }

                foreach ((string k, long v) in report.UnexplainedClasses)
                {
                    AddCount(unexplainedClasses, k, v);
                }

                foreach (string ex in report.Examples)
                {
                    AddExample(examples, ex);
                }

                tileSummaries.Add(new JsonObject
                {
                    ["tile"] = new JsonArray(mapId, tx, ty),
                    ["state"] = (report.UnexplainedCount == 0 && report.ExpectedCount == 0) ? TileState.Empty.ToString() : TileState.Ok.ToString(),
                    ["serverSpawns"] = report.ServerSpawns,
                    ["matchedSpawns"] = report.MatchedSpawns,
                    ["unexplainedCount"] = report.UnexplainedCount,
                    ["expectedCount"] = report.ExpectedCount,
                });
            }
        }

        long totalUnexplained = unexplainedClasses.Values.Sum();
        long totalExpected = expectedClasses.Values.Sum();

        return new JsonObject
        {
            ["layer"] = LayerIds.DiffModels,
            ["map"] = mapId,
            ["center"] = new JsonArray(centerX, centerY),
            ["radius"] = radius,
            ["tilesCompared"] = comparedTiles,
            ["tilesMissing"] = missingTiles,
            ["tilesRejected"] = rejectedTiles,
            ["unexplainedCount"] = totalUnexplained,
            ["expectedCount"] = totalExpected,
            ["totals"] = new JsonObject
            {
                ["serverSpawns"] = totalServerSpawns,
                ["matchedSpawns"] = totalMatchedSpawns,
                ["duplicateSpawnIds"] = totalDuplicateSpawnIds,
                ["skippedWmos"] = totalSkippedWmos,
                ["skippedWmoGroups"] = totalSkippedWmoGroups,
                ["renderOnlyTrianglesKept"] = totalRenderOnlyTrianglesKept,
                ["skippedM2s"] = totalSkippedM2s,
                ["tiltedDoodads"] = totalTiltedDoodads,
                ["unmatchedClientInstances"] = totalUnmatchedClient,
                ["unmatchedServerSpawns"] = totalUnmatchedServer,
                ["geometryMismatches"] = totalGeometryMismatches,
                ["maxPositionDelta"] = maxPositionDelta,
                ["maxVertexDelta"] = maxVertexDelta,
            },
            ["classes"] = new JsonObject
            {
                ["expected"] = ToJsonObject(expectedClasses),
                ["unexplained"] = ToJsonObject(unexplainedClasses),
            },
            ["tiles"] = tileSummaries,
            ["examples"] = new JsonArray([.. examples.Select(e => (JsonNode)e)]),
        };
    }

    private static TileReport CompareTile(
        TileKey tile,
        VMapTreeData tree,
        IReadOnlyList<VMapSpawn> spawns,
        ServerData serverData,
        IReadOnlyList<ClientPlacement> placements,
        IClientFileSource files,
        ClientModelCache cache,
        PartedGeometry? geometry,
        CancellationToken cancellationToken)
    {
        var report = new TileReport
        {
            ServerSpawns = spawns.Count,
        };

        // Plan all placements listed by the tile's ADT (onlyOwnedBy: null) so multi-tile spans listed in this .vmtile pair cleanly.
        TilePlan plan = ModelPlanner.Plan(tile.MapId, onlyOwnedBy: null, placements, PlanScope.All, files, cache, cancellationToken);
        if (plan.Problems.Missing > 0)
        {
            report.NoteExpected("missing-client-model-file", plan.Problems.Missing);
        }

        if (plan.Problems.Rejected > 0)
        {
            foreach (string sample in plan.Problems.Samples)
            {
                report.NoteUnexplained("rejected-client-model", $"{tile}: {sample}");
            }
        }

        bool OwnsPoint(WorldPoint p) => tile.IsGlobal || WorldGrid.TileOf(tile.MapId, p.X, p.Y) == tile;

        var candidates = new List<ExpectedSpawn>();
        foreach (PlannedWmo wmo in plan.Wmos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool ownedHere = OwnsPoint(wmo.Position);
            if (wmo.SkipReason is not null)
            {
                // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.cpp:562, 583-584
                if (ownedHere)
                {
                    report.SkippedWmos++;
                    report.NoteExpected("extractor-skipped-wmo");
                    DrawSkippedWmo(geometry, wmo);
                }

                continue;
            }

            WmoModel model = wmo.Model;
            var keptGroups = new List<int>();
            for (int g = 0; g < model.Groups.Count; g++)
            {
                string? groupSkip = model.SkipReason(g);
                WmoGroupFile group = model.Groups[g];
                if (groupSkip is not null)
                {
                    // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.cpp:512-526 (unreachable 0x80, antiportal 0x4000000)
                    if (ownedHere && group.TriangleCount > 0)
                    {
                        report.SkippedWmoGroups++;
                        report.NoteExpected("extractor-skipped-wmo-group");
                        DrawWmoGroup(geometry, wmo, g, ColorKeys.Expected, "extractor-skipped-wmo-group",
                            $"WMO group skipped by vmap_extractor ({groupSkip})", onlyRenderOnly: false);
                    }
                }
                else
                {
                    keptGroups.Add(g);
                    if (ownedHere)
                    {
                        int renderOnly = group.TriangleFlags.Count(f => !WmoMaterialFlags.IsCollidable(f));
                        if (renderOnly > 0)
                        {
                            // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.cpp:280-354 (precise mode keeps all MOVI triangles)
                            report.RenderOnlyTrianglesKept += renderOnly;
                            report.NoteExpected("wmo-render-only-kept-by-server", renderOnly);
                            DrawWmoGroup(geometry, wmo, g, ColorKeys.Expected, "wmo-render-only-kept-by-server",
                                "MOPY marks these triangles render-only, but vmap_extractor precise mode writes them as collision triangles",
                                onlyRenderOnly: true);
                        }
                    }
                }
            }

            candidates.Add(new ExpectedSpawn(
                Kind: "wmo",
                IsM2: false,
                UniqueId: wmo.Placement.UniqueId,
                DoodadIndex: -1,
                ServerName: ClientModelNames.ServerModelName(model.Path),
                Transform: wmo.Transform,
                ServerRotation: wmo.Transform.Rotation,
                WorldPosition: wmo.Position,
                OwnedHere: ownedHere,
                Wmo: wmo,
                M2: null,
                KeptGroups: keptGroups));
        }

        foreach (PlannedM2 m2 in plan.M2s)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool ownedHere = OwnsPoint(m2.Position);
            if (m2.SkipReason is not null)
            {
                // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/model.cpp:49, 183; vmapexport.cpp:219-229
                if (ownedHere)
                {
                    report.SkippedM2s++;
                    report.NoteExpected("extractor-skipped-m2");
                }

                continue;
            }

            Mat3d serverRotation = m2.Transform.Rotation;
            if (m2.IsWmoDoodad)
            {
                WmoModel? parent = cache.GetWmo(files, ClientModelNames.ClientFilePath(m2.Source.ModelPath)).Model;
                if (parent is not null && m2.DoodadIndex >= 0 && m2.DoodadIndex < parent.Root.Doodads.Count)
                {
                    WmoDoodad doodad = parent.Root.Doodads[m2.DoodadIndex];
                    (float rx, float ry, float rz) = PlacementTransform.ExtractorDoodadRotation(doodad.Rotation, m2.Source.Rotation);
                    serverRotation = PlacementTransform.ServerRotation(rx, ry, rz);
                }
            }

            candidates.Add(new ExpectedSpawn(
                Kind: m2.IsWmoDoodad ? "doodad" : "m2",
                IsM2: true,
                UniqueId: m2.UniqueId,
                DoodadIndex: m2.IsWmoDoodad ? m2.DoodadIndex : -1,
                ServerName: ClientModelNames.ServerModelName(m2.ModelPath),
                Transform: m2.Transform,
                ServerRotation: serverRotation,
                WorldPosition: m2.Position,
                OwnedHere: ownedHere,
                Wmo: null,
                M2: m2,
                KeptGroups: null));
        }

        var used = new bool[candidates.Count];
        var seenSpawnIds = new HashSet<uint>();
        foreach (VMapSpawn entry in spawns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ModelSpawn spawn = entry.Spawn;
            if (entry.Slot >= (uint)tree.SlotCount)
            {
                report.GeometryMismatches++;
                report.NoteUnexplained("invalid-tree-slot", FormattableString.Invariant($"{tile} spawn {spawn.Id} slot {entry.Slot} >= tree slots {tree.SlotCount}"));
                continue;
            }

            if (!seenSpawnIds.Add(spawn.Id))
            {
                // ref: vmangos@0e3ff01/src/game/vmap/TileAssembler.cpp:245-246 (TileEntries multimap writes duplicate ADT entries twice)
                report.DuplicateSpawnIds++;
                report.NoteExpected("duplicate-tile-spawn-id");
                continue;
            }

            string modelName = DisplayName(spawn.Name);
            var spawnPos = new Vec3d(spawn.Position.X, spawn.Position.Y, spawn.Position.Z);
            int best = -1;
            double bestDistance = double.MaxValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                ExpectedSpawn c = candidates[i];
                if (used[i] || c.IsM2 != spawn.IsM2 || !string.Equals(c.ServerName, modelName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                double d = (c.Transform.Position - spawnPos).Length;
                if (d < bestDistance)
                {
                    best = i;
                    bestDistance = d;
                }
            }

            if (best < 0 || bestDistance > MatchRadius)
            {
                report.UnmatchedServerSpawns++;
                report.NoteUnexplained(
                    "server-only-spawn",
                    FormattableString.Invariant($"{tile} spawn {spawn.Id} ({modelName}) at {spawn.Position}: no matching client placement within {MatchRadius} yd"));
                ParsedFile<WorldModel> serverModel = serverData.ModelFile(spawn.Name);
                if (serverModel.IsOk)
                {
                    DrawServerInstance(geometry, new ModelInstance(spawn, serverModel.Value), spawn, ColorKeys.OnlyServer, "server-only-spawn",
                        "server vmap spawn has no matching placement in the client ADT");
                }

                continue;
            }

            used[best] = true;
            report.MatchedSpawns++;
            report.MaxPositionDelta = Math.Max(report.MaxPositionDelta, bestDistance);
            ExpectedSpawn match = candidates[best];

            ParsedFile<WorldModel> loadedModel = serverData.ModelFile(spawn.Name);
            if (!loadedModel.IsOk)
            {
                report.GeometryMismatches++;
                report.NoteUnexplained("missing-server-model", FormattableString.Invariant($"{tile} spawn {spawn.Id} model {modelName}: {loadedModel.Detail}"));
                continue;
            }

            WorldModel serverWorldModel = loadedModel.Value;
            var instance = new ModelInstance(spawn, serverWorldModel);
            var serverEquivalent = new InstanceTransform(match.ServerRotation, match.Transform.Position, match.Transform.Scale);

            double maxServerEquivDelta = 0;
            double maxClientComposedDelta = 0;
            bool countMismatch = false;

            if (match.IsM2)
            {
                M2ModelFile m2Model = match.M2!.Model;
                if (serverWorldModel.Groups.Count != 1 || serverWorldModel.Groups[0].Vertices.Count != m2Model.BoundingVertices.Length)
                {
                    countMismatch = true;
                }
                else
                {
                    IReadOnlyList<Vector3> sVerts = serverWorldModel.Groups[0].Vertices;
                    for (int v = 0; v < m2Model.BoundingVertices.Length; v++)
                    {
                        Vector3 cv = m2Model.BoundingVertices[v];
                        Vector3 sw = VMapFormat.ToWorld(instance.FromModel(sVerts[v]));
                        var swPt = new WorldPoint(sw.X, sw.Y, sw.Z);
                        maxServerEquivDelta = Math.Max(maxServerEquivDelta, Distance(serverEquivalent.ModelToWorld(Vec3d.From(cv)), swPt));
                        maxClientComposedDelta = Math.Max(maxClientComposedDelta, Distance(match.Transform.ModelToWorld(Vec3d.From(cv)), swPt));
                    }
                }
            }
            else
            {
                WmoModel wmoModel = match.Wmo!.Model;
                List<int> kept = match.KeptGroups!;
                if (serverWorldModel.Groups.Count != kept.Count)
                {
                    countMismatch = true;
                }
                else
                {
                    for (int k = 0; k < kept.Count; k++)
                    {
                        WmoGroupFile cg = wmoModel.Groups[kept[k]];
                        GroupModel sg = serverWorldModel.Groups[k];
                        if (cg.Vertices.Length != sg.Vertices.Count || cg.TriangleCount != sg.Triangles.Count)
                        {
                            countMismatch = true;
                            break;
                        }

                        for (int v = 0; v < cg.Vertices.Length; v++)
                        {
                            Vector3 cv = cg.Vertices[v];
                            Vector3 sw = VMapFormat.ToWorld(instance.FromModel(sg.Vertices[v]));
                            var swPt = new WorldPoint(sw.X, sw.Y, sw.Z);
                            maxServerEquivDelta = Math.Max(maxServerEquivDelta, Distance(serverEquivalent.ModelToWorld(Vec3d.From(cv)), swPt));
                            maxClientComposedDelta = Math.Max(maxClientComposedDelta, Distance(match.Transform.ModelToWorld(Vec3d.From(cv)), swPt));
                        }
                    }
                }
            }

            report.MaxVertexDelta = Math.Max(report.MaxVertexDelta, maxServerEquivDelta);

            if (countMismatch || maxServerEquivDelta > WorldTolerance)
            {
                report.GeometryMismatches++;
                report.NoteUnexplained(
                    "geometry-mismatch",
                    FormattableString.Invariant($"{tile} {match.Kind} {match.UniqueId} ({match.ServerName}): countMismatch={countMismatch}, vertexDelta={maxServerEquivDelta:G4} yd"));
                DrawServerInstance(geometry, instance, spawn, ColorKeys.OnlyServer, "geometry-mismatch",
                    "server model vertices or triangle counts disagree with the client model");
            }
            else if (match.DoodadIndex >= 0 && maxClientComposedDelta > WorldTolerance && match.OwnedHere)
            {
                // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.cpp:526-565 (extractor adds Euler angles instead of composing quaternions)
                report.TiltedDoodads++;
                report.NoteExpected("tilted-wmo-doodad-rotation");
                DrawClientM2(geometry, match.M2!, ColorKeys.Expected, "tilted-wmo-doodad-rotation",
                    FormattableString.Invariant($"WMO doodad quaternion composition differs from extractor Euler sum by up to {maxClientComposedDelta:G4} yd"));
            }
        }

        for (int i = 0; i < candidates.Count; i++)
        {
            if (used[i])
            {
                continue;
            }

            ExpectedSpawn missing = candidates[i];
            if (!missing.OwnedHere)
            {
                continue;
            }

            report.UnmatchedClientInstances++;
            report.NoteUnexplained(
                "client-only-instance",
                FormattableString.Invariant($"{tile} {missing.Kind} {missing.UniqueId}/{missing.DoodadIndex} ({missing.ServerName}) at {missing.WorldPosition}: not in server .vmtile"));
            if (missing.IsM2)
            {
                DrawClientM2(geometry, missing.M2!, ColorKeys.OnlyReference, "client-only-instance",
                    "client M2 placement has no matching spawn in the server .vmtile");
            }
            else
            {
                for (int g = 0; g < missing.Wmo!.Model.Groups.Count; g++)
                {
                    if (missing.Wmo.Model.SkipReason(g) is null)
                    {
                        DrawWmoGroup(geometry, missing.Wmo, g, ColorKeys.OnlyReference, "client-only-instance",
                            "client WMO placement has no matching spawn in the server .vmtile", onlyRenderOnly: false);
                    }
                }
            }
        }

        return report;
    }

    private static void DrawSkippedWmo(PartedGeometry? geometry, PlannedWmo wmo)
    {
        if (geometry is null)
        {
            return;
        }

        for (int g = 0; g < wmo.Model.Groups.Count; g++)
        {
            if (wmo.Model.Groups[g].TriangleCount > 0)
            {
                DrawWmoGroup(geometry, wmo, g, ColorKeys.Expected, "extractor-skipped-wmo",
                    $"WMO instance skipped by vmap_extractor ({wmo.SkipReason})", onlyRenderOnly: false);
            }
        }
    }

    private static void DrawWmoGroup(
        PartedGeometry? geometry,
        PlannedWmo wmo,
        int groupIndex,
        string colorKey,
        string className,
        string note,
        bool onlyRenderOnly)
    {
        if (geometry is null)
        {
            return;
        }

        WmoGroupFile group = wmo.Model.Groups[groupIndex];
        int count = onlyRenderOnly
            ? group.TriangleFlags.Count(f => !WmoMaterialFlags.IsCollidable(f))
            : group.TriangleCount;
        if (count == 0)
        {
            return;
        }

        string key = FormattableString.Invariant($"wmo:{wmo.Placement.UniqueId}:{groupIndex}:{className}");
        uint attr = geometry.Attribute(key, () => new JsonObject
        {
            ["colorKey"] = colorKey,
            ["kind"] = "wmo",
            ["class"] = className,
            ["model"] = wmo.Model.Path,
            ["uniqueId"] = wmo.Placement.UniqueId,
            ["group"] = groupIndex,
            ["groupName"] = wmo.Model.GroupName(groupIndex),
            ["triangles"] = count,
            ["note"] = note,
        });

        GeometryBuilder builder = geometry.Writable(count * BytesPerTriangle);
        geometry.Describe(builder, attr);
        var remap = new uint[group.Vertices.Length];
        Array.Fill(remap, uint.MaxValue);

        for (int t = 0; t < group.TriangleCount; t++)
        {
            if (onlyRenderOnly && WmoMaterialFlags.IsCollidable(group.TriangleFlags[t]))
            {
                continue;
            }

            uint a = MapClientVertex(builder, remap, group.Vertices, group.Indices[3 * t], wmo.Transform);
            uint b = MapClientVertex(builder, remap, group.Vertices, group.Indices[(3 * t) + 1], wmo.Transform);
            uint c = MapClientVertex(builder, remap, group.Vertices, group.Indices[(3 * t) + 2], wmo.Transform);
            geometry.AddTriangle(builder, a, b, c, attr);
        }
    }

    private static void DrawClientM2(
        PartedGeometry? geometry,
        PlannedM2 m2,
        string colorKey,
        string className,
        string note)
    {
        if (geometry is null || !m2.Model.HasCollisionHull)
        {
            return;
        }

        M2ModelFile model = m2.Model;
        int tris = model.BoundingTriangleCount;
        string key = FormattableString.Invariant($"m2:{m2.UniqueId}:{m2.DoodadIndex}:{className}");
        uint attr = geometry.Attribute(key, () => new JsonObject
        {
            ["colorKey"] = colorKey,
            ["kind"] = m2.IsWmoDoodad ? "wmo-doodad" : "m2",
            ["class"] = className,
            ["model"] = m2.ModelPath,
            ["uniqueId"] = m2.UniqueId,
            ["doodadIndex"] = m2.DoodadIndex,
            ["triangles"] = tris,
            ["note"] = note,
        });

        GeometryBuilder builder = geometry.Writable(tris * BytesPerTriangle);
        geometry.Describe(builder, attr);
        var remap = new uint[model.BoundingVertices.Length];
        Array.Fill(remap, uint.MaxValue);

        for (int t = 0; t < tris; t++)
        {
            uint a = MapClientVertex(builder, remap, model.BoundingVertices, model.BoundingIndices[3 * t], m2.Transform);
            uint b = MapClientVertex(builder, remap, model.BoundingVertices, model.BoundingIndices[(3 * t) + 1], m2.Transform);
            uint c = MapClientVertex(builder, remap, model.BoundingVertices, model.BoundingIndices[(3 * t) + 2], m2.Transform);
            geometry.AddTriangle(builder, a, b, c, attr);
        }
    }

    private static void DrawServerInstance(
        PartedGeometry? geometry,
        ModelInstance instance,
        ModelSpawn spawn,
        string colorKey,
        string className,
        string note)
    {
        if (geometry is null)
        {
            return;
        }

        WorldModel model = instance.Model;
        int totalTris = model.Groups.Sum(g => g.Triangles.Count);
        if (totalTris == 0)
        {
            return;
        }

        string key = FormattableString.Invariant($"srv:{spawn.Id}:{className}");
        uint attr = geometry.Attribute(key, () => new JsonObject
        {
            ["colorKey"] = colorKey,
            ["kind"] = spawn.IsM2 ? "server-m2" : "server-wmo",
            ["class"] = className,
            ["model"] = DisplayName(spawn.Name),
            ["spawnId"] = spawn.Id,
            ["triangles"] = totalTris,
            ["note"] = note,
        });

        GeometryBuilder builder = geometry.Writable(totalTris * BytesPerTriangle);
        geometry.Describe(builder, attr);

        foreach (GroupModel group in model.Groups)
        {
            var remap = new uint[group.Vertices.Count];
            Array.Fill(remap, uint.MaxValue);
            foreach (MeshTriangle tri in group.Triangles)
            {
                uint a = MapServerVertex(builder, remap, instance, group, tri.Index0);
                uint b = MapServerVertex(builder, remap, instance, group, tri.Index1);
                uint c = MapServerVertex(builder, remap, instance, group, tri.Index2);
                geometry.AddTriangle(builder, a, b, c, attr);
            }
        }
    }

    private static uint MapClientVertex(GeometryBuilder builder, uint[] remap, Vector3[] vertices, int index, InstanceTransform transform)
    {
        if (remap[index] == uint.MaxValue)
        {
            WorldPoint world = transform.ModelToWorld(Vec3d.From(vertices[index]));
            remap[index] = builder.AddVertex(world);
        }

        return remap[index];
    }

    private static uint MapServerVertex(GeometryBuilder builder, uint[] remap, ModelInstance instance, GroupModel group, uint index)
    {
        if (remap[index] == uint.MaxValue)
        {
            Vector3 w = VMapFormat.ToWorld(instance.FromModel(group.Vertices[(int)index]));
            remap[index] = builder.AddVertex(w.X, w.Y, w.Z);
        }

        return remap[index];
    }

    private static string DisplayName(string name)
    {
        int nul = name.IndexOf('\0', StringComparison.Ordinal);
        return nul >= 0 ? name[..nul] : name;
    }

    private static double Distance(WorldPoint a, WorldPoint b)
        => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)) + ((a.Z - b.Z) * (a.Z - b.Z)));

    private static void WriteStats(JsonObject stats, TileReport report)
    {
        stats["serverSpawns"] = report.ServerSpawns;
        stats["matchedSpawns"] = report.MatchedSpawns;
        stats["duplicateSpawnIds"] = report.DuplicateSpawnIds;
        stats["skippedWmos"] = report.SkippedWmos;
        stats["skippedWmoGroups"] = report.SkippedWmoGroups;
        stats["renderOnlyTrianglesKept"] = report.RenderOnlyTrianglesKept;
        stats["skippedM2s"] = report.SkippedM2s;
        stats["tiltedDoodads"] = report.TiltedDoodads;
        stats["unmatchedClientInstances"] = report.UnmatchedClientInstances;
        stats["unmatchedServerSpawns"] = report.UnmatchedServerSpawns;
        stats["geometryMismatches"] = report.GeometryMismatches;
        stats["maxPositionDelta"] = report.MaxPositionDelta;
        stats["maxVertexDelta"] = report.MaxVertexDelta;
        stats["unexplainedCount"] = report.UnexplainedCount;
        stats["expectedCount"] = report.ExpectedCount;
    }

    private static void AddCount(SortedDictionary<string, long> map, string key, long count)
    {
        if (count > 0)
        {
            map[key] = map.GetValueOrDefault(key) + count;
        }
    }

    private static void AddExample(List<string> examples, string text)
    {
        if (examples.Count < MaxExamples)
        {
            examples.Add(text);
        }
    }

    private static JsonObject ToJsonObject(SortedDictionary<string, long> map)
    {
        var obj = new JsonObject();
        foreach ((string k, long v) in map)
        {
            obj[k] = v;
        }

        return obj;
    }

    private sealed record ExpectedSpawn(
        string Kind,
        bool IsM2,
        uint UniqueId,
        int DoodadIndex,
        string ServerName,
        InstanceTransform Transform,
        Mat3d ServerRotation,
        WorldPoint WorldPosition,
        bool OwnedHere,
        PlannedWmo? Wmo,
        PlannedM2? M2,
        List<int>? KeptGroups);

    private sealed class TileReport
    {
        public long ServerSpawns { get; set; }

        public long MatchedSpawns { get; set; }

        public long DuplicateSpawnIds { get; set; }

        public long SkippedWmos { get; set; }

        public long SkippedWmoGroups { get; set; }

        public long RenderOnlyTrianglesKept { get; set; }

        public long SkippedM2s { get; set; }

        public long TiltedDoodads { get; set; }

        public long UnmatchedClientInstances { get; set; }

        public long UnmatchedServerSpawns { get; set; }

        public long GeometryMismatches { get; set; }

        public double MaxPositionDelta { get; set; }

        public double MaxVertexDelta { get; set; }

        public SortedDictionary<string, long> ExpectedClasses { get; } = new(StringComparer.Ordinal);

        public SortedDictionary<string, long> UnexplainedClasses { get; } = new(StringComparer.Ordinal);

        public List<string> Examples { get; } = [];

        public long UnexplainedCount => UnexplainedClasses.Values.Sum();

        public long ExpectedCount => ExpectedClasses.Values.Sum();

        public void NoteExpected(string cls, long count = 1) => AddCount(ExpectedClasses, cls, count);

        public void NoteUnexplained(string cls, string example)
        {
            AddCount(UnexplainedClasses, cls, 1);
            AddExample(Examples, example);
        }
    }

    private sealed record PlacementLoad(TileState State, IReadOnlyList<ClientPlacement> Placements, string? Detail);

    private static PlacementLoad ReadPlacements(IClientPlacementSource placements, TileKey tile)
    {
        try
        {
            IReadOnlyList<ClientPlacement>? list = placements.Placements(tile);
            if (list is null)
            {
                return new PlacementLoad(TileState.Missing, [], "Client ADT/WDT placement file is missing.");
            }

            return new PlacementLoad(list.Count == 0 ? TileState.Empty : TileState.Ok, list, null);
        }
        catch (InvalidDataException e)
        {
            return new PlacementLoad(TileState.Rejected, [], e.Message);
        }
    }
}
