using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Navmesh;
using ArcaneCore.Atlas.Server;
using ArcaneCore.Game.Maps.Collision.MMaps;
using DotRecast.Detour;

namespace ArcaneCore.Atlas.Diff;

/// <summary>
/// Compares the server's parsed <c>.mmtile</c> (<see cref="NavMeshTile"/>) against the vendored DotRecast reference mesh
/// (<see cref="ResidentNavTile"/> / <see cref="DtMeshData"/>) polygon by polygon, detail mesh by detail mesh, BV-tree
/// verification state, off-mesh connections, and cross-tile portals (<see cref="NavMeshTile.GetCrossPortals"/> vs
/// DotRecast's <c>ConnectExtLinks</c>).
/// </summary>
internal static class NavDiff
{
    /// <summary>Coordinate comparison tolerance in yards.</summary>
    public const double Tolerance = 1e-3;

    private const int MaxExamples = 20;

    /// <summary>Builds the <see cref="LayerIds.DiffNav"/> geometry layer for <paramref name="tile"/>.</summary>
    public static LayerBuild BuildLayer(AtlasServices services, TileKey tile, CancellationToken cancellationToken)
    {
        if (!tile.IsInGrid)
        {
            return new LayerBuild(new TileStatus(LayerIds.DiffNav, tile, TileState.NotApplicable, "Navmesh diff is per grid tile; the map's global pseudo-tile has no navmesh."), []);
        }

        ServerData? serverData = services.Get<ServerData>();
        if (serverData is null)
        {
            return LayerBuild.Missing(LayerIds.DiffNav, tile, "No ServerData service is registered.");
        }

        ReferenceNavMeshService? navService = services.Get<ReferenceNavMeshService>();
        if (navService is null)
        {
            return LayerBuild.Missing(LayerIds.DiffNav, tile, "No ReferenceNavMeshService is registered.");
        }

        ParsedFile<NavTileData> serverFile = serverData.NavFile(tile);
        NavMapOpen open = navService.OpenMap(tile.MapId);
        if (open.Map is not { } navMap)
        {
            if (serverFile.State == TileState.Rejected || open.State == NavTileState.Rejected)
            {
                return LayerBuild.Rejected(LayerIds.DiffNav, tile, serverFile.Detail ?? open.Detail ?? "Navmesh map rejected.");
            }

            return LayerBuild.Missing(LayerIds.DiffNav, tile, open.Detail ?? serverFile.Detail);
        }

        return navMap.Read(ReferenceNavLayerSource.Neighbourhood(tile), view =>
        {
            NavTileOutcome refOutcome = view.Tiles[tile];
            if (serverFile.State == TileState.Rejected || refOutcome.State == NavTileState.Rejected)
            {
                string reason = serverFile.State == TileState.Rejected
                    ? $"Server .mmtile rejected: {serverFile.Detail}"
                    : $"DotRecast .mmtile rejected: {refOutcome.Detail}";
                return LayerBuild.Rejected(LayerIds.DiffNav, tile, reason);
            }

            if (!serverFile.IsOk)
            {
                return LayerBuild.Missing(LayerIds.DiffNav, tile, $"Server .mmtile missing: {serverFile.Detail}");
            }

            if (refOutcome.State != NavTileState.Ok || view.Resident(tile) is not { } resident)
            {
                return LayerBuild.Missing(LayerIds.DiffNav, tile, $"DotRecast .mmtile missing: {refOutcome.Detail}");
            }

            var builder = new GeometryBuilder(LayerIds.DiffNav, tile, WorldGrid.Origin(tile));
            TileReport report = CompareTile(tile, serverFile.Value, serverData, view, resident, builder, cancellationToken);
            WriteStats(builder.Stats, report);

            if (builder.IsEmpty)
            {
                return LayerBuild.Empty(
                    LayerIds.DiffNav,
                    tile,
                    FormattableString.Invariant($"All {report.PolygonsChecked} polygons, {report.VerticesChecked} vertices, and {report.PortalsMatched} cross-tile portals matched."));
            }

            string detail = FormattableString.Invariant(
                $"{report.UnexplainedCount} unexplained, {report.OffMeshConnectionsIgnored + (report.BvTreeRejectedByServer ? 1 : 0)} expected navmesh geometry difference(s)");
            return LayerBuild.Of(LayerIds.DiffNav, tile, [builder.Build()], detail);
        });
    }

