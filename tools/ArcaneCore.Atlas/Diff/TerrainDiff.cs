using System.Globalization;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Client.Terrain;
using ArcaneCore.Atlas.Server;
using ArcaneCore.Game.Maps.Terrain;

namespace ArcaneCore.Atlas.Diff;

/// <summary>
/// Compares the server's parsed <c>.map</c> terrain tile (<see cref="TerrainTile"/>) against the 1.12.1 client ADT
/// (<see cref="AdtFile"/>) sample by sample across all 256 MCNK chunks (37,120 height samples, 1,024 4x4 hole nibbles /
/// 16,384 squares, and MCLQ liquid layers), classifying known vmangos extractor transforms as <see cref="ColorKeys.Expected"/>
/// and flagging any residual discrepancies as <see cref="ColorKeys.OnlyReference"/> or <see cref="ColorKeys.OnlyServer"/>.
/// </summary>
internal static class TerrainDiff
{
    /// <summary>Height comparison tolerance in yards.</summary>
    public const double Tolerance = 1e-3;

    /// <summary>Grid resolution (squares per tile edge).</summary>
    public const int Squares = 128;

    /// <summary>Extractor <c>CONF_use_minHeight</c> floor (<c>contrib/extractor/System.cpp:92</c>).</summary>
    public const float ExtractorMinHeight = -500f;

    private const int MaxExamples = 20;

    // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:286-300 (liquid layer order, LiquidType.dbc entry and map type flag)
    private static readonly (AdtLiquidKind Kind, ushort Entry, byte MapFlag)[] LayerTypes =
    [
        (AdtLiquidKind.River, 1, 8),
        (AdtLiquidKind.Ocean, 2, 2),
        (AdtLiquidKind.Magma, 3, 1),
        (AdtLiquidKind.Slime, 4, 4),
    ];

    /// <summary>Builds the <see cref="LayerIds.DiffTerrain"/> geometry layer for <paramref name="tile"/>.</summary>
    public static LayerBuild BuildLayer(AtlasServices services, TileKey tile, CancellationToken cancellationToken)
    {
        if (!tile.IsInGrid)
        {
            return new LayerBuild(new TileStatus(LayerIds.DiffTerrain, tile, TileState.NotApplicable, "Terrain diff is per grid tile; the map's global pseudo-tile has no terrain."), []);
        }

        ServerData? serverData = services.Get<ServerData>();
        if (serverData is null)
        {
            return LayerBuild.Missing(LayerIds.DiffTerrain, tile, "No ServerData service is registered.");
        }

        ClientTerrainService? clientService = services.Get<ClientTerrainService>();
        if (clientService is null)
        {
            return LayerBuild.Missing(LayerIds.DiffTerrain, tile, "No ClientTerrainService is registered.");
        }

        ParsedFile<TerrainTile> serverFile = serverData.TerrainFile(tile);
        AdtLoad clientLoad = clientService.LoadAdt(tile);

        if (serverFile.State == TileState.Rejected)
        {
            return LayerBuild.Rejected(LayerIds.DiffTerrain, tile, $"Server .map rejected: {serverFile.Detail}");
        }

        if (clientLoad.State == TileState.Rejected)
        {
            return LayerBuild.Rejected(LayerIds.DiffTerrain, tile, $"Client ADT rejected: {clientLoad.Detail}");
        }

        if (!serverFile.IsOk)
        {
            return LayerBuild.Missing(LayerIds.DiffTerrain, tile, $"Server .map missing: {serverFile.Detail}");
        }

        if (clientLoad.File is not { } adt)
        {
            return LayerBuild.Missing(LayerIds.DiffTerrain, tile, $"Client ADT missing: {clientLoad.Detail}");
        }

        WorldPoint origin = WorldGrid.Origin(tile);
        var builder = new GeometryBuilder(LayerIds.DiffTerrain, tile, origin);
        TileReport report = CompareTile(tile, adt, serverFile.Value, builder, cancellationToken);
        WriteStats(builder.Stats, report);

        if (builder.IsEmpty)
        {
            return LayerBuild.Empty(
                LayerIds.DiffTerrain,
                tile,
                FormattableString.Invariant($"All {report.Samples:N0} height samples and 1,024 hole blocks ({report.HoleSquaresChecked:N0} squares) matched within {Tolerance:G} yd."));
        }

        string detail = FormattableString.Invariant(
            $"{report.UnexplainedCount} unexplained, {report.ExpectedHeightDiffs + report.NonFloatHoleMaskIgnored} expected geometry diff(s) across {report.Samples:N0} samples and 1,024 hole blocks");
        return LayerBuild.Of(LayerIds.DiffTerrain, tile, [builder.Build()], detail);
    }

