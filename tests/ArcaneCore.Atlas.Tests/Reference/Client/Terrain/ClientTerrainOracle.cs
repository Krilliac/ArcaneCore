using System.Globalization;
using System.Text;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Client.Terrain;
using ArcaneCore.Game.Maps.Terrain;

namespace ArcaneCore.Atlas.Tests.Reference.Client.Terrain;

/// <summary>
/// Compares client ADTs with the server's <c>.map</c> files sample by sample and classifies every disagreement by the
/// vmangos extractor rule that explains it (see <see cref="ExtractorEmulation"/>). A disagreement no rule explains is
/// counted as unexplained and kept as an example, never dropped.
/// </summary>
internal sealed class ClientTerrainOracle
{
    /// <summary>Height tolerance in yards.</summary>
    public const double Tolerance = 1e-3;

    private const int Squares = ExtractorEmulation.Squares;
    private const int MaxExamples = 12;

    private readonly List<string> _examples = [];

    /// <summary>Tiles compared.</summary>
    public int Tiles { get; private set; }

    /// <summary>Tiles the server has a .map for while the client service could not load the ADT.</summary>
    public int ServerOnlyTiles { get; private set; }

    /// <summary>Tiles whose MCNK header positions disagree with the tile the file name gives.</summary>
    public int PositionMismatchTiles { get; private set; }

    /// <summary>Tiles the extractor stores as one flat height (its max == min rule).</summary>
    public int FlatTiles { get; private set; }

    /// <summary>Tiles the extractor clamped at its minimum height.</summary>
    public int ClampedTiles { get; private set; }

    /// <summary>Tiles whose server height storage differs from what the extractor rules predict.</summary>
    public int StorageMismatches { get; private set; }

    /// <summary>MCNKs with no MCVT (the extractor keeps the header height for all their samples).</summary>
    public long ChunksWithoutHeights { get; private set; }

    /// <summary>Client reference samples compared (145 per MCNK).</summary>
    public long Samples { get; private set; }

    /// <summary>Samples equal to the server's within <see cref="Tolerance"/>.</summary>
    public long Exact { get; private set; }

    /// <summary>Samples below the extractor floor that the server holds at the floor.</summary>
    public long Clamped { get; private set; }

    /// <summary>Shared chunk-edge corners where the server keeps the later chunk's value (the extractor's write order).</summary>
    public long EdgeOverwritten { get; private set; }

    /// <summary>Samples no extractor rule explains.</summary>
    public long Unexplained { get; private set; }

    /// <summary>MCNKs with a non-zero hole word.</summary>
    public long ChunksWithHoles { get; private set; }

    /// <summary>Squares the client marks as holes.</summary>
    public long HoleSquares { get; private set; }

    /// <summary>MCNKs whose hole word differs from the server's.</summary>
    public long HoleWordMismatches { get; private set; }

    /// <summary>Squares where the server's GetHeight hole answer differs from the client hole flag.</summary>
    public long HoleQueryMismatches { get; private set; }

    /// <summary>Squares checked through the server's GetHeight for holes.</summary>
    public long HoleQueries { get; private set; }

    /// <summary>Tiles the extractor gives a liquid section.</summary>
    public int LiquidTiles { get; private set; }

    /// <summary>Liquid tiles stored as one level.</summary>
    public int FlatLiquidTiles { get; private set; }

    /// <summary>MCNKs with at least one MCLQ layer.</summary>
    public long LiquidChunks { get; private set; }

    /// <summary>MCLQ layers in all compared MCNKs.</summary>
    public long LiquidLayers { get; private set; }

    /// <summary>MCNKs with two or more MCLQ layers (the server keeps one type per chunk).</summary>
    public long StackedChunks { get; private set; }

    /// <summary>Squares shown by two or more layers of the same MCNK.</summary>
    public long OverlappingSquares { get; private set; }

    /// <summary>Visible squares summed over all layers.</summary>
    public long LayerSquares { get; private set; }

    /// <summary>Corner heights of visible layer squares.</summary>
    public long LayerVertices { get; private set; }

    /// <summary>Corner heights of visible layer squares that the server's merged grid does not hold.</summary>
    public long LayerVerticesNotKept { get; private set; }

