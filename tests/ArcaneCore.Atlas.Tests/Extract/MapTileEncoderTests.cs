using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Extract;
using ArcaneCore.Atlas.Reference.Client.Terrain;
using ArcaneCore.Atlas.Reference.Dbc;
using ArcaneCore.Atlas.Tests.Reference.Client.Terrain;
using ArcaneCore.Atlas.Tests.Reference.Mpq;
using ArcaneCore.Game.Maps.Terrain;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Extract;

/// <summary>Synthetic inputs shared by the extractor tests.</summary>
internal static class ExtractFixtures
{
    /// <summary>An AreaTable.dbc in the build 5875 layout holding only ids and explore flags.</summary>
    public static byte[] AreaTable(params (uint Id, uint ExploreFlag)[] rows)
    {
        var table = new WdbcTestBuilder(ClientMapDirectory.AreaFieldCount);
        foreach ((uint id, uint flag) in rows)
        {
            table.Row((0, id), (3, flag));
        }

        return table.Build();
    }

    /// <summary>The explore flags of <see cref="AreaTable"/>.</summary>
    public static AreaExploreFlags Areas(params (uint Id, uint ExploreFlag)[] rows) => AreaExploreFlags.FromTable(WdbcFile.Parse(AreaTable(rows)));

    /// <summary>A parsed synthetic ADT.</summary>
    public static AdtFile Adt(AdtSpec spec) => AdtFile.Parse(SyntheticTerrain.Adt(spec).Bytes);
}

/// <summary>
/// <see cref="MapTileEncoder"/> on synthetic ADTs: the exact bytes of small files, and the server's own reader
/// (<see cref="TerrainTile"/>) reading back what was written.
/// </summary>
public sealed class MapTileEncoderTests
{
    private const int FileHeader = 40;
    private const int AreaHeader = 8;
    private const int HeightHeader = 16;
    private const int LiquidHeader = 16;
    private const int HolesBytes = 512;
    private const int FloatHeights = ((129 * 129) + (128 * 128)) * 4;

    private static readonly AreaExploreFlags NoAreas = ExtractFixtures.Areas((1, 0));