    /// <summary>Runs the terrain diff over a tile neighbourhood and returns a structured JSON report.</summary>
    public static JsonObject RunCommand(AtlasServices services, uint mapId, int centerX, int centerY, int radius, CancellationToken cancellationToken)
    {
        ServerData? serverData = services.Get<ServerData>();
        ClientTerrainService? clientService = services.Get<ClientTerrainService>();

        var expectedClasses = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var unexplainedClasses = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var examples = new List<string>();
        var tileSummaries = new JsonArray();

        long totalSamples = 0;
        long totalExact = 0;
        long totalClamped = 0;
        long totalEdgeOverwritten = 0;
        long totalPackedQuantized = 0;
        long totalUnexplainedHeights = 0;
        long totalHoleWordsChecked = 0;
        long totalHoleSquaresChecked = 0;
        long totalHoleWordMismatches = 0;
        long totalHoleMismatches = 0;
        long totalNonFloatHoleMaskIgnored = 0;
        long totalLiquidSquaresChecked = 0;
        long totalLiquidFlagMismatches = 0;
        long totalLiquidLevelMismatches = 0;
        long totalLiquidNotKeptReset = 0;
        long totalLiquidNotKeptMaxOfLayers = 0;
        long totalLiquidNotKeptEdgeRewrite = 0;
        long totalLiquidNwCornerSampling = 0;
        int comparedTiles = 0;
        int missingTiles = 0;
        int rejectedTiles = 0;

        int minX = Math.Max(0, centerX - radius);
        int maxX = Math.Min(WorldGrid.TilesPerSide - 1, centerX + radius);
        int minY = Math.Max(0, centerY - radius);
        int maxY = Math.Min(WorldGrid.TilesPerSide - 1, centerY + radius);

        for (int tx = minX; tx <= maxX; tx++)
        {
            for (int ty = minY; ty <= maxY; ty++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tile = new TileKey(mapId, tx, ty);
                if (serverData is null || clientService is null)
                {
                    missingTiles++;
                    tileSummaries.Add(new JsonObject
                    {
                        ["tile"] = new JsonArray(mapId, tx, ty),
                        ["state"] = TileState.Missing.ToString(),
                        ["detail"] = serverData is null ? "No ServerData service is registered." : "No ClientTerrainService is registered.",
                    });
                    continue;
                }

                ParsedFile<TerrainTile> serverFile = serverData.TerrainFile(tile);
                AdtLoad clientLoad = clientService.LoadAdt(tile);

                if (serverFile.State == TileState.Rejected || clientLoad.State == TileState.Rejected)
                {
                    rejectedTiles++;
                    string reason = serverFile.State == TileState.Rejected
                        ? $"Server .map rejected: {serverFile.Detail}"
                        : $"Client ADT rejected: {clientLoad.Detail}";
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

                if (!serverFile.IsOk || clientLoad.File is not { } adt)
                {
                    missingTiles++;
                    if (serverFile.IsOk && clientLoad.File is null)
                    {
                        AddCount(unexplainedClasses, "server-only-tile", 1);
                        AddExample(examples, $"{tile}: server has .map but client ADT is {clientLoad.State} ({clientLoad.Detail})");
                    }
                    else if (!serverFile.IsOk && clientLoad.File is not null)
                    {
                        AddCount(unexplainedClasses, "client-only-tile", 1);
                        AddExample(examples, $"{tile}: client has ADT but server .map is {serverFile.State} ({serverFile.Detail})");
                    }

                    tileSummaries.Add(new JsonObject
                    {
                        ["tile"] = new JsonArray(mapId, tx, ty),
                        ["state"] = TileState.Missing.ToString(),
                        ["detail"] = !serverFile.IsOk ? serverFile.Detail : clientLoad.Detail,
                    });
                    continue;
                }

                comparedTiles++;
                TileReport report = CompareTile(tile, adt, serverFile.Value, builder: null, cancellationToken);
                totalSamples += report.Samples;
                totalExact += report.Exact;
                totalClamped += report.Clamped;
                totalEdgeOverwritten += report.EdgeOverwritten;
                totalPackedQuantized += report.PackedQuantized;
                totalUnexplainedHeights += report.UnexplainedHeights;
                totalHoleWordsChecked += report.HoleWordsChecked;
                totalHoleSquaresChecked += report.HoleSquaresChecked;
                totalHoleWordMismatches += report.HoleWordMismatches;
                totalHoleMismatches += report.HoleMismatches;
                totalNonFloatHoleMaskIgnored += report.NonFloatHoleMaskIgnored;
                totalLiquidSquaresChecked += report.LiquidSquaresChecked;
                totalLiquidFlagMismatches += report.LiquidFlagMismatches;
                totalLiquidLevelMismatches += report.LiquidLevelMismatches;
                totalLiquidNotKeptReset += report.LiquidNotKeptReset;
                totalLiquidNotKeptMaxOfLayers += report.LiquidNotKeptMaxOfLayers;
                totalLiquidNotKeptEdgeRewrite += report.LiquidNotKeptEdgeRewrite;
                totalLiquidNwCornerSampling += report.LiquidNwCornerSampling;

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

                JsonObject tileJson = new()
                {
                    ["tile"] = new JsonArray(mapId, tx, ty),
                    ["state"] = (report.UnexplainedCount == 0 && report.ExpectedHeightDiffs == 0 && report.NonFloatHoleMaskIgnored == 0)
                        ? TileState.Empty.ToString()
                        : TileState.Ok.ToString(),
                    ["samples"] = report.Samples,
                    ["exactSamples"] = report.Exact,
                    ["unexplainedHeightMismatches"] = report.UnexplainedHeights,
                    ["holeMismatches"] = report.HoleMismatches,
                    ["unexplainedCount"] = report.UnexplainedCount,
                    ["expectedCount"] = report.ExpectedCount,
                };
                tileSummaries.Add(tileJson);
            }
        }

        long totalUnexplained = unexplainedClasses.Values.Sum();
        long totalExpected = expectedClasses.Values.Sum();

        return new JsonObject
        {
            ["layer"] = LayerIds.DiffTerrain,
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
                ["samples"] = totalSamples,
                ["exactSamples"] = totalExact,
                ["clampedSamples"] = totalClamped,
                ["edgeOverwrittenSamples"] = totalEdgeOverwritten,
                ["packedQuantizedSamples"] = totalPackedQuantized,
                ["unexplainedHeightMismatches"] = totalUnexplainedHeights,
                ["holeBlocksChecked"] = comparedTiles * 1024L,
                ["holeWordsChecked"] = totalHoleWordsChecked,
                ["holeSquaresChecked"] = totalHoleSquaresChecked,
                ["holeWordMismatches"] = totalHoleWordMismatches,
                ["holeMismatches"] = totalHoleMismatches,
                ["nonFloatHoleMaskIgnored"] = totalNonFloatHoleMaskIgnored,
                ["liquidSquaresChecked"] = totalLiquidSquaresChecked,
                ["liquidFlagMismatches"] = totalLiquidFlagMismatches,
                ["liquidLevelMismatches"] = totalLiquidLevelMismatches,
                ["liquidNotKeptReset"] = totalLiquidNotKeptReset,
                ["liquidNotKeptMaxOfLayers"] = totalLiquidNotKeptMaxOfLayers,
                ["liquidNotKeptEdgeRewrite"] = totalLiquidNotKeptEdgeRewrite,
                ["liquidNwCornerSampling"] = totalLiquidNwCornerSampling,
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

    private static TileReport CompareTile(TileKey tile, AdtFile adt, TerrainTile server, GeometryBuilder? builder, CancellationToken cancellationToken)
    {
        var report = new TileReport();
        WorldPoint origin = WorldGrid.Origin(tile);
        double sq = WorldGrid.SquareSize;

        // Attribute ids in builder:
        // 1 = height only-ref, 2 = height only-server, 3 = height expected
        // 4 = hole only-ref, 5 = hole only-server, 6 = hole expected
        // 7 = liquid only-ref, 8 = liquid only-server
        const uint AttrHeightRef = 1;
        const uint AttrHeightServer = 2;
        const uint AttrHeightExpected = 3;
        const uint AttrHoleRef = 4;
        const uint AttrHoleServer = 5;
        const uint AttrHoleExpected = 6;
        const uint AttrLiquidRef = 7;
        const uint AttrLiquidServer = 8;

        (float[] v9, float[] v8) = RawHeights(adt);
        (float min, float max) = Range(v9, v8);
        bool clamped = ClampLow(v9, v8, ref min, ref max);
        double packedSlack = server.Storage == TerrainTile.HeightStorage.Packed
            ? Math.Max(0.05, Math.Abs(max - min) / 65535.0 * 2.0)
            : Tolerance;

        for (int i = 0; i < 16; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int j = 0; j < 16; j++)
            {
                AdtChunk chunk = adt.Chunk(i, j);
                for (int y = 0; y <= 8; y++)
                {
                    for (int x = 0; x <= 8; x++)
                    {
                        int row = (i * 8) + y;
                        int col = (j * 8) + x;
                        bool sharedEdge = (y == 8 && i < 15) || (x == 8 && j < 15);
                        float clientH = chunk.OuterHeight(y, x);
                        float extractorH = v9[(row * (Squares + 1)) + col];
                        float serverH = server.CornerHeight(row, col);
                        double wx = origin.X - (row * sq);
                        double wy = origin.Y - (col * sq);
                        ClassifySample(tile, "V9", row, col, wx, wy, clientH, extractorH, serverH, clamped, sharedEdge, server.Storage, packedSlack, report, builder, AttrHeightRef, AttrHeightServer, AttrHeightExpected);
                    }
                }

                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        int row = (i * 8) + y;
                        int col = (j * 8) + x;
                        float clientH = chunk.InnerHeight(y, x);
                        float extractorH = v8[(row * Squares) + col];
                        float serverH = server.CentreHeight(row, col);
                        double wx = origin.X - ((row + 0.5) * sq);
                        double wy = origin.Y - ((col + 0.5) * sq);
                        ClassifySample(tile, "V8", row, col, wx, wy, clientH, extractorH, serverH, clamped, sharedEdge: false, server.Storage, packedSlack, report, builder, AttrHeightRef, AttrHeightServer, AttrHeightExpected);
                    }
                }

                // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:755-763 (holes[i][j] = the MCNK hole word, unchanged)
                report.HoleWordsChecked++;
                ushort serverHoleWord = ServerHoleWord(server, i, j);
                if (serverHoleWord != chunk.Holes)
                {
                    report.HoleWordMismatches++;
                }
            }
        }

        // Compare 128 x 128 squares (1,024 4x4 hole blocks / 16,384 squares) between client hole bitmap and server hole behavior.
        for (int r = 0; r < Squares; r++)
        {
            for (int c = 0; c < Squares; c++)
            {
                report.HoleSquaresChecked++;
                bool clientHole = adt.Chunk(r / 8, c / 8).IsHoleSquare(r % 8, c % 8);
                bool serverHole = server.IsServerHole(r, c);
                if (clientHole == serverHole)
                {
                    continue;
                }

                // ref: src/ArcaneCore.Game/Maps/Terrain/TerrainTile.cs:244 (only float storage consults the hole mask; packed/flat ignore it)
                if (clientHole && !serverHole && server.Storage != TerrainTile.HeightStorage.Float && server.HoleMaskCovers(r, c))
                {
                    report.NonFloatHoleMaskIgnored++;
                    report.NoteExpected("non-float-hole-mask-ignored");
                    DrawSquare(builder, origin, server, r, c, AttrHoleExpected, ColorKeys.Expected, "non-float-hole-mask-ignored",
                        "client hole bit is set in the .map file, but the server only checks the hole mask on float-stored tiles");
                }
                else
                {
                    report.HoleMismatches++;
                    string cls = clientHole ? "hole-only-reference" : "hole-only-server";
                    report.NoteUnexplained(cls, FormattableString.Invariant($"{tile} square ({r}, {c}): client hole={clientHole}, server hole={serverHole}"));
                    uint attr = clientHole ? AttrHoleRef : AttrHoleServer;
                    string colorKey = clientHole ? ColorKeys.OnlyReference : ColorKeys.OnlyServer;
                    DrawSquare(builder, origin, server, r, c, attr, colorKey, cls,
                        clientHole ? "square is a hole in the client ADT but solid in the server .map" : "square is a hole in the server .map but solid in the client ADT");
                }
            }
        }

        CompareLiquid(tile, adt, server, origin, report, builder, AttrLiquidRef, AttrLiquidServer);
        return report;
    }