    /// <summary>Vertices the extractor read for a shown square and then reset because their own square is hidden.</summary>
    public long ReadButReset { get; private set; }

    /// <summary>Not-kept corner heights under a square the merged grid reset to the floor (the extractor's reset of hidden squares).</summary>
    public long NotKeptReset { get; private set; }

    /// <summary>Not-kept corner heights where another layer of the same MCNK is higher at that corner (the extractor keeps the maximum).</summary>
    public long NotKeptMaxOfLayers { get; private set; }

    /// <summary>Not-kept corner heights on an MCNK edge that a later neighbouring MCNK rewrote in the merged grid.</summary>
    public long NotKeptEdgeRewrite { get; private set; }

    /// <summary>Not-kept corner heights that fit none of the classes above.</summary>
    public long NotKeptOther { get; private set; }

    /// <summary>MCNKs whose server liquid type flags differ from the extractor rules' merge of the client layers.</summary>
    public long LiquidFlagMismatches { get; private set; }

    /// <summary>Squares whose server liquid level differs from the extractor rules' merge of the client layers.</summary>
    public long LiquidLevelMismatches { get; private set; }

    /// <summary>Up to a dozen disagreements, for the report.</summary>
    public IReadOnlyList<string> Examples => _examples;

    /// <summary>Records a tile the server has but the client service could not load.</summary>
    public void ServerOnly(TileKey tile, AdtLoad load)
    {
        ServerOnlyTiles++;
        Example($"{tile}: the server has a .map but the client ADT is {load.State}: {load.Detail}");
    }

    /// <summary>Compares the heights, holes and liquid of one tile.</summary>
    public void Compare(TileKey tile, AdtFile adt, ServerMapTile server)
    {
        ArgumentNullException.ThrowIfNull(adt);
        ArgumentNullException.ThrowIfNull(server);
        Tiles++;
        if (AdtGeometry.CountPositionMismatches(adt, tile) != 0)
        {
            PositionMismatchTiles++;
            Example($"{tile}: MCNK positions do not match the tile its name gives");
        }

        CompareHeights(tile, adt, server);
        CompareLiquid(tile, adt, server);
    }