    private static uint U32(byte[] file, int at) => BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at));

    private static ushort U16(byte[] file, int at) => BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at));

    private static float F32(byte[] file, int at) => BinaryPrimitives.ReadSingleLittleEndian(file.AsSpan(at));

    private static string Tag(byte[] file, int at) => Encoding.ASCII.GetString(file, at, 4);

    [Fact]
    public void Encode_FlatTileWithoutAreasOrLiquid_IsTheHeadersAndTheHoles_ByteForByte()
    {
        byte[] file = MapTileEncoder.Encode(ExtractFixtures.Adt(new AdtSpec()), NoAreas);

        using var expected = new MemoryStream();
        using (var w = new BinaryWriter(expected, Encoding.ASCII, leaveOpen: true))
        {
            w.Write("MAPS"u8);
            w.Write("z1.4"u8);
            w.Write(40u);  // area offset
            w.Write(8u);   // area size: the header alone
            w.Write(48u);  // height offset
            w.Write(16u);  // height size: the header alone
            w.Write(0u);   // no liquid
            w.Write(0u);
            w.Write(64u);  // holes offset
            w.Write(512u); // holes size
            w.Write("AREA"u8);
            w.Write((ushort)1);      // no per-chunk areas
            w.Write((ushort)0xFFFF); // area id 0 has no explore flag
            w.Write("MHGT"u8);
            w.Write(1u);             // no height samples
            w.Write(0f);
            w.Write(0f);
            w.Write(new byte[512]);
        }

        Assert.Equal(expected.ToArray(), file);
        TerrainTile tile = TerrainTile.Parse(file);
        Assert.True(tile.HasData);
        Assert.Equal(TerrainTile.HeightStorage.Flat, tile.Storage);
        Assert.Equal(0f, tile.GridHeight);
    }

    [Fact]
    public void Encode_SlopedTile_StoresEverySampleAsTheServerReadsIt()
    {
        AdtFile adt = ExtractFixtures.Adt(SyntheticTerrain.Plane(new TileKey(0, 48, 32), 120f, 0.02f, -0.01f));

        byte[] file = MapTileEncoder.Encode(adt, NoAreas);

        Assert.Equal(FileHeader + AreaHeader + HeightHeader + FloatHeights + HolesBytes, file.Length);
        Assert.Equal((uint)(HeightHeader + FloatHeights), U32(file, 20));
        Assert.Equal(0u, U32(file, 48 + 4));
        TerrainTile tile = TerrainTile.Parse(file);
        Assert.Equal(TerrainTile.HeightStorage.Float, tile.Storage);
        float min = float.MaxValue;
        float max = float.MinValue;
        for (int row = 0; row <= 128; row++)
        {
            for (int col = 0; col <= 128; col++)
            {
                // A plane gives a shared corner the same value from either chunk, so any owner will do.
                AdtChunk chunk = adt.Chunk(Math.Min(row / 8, 15), Math.Min(col / 8, 15));
                float expected = chunk.OuterHeight(row - (chunk.Row * 8), col - (chunk.Column * 8));
                Assert.Equal(expected, tile.CornerHeight(row, col));
                min = Math.Min(min, expected);
                max = Math.Max(max, expected);
            }
        }

        for (int row = 0; row < 128; row++)
        {
            for (int col = 0; col < 128; col++)
            {
                float expected = adt.Chunk(row / 8, col / 8).InnerHeight(row % 8, col % 8);
                Assert.Equal(expected, tile.CentreHeight(row, col));
                min = Math.Min(min, expected);
                max = Math.Max(max, expected);
            }
        }

        Assert.Equal(min, F32(file, 48 + 8));
        Assert.Equal(max, F32(file, 48 + 12));
    }

    [Fact]
    public void Encode_CornerSharedByChunks_KeepsTheChunkVisitedLast()
    {
        // Every corner sample of chunk (row, column) is row * 100 + column, so a stored corner names its writer.
        AdtFile adt = ExtractFixtures.Adt(new AdtSpec { Outer = (row, col, _, _) => (row * 100) + col });

        TerrainTile tile = TerrainTile.Parse(MapTileEncoder.Encode(adt, NoAreas));

        Assert.Equal(0f, tile.CornerHeight(0, 0));
        Assert.Equal(1f, tile.CornerHeight(3, 8));      // chunks (0,0) and (0,1): the later column wins
        Assert.Equal(100f, tile.CornerHeight(8, 3));    // chunks (0,0) and (1,0): the later row wins
        Assert.Equal(101f, tile.CornerHeight(8, 8));    // four chunks meet: (1,1) is visited last
        Assert.Equal(1515f, tile.CornerHeight(128, 128));
        Assert.Equal(1500f, tile.CornerHeight(128, 0));
        Assert.Equal(15f, tile.CornerHeight(0, 128));
    }

    [Fact]
    public void Encode_ChunkWithoutHeightSamples_UsesItsBaseHeight()
    {
        var spec = new AdtSpec { BaseHeight = (row, col) => row == 2 && col == 5 ? 7.5f : 1f, Outer = (_, _, _, _) => 3f, Inner = (_, _, _, _) => 4f };
        spec.WithoutHeights.Add((2 * 16) + 5);

        TerrainTile tile = TerrainTile.Parse(MapTileEncoder.Encode(ExtractFixtures.Adt(spec), NoAreas));

        Assert.Equal(7.5f, tile.CentreHeight((2 * 8) + 3, (5 * 8) + 3));
        Assert.Equal(7.5f, tile.CornerHeight((2 * 8) + 3, (5 * 8) + 3));
        Assert.Equal(5f, tile.CentreHeight(0, 0));
        Assert.Equal(4f, tile.CornerHeight(0, 0));
    }

    [Fact]
    public void Encode_HeightsBelowTheLimit_AreRaisedToMinusFiveHundred()
    {
        AdtFile adt = ExtractFixtures.Adt(new AdtSpec { BaseHeight = (row, col) => row == 4 && col == 4 ? -700f : 10f });

        byte[] file = MapTileEncoder.Encode(adt, NoAreas);

        TerrainTile tile = TerrainTile.Parse(file);
        Assert.Equal(-500f, tile.CentreHeight((4 * 8) + 1, (4 * 8) + 1));
        Assert.Equal(-500f, tile.CornerHeight((4 * 8) + 1, (4 * 8) + 1));
        Assert.Equal(10f, tile.CentreHeight(0, 0));
        Assert.Equal(-500f, F32(file, 48 + 8));
        Assert.Equal(10f, F32(file, 48 + 12));
    }

    [Fact]
    public void Encode_TileEntirelyBelowTheLimit_IsFlatAtTheLimit()
    {
        byte[] file = MapTileEncoder.Encode(ExtractFixtures.Adt(new AdtSpec { BaseHeight = (row, col) => -600f - row - col }), NoAreas);

        Assert.Equal(FileHeader + AreaHeader + HeightHeader + HolesBytes, file.Length);
        Assert.Equal(1u, U32(file, 48 + 4));
        Assert.Equal(-500f, F32(file, 48 + 8));
        Assert.Equal(-500f, F32(file, 48 + 12));
    }

    [Fact]
    public void Encode_OneAreaEverywhere_StoresOnlyItsExploreFlag()
    {
        var spec = new AdtSpec();
        for (int i = 0; i < 256; i++)
        {
            spec.Areas[i] = 12;
        }

        byte[] file = MapTileEncoder.Encode(ExtractFixtures.Adt(spec), ExtractFixtures.Areas((12, 0x0123), (40, 0x0456)));

        Assert.Equal("AREA", Tag(file, 40));
        Assert.Equal(8u, U32(file, 12));
        Assert.Equal(1, U16(file, 44));
        Assert.Equal(0x0123, U16(file, 46));
        Assert.Equal(0x0123, TerrainTile.Parse(file).GetAreaFlag(0f, 0f));
    }

    [Fact]
    public void Encode_MixedAreas_StoresAFlagPerChunk_AndNoFlagForUnknownIds()
    {
        var spec = new AdtSpec();
        for (int i = 0; i < 256; i++)
        {
            spec.Areas[i] = 12;
        }

        spec.Areas[1] = 40;       // has a row
        spec.Areas[2] = 30;       // inside the table's id range, but no row
        spec.Areas[3] = 5000;     // past the table
        spec.Areas[4] = 0;        // no area

        byte[] file = MapTileEncoder.Encode(ExtractFixtures.Adt(spec), ExtractFixtures.Areas((12, 0x0123), (40, 0x0456)));

        Assert.Equal(8u + 512u, U32(file, 12));
        Assert.Equal(0, U16(file, 44));
        Assert.Equal(0, U16(file, 46));
        Assert.Equal(0x0123, U16(file, 48));
        Assert.Equal(0x0456, U16(file, 48 + 2));
        Assert.Equal(0xFFFF, U16(file, 48 + 4));
        Assert.Equal(0xFFFF, U16(file, 48 + 6));
        Assert.Equal(0xFFFF, U16(file, 48 + 8));
        Assert.Equal(0x0123, U16(file, 48 + 10));
        Assert.Equal(48u + 512u, U32(file, 16));
    }

    [Fact]
    public void Encode_Holes_AreStoredPerChunk()
    {
        var spec = new AdtSpec();
        spec.Holes[(3 * 16) + 4] = 0x8001;

        byte[] file = MapTileEncoder.Encode(ExtractFixtures.Adt(SlopeOf(spec)), NoAreas);

        int holes = (int)U32(file, 32);
        Assert.Equal(512u, U32(file, 36));
        Assert.Equal(file.Length, holes + 512);
        Assert.Equal(0x8001, U16(file, holes + (((3 * 16) + 4) * 2)));
        Assert.Equal(0, U16(file, holes));
        TerrainTile tile = TerrainTile.Parse(file);
        Assert.True(tile.HoleMaskCovers((3 * 8) + 0, (4 * 8) + 0));  // bit 0: squares 0..1 x 0..1
        Assert.True(tile.HoleMaskCovers((3 * 8) + 7, (4 * 8) + 7));  // bit 15: squares 6..7 x 6..7
        Assert.False(tile.HoleMaskCovers((3 * 8) + 3, (4 * 8) + 3));
    }

    [Fact]
    public void Encode_StackedLiquidLayers_MergeIntoOneSurface_AndStoreTheShownRectangle()
    {
        // The fixture's chunk (6, 7): a river shown on squares y 0..3 (dark at (2, 2)) with surface 150 + y, and an ocean
        // layer at 160 shown on square (2, 2) alone.
        AdtFile adt = AdtFile.Parse(TerrainFixture.GoldshireAdt().Bytes);

        byte[] file = MapTileEncoder.Encode(adt, NoAreas);

        int liquid = (int)U32(file, 24);
        Assert.Equal(48 + HeightHeader + FloatHeights, liquid);
        Assert.Equal((uint)(LiquidHeader + (256 * 2) + 256 + (4 * 9 * 5)), U32(file, 28));
        Assert.Equal((uint)(liquid + U32(file, 28)), U32(file, 32));
        Assert.Equal("MLIQ", Tag(file, liquid));
        Assert.Equal(0, file[liquid + 4]);              // per-chunk types and per-vertex levels are stored
        Assert.Equal(0, file[liquid + 5]);
        Assert.Equal(0, U16(file, liquid + 6));
        Assert.Equal(7 * 8, file[liquid + 8]);          // first shown column
        Assert.Equal(6 * 8, file[liquid + 9]);          // first shown row
        Assert.Equal(9, file[liquid + 10]);             // eight shown columns and the closing vertex
        Assert.Equal(5, file[liquid + 11]);             // four shown rows and the closing vertex
        Assert.Equal(-500f, F32(file, liquid + 12));    // the tile also holds squares without liquid

        int chunk = (6 * 16) + 7;
        Assert.Equal(1, U16(file, liquid + LiquidHeader + (chunk * 2)));      // the river shows more squares than the ocean
        Assert.Equal(0x18, file[liquid + LiquidHeader + 512 + chunk]);        // water, deep
        Assert.Equal(0, U16(file, liquid + LiquidHeader));
        Assert.Equal(0, file[liquid + LiquidHeader + 512]);

        int levels = liquid + LiquidHeader + 512 + 256;
        float Level(int row, int column) => F32(file, levels + (((row * 9) + column) * 4));
        Assert.Equal(150f, Level(0, 0));
        Assert.Equal(151f, Level(1, 7));
        Assert.Equal(160f, Level(2, 2));    // the ocean is the higher surface on the square both layers show
        Assert.Equal(160f, Level(3, 3));
        Assert.Equal(152f, Level(2, 4));
        Assert.Equal(-500f, Level(0, 8));   // the closing column's vertices belong to squares that show nothing
        Assert.Equal(-500f, Level(4, 0));   // and so do the closing row's

        TerrainTile tile = TerrainTile.Parse(file);
        Assert.True(tile.TryGetLiquidSquare((6 * 8) + 2, (7 * 8) + 2, out byte type, out ushort entry, out float level));
        Assert.Equal(0x18, type);
        Assert.Equal(1, entry);
        Assert.Equal(160f, level);
        Assert.False(tile.TryGetLiquidSquare(0, 0, out _, out _, out _));
    }

    [Fact]
    public void Encode_Liquid_MatchesTheExtractorModelProvenOnRealTiles()
    {
        AdtFile adt = AdtFile.Parse(TerrainFixture.GoldshireAdt().Bytes);
        ExtractorEmulation.LiquidModel model = ExtractorEmulation.Liquid(adt);

        byte[] file = MapTileEncoder.Encode(adt, NoAreas);

        int liquid = (int)U32(file, 24);
        Assert.True(model.HasLiquid);
        Assert.Equal(model.NoType, (file[liquid + 4] & 1) != 0);
        Assert.Equal(model.NoHeight, (file[liquid + 4] & 2) != 0);
        Assert.Equal(model.OffsetColumn, file[liquid + 8]);
        Assert.Equal(model.OffsetRow, file[liquid + 9]);
        Assert.Equal(model.Columns, file[liquid + 10]);
        Assert.Equal(model.Rows, file[liquid + 11]);
        Assert.Equal(model.Level, F32(file, liquid + 12));
        int at = liquid + LiquidHeader;
        for (int c = 0; c < 256; c++)
        {
            Assert.Equal(model.Entry[c], U16(file, at + (c * 2)));
            Assert.Equal(model.Flags[c], file[at + 512 + c]);
        }

        at += 512 + 256;
        for (int row = 0; row < model.Rows; row++)
        {
            for (int column = 0; column < model.Columns; column++)
            {
                Assert.Equal(model.Grid[((row + model.OffsetRow) * 129) + column + model.OffsetColumn], F32(file, at));
                at += 4;
            }
        }
    }

    [Fact]
    public void Encode_OneLiquidAtOneLevelOverTheWholeTile_StoresTheHeaderAlone()
    {
        var spec = new AdtSpec();
        for (int i = 0; i < 256; i++)
        {
            spec.Liquid[i] = [new LiquidLayerSpec(AdtLiquidKind.Ocean, 0, 0, (_, _) => 0f, (_, _) => 0)];
        }

        byte[] file = MapTileEncoder.Encode(ExtractFixtures.Adt(spec), NoAreas);

        Assert.Equal(FileHeader + AreaHeader + HeightHeader + LiquidHeader + HolesBytes, file.Length);
        Assert.Equal(64u, U32(file, 24));
        Assert.Equal(16u, U32(file, 28));
        Assert.Equal(80u, U32(file, 32));
        Assert.Equal(3, file[64 + 4]);       // no per-chunk types, no per-vertex levels
        Assert.Equal(0x02, file[64 + 5]);    // ocean
        Assert.Equal(2, U16(file, 64 + 6));  // LiquidType.dbc entry
        Assert.Equal(0, file[64 + 8]);
        Assert.Equal(0, file[64 + 9]);
        Assert.Equal(129, file[64 + 10]);
        Assert.Equal(129, file[64 + 11]);
        Assert.Equal(0f, F32(file, 64 + 12));
        TerrainTile tile = TerrainTile.Parse(file);
        Assert.True(tile.TryGetLiquidSquare(127, 127, out byte type, out ushort entry, out float level));
        Assert.Equal(0x02, type);
        Assert.Equal(2, entry);
        Assert.Equal(0f, level);
    }

    [Theory]
    [InlineData(10f, 20f, 3, 0x01)] // equal squares: the higher layer names the chunk
    [InlineData(20f, 10f, 1, 0x08)] // equal squares, the first is higher: it stays
    public void Encode_LayersShowingEquallyManySquares_TakeTheHigherLayersType(float river, float magma, ushort expectedEntry, byte expectedFlags)
    {
        var spec = new AdtSpec();
        spec.Liquid[0] =
        [
            new LiquidLayerSpec(AdtLiquidKind.River, river, river, (_, _) => river, (y, x) => y == 0 && x < 4 ? (byte)0 : AdtLiquidLayer.HiddenSquare),
            new LiquidLayerSpec(AdtLiquidKind.Magma, magma, magma, (_, _) => magma, (y, x) => y == 1 && x < 4 ? (byte)0 : AdtLiquidLayer.HiddenSquare),
        ];

        byte[] file = MapTileEncoder.Encode(ExtractFixtures.Adt(spec), NoAreas);

        int liquid = (int)U32(file, 24);
        Assert.Equal(expectedEntry, U16(file, liquid + LiquidHeader));
        Assert.Equal(expectedFlags, file[liquid + LiquidHeader + 512]);
    }

    [Fact]
    public void Encode_LiquidFlagWithEverySquareHidden_StoresNoLiquid()
    {
        var spec = new AdtSpec();
        spec.Liquid[9] = [new LiquidLayerSpec(AdtLiquidKind.River, 5, 5, (_, _) => 5f, (_, _) => AdtLiquidLayer.HiddenSquare)];

        byte[] file = MapTileEncoder.Encode(ExtractFixtures.Adt(spec), NoAreas);

        Assert.Equal(0u, U32(file, 24));
        Assert.Equal(0u, U32(file, 28));
        Assert.Equal(FileHeader + AreaHeader + HeightHeader + HolesBytes, file.Length);
    }

    private static AdtSpec SlopeOf(AdtSpec spec)
    {
        // Hole lookups are only made on tiles that store float heights, so give the tile a slope.
        var sloped = new AdtSpec { Tile = spec.Tile, BaseHeight = (row, _) => row };
        foreach ((int chunk, ushort holes) in spec.Holes)
        {
            sloped.Holes[chunk] = holes;
        }

        return sloped;
    }
}