    private static void ClassifySample(
        TileKey tile,
        string grid,
        int row,
        int col,
        double wx,
        double wy,
        float client,
        float extractor,
        float server,
        bool clamped,
        bool sharedEdge,
        TerrainTile.HeightStorage storage,
        double packedSlack,
        TileReport report,
        GeometryBuilder? builder,
        uint attrRef,
        uint attrServer,
        uint attrExpected)
    {
        report.Samples++;
        if (Math.Abs(client - server) <= Tolerance)
        {
            report.Exact++;
            return;
        }

        bool matchesExtractor = Math.Abs(extractor - server) <= Tolerance
            || (storage == TerrainTile.HeightStorage.Packed && Math.Abs(extractor - server) <= packedSlack);

        if (matchesExtractor)
        {
            if (clamped && client < ExtractorMinHeight)
            {
                // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:479-494 (CONF_allow_height_limit clamps below -500.0f)
                report.Clamped++;
                report.NoteExpected("height-clamped-at-min");
                DrawHeightDelta(builder, wx, wy, client, server, attrExpected, ColorKeys.Expected, "height-clamped-at-min",
                    "client sample is below -500 yd and clamped by the extractor's CONF_use_minHeight rule");
                return;
            }

            if (sharedEdge)
            {
                // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:410-454 (later MCNK overwrites shared border V9 sample)
                report.EdgeOverwritten++;
                report.NoteExpected("shared-chunk-edge-overwrite");
                DrawHeightDelta(builder, wx, wy, client, server, attrExpected, ColorKeys.Expected, "shared-chunk-edge-overwrite",
                    "shared MCNK border sample overwritten by the later chunk in extractor write order");
                return;
            }

            if (storage == TerrainTile.HeightStorage.Packed)
            {
                report.PackedQuantized++;
                report.NoteExpected("packed-height-quantization");
                DrawHeightDelta(builder, wx, wy, client, server, attrExpected, ColorKeys.Expected, "packed-height-quantization",
                    "uint16/uint8 packed height quantization difference");
                return;
            }
        }

        report.UnexplainedHeights++;
        report.NoteUnexplained(
            "height-mismatch",
            FormattableString.Invariant($"{tile} {grid}[{row}, {col}]: client {client:G6}, extractor {extractor:G6}, server {server:G6}"));
        if (builder is not null)
        {
            builder.DescribeAttribute(attrRef, () => new JsonObject
            {
                ["colorKey"] = ColorKeys.OnlyReference,
                ["kind"] = "terrain height mismatch (client sample)",
                ["class"] = "height-mismatch",
            });
            builder.DescribeAttribute(attrServer, () => new JsonObject
            {
                ["colorKey"] = ColorKeys.OnlyServer,
                ["kind"] = "terrain height mismatch (server sample)",
                ["class"] = "height-mismatch",
            });
            uint vc = builder.AddVertex(wx, wy, client);
            uint vs = builder.AddVertex(wx, wy, server);
            builder.AddLine(vc, vs, attrRef);
            builder.AddPoint(vc, attrRef);
            builder.AddPoint(vs, attrServer);
        }
    }