    /// <summary>A multi-line summary of every count.</summary>
    public string Describe(string name)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"{name}: {Tiles} tiles compared, {ServerOnlyTiles} server-only, {PositionMismatchTiles} with MCNK position mismatches");
        text.AppendLine(CultureInfo.InvariantCulture, $"  heights: {Samples} samples: {Exact} exact (<= {Tolerance} yd), {Clamped} clamped at {ExtractorEmulation.MinHeight} (extractor height limit), {EdgeOverwritten} chunk-edge corners the later chunk overwrote, {Unexplained} unexplained");
        text.AppendLine(CultureInfo.InvariantCulture, $"  tiles: {FlatTiles} flat (no-height), {ClampedTiles} clamped, {StorageMismatches} storage mismatches; {ChunksWithoutHeights} MCNKs without MCVT");
        text.AppendLine(CultureInfo.InvariantCulture, $"  holes: {ChunksWithHoles} MCNKs with holes, {HoleSquares} hole squares, {HoleWordMismatches} hole-word mismatches, {HoleQueryMismatches} of {HoleQueries} GetHeight hole answers differ");
        text.AppendLine(CultureInfo.InvariantCulture, $"  liquid: {LiquidTiles} tiles with a liquid section ({FlatLiquidTiles} one level), {LiquidChunks} MCNKs with {LiquidLayers} MCLQ layers, {StackedChunks} MCNKs with 2+ layers, {OverlappingSquares} squares shown by 2+ layers");
        text.AppendLine(CultureInfo.InvariantCulture, $"  liquid vs server merge: {LayerSquares} visible layer squares, {LayerVerticesNotKept} of {LayerVertices} layer corner heights not kept by the merged grid, {ReadButReset} read-then-reset vertices; not kept by class: {NotKeptReset} reset, {NotKeptMaxOfLayers} max-of-layers, {NotKeptEdgeRewrite} neighbour edge rewrite, {NotKeptOther} other; {LiquidFlagMismatches} type-flag and {LiquidLevelMismatches} level mismatches with the extractor merge rules");
        foreach (string example in _examples)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  e.g. {example}");
        }

        return text.ToString();
    }

    private static (double X, double Y) SquareCentre(TileKey tile, int row, int column)
    {
        WorldPoint origin = WorldGrid.Origin(tile);
        return (origin.X - ((row + 0.5) * WorldGrid.SquareSize), origin.Y - ((column + 0.5) * WorldGrid.SquareSize));
    }

    private void CompareHeights(TileKey tile, AdtFile adt, ServerMapTile server)
    {
        (float[] v9, float[] v8) = ExtractorEmulation.RawHeights(adt);
        (float min, float max) = ExtractorEmulation.Range(v9, v8);
        bool clamped = ExtractorEmulation.ClampLow(v9, v8, ref min, ref max);
        // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:505-506 (max == min: MAP_HEIGHT_NO_HEIGHT, one height for the tile)
        // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:95 (CONF_allow_float_to_int is off by default, so heights stay float)
        bool flat = max == min;
        string expected = flat ? "Flat" : "Float";
        FlatTiles += flat ? 1 : 0;
        ClampedTiles += clamped ? 1 : 0;
        if (server.Storage != expected)
        {
            StorageMismatches++;
            Example($"{tile}: the server stores heights as {server.Storage}, the extractor rules give {expected}");
        }

        for (int i = 0; i < 16; i++)
        {
            for (int j = 0; j < 16; j++)
            {
                AdtChunk chunk = adt.Chunk(i, j);
                ChunksWithoutHeights += chunk.HasHeights ? 0 : 1;
                for (int y = 0; y <= 8; y++)
                {
                    for (int x = 0; x <= 8; x++)
                    {
                        int row = (i * 8) + y;
                        int column = (j * 8) + x;
                        bool shared = (y == 8 && i < 15) || (x == 8 && j < 15);
                        Classify(tile, "V9", row, column, chunk.OuterHeight(y, x), v9[(row * (Squares + 1)) + column], server.V9(row, column), clamped, shared);
                    }
                }

                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        int row = (i * 8) + y;
                        int column = (j * 8) + x;
                        Classify(tile, "V8", row, column, chunk.InnerHeight(y, x), v8[(row * Squares) + column], server.V8(row, column), clamped, false);
                    }
                }

                // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:755-763 (holes[i][j] = the MCNK hole word, unchanged)
                ChunksWithHoles += chunk.Holes != 0 ? 1 : 0;
                HoleSquares += chunk.HoleSquares;
                if (server.Holes(i, j) != chunk.Holes)
                {
                    HoleWordMismatches++;
                    Example($"{tile} MCNK ({i}, {j}): client holes 0x{chunk.Holes:X4}, server 0x{server.Holes(i, j):X4}");
                }
            }
        }

        if (server.Storage != "Float")
        {
            // A flat tile's GetHeight answers the one height everywhere, holes included.
            return;
        }

        for (int row = 0; row < Squares; row++)
        {
            for (int column = 0; column < Squares; column++)
            {
                (double x, double y) = SquareCentre(tile, row, column);
                bool serverHole = server.Tile.GetHeight((float)x, (float)y) == TerrainTile.InvalidHeightValue;
                bool clientHole = adt.Chunk(row / 8, column / 8).IsHoleSquare(row % 8, column % 8);
                HoleQueries++;
                if (serverHole != clientHole)
                {
                    HoleQueryMismatches++;
                    Example($"{tile} square ({row}, {column}): client hole {clientHole}, server GetHeight hole {serverHole}");
                }
            }
        }
    }

    private void Classify(TileKey tile, string grid, int row, int column, float client, float extractor, float server, bool clamped, bool sharedEdge)
    {
        Samples++;
        if (Math.Abs(client - server) <= Tolerance)
        {
            Exact++;
            return;
        }

        if (Math.Abs(extractor - server) <= Tolerance)
        {
            if (clamped && client < ExtractorEmulation.MinHeight)
            {
                Clamped++;
                return;
            }

            if (sharedEdge)
            {
                EdgeOverwritten++;
                return;
            }
        }

        Unexplained++;
        Example(FormattableString.Invariant($"{tile} {grid}[{row}, {column}]: client {client}, extractor rules {extractor}, server {server}"));
    }

    private void CompareLiquid(TileKey tile, AdtFile adt, ServerMapTile server)
    {
        ExtractorEmulation.LiquidModel model = ExtractorEmulation.Liquid(adt);
        LiquidTiles += model.HasLiquid ? 1 : 0;
        FlatLiquidTiles += model.HasLiquid && model.NoHeight ? 1 : 0;
        ReadButReset += model.ReadButReset;
        WorldPoint origin = WorldGrid.Origin(tile);
        double chunkSize = WorldGrid.TileSize / 16;
        for (int i = 0; i < 16; i++)
        {
            for (int j = 0; j < 16; j++)
            {
                AdtChunk chunk = adt.Chunk(i, j);
                int count = chunk.LiquidLayers.Count;
                LiquidChunks += count > 0 ? 1 : 0;
                LiquidLayers += count;
                StackedChunks += count > 1 ? 1 : 0;
                CountLayerSquares(chunk, i, j, model);
                byte serverFlags = (byte)server.Tile.GetTerrainType((float)(origin.X - ((i + 0.5) * chunkSize)), (float)(origin.Y - ((j + 0.5) * chunkSize)));
                if (serverFlags != model.Flags[(i * 16) + j])
                {
                    LiquidFlagMismatches++;
                    Example($"{tile} MCNK ({i}, {j}): server liquid flags 0x{serverFlags:X2}, extractor rules 0x{model.Flags[(i * 16) + j]:X2} from {count} layer(s)");
                }
            }
        }

        for (int row = 0; row < Squares; row++)
        {
            for (int column = 0; column < Squares; column++)
            {
                (double x, double y) = SquareCentre(tile, row, column);
                float level = server.Tile.GetLiquidLevel((float)x, (float)y);
                float expected = ExpectedLevel(model, row, column);
                if (Math.Abs(level - expected) > Tolerance)
                {
                    LiquidLevelMismatches++;
                    Example(FormattableString.Invariant($"{tile} liquid vertex ({row}, {column}): server {level}, extractor rules {expected}"));
                }
            }
        }
    }

    // The server answers a square with the stored vertex at its north-west corner (TerrainTile.GetLiquidLevel).
    // ref: src/ArcaneCore.Game/Maps/Terrain/TerrainTile.cs:261-279 (no map: the one level; outside the stored rectangle: invalid)
    private static float ExpectedLevel(ExtractorEmulation.LiquidModel model, int row, int column)
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
        return r >= 0 && r < model.Rows && c >= 0 && c < model.Columns ? model.Grid[(row * (Squares + 1)) + column] : TerrainTile.InvalidHeightValue;
    }

    private void CountLayerSquares(AdtChunk chunk, int i, int j, ExtractorEmulation.LiquidModel model)
    {
        var shown = new int[64];
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

                    LayerSquares++;
                    shown[(y * 8) + x]++;
                    for (int vy = y; vy <= y + 1; vy++)
                    {
                        for (int vx = x; vx <= x + 1; vx++)
                        {
                            LayerVertices++;
                            float merged = model.Grid[((((i * 8) + vy) * (Squares + 1)) + (j * 8)) + vx];
                            float own = layer.Height(vy, vx);
                            if (Math.Abs(own - merged) <= Tolerance)
                            {
                                continue;
                            }

                            LayerVerticesNotKept++;
                            float best = chunkMax[(vy * 9) + vx];
                            if (merged == ExtractorEmulation.MinHeight)
                            {
                                NotKeptReset++;
                            }
                            else if (Math.Abs(best - merged) <= Tolerance && own < best)
                            {
                                NotKeptMaxOfLayers++;
                            }
                            else if (vy is 0 or 8 || vx is 0 or 8)
                            {
                                NotKeptEdgeRewrite++;
                            }
                            else
                            {
                                NotKeptOther++;
                            }
                        }
                    }
                }
            }
        }

        OverlappingSquares += shown.Count(n => n > 1);
    }

    private void Example(string text)
    {
        if (_examples.Count < MaxExamples)
        {
            _examples.Add(text);
        }
    }
}