    /// <summary>Runs the navmesh diff over a tile neighbourhood and returns a structured JSON report.</summary>
    public static JsonObject RunCommand(AtlasServices services, uint mapId, int centerX, int centerY, int radius, CancellationToken cancellationToken)
    {
        ServerData? serverData = services.Get<ServerData>();
        ReferenceNavMeshService? navService = services.Get<ReferenceNavMeshService>();

        var expectedClasses = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var unexplainedClasses = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var examples = new List<string>();
        var tileSummaries = new JsonArray();

        long totalPolygonsChecked = 0;
        long totalGroundPolygonsMatched = 0;
        long totalPolygonMismatches = 0;
        long totalVerticesChecked = 0;
        long totalVertexMismatches = 0;
        long totalDetailTrianglesChecked = 0;
        long totalDetailMismatches = 0;
        long totalOffMeshIgnored = 0;
        long totalBvTreeRejected = 0;
        long totalBorderEdgesChecked = 0;
        long totalDiagonalBorderEdges = 0;
        long totalPortalsMatched = 0;
        long totalPortalByteQuantization = 0;
        long totalPortalSlabClimbRule = 0;
        long totalPortalOnlyRef = 0;
        long totalPortalOnlyServer = 0;
        long totalPortalEndpointMismatches = 0;
        double maxPortalMidpointDelta = 0;
        int comparedTiles = 0;
        int missingTiles = 0;
        int rejectedTiles = 0;

        int minX = Math.Max(0, centerX - radius);
        int maxX = Math.Min(WorldGrid.TilesPerSide - 1, centerX + radius);
        int minY = Math.Max(0, centerY - radius);
        int maxY = Math.Min(WorldGrid.TilesPerSide - 1, centerY + radius);

        NavMapOpen? mapOpen = navService?.OpenMap(mapId);

        for (int tx = minX; tx <= maxX; tx++)
        {
            for (int ty = minY; ty <= maxY; ty++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tile = new TileKey(mapId, tx, ty);
                if (serverData is null || navService is null || mapOpen is null)
                {
                    missingTiles++;
                    tileSummaries.Add(new JsonObject
                    {
                        ["tile"] = new JsonArray(mapId, tx, ty),
                        ["state"] = TileState.Missing.ToString(),
                        ["detail"] = "Required server or reference navmesh services are not registered.",
                    });
                    continue;
                }

                ParsedFile<NavTileData> serverFile = serverData.NavFile(tile);
                if (mapOpen.Map is not { } navMap)
                {
                    if (serverFile.State == TileState.Rejected || mapOpen.State == NavTileState.Rejected)
                    {
                        rejectedTiles++;
                        string reason = serverFile.Detail ?? mapOpen.Detail ?? "Navmesh map rejected.";
                        AddCount(unexplainedClasses, "rejected-tile", 1);
                        AddExample(examples, $"{tile}: {reason}");
                        tileSummaries.Add(new JsonObject
                        {
                            ["tile"] = new JsonArray(mapId, tx, ty),
                            ["state"] = TileState.Rejected.ToString(),
                            ["detail"] = reason,
                        });
                    }
                    else
                    {
                        missingTiles++;
                        tileSummaries.Add(new JsonObject
                        {
                            ["tile"] = new JsonArray(mapId, tx, ty),
                            ["state"] = TileState.Missing.ToString(),
                            ["detail"] = mapOpen.Detail ?? serverFile.Detail,
                        });
                    }

                    continue;
                }

                TileReport? tileReport = navMap.Read(ReferenceNavLayerSource.Neighbourhood(tile), view =>
                {
                    NavTileOutcome refOutcome = view.Tiles[tile];
                    if (serverFile.State == TileState.Rejected || refOutcome.State == NavTileState.Rejected)
                    {
                        rejectedTiles++;
                        string reason = serverFile.State == TileState.Rejected
                            ? $"Server .mmtile rejected: {serverFile.Detail}"
                            : $"DotRecast .mmtile rejected: {refOutcome.Detail}";
                        AddCount(unexplainedClasses, "rejected-tile", 1);
                        AddExample(examples, $"{tile}: {reason}");
                        tileSummaries.Add(new JsonObject
                        {
                            ["tile"] = new JsonArray(mapId, tx, ty),
                            ["state"] = TileState.Rejected.ToString(),
                            ["detail"] = reason,
                        });
                        return null;
                    }

                    if (!serverFile.IsOk || refOutcome.State != NavTileState.Ok || view.Resident(tile) is not { } resident)
                    {
                        missingTiles++;
                        if (serverFile.IsOk && refOutcome.State != NavTileState.Ok)
                        {
                            AddCount(unexplainedClasses, "server-only-tile", 1);
                            AddExample(examples, $"{tile}: server parsed .mmtile but DotRecast state is {refOutcome.State} ({refOutcome.Detail})");
                        }
                        else if (!serverFile.IsOk && refOutcome.State == NavTileState.Ok)
                        {
                            AddCount(unexplainedClasses, "reference-only-tile", 1);
                            AddExample(examples, $"{tile}: DotRecast parsed .mmtile but server state is {serverFile.State} ({serverFile.Detail})");
                        }

                        tileSummaries.Add(new JsonObject
                        {
                            ["tile"] = new JsonArray(mapId, tx, ty),
                            ["state"] = TileState.Missing.ToString(),
                            ["detail"] = !serverFile.IsOk ? serverFile.Detail : refOutcome.Detail,
                        });
                        return null;
                    }

                    return CompareTile(tile, serverFile.Value, serverData, view, resident, builder: null, cancellationToken);
                });

                if (tileReport is null)
                {
                    continue;
                }

                comparedTiles++;
                totalPolygonsChecked += tileReport.PolygonsChecked;
                totalGroundPolygonsMatched += tileReport.GroundPolygonsMatched;
                totalPolygonMismatches += tileReport.PolygonMismatches;
                totalVerticesChecked += tileReport.VerticesChecked;
                totalVertexMismatches += tileReport.VertexMismatches;
                totalDetailTrianglesChecked += tileReport.DetailTrianglesChecked;
                totalDetailMismatches += tileReport.DetailMismatches;
                totalOffMeshIgnored += tileReport.OffMeshConnectionsIgnored;
                if (tileReport.BvTreeRejectedByServer)
                {
                    totalBvTreeRejected++;
                }

                totalBorderEdgesChecked += tileReport.BorderEdgesChecked;
                totalDiagonalBorderEdges += tileReport.DiagonalBorderEdges;
                totalPortalsMatched += tileReport.PortalsMatched;
                totalPortalByteQuantization += tileReport.PortalByteQuantization;
                totalPortalSlabClimbRule += tileReport.PortalSlabClimbRule;
                totalPortalOnlyRef += tileReport.PortalsOnlyReference;
                totalPortalOnlyServer += tileReport.PortalsOnlyServer;
                totalPortalEndpointMismatches += tileReport.PortalEndpointMismatches;
                maxPortalMidpointDelta = Math.Max(maxPortalMidpointDelta, tileReport.MaxPortalMidpointDelta);

                foreach ((string k, long v) in tileReport.ExpectedClasses)
                {
                    AddCount(expectedClasses, k, v);
                }

                foreach ((string k, long v) in tileReport.UnexplainedClasses)
                {
                    AddCount(unexplainedClasses, k, v);
                }

                foreach (string ex in tileReport.Examples)
                {
                    AddExample(examples, ex);
                }

                bool hasDrawnDiff = tileReport.UnexplainedCount > 0
                    || tileReport.OffMeshConnectionsIgnored > 0
                    || tileReport.BvTreeRejectedByServer;

                tileSummaries.Add(new JsonObject
                {
                    ["tile"] = new JsonArray(mapId, tx, ty),
                    ["state"] = hasDrawnDiff ? TileState.Ok.ToString() : TileState.Empty.ToString(),
                    ["polygonsChecked"] = tileReport.PolygonsChecked,
                    ["groundPolygonsMatched"] = tileReport.GroundPolygonsMatched,
                    ["portalsMatched"] = tileReport.PortalsMatched,
                    ["bvTreeRejectedByServer"] = tileReport.BvTreeRejectedByServer,
                    ["offMeshConnectionsIgnored"] = tileReport.OffMeshConnectionsIgnored,
                    ["unexplainedCount"] = tileReport.UnexplainedCount,
                    ["expectedCount"] = tileReport.ExpectedCount,
                });
            }
        }

        long totalUnexplained = unexplainedClasses.Values.Sum();
        long totalExpected = expectedClasses.Values.Sum();

        return new JsonObject
        {
            ["layer"] = LayerIds.DiffNav,
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
                ["polygonsChecked"] = totalPolygonsChecked,
                ["groundPolygonsMatched"] = totalGroundPolygonsMatched,
                ["polygonMismatches"] = totalPolygonMismatches,
                ["verticesChecked"] = totalVerticesChecked,
                ["vertexMismatches"] = totalVertexMismatches,
                ["detailTrianglesChecked"] = totalDetailTrianglesChecked,
                ["detailMismatches"] = totalDetailMismatches,
                ["offMeshConnectionsIgnored"] = totalOffMeshIgnored,
                ["bvTreesRejectedByServer"] = totalBvTreeRejected,
                ["borderEdgesChecked"] = totalBorderEdgesChecked,
                ["diagonalBorderEdges"] = totalDiagonalBorderEdges,
                ["portalsMatched"] = totalPortalsMatched,
                ["portalByteQuantization"] = totalPortalByteQuantization,
                ["portalSlabClimbRule"] = totalPortalSlabClimbRule,
                ["portalsOnlyReference"] = totalPortalOnlyRef,
                ["portalsOnlyServer"] = totalPortalOnlyServer,
                ["portalEndpointMismatches"] = totalPortalEndpointMismatches,
                ["maxPortalMidpointDelta"] = maxPortalMidpointDelta,
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
        NavTileData serverData,
        ServerData allServerData,
        NavMapView view,
        ResidentNavTile resident,
        GeometryBuilder? builder,
        CancellationToken cancellationToken)
    {
        var report = new TileReport();
        NavMeshTile server = serverData.Tile;
        ParsedNavTile dotFile = resident.File;
        DtMeshData dot = dotFile.Data;
        float[] rawVerts = dotFile.RawVertices;

        uint nextAttr = 1;

        // 1. Compare raw vertices (before DotRecast's AddTile snaps off-mesh endpoints).
        int dotVertCount = rawVerts.Length / 3;
        int maxVerts = Math.Max(server.Vertices.Length, dotVertCount);
        report.VerticesChecked = maxVerts;
        if (server.Vertices.Length != dotVertCount)
        {
            report.VertexMismatches++;
            report.NoteUnexplained(
                "vertex-mismatch",
                FormattableString.Invariant($"{tile}: server has {server.Vertices.Length} vertices, DotRecast has {dotVertCount}"));
        }

        int sharedVerts = Math.Min(server.Vertices.Length, dotVertCount);
        for (int v = 0; v < sharedVerts; v++)
        {
            Vector3 sv = server.Vertices[v];
            var dv = new Vector3(rawVerts[v * 3], rawVerts[(v * 3) + 1], rawVerts[(v * 3) + 2]);
            if (Vector3.Distance(sv, dv) > Tolerance)
            {
                report.VertexMismatches++;
                report.NoteUnexplained(
                    "vertex-mismatch",
                    FormattableString.Invariant($"{tile} vertex {v}: server {sv}, DotRecast {dv}"));
            }
        }

        // 2. Compare polygons.
        int maxPolys = Math.Max(server.Polys.Length, dot.polys.Length);
        report.PolygonsChecked = maxPolys;
        for (int p = 0; p < maxPolys; p++)
        {
            if ((p & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (p >= server.Polys.Length)
            {
                DtPoly dp = dot.polys[p];
                if (dp.GetPolyType() == DtPolyTypes.DT_POLYTYPE_OFFMESH_CONNECTION)
                {
                    report.OffMeshConnectionsIgnored++;
                    report.NoteExpected("offmesh-connection-ignored-by-server");
                }
                else
                {
                    report.PolygonMismatches++;
                    report.NoteUnexplained("polygon-mismatch", FormattableString.Invariant($"{tile} poly {p}: present only in DotRecast"));
                    DrawDotPoly(builder, ref nextAttr, rawVerts, dp, ColorKeys.OnlyReference, "polygon-mismatch", "polygon present only in DotRecast");
                }

                continue;
            }

            if (p >= dot.polys.Length)
            {
                NavPoly sp = server.Polys[p];
                if (!sp.IsOffMeshConnection)
                {
                    report.PolygonMismatches++;
                    report.NoteUnexplained("polygon-mismatch", FormattableString.Invariant($"{tile} poly {p}: present only in server"));
                    DrawServerPoly(builder, ref nextAttr, server, p, sp, ColorKeys.OnlyServer, "polygon-mismatch", "polygon present only in server");
                }

                continue;
            }

            NavPoly sPoly = server.Polys[p];
            DtPoly dPoly = dot.polys[p];
            bool sOff = sPoly.IsOffMeshConnection;
            bool dOff = dPoly.GetPolyType() == DtPolyTypes.DT_POLYTYPE_OFFMESH_CONNECTION;

            if (sOff && dOff)
            {
                // ref: src/ArcaneCore.Game/Maps/Collision/MMaps/NavMeshTile.cs:256 (offMeshConCount is discarded; Passes refuses IsOffMeshConnection)
                report.OffMeshConnectionsIgnored++;
                report.NoteExpected("offmesh-connection-ignored-by-server");
                continue;
            }

            if (!PolygonsEqual(sPoly, dPoly))
            {
                report.PolygonMismatches++;
                report.NoteUnexplained(
                    "polygon-mismatch",
                    FormattableString.Invariant($"{tile} poly {p}: flags (0x{sPoly.Flags:X4} vs 0x{dPoly.flags:X4}), area ({sPoly.Area} vs {dPoly.GetArea()}), or vertices/neighbours differ"));
                if (!dOff)
                {
                    DrawDotPoly(builder, ref nextAttr, rawVerts, dPoly, ColorKeys.OnlyReference, "polygon-mismatch", "DotRecast polygon differs from server polygon");
                }

                if (!sOff)
                {
                    DrawServerPoly(builder, ref nextAttr, server, p, sPoly, ColorKeys.OnlyServer, "polygon-mismatch", "server polygon differs from DotRecast polygon");
                }
            }
            else
            {
                report.GroundPolygonsMatched++;
            }
        }

        // Draw off-mesh connections that DotRecast links but the server ignores.
        if (dot.offMeshCons is not null)
        {
            foreach (DtOffMeshConnection con in dot.offMeshCons)
            {
                if (report.OffMeshConnectionsIgnored == 0)
                {
                    report.OffMeshConnectionsIgnored++;
                    report.NoteExpected("offmesh-connection-ignored-by-server");
                }

                if (builder is not null)
                {
                    uint attr = nextAttr++;
                    WorldPoint from = NavGeometry.ToWorld(con.pos[0]);
                    WorldPoint to = NavGeometry.ToWorld(con.pos[1]);
                    int polyIdx = con.poly;
                    uint userId = (uint)con.userId;
                    builder.DescribeAttribute(attr, () => new JsonObject
                    {
                        ["colorKey"] = ColorKeys.Expected,
                        ["kind"] = "offmesh-connection",
                        ["class"] = "offmesh-connection-ignored-by-server",
                        ["poly"] = polyIdx,
                        ["userId"] = userId,
                        ["note"] = "DotRecast links this off-mesh connection, while the server reader skips off-mesh connections",
                    });
                    uint va = builder.AddVertex(from);
                    uint vb = builder.AddVertex(to);
                    builder.AddLine(va, vb, attr);
                    builder.AddPoint(va, attr);
                    builder.AddPoint(vb, attr);
                }
            }
        }

        // 3. Compare detail meshes.
        CompareDetails(tile, server, dot, report);

        // 4. Compare BV-tree usability.
        // ref: src/ArcaneCore.Game/Maps/Collision/MMaps/NavMeshTile.cs:125-139, 331 (TryVerifyBvTree rejects malformed or out-of-bounds BV trees)
        if (serverData.HeaderBvNodeCount > 0 && !server.HasBvTree)
        {
            report.BvTreeRejectedByServer = true;
            report.NoteExpected("bv-tree-rejected-by-server");
            DrawBvBox(builder, ref nextAttr, server, serverData.HeaderBvNodeCount);
        }

        // 5. Compare cross-tile portals (server GetCrossPortals vs DotRecast external links).
        ComparePortals(tile, server, allServerData, view, resident, report, builder, ref nextAttr, cancellationToken);

        return report;
    }

    private static bool PolygonsEqual(NavPoly s, DtPoly d)
    {
        if (s.Flags != (ushort)d.flags || s.Area != d.GetArea() || s.Type != d.GetPolyType() || s.VertexCount != d.vertCount)
        {
            return false;
        }

        for (int i = 0; i < s.VertexCount; i++)
        {
            if (s.Vertices[i] != d.verts[i] || s.Neighbours[i] != d.neis[i])
            {
                return false;
            }
        }

        return true;
    }

    private static void CompareDetails(TileKey tile, NavMeshTile server, DtMeshData dot, TileReport report)
    {
        DtPolyDetail[]? dotMeshes = dot.detailMeshes;
        int dotMeshCount = dotMeshes?.Length ?? 0;
        if (server.Details.Length != dotMeshCount)
        {
            report.DetailMismatches++;
            report.NoteUnexplained(
                "detail-mesh-mismatch",
                FormattableString.Invariant($"{tile}: server has {server.Details.Length} detail meshes, DotRecast has {dotMeshCount}"));
            return;
        }

        for (int i = 0; i < server.Details.Length; i++)
        {
            NavPolyDetail sd = server.Details[i];
            DtPolyDetail dd = dotMeshes![i];
            report.DetailTrianglesChecked += sd.TriangleCount;
            if (sd.VertexBase != (uint)dd.vertBase
                || sd.TriangleBase != (uint)dd.triBase
                || sd.VertexCount != dd.vertCount
                || sd.TriangleCount != dd.triCount)
            {
                report.DetailMismatches++;
                report.NoteUnexplained(
                    "detail-mesh-mismatch",
                    FormattableString.Invariant($"{tile} detail mesh {i}: server ({sd.VertexBase},{sd.TriangleBase},{sd.VertexCount},{sd.TriangleCount}) vs DotRecast ({dd.vertBase},{dd.triBase},{dd.vertCount},{dd.triCount})"));
            }
        }

        float[]? dotDetailVerts = dot.detailVerts;
        int dotDetailVertCount = (dotDetailVerts?.Length ?? 0) / 3;
        if (server.DetailVertices.Length != dotDetailVertCount)
        {
            report.DetailMismatches++;
            report.NoteUnexplained(
                "detail-mesh-mismatch",
                FormattableString.Invariant($"{tile}: server has {server.DetailVertices.Length} detail vertices, DotRecast has {dotDetailVertCount}"));
        }
        else if (dotDetailVerts is not null)
        {
            for (int v = 0; v < server.DetailVertices.Length; v++)
            {
                Vector3 sv = server.DetailVertices[v];
                var dv = new Vector3(dotDetailVerts[v * 3], dotDetailVerts[(v * 3) + 1], dotDetailVerts[(v * 3) + 2]);
                if (Vector3.Distance(sv, dv) > Tolerance)
                {
                    report.DetailMismatches++;
                    report.NoteUnexplained(
                        "detail-mesh-mismatch",
                        FormattableString.Invariant($"{tile} detail vertex {v}: server {sv} vs DotRecast {dv}"));
                    break;
                }
            }
        }

        int[]? dotTris = dot.detailTris;
        int dotTriBytes = dotTris?.Length ?? 0;
        if (server.DetailTriangles.Length != dotTriBytes)
        {
            report.DetailMismatches++;
            report.NoteUnexplained(
                "detail-mesh-mismatch",
                FormattableString.Invariant($"{tile}: server has {server.DetailTriangles.Length} detail triangle bytes, DotRecast has {dotTriBytes}"));
        }
        else if (dotTris is not null)
        {
            for (int t = 0; t < server.DetailTriangles.Length; t++)
            {
                if (server.DetailTriangles[t] != (byte)dotTris[t])
                {
                    report.DetailMismatches++;
                    report.NoteUnexplained(
                        "detail-mesh-mismatch",
                        FormattableString.Invariant($"{tile} detail triangle byte {t}: server {server.DetailTriangles[t]} vs DotRecast {dotTris[t]}"));
                    break;
                }
            }
        }
    }

    private static void ComparePortals(
        TileKey tile,
        NavMeshTile server,
        ServerData allServerData,
        NavMapView view,
        ResidentNavTile resident,
        TileReport report,
        GeometryBuilder? builder,
        ref uint nextAttr,
        CancellationToken cancellationToken)
    {
        List<NavLink> allLinks = NavLinkInventory.Collect(view, resident);
        DtMeshTile dotTile = resident.Tile;
        var serverNeighbours = new Dictionary<(int Dx, int Dy), NavMeshTile?>();

        for (int p = 0; p < server.Polys.Length; p++)
        {
            if ((p & 127) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            NavPoly poly = server.Polys[p];
            if (poly.IsOffMeshConnection)
            {
                continue;
            }

            for (int e = 0; e < poly.VertexCount; e++)
            {
                ushort nei = poly.Neighbours[e];
                if ((nei & NavMeshFormat.ExternalLink) == 0)
                {
                    continue;
                }

                report.BorderEdgesChecked++;
                int side = nei & 0xff;
                if (NavMeshFormat.SideOffset(side) is not { } offset)
                {
                    // ref: src/ArcaneCore.Game/Maps/Collision/MMaps/NavMeshFormat.cs:62-75 (only sides 0, 2, 4, 6 are cardinal tile borders)
                    report.DiagonalBorderEdges++;
                    report.NoteExpected("diagonal-border-edge");
                    continue;
                }

                if (!serverNeighbours.TryGetValue(offset, out NavMeshTile? otherServer))
                {
                    otherServer = ResolveServerNeighbour(tile, server, offset, allServerData);
                    serverNeighbours[offset] = otherServer;
                }

                CrossPortal[] serverPortals = otherServer is not null ? server.GetCrossPortals(p, e, otherServer) : [];
                List<NavLink> refLinks = allLinks
                    .Where(l => l.Kind == NavLinkKind.External && l.SourcePoly == p && l.Link.edge == e && l.TargetPoly is not null && l.TargetTile is not null)
                    .ToList();

                if (otherServer is null && refLinks.Count == 0)
                {
                    continue;
                }

                WorldPoint edgeA = World(server.Vertices[poly.Vertices[e]]);
                WorldPoint edgeB = World(server.Vertices[poly.Vertices[(e + 1) % poly.VertexCount]]);
                double edgeLen = Distance(edgeA, edgeB);
                double quantBound = (edgeLen / 127.0) + 0.05;
                WorldPoint fromCenter = World(server.Center(p));

                var matchedRef = new bool[refLinks.Count];
                foreach (CrossPortal portal in serverPortals)
                {
                    WorldPoint sA = World(portal.A);
                    WorldPoint sB = World(portal.B);
                    WorldPoint sMid = Lerp(sA, sB, 0.5);
                    WorldPoint toCenter = otherServer is not null ? World(otherServer.Center(portal.Poly)) : sMid;

                    int matchIdx = -1;
                    for (int i = 0; i < refLinks.Count; i++)
                    {
                        if (!matchedRef[i] && refLinks[i].TargetPolyIndex == portal.Poly)
                        {
                            matchIdx = i;
                            break;
                        }
                    }

                    if (matchIdx < 0)
                    {
                        if (Distance(sA, sB) < 0.025)
                        {
                            // ref: vmangos@0e3ff01 dep/recastnavigation/Detour/Source/DetourNavMesh.cpp:259-292 (OverlapSlabs shrinks horizontal slab ends by px=0.01f;
                            // NavMesh.TryOverlap now applies the same px=0.01f shrink on production tiles, kept for synthetic/older portal tables)
                            report.PortalSlabClimbRule++;
                            report.NoteExpected("portal-slab-climb-rule");
                            continue;
                        }

                        report.PortalsOnlyServer++;
                        report.NoteUnexplained(
                            "portal-only-server",
                            FormattableString.Invariant($"{tile} poly {p} edge {e} -> target poly {portal.Poly}: server portal has no matching DotRecast link"));
                        DrawPortal(builder, ref nextAttr, sA, sB, fromCenter, toCenter, p, e, portal.Poly, ColorKeys.OnlyServer,
                            "portal-only-server", "cross-tile portal derived by server but absent from DotRecast external links");
                        continue;
                    }

                    matchedRef[matchIdx] = true;
                    NavLink link = refLinks[matchIdx];
                    WorldPoint rA = Lerp(edgeA, edgeB, link.Link.bmin / 255.0);
                    WorldPoint rB = Lerp(edgeA, edgeB, link.Link.bmax / 255.0);
                    WorldPoint rMid = Lerp(rA, rB, 0.5);
                    double midDelta = Distance(sMid, rMid);
                    report.MaxPortalMidpointDelta = Math.Max(report.MaxPortalMidpointDelta, midDelta);

                    if (midDelta <= quantBound)
                    {
                        report.PortalsMatched++;
                        if (midDelta > Tolerance)
                        {
                            // ref: vmangos@0e3ff01 dep/recastnavigation/Detour/Source/DetourNavMesh.cpp:436-437 (bmin/bmax quantized to 0..255 byte fractions)
                            report.PortalByteQuantization++;
                            report.NoteExpected("portal-byte-quantization");
                        }
                    }
                    else
                    {
                        report.PortalEndpointMismatches++;
                        report.NoteUnexplained(
                            "portal-endpoint-mismatch",
                            FormattableString.Invariant($"{tile} poly {p} edge {e} -> poly {portal.Poly}: portal midpoint delta {midDelta:G4} yd exceeds quantization bound {quantBound:G4} yd"));
                        DrawPortal(builder, ref nextAttr, rA, rB, fromCenter, toCenter, p, e, portal.Poly, ColorKeys.OnlyReference,
                            "portal-endpoint-mismatch", "DotRecast portal sub-edge disagrees with server portal beyond byte quantization");
                        DrawPortal(builder, ref nextAttr, sA, sB, fromCenter, toCenter, p, e, portal.Poly, ColorKeys.OnlyServer,
                            "portal-endpoint-mismatch", "server portal sub-edge disagrees with DotRecast portal beyond byte quantization");
                    }
                }

                int oppositeSide = (side + 4) & 7;
                for (int i = 0; i < refLinks.Count; i++)
                {
                    if (matchedRef[i])
                    {
                        continue;
                    }

                    NavLink link = refLinks[i];
                    if (otherServer is not null && otherServer.GetBorderEdges(oppositeSide).Any(be => be.Poly == link.TargetPolyIndex))
                    {
                        // ref: vmangos@0e3ff01 dep/recastnavigation/Detour/Source/DetourNavMesh.cpp:259-292 (OverlapSlabs checks 2*walkableClimb at slab endpoints;
                        // NavMesh.TryOverlap now matches Detour on production tiles, kept for synthetic/custom portal tables)
                        report.PortalSlabClimbRule++;
                        report.NoteExpected("portal-slab-climb-rule");
                        continue;
                    }

                    report.PortalsOnlyReference++;
                    report.NoteUnexplained(
                        "portal-only-reference",
                        FormattableString.Invariant($"{tile} poly {p} edge {e} -> target poly {link.TargetPolyIndex}: DotRecast link has no matching server portal"));
                    WorldPoint rA = Lerp(edgeA, edgeB, link.Link.bmin / 255.0);
                    WorldPoint rB = Lerp(edgeA, edgeB, link.Link.bmax / 255.0);
                    WorldPoint toCenter = NavGeometry.PolyCenter(link.TargetTile!.data.verts, link.TargetPoly!.verts, link.TargetPoly.vertCount);
                    DrawPortal(builder, ref nextAttr, rA, rB, fromCenter, toCenter, p, e, link.TargetPolyIndex, ColorKeys.OnlyReference,
                        "portal-only-reference", "external link built by DotRecast but absent from server cross-tile portals");
                }
            }
        }
    }

    private static NavMeshTile? ResolveServerNeighbour(TileKey tile, NavMeshTile mesh, (int Dx, int Dy) offset, ServerData serverData)
    {
        TileKey neighbourKey = NavLayerSource.NeighbourTerrainTile(tile, offset.Dx, offset.Dy);
        if (!neighbourKey.IsInGrid)
        {
            return null;
        }

        ParsedFile<NavTileData> file = serverData.NavFile(neighbourKey);
        if (!file.IsOk)
        {
            return null;
        }

        NavMeshTile other = file.Value.Tile;
        return other.X == mesh.X + offset.Dx && other.Y == mesh.Y + offset.Dy ? other : null;
    }

    private static void DrawDotPoly(
        GeometryBuilder? builder,
        ref uint nextAttr,
        float[] rawVerts,
        DtPoly poly,
        string colorKey,
        string className,
        string note)
    {
        if (builder is null || poly.vertCount < 3)
        {
            return;
        }

        uint attr = nextAttr++;
        int polyIdx = poly.index;
        int flags = poly.flags;
        int area = poly.GetArea();
        builder.DescribeAttribute(attr, () => new JsonObject
        {
            ["colorKey"] = colorKey,
            ["kind"] = "navmesh-polygon",
            ["class"] = className,
            ["poly"] = polyIdx,
            ["flags"] = flags,
            ["area"] = area,
            ["note"] = note,
        });

        var indices = new uint[poly.vertCount];
        for (int i = 0; i < poly.vertCount; i++)
        {
            indices[i] = builder.AddVertex(NavGeometry.ToWorld(rawVerts, poly.verts[i]));
        }

        for (int i = 1; i + 1 < poly.vertCount; i++)
        {
            builder.AddTriangle(indices[0], indices[i], indices[i + 1], attr, edges: false);
        }

        for (int i = 0; i < poly.vertCount; i++)
        {
            builder.AddEdge(indices[i], indices[(i + 1) % poly.vertCount], attr);
        }
    }

    private static void DrawServerPoly(
        GeometryBuilder? builder,
        ref uint nextAttr,
        NavMeshTile server,
        int polyIndex,
        NavPoly poly,
        string colorKey,
        string className,
        string note)
    {
        if (builder is null || poly.VertexCount < 3)
        {
            return;
        }

        uint attr = nextAttr++;
        int flags = poly.Flags;
        int area = poly.Area;
        builder.DescribeAttribute(attr, () => new JsonObject
        {
            ["colorKey"] = colorKey,
            ["kind"] = "navmesh-polygon",
            ["class"] = className,
            ["poly"] = polyIndex,
            ["flags"] = flags,
            ["area"] = area,
            ["note"] = note,
        });

        var indices = new uint[poly.VertexCount];
        for (int i = 0; i < poly.VertexCount; i++)
        {
            indices[i] = builder.AddVertex(World(server.Vertices[poly.Vertices[i]]));
        }

        for (int i = 1; i + 1 < poly.VertexCount; i++)
        {
            builder.AddTriangle(indices[0], indices[i], indices[i + 1], attr, edges: false);
        }

        for (int i = 0; i < poly.VertexCount; i++)
        {
            builder.AddEdge(indices[i], indices[(i + 1) % poly.VertexCount], attr);
        }
    }

    private static void DrawBvBox(GeometryBuilder? builder, ref uint nextAttr, NavMeshTile mesh, int headerBvNodes)
    {
        if (builder is null)
        {
            return;
        }

        uint attr = nextAttr++;
        int polyCount = mesh.Polys.Length;
        builder.DescribeAttribute(attr, () => new JsonObject
        {
            ["colorKey"] = ColorKeys.Expected,
            ["kind"] = "bv-tree-rejected",
            ["class"] = "bv-tree-rejected-by-server",
            ["headerBvNodes"] = headerBvNodes,
            ["polygons"] = polyCount,
            ["note"] = "DotRecast walks this tile's BV-tree while the server rejected it in TryVerifyBvTree and scans polygons linearly",
        });

        Vector3 a = NavMeshFormat.ToWorld(mesh.BoundLow);
        Vector3 b = NavMeshFormat.ToWorld(mesh.BoundHigh);
        Vector3 low = Vector3.Min(a, b);
        Vector3 high = Vector3.Max(a, b);
        var corner = new uint[8];
        for (int i = 0; i < 8; i++)
        {
            corner[i] = builder.AddVertex((i & 1) == 0 ? low.X : high.X, (i & 2) == 0 ? low.Y : high.Y, (i & 4) == 0 ? low.Z : high.Z);
        }

        for (int i = 0; i < 8; i++)
        {
            for (int bit = 1; bit <= 4; bit <<= 1)
            {
                if ((i & bit) == 0)
                {
                    builder.AddLine(corner[i], corner[i | bit], attr);
                }
            }
        }
    }

    private static void DrawPortal(
        GeometryBuilder? builder,
        ref uint nextAttr,
        WorldPoint a,
        WorldPoint b,
        WorldPoint fromCenter,
        WorldPoint toCenter,
        int fromPoly,
        int edge,
        int toPoly,
        string colorKey,
        string className,
        string note)
    {
        if (builder is null)
        {
            return;
        }

        uint attr = nextAttr++;
        builder.DescribeAttribute(attr, () => new JsonObject
        {
            ["colorKey"] = colorKey,
            ["kind"] = "cross-tile-portal",
            ["class"] = className,
            ["fromPoly"] = fromPoly,
            ["edge"] = edge,
            ["toPoly"] = toPoly,
            ["note"] = note,
        });

        WorldPoint mid = Lerp(a, b, 0.5);
        uint va = builder.AddVertex(a);
        uint vb = builder.AddVertex(b);
        uint vm = builder.AddVertex(mid);
        uint vf = builder.AddVertex(fromCenter);
        uint vt = builder.AddVertex(toCenter);
        builder.AddLine(va, vb, attr);
        builder.AddLine(vf, vm, attr);
        builder.AddLine(vm, vt, attr);
        builder.AddPoint(vm, attr);
    }

    private static void WriteStats(JsonObject stats, TileReport report)
    {
        stats["polygonsChecked"] = report.PolygonsChecked;
        stats["groundPolygonsMatched"] = report.GroundPolygonsMatched;
        stats["polygonMismatches"] = report.PolygonMismatches;
        stats["verticesChecked"] = report.VerticesChecked;
        stats["vertexMismatches"] = report.VertexMismatches;
        stats["detailTrianglesChecked"] = report.DetailTrianglesChecked;
        stats["detailMismatches"] = report.DetailMismatches;
        stats["offMeshConnectionsIgnored"] = report.OffMeshConnectionsIgnored;
        stats["bvTreeRejectedByServer"] = report.BvTreeRejectedByServer;
        stats["borderEdgesChecked"] = report.BorderEdgesChecked;
        stats["diagonalBorderEdges"] = report.DiagonalBorderEdges;
        stats["portalsMatched"] = report.PortalsMatched;
        stats["portalByteQuantization"] = report.PortalByteQuantization;
        stats["portalSlabClimbRule"] = report.PortalSlabClimbRule;
        stats["portalsOnlyReference"] = report.PortalsOnlyReference;
        stats["portalsOnlyServer"] = report.PortalsOnlyServer;
        stats["portalEndpointMismatches"] = report.PortalEndpointMismatches;
        stats["maxPortalMidpointDelta"] = report.MaxPortalMidpointDelta;
        stats["unexplainedCount"] = report.UnexplainedCount;
        stats["expectedCount"] = report.ExpectedCount;
        stats["expectedClasses"] = ToJsonObject(report.ExpectedClasses);
        stats["unexplainedClasses"] = ToJsonObject(report.UnexplainedClasses);
    }

    private static WorldPoint World(Vector3 recast)
    {
        Vector3 w = NavMeshFormat.ToWorld(recast);
        return new WorldPoint(w.X, w.Y, w.Z);
    }

    private static WorldPoint Lerp(WorldPoint a, WorldPoint b, double t)
        => new(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t), a.Z + ((b.Z - a.Z) * t));

    private static double Distance(WorldPoint a, WorldPoint b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        double dz = a.Z - b.Z;
        return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    private static JsonObject ToJsonObject(SortedDictionary<string, long> counts)
    {
        var obj = new JsonObject();
        foreach ((string k, long v) in counts)
        {
            obj[k] = v;
        }

        return obj;
    }

    private static void AddCount(SortedDictionary<string, long> dict, string key, long delta)
        => dict[key] = dict.GetValueOrDefault(key) + delta;

    private static void AddExample(List<string> list, string example)
    {
        if (list.Count < MaxExamples)
        {
            list.Add(example);
        }
    }

    private sealed class TileReport
    {
        public int PolygonsChecked { get; set; }

        public int GroundPolygonsMatched { get; set; }

        public int PolygonMismatches { get; set; }

        public int VerticesChecked { get; set; }

        public int VertexMismatches { get; set; }

        public int DetailTrianglesChecked { get; set; }

        public int DetailMismatches { get; set; }

        public int OffMeshConnectionsIgnored { get; set; }

        public bool BvTreeRejectedByServer { get; set; }

        public int BorderEdgesChecked { get; set; }

        public int DiagonalBorderEdges { get; set; }

        public int PortalsMatched { get; set; }

        public int PortalByteQuantization { get; set; }

        public int PortalSlabClimbRule { get; set; }

        public int PortalsOnlyReference { get; set; }

        public int PortalsOnlyServer { get; set; }

        public int PortalEndpointMismatches { get; set; }

        public double MaxPortalMidpointDelta { get; set; }

        public SortedDictionary<string, long> ExpectedClasses { get; } = new(StringComparer.Ordinal);

        public SortedDictionary<string, long> UnexplainedClasses { get; } = new(StringComparer.Ordinal);

        public List<string> Examples { get; } = [];

        public long UnexplainedCount => UnexplainedClasses.Values.Sum();

        public long ExpectedCount => ExpectedClasses.Values.Sum();

        public void NoteExpected(string className, long count = 1)
            => AddCount(ExpectedClasses, className, count);

        public void NoteUnexplained(string className, string example)
        {
            AddCount(UnexplainedClasses, className, 1);
            AddExample(Examples, example);
        }
    }
}