    private static void DrawHeightDelta(
        GeometryBuilder? builder,
        double wx,
        double wy,
        float client,
        float server,
        uint attr,
        string colorKey,
        string className,
        string note)
    {
        if (builder is null)
        {
            return;
        }

        builder.DescribeAttribute(attr, () => new JsonObject
        {
            ["colorKey"] = colorKey,
            ["kind"] = "terrain height difference",
            ["class"] = className,
            ["note"] = note,
        });
        uint vc = builder.AddVertex(wx, wy, client);
        uint vs = builder.AddVertex(wx, wy, server);
        builder.AddLine(vc, vs, attr);
        builder.AddPoint(vc, attr);
        builder.AddPoint(vs, attr);
    }

    private static void DrawSquare(
        GeometryBuilder? builder,
        WorldPoint origin,
        TerrainTile server,
        int r,
        int c,
        uint attr,
        string colorKey,
        string className,
        string note)
    {
        if (builder is null)
        {
            return;
        }

        builder.DescribeAttribute(attr, () => new JsonObject
        {
            ["colorKey"] = colorKey,
            ["kind"] = "terrain hole difference",
            ["class"] = className,
            ["note"] = note,
        });
        double sq = WorldGrid.SquareSize;
        uint a = builder.AddVertex(origin.X - (r * sq), origin.Y - (c * sq), TerrainLayerSource.DrawHeight(server, r, c));
        uint b = builder.AddVertex(origin.X - ((r + 1) * sq), origin.Y - (c * sq), TerrainLayerSource.DrawHeight(server, r + 1, c));
        uint cc = builder.AddVertex(origin.X - (r * sq), origin.Y - ((c + 1) * sq), TerrainLayerSource.DrawHeight(server, r, c + 1));
        uint d = builder.AddVertex(origin.X - ((r + 1) * sq), origin.Y - ((c + 1) * sq), TerrainLayerSource.DrawHeight(server, r + 1, c + 1));
        builder.AddTriangle(a, b, d, attr, edges: false);
        builder.AddTriangle(a, d, cc, attr, edges: false);
        builder.AddEdge(a, b, attr);
        builder.AddEdge(b, d, attr);
        builder.AddEdge(d, cc, attr);
        builder.AddEdge(cc, a, attr);
    }