/// <summary><see cref="AreaExploreFlags"/> on synthetic AreaTable.dbc files.</summary>
public sealed class AreaExploreFlagsTests
{
    [Fact]
    public void FlagOf_AnswersTheRowsFlag_AndNoneOutsideTheTable()
    {
        AreaExploreFlags areas = ExtractFixtures.Areas((40, 0x0456), (12, 0x0123), (3, 0x1FFFF));

        Assert.Equal(40u, areas.MaxId);
        Assert.Equal(0x0123, areas.FlagOf(12));
        Assert.Equal(0x0456, areas.FlagOf(40));
        Assert.Equal(0xFFFF, areas.FlagOf(3));   // cut to 16 bits, as the reference stores it
        Assert.Equal(AreaExploreFlags.None, areas.FlagOf(0));
        Assert.Equal(AreaExploreFlags.None, areas.FlagOf(13));
        Assert.Equal(AreaExploreFlags.None, areas.FlagOf(41));
        Assert.Equal(AreaExploreFlags.None, areas.FlagOf(uint.MaxValue));
    }

    [Fact]
    public void Load_ReadsTheClientsTable_AndRefusesAMissingOrForeignOne()
    {
        var files = new MemoryClientFiles();
        Assert.Throws<FileNotFoundException>(() => AreaExploreFlags.Load(files));

        files.Add(ClientMapDirectory.AreaTablePath, ExtractFixtures.AreaTable((12, 7)));
        Assert.Equal(7, AreaExploreFlags.Load(files).FlagOf(12));

        var foreign = new WdbcTestBuilder(ClientMapDirectory.AreaFieldCount + 1);
        foreign.Row((0, 12), (3, 7));
        files.Add(ClientMapDirectory.AreaTablePath, foreign.Build());
        InvalidDataException refused = Assert.Throws<InvalidDataException>(() => AreaExploreFlags.Load(files));
        Assert.Contains("5875", refused.Message, StringComparison.Ordinal);

        files.Add(ClientMapDirectory.AreaTablePath, [1, 2, 3]);
        Assert.Throws<InvalidDataException>(() => AreaExploreFlags.Load(files));
    }
}