    private static void CompareLiquid(
        TileKey tile,
        AdtFile adt,
        TerrainTile server,
        WorldPoint origin,
        TileReport report,
        GeometryBuilder? builder,
        uint attrLiquidRef,
        uint attrLiquidServer)
    {
        LiquidModel model = EmulateLiquid(adt);
        double chunkSize = WorldGrid.TileSize / 16.0;
        double sq = WorldGrid.SquareSize;

        for (int i = 0; i < 16; i++)
        {
            for (int j = 0; j < 16; j++)
            {
                AdtChunk chunk = adt.Chunk(i, j);
                CountLayerLiquidDiffs(chunk, i, j, model, server, report);
                byte serverFlags = (byte)server.GetTerrainType((float)(origin.X - ((i + 0.5) * chunkSize)), (float)(origin.Y - ((j + 0.5) * chunkSize)));
                byte expectedFlags = model.Flags[(i * 16) + j];
                if (serverFlags != expectedFlags)
                {
                    report.LiquidFlagMismatches++;
                    report.NoteUnexplained(
                        "liquid-flag-mismatch",
                        FormattableString.Invariant($"{tile} MCNK ({i}, {j}): server liquid flags 0x{serverFlags:X2}, extractor rules 0x{expectedFlags:X2}"));
                }
            }
        }

        for (int row = 0; row < Squares; row++)
        {
            for (int col = 0; col < Squares; col++)
            {
                report.LiquidSquaresChecked++;
                double cx = origin.X - ((row + 0.5) * sq);
                double cy = origin.Y - ((col + 0.5) * sq);
                float level = server.GetLiquidLevel((float)cx, (float)cy);
                float expected = ExpectedLiquidLevel(model, row, col);
                if (Math.Abs(level - expected) > Tolerance)
                {
                    report.LiquidLevelMismatches++;
                    report.NoteUnexplained(
                        "liquid-level-mismatch",
                        FormattableString.Invariant($"{tile} liquid square ({row}, {col}): server {level:G6}, extractor rules {expected:G6}"));
                    if (builder is not null)
                    {
                        bool refHas = expected > TerrainTile.InvalidHeight;
                        uint attr = refHas ? attrLiquidRef : attrLiquidServer;
                        string colorKey = refHas ? ColorKeys.OnlyReference : ColorKeys.OnlyServer;
                        double drawZ = refHas ? expected : (level > TerrainTile.InvalidHeight ? level : TerrainLayerSource.DrawHeight(server, row, col));
                        builder.DescribeAttribute(attr, () => new JsonObject
                        {
                            ["colorKey"] = colorKey,
                            ["kind"] = "liquid level mismatch",
                            ["class"] = "liquid-level-mismatch",
                        });
                        uint a = builder.AddVertex(origin.X - (row * sq), origin.Y - (col * sq), drawZ);
                        uint b = builder.AddVertex(origin.X - ((row + 1) * sq), origin.Y - (col * sq), drawZ);
                        uint cc = builder.AddVertex(origin.X - (row * sq), origin.Y - ((col + 1) * sq), drawZ);
                        uint d = builder.AddVertex(origin.X - ((row + 1) * sq), origin.Y - ((col + 1) * sq), drawZ);
                        builder.AddTriangle(a, b, d, attr);
                        builder.AddTriangle(a, d, cc, attr);
                    }
                }
            }
        }
    }

    private static void CountLayerLiquidDiffs(AdtChunk chunk, int i, int j, LiquidModel model, TerrainTile server, TileReport report)
    {
        var chunkMax = new float[81];
        Array.Fill(chunkMax, float.NegativeInfinity);
        foreach (AdtLiquidLayer layer in chunk.LiquidLayers)
        {
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    if (!layer.IsVisible(y, x))
                    {
                        continue;
                    }

                    for (int vy = y; vy <= y + 1; vy++)
                    {
                        for (int vx = x; vx <= x + 1; vx++)
                        {
                            chunkMax[(vy * 9) + vx] = Math.Max(chunkMax[(vy * 9) + vx], layer.Height(vy, vx));
                        }
                    }
                }
            }
        }

        foreach (AdtLiquidLayer layer in chunk.LiquidLayers)
        {
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    if (!layer.IsVisible(y, x))
                    {
                        continue;
                    }

                    int row = (i * 8) + y;
                    int col = (j * 8) + x;
                    bool serverHasSquare = server.TryGetLiquidSquare(row, col, out _, out _, out float serverSquareLevel);

                    for (int vy = y; vy <= y + 1; vy++)
                    {
                        for (int vx = x; vx <= x + 1; vx++)
                        {
                            float merged = model.Grid[((((i * 8) + vy) * (Squares + 1)) + (j * 8)) + vx];
                            float own = layer.Height(vy, vx);
                            if (Math.Abs(own - merged) > Tolerance)
                            {
                                float best = chunkMax[(vy * 9) + vx];
                                if (merged == ExtractorMinHeight)
                                {
                                    // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:703-706 (hidden square resets its own NW vertex to -500)
                                    report.LiquidNotKeptReset++;
                                    report.NoteExpected("liquid-hidden-vertex-reset");
                                }
                                else if (Math.Abs(best - merged) <= Tolerance && own < best)
                                {
                                    // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:604-614 (multiple MCLQ layers keep max vertex height)
                                    report.LiquidNotKeptMaxOfLayers++;
                                    report.NoteExpected("multi-layer-liquid-merge");
                                }
                                else if (vy is 0 or 8 || vx is 0 or 8)
                                {
                                    // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:626-633 (neighbouring MCNK overwrites shared edge vertex)
                                    report.LiquidNotKeptEdgeRewrite++;
                                    report.NoteExpected("liquid-chunk-edge-overwrite");
                                }
                            }
                            else if (serverHasSquare && (vy != y || vx != x) && Math.Abs(own - serverSquareLevel) > Tolerance)
                            {
                                // ref: src/ArcaneCore.Game/Maps/Terrain/TerrainTile.cs:261-279 (GetLiquidLevel returns the NW corner vertex of the square)
                                report.LiquidNwCornerSampling++;
                                report.NoteExpected("liquid-square-nw-corner-sampling");
                            }
                        }
                    }
                }
            }
        }
    }

    private static ushort ServerHoleWord(TerrainTile server, int chunkRow, int chunkCol)
    {
        ushort mask = 0;
        for (int hr = 0; hr < 4; hr++)
        {
            for (int hc = 0; hc < 4; hc++)
            {
                int r = (chunkRow * 8) + (hr * 2);
                int c = (chunkCol * 8) + (hc * 2);
                if (server.HoleMaskCovers(r, c))
                {
                    mask |= (ushort)(1 << ((hr * 4) + hc));
                }
            }
        }

        return mask;
    }

    // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:410-454
    private static (float[] V9, float[] V8) RawHeights(AdtFile adt)
    {
        var v9 = new float[(Squares + 1) * (Squares + 1)];
        var v8 = new float[Squares * Squares];
        for (int i = 0; i < 16; i++)
        {
            for (int j = 0; j < 16; j++)
            {
                AdtChunk chunk = adt.Chunk(i, j);
                for (int y = 0; y <= 8; y++)
                {
                    for (int x = 0; x <= 8; x++)
                    {
                        v9[((((i * 8) + y) * (Squares + 1)) + (j * 8)) + x] = chunk.OuterHeight(y, x);
                    }
                }

                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        v8[((((i * 8) + y) * Squares) + (j * 8)) + x] = chunk.InnerHeight(y, x);
                    }
                }
            }
        }

        return (v9, v8);
    }

    // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:456-477
    private static (float Min, float Max) Range(float[] v9, float[] v8)
    {
        float min = 20000f;
        float max = -20000f;
        foreach (float h in v8)
        {
            max = Math.Max(max, h);
            min = Math.Min(min, h);
        }

        foreach (float h in v9)
        {
            max = Math.Max(max, h);
            min = Math.Min(min, h);
        }

        return (min, max);
    }

    // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:479-494
    private static bool ClampLow(float[] v9, float[] v8, ref float min, ref float max)
    {
        if (!(min < ExtractorMinHeight))
        {
            return false;
        }

        for (int i = 0; i < v8.Length; i++)
        {
            v8[i] = Math.Max(v8[i], ExtractorMinHeight);
        }

        for (int i = 0; i < v9.Length; i++)
        {
            v9[i] = Math.Max(v9[i], ExtractorMinHeight);
        }

        min = ExtractorMinHeight;
        max = Math.Max(max, ExtractorMinHeight);
        return true;
    }

    // ref: src/ArcaneCore.Game/Maps/Terrain/TerrainTile.cs:261-279
    private static float ExpectedLiquidLevel(LiquidModel model, int row, int column)
    {
        if (!model.HasLiquid)
        {
            return TerrainTile.InvalidHeightValue;
        }

        if (model.NoHeight)
        {
            return model.Level;
        }

        int r = row - model.OffsetRow;
        int c = column - model.OffsetColumn;
        return r >= 0 && r < model.Rows && c >= 0 && c < model.Columns
            ? model.Grid[(row * (Squares + 1)) + column]
            : TerrainTile.InvalidHeightValue;
    }

    // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:557-741
    private static LiquidModel EmulateLiquid(AdtFile adt)
    {
        const int vertices = Squares + 1;
        var show = new bool[Squares * Squares];
        var grid = new float[vertices * vertices];
        Array.Fill(grid, ExtractorMinHeight);
        var entry = new ushort[256];
        var flags = new byte[256];
        for (int i = 0; i < 16; i++)
        {
            for (int j = 0; j < 16; j++)
            {
                AdtChunk chunk = adt.Chunk(i, j);
                var cellHeight = new float[9 * 9];
                var cellSet = new bool[9 * 9];
                int bestCells = 0;
                float bestHeight = 0f;
                int count = 0;
                foreach (AdtLiquidLayer layer in chunk.LiquidLayers)
                {
                    (AdtLiquidKind _, ushort layerEntry, byte mapFlag) = LayerTypes[(int)layer.Kind];
                    int layerCells = 0;
                    float layerHeight = -20000f;
                    for (int y = 0; y < 8; y++)
                    {
                        for (int x = 0; x < 8; x++)
                        {
                            if (!layer.IsVisible(y, x))
                            {
                                continue;
                            }

                            show[(((i * 8) + y) * Squares) + (j * 8) + x] = true;
                            if ((layer.SquareFlags(y, x) & AdtLiquidLayer.DarkWaterSquare) != 0)
                            {
                                flags[(i * 16) + j] |= 0x10;
                            }

                            layerCells++;
                            count++;
                            for (int vy = y; vy <= y + 1; vy++)
                            {
                                for (int vx = x; vx <= x + 1; vx++)
                                {
                                    float h = layer.Height(vy, vx);
                                    int at = (vy * 9) + vx;
                                    if (!cellSet[at] || cellHeight[at] < h)
                                    {
                                        cellHeight[at] = h;
                                        cellSet[at] = true;
                                    }

                                    layerHeight = Math.Max(layerHeight, h);
                                }
                            }
                        }
                    }

                    if (layerCells > bestCells || (layerCells == bestCells && layerHeight > bestHeight))
                    {
                        bestCells = layerCells;
                        bestHeight = layerHeight;
                        entry[(i * 16) + j] = layerEntry;
                        flags[(i * 16) + j] = (byte)((flags[(i * 16) + j] & 0x10) | mapFlag);
                    }
                }

                if (count == 0)
                {
                    continue;
                }

                for (int y = 0; y <= 8; y++)
                {
                    for (int x = 0; x <= 8; x++)
                    {
                        if (cellSet[(y * 9) + x])
                        {
                            grid[((((i * 8) + y) * vertices) + (j * 8)) + x] = cellHeight[(y * 9) + x];
                        }
                    }
                }
            }
        }

        ushort firstEntry = entry[0];
        byte firstFlag = flags[0];
        bool fullType = false;
        for (int c = 0; c < 256 && !fullType; c++)
        {
            fullType = entry[c] != firstEntry || flags[c] != firstFlag;
        }

        if (firstFlag == 0 && !fullType)
        {
            return new LiquidModel(false, false, entry, flags, 0, 0, 0, 0, 0f, grid);
        }

        int minColumn = 255;
        int minRow = 255;
        int maxColumn = 0;
        int maxRow = 0;
        float maxLevel = -20000f;
        float minLevel = 20000f;
        for (int y = 0; y < Squares; y++)
        {
            for (int x = 0; x < Squares; x++)
            {
                int at = (y * vertices) + x;
                if (show[(y * Squares) + x])
                {
                    minColumn = Math.Min(minColumn, x);
                    maxColumn = Math.Max(maxColumn, x);
                    minRow = Math.Min(minRow, y);
                    maxRow = Math.Max(maxRow, y);
                    maxLevel = Math.Max(maxLevel, grid[at]);
                    minLevel = Math.Min(minLevel, grid[at]);
                }
                else
                {
                    grid[at] = ExtractorMinHeight;
                    minLevel = Math.Min(minLevel, ExtractorMinHeight);
                }
            }
        }

        return new LiquidModel(
            HasLiquid: true,
            NoHeight: maxLevel == minLevel,
            Entry: entry,
            Flags: flags,
            OffsetRow: minRow,
            OffsetColumn: minColumn,
            Rows: maxRow - minRow + 2,
            Columns: maxColumn - minColumn + 2,
            Level: minLevel,
            Grid: grid);
    }

    private static void WriteStats(JsonObject stats, TileReport report)
    {
        stats["samples"] = report.Samples;
        stats["exactSamples"] = report.Exact;
        stats["clampedSamples"] = report.Clamped;
        stats["edgeOverwrittenSamples"] = report.EdgeOverwritten;
        stats["packedQuantizedSamples"] = report.PackedQuantized;
        stats["unexplainedHeightMismatches"] = report.UnexplainedHeights;
        stats["holeBlocksChecked"] = 1024;
        stats["holeWordsChecked"] = report.HoleWordsChecked;
        stats["holeSquaresChecked"] = report.HoleSquaresChecked;
        stats["holeWordMismatches"] = report.HoleWordMismatches;
        stats["holeMismatches"] = report.HoleMismatches;
        stats["nonFloatHoleMaskIgnored"] = report.NonFloatHoleMaskIgnored;
        stats["liquidSquaresChecked"] = report.LiquidSquaresChecked;
        stats["liquidFlagMismatches"] = report.LiquidFlagMismatches;
        stats["liquidLevelMismatches"] = report.LiquidLevelMismatches;
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

    private sealed record LiquidModel(
        bool HasLiquid,
        bool NoHeight,
        ushort[] Entry,
        byte[] Flags,
        int OffsetRow,
        int OffsetColumn,
        int Rows,
        int Columns,
        float Level,
        float[] Grid);

    private sealed class TileReport
    {
        public long Samples { get; set; }

        public long Exact { get; set; }

        public long Clamped { get; set; }

        public long EdgeOverwritten { get; set; }

        public long PackedQuantized { get; set; }

        public long UnexplainedHeights { get; set; }

        public long HoleWordsChecked { get; set; }

        public long HoleSquaresChecked { get; set; }

        public long HoleWordMismatches { get; set; }

        public long HoleMismatches { get; set; }

        public long NonFloatHoleMaskIgnored { get; set; }

        public long LiquidSquaresChecked { get; set; }

        public long LiquidFlagMismatches { get; set; }

        public long LiquidLevelMismatches { get; set; }

        public long LiquidNotKeptReset { get; set; }

        public long LiquidNotKeptMaxOfLayers { get; set; }

        public long LiquidNotKeptEdgeRewrite { get; set; }

        public long LiquidNwCornerSampling { get; set; }

        public long ExpectedHeightDiffs => Clamped + EdgeOverwritten + PackedQuantized;

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
}
