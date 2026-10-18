using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Client.Terrain;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Client.Terrain;

public sealed class AdtFileTests
{
    private static AdtSpec RichSpec()
    {
        var spec = new AdtSpec
        {
            Tile = new TileKey(0, 33, 31),
            BaseHeight = (row, col) => 100 + row + (col / 16f),
            Outer = (row, col, y, x) => (y * 9) + x + (row * 0.25f),
            Inner = (row, col, y, x) => 1000 + (y * 8) + x + (col * 0.5f),
        };
        spec.Holes[5] = 0x8001;
        spec.Areas[5] = 12;
        spec.Areas[255] = 44;
        spec.WithoutHeights.Add(17);
        spec.Textures.Add("Tileset\\Elwynn\\ElwynnGrassBase.blp");
        spec.Textures.Add("Tileset\\Elwynn\\ElwynnDirt.blp");
        spec.Liquid[40] =
        [
            new LiquidLayerSpec(AdtLiquidKind.River, 10, 12, (y, x) => 10 + (y * 0.25f), (y, x) => x == 3 ? AdtLiquidLayer.HiddenSquare : (byte)0),
            new LiquidLayerSpec(AdtLiquidKind.Magma, -5, -3, (y, x) => -5f, (y, x) => (byte)(y == 0 ? 0x80 : 0)),
        ];
        spec.Doodads.Add(new DoodadSpec("World\\Azeroth\\Tree01.mdx", 7001, new WorldPoint(1, 2, 3), new WorldPoint(0, 90, 0), 2048, 0x1));
        spec.Doodads.Add(new DoodadSpec("World\\Azeroth\\Rock02.mdx", 7002, new WorldPoint(4, 5, 6), new WorldPoint(10, 20, 30), 1024, 0));
        spec.Doodads.Add(new DoodadSpec("World\\Azeroth\\Tree01.mdx", 7003, new WorldPoint(7, 8, 9), new WorldPoint(0, 0, 0), 512, 0));
        spec.Wmos.Add(new WmoSpec("World\\wmo\\Inn.wmo", 9001, new WorldPoint(100, 200, 300), new WorldPoint(0, 180, 0), new WorldPoint(90, 190, 290), new WorldPoint(110, 210, 310), 0x1, 2, 3));
        return spec;
    }

    [Fact]
    public void Parse_ReadsEveryChunkSampleAndTable()
    {
        AdtSpec spec = RichSpec();
        BuiltAdt built = SyntheticTerrain.Adt(spec);

        var adt = AdtFile.Parse(built.Bytes);

        Assert.Equal(built.Bytes.Length, adt.ByteLength);
        Assert.Equal(256, adt.Chunks.Count);
        foreach (AdtChunk chunk in adt.Chunks)
        {
            Assert.Equal((chunk.Row * 16) + chunk.Column, chunk.Index);
            Assert.Same(chunk, adt.Chunk(chunk.Row, chunk.Column));
            Assert.Equal(spec.BaseHeight(chunk.Row, chunk.Column), chunk.BaseHeight);
            Assert.Equal(spec.Holes.GetValueOrDefault(chunk.Index), chunk.Holes);
            Assert.Equal(spec.Areas.GetValueOrDefault(chunk.Index), chunk.AreaId);
            Assert.Equal(!spec.WithoutHeights.Contains(chunk.Index), chunk.HasHeights);
            if (!chunk.HasHeights)
            {
                continue;
            }

            for (int y = 0; y < 9; y++)
            {
                for (int x = 0; x < 9; x++)
                {
                    Assert.Equal(chunk.BaseHeight + spec.Outer(chunk.Row, chunk.Column, y, x), chunk.OuterHeight(y, x));
                }
            }

            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    Assert.Equal(chunk.BaseHeight + spec.Inner(chunk.Row, chunk.Column, y, x), chunk.InnerHeight(y, x));
                }
            }
        }

        Assert.Equal(new[] { "Tileset\\Elwynn\\ElwynnGrassBase.blp", "Tileset\\Elwynn\\ElwynnDirt.blp" }, adt.Textures);
        Assert.Equal(0, AdtGeometry.CountPositionMismatches(adt, spec.Tile));

        // The chunk positions encode the tile: against the neighbouring tile every chunk is off by a whole tile.
        Assert.Equal(256, AdtGeometry.CountPositionMismatches(adt, new TileKey(0, 32, 31)));
        Assert.Equal(256, AdtGeometry.CountPositionMismatches(adt, new TileKey(0, 33, 32)));
    }

    [Fact]
    public void FlatChunk_HasNoHeightsAndEveryMcvtSampleEqualsTheBase()
    {
        BuiltAdt built = SyntheticTerrain.Adt(RichSpec());
        var adt = AdtFile.Parse(built.Bytes);

        AdtChunk flat = adt.Chunks[17];

        Assert.False(flat.HasHeights);
        Assert.Equal(flat.BaseHeight, flat.OuterHeight(4, 4));
        Assert.Equal(flat.BaseHeight, flat.InnerHeight(7, 7));
    }

    [Fact]
    public void Holes_FollowTheBitmapFormulaForEverySquare()
    {
        BuiltAdt built = SyntheticTerrain.Adt(RichSpec());
        var adt = AdtFile.Parse(built.Bytes);
        AdtChunk chunk = adt.Chunks[5];

        Assert.Equal(0x8001, chunk.Holes);
        int holes = 0;
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                bool expected = ((chunk.Holes >> (((y / 2) * 4) + (x / 2))) & 1) != 0;
                Assert.Equal(expected, chunk.IsHoleSquare(y, x));
                holes += expected ? 1 : 0;
            }
        }

        // Bit 0 covers squares (0..1, 0..1) and bit 15 squares (6..7, 6..7): four squares each.
        Assert.Equal(8, holes);
        Assert.Equal(8, chunk.HoleSquares);
        Assert.True(chunk.IsHoleSquare(1, 1));
        Assert.False(chunk.IsHoleSquare(1, 2));
        Assert.True(chunk.IsHoleSquare(7, 6));
    }

    [Fact]
    public void LiquidLayers_AreKeptApartInFlagOrderWithTheirSquareFlags()
    {
        BuiltAdt built = SyntheticTerrain.Adt(RichSpec());
        var adt = AdtFile.Parse(built.Bytes);

        IReadOnlyList<AdtLiquidLayer> layers = adt.Chunks[40].LiquidLayers;

        Assert.Equal(2, layers.Count);
        Assert.Equal(AdtLiquidKind.River, layers[0].Kind);
        Assert.Equal(AdtLiquidKind.Magma, layers[1].Kind);
        Assert.Equal(10f, layers[0].MinHeight);
        Assert.Equal(12f, layers[0].MaxHeight);
        Assert.Equal(10.5f, layers[0].Height(2, 7));
        Assert.Equal(-5f, layers[1].Height(8, 8));
        Assert.False(layers[0].IsVisible(4, 3));
        Assert.True(layers[0].IsVisible(4, 2));
        Assert.Equal(56, layers[0].VisibleSquares);
        Assert.True(layers[1].IsDarkWater(0, 5));
        Assert.False(layers[1].IsDarkWater(1, 5));
        Assert.False(layers[0].IsDarkWater(4, 3));
        Assert.Empty(adt.Chunks[0].LiquidLayers);
    }

    [Fact]
    public void LiquidKind_MapsToFlagAndSharedColourKey()
    {
        Assert.Equal(0x04u, AdtLiquidLayer.FlagOf(AdtLiquidKind.River));
        Assert.Equal(0x08u, AdtLiquidLayer.FlagOf(AdtLiquidKind.Ocean));
        Assert.Equal(0x10u, AdtLiquidLayer.FlagOf(AdtLiquidKind.Magma));
        Assert.Equal(0x20u, AdtLiquidLayer.FlagOf(AdtLiquidKind.Slime));
        Assert.Equal(ColorKeys.Water, AdtLiquidLayer.ColorKeyOf(AdtLiquidKind.River));
        Assert.Equal(ColorKeys.Ocean, AdtLiquidLayer.ColorKeyOf(AdtLiquidKind.Ocean));
        Assert.Equal(ColorKeys.Magma, AdtLiquidLayer.ColorKeyOf(AdtLiquidKind.Magma));
        Assert.Equal(ColorKeys.Slime, AdtLiquidLayer.ColorKeyOf(AdtLiquidKind.Slime));
    }

    [Fact]
    public void Placements_ResolveNamesAndKeepRawFields()
    {
        BuiltAdt built = SyntheticTerrain.Adt(RichSpec());
        var adt = AdtFile.Parse(built.Bytes);

        const string first = "World\\Azeroth\\Tree01.mdx";
        Assert.Equal(new[] { first, "World\\Azeroth\\Rock02.mdx" }, adt.DoodadNames);
        Assert.Equal(new[] { 0u, (uint)first.Length + 1 }, adt.DoodadNameOffsets);
        Assert.Equal(new[] { "World\\wmo\\Inn.wmo" }, adt.WmoNames);
        Assert.Equal(3, adt.Doodads.Count);
        AdtDoodad second = adt.Doodads[1];
        Assert.Equal("World\\Azeroth\\Rock02.mdx", second.ModelPath);
        Assert.Equal(1u, second.NameIndex);
        Assert.Equal(7002u, second.UniqueId);
        Assert.Equal(new WorldPoint(4, 5, 6), second.Position);
        Assert.Equal(new WorldPoint(10, 20, 30), second.Rotation);
        Assert.Equal(1.0, second.Scale);
        Assert.Equal(2.0, adt.Doodads[0].Scale);
        Assert.Equal(0.5, adt.Doodads[2].Scale);
        Assert.Equal("World\\Azeroth\\Tree01.mdx", adt.Doodads[2].ModelPath);
        AdtWmoObject wmo = Assert.Single(adt.WmoObjects);
        Assert.Equal("World\\wmo\\Inn.wmo", wmo.ModelPath);
        Assert.Equal(9001u, wmo.UniqueId);
        Assert.Equal(new WorldPoint(100, 200, 300), wmo.Position);
        Assert.Equal(new WorldPoint(90, 190, 290), wmo.BoundsMin);
        Assert.Equal(new WorldPoint(110, 210, 310), wmo.BoundsMax);
        Assert.Equal((ushort)2, wmo.DoodadSet);
        Assert.Equal((ushort)3, wmo.NameSet);
        Assert.Equal((ushort)1, wmo.Flags);
    }

    [Fact]
    public void Parse_AcceptsAnAdtWithoutOptionalChunks()
    {
        BuiltAdt built = SyntheticTerrain.Adt(new AdtSpec());

        var adt = AdtFile.Parse(built.Bytes);

        Assert.Empty(adt.Textures);
        Assert.Empty(adt.Doodads);
        Assert.Empty(adt.WmoObjects);
        Assert.Empty(adt.DoodadNames);
        Assert.Equal(256, adt.Chunks.Count);
    }

    [Fact]
    public void Parse_RefusesAnEmptyFile()
    {
        InvalidDataException e = Assert.Throws<InvalidDataException>(() => AdtFile.Parse([]));

        Assert.Contains("empty", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesAWrongVersion()
    {
        BuiltAdt built = SyntheticTerrain.Adt(new AdtSpec { Version = 20 });

        InvalidDataException e = Assert.Throws<InvalidDataException>(() => AdtFile.Parse(built.Bytes));

        Assert.Contains("20", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesAFileThatDoesNotStartWithMver()
    {
        BuiltAdt built = SyntheticTerrain.Adt(new AdtSpec());

        InvalidDataException e = Assert.Throws<InvalidDataException>(() => AdtFile.Parse(built.WithTag(0, "MVRE")));

        Assert.Contains("MVER", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesAMissingMhdr()
    {
        BuiltAdt built = SyntheticTerrain.Adt(new AdtSpec());

        InvalidDataException e = Assert.Throws<InvalidDataException>(() => AdtFile.Parse(built.WithTag(built.MhdrStart, "MHDX")));

        Assert.Contains("MHDR", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesMh2oLiquidThat1121NeverWrote()
    {
        BuiltAdt built = SyntheticTerrain.Adt(new AdtSpec());

        InvalidDataException e = Assert.Throws<InvalidDataException>(() => AdtFile.Parse(built.WithU32(built.MhdrData + (10 * 4), 1234)));

        Assert.Contains("MH2O", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesAnMhdrOffsetThatMissesItsChunk()
    {
        BuiltAdt built = SyntheticTerrain.Adt(new AdtSpec());

        InvalidDataException wrongChunk = Assert.Throws<InvalidDataException>(() => AdtFile.Parse(built.WithU32(built.MhdrData + (1 * 4), 4)));
        InvalidDataException pastEnd = Assert.Throws<InvalidDataException>(() => AdtFile.Parse(built.WithU32(built.MhdrData + (1 * 4), 0x7FFFFFF0)));

        Assert.Contains("MCIN", wrongChunk.Message, StringComparison.Ordinal);
        Assert.Contains("MCIN", pastEnd.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesAnMcinEntryThatMissesItsMcnk()
    {
        BuiltAdt built = SyntheticTerrain.Adt(new AdtSpec());
        int entry = built.McinData + (9 * 16);

        InvalidDataException offset = Assert.Throws<InvalidDataException>(() => AdtFile.Parse(built.WithU32(entry, (uint)built.McinStart)));
        InvalidDataException size = Assert.Throws<InvalidDataException>(() => AdtFile.Parse(built.WithU32(entry + 4, 99)));
        InvalidDataException pastEnd = Assert.Throws<InvalidDataException>(() => AdtFile.Parse(built.WithU32(entry, uint.MaxValue)));

        Assert.Contains("entry 9", offset.Message, StringComparison.Ordinal);
        Assert.Contains("entry 9", size.Message, StringComparison.Ordinal);
        Assert.Contains("entry 9", pastEnd.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesAnMcnkThatDisagreesWithItsMcinSlot()
    {
        BuiltAdt built = SyntheticTerrain.Adt(new AdtSpec());

        InvalidDataException column = Assert.Throws<InvalidDataException>(() => AdtFile.Parse(built.WithU32(built.McnkData(20) + 0x04, 9)));
        InvalidDataException row = Assert.Throws<InvalidDataException>(() => AdtFile.Parse(built.WithU32(built.McnkData(20) + 0x08, 9)));

        Assert.Contains("MCIN slot", column.Message, StringComparison.Ordinal);
        Assert.Contains("MCIN slot", row.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesNonFiniteHeaderPositionAndHeights()
    {
        BuiltAdt built = SyntheticTerrain.Adt(new AdtSpec());
        int mcvtData = built.McnkStarts[3] + 136 + 8;

        InvalidDataException position = Assert.Throws<InvalidDataException>(() => AdtFile.Parse(built.WithF32(built.McnkData(3) + 0x68, float.NaN)));
        InvalidDataException baseHeight = Assert.Throws<InvalidDataException>(() => AdtFile.Parse(built.WithF32(built.McnkData(3) + 0x70, float.PositiveInfinity)));
        InvalidDataException sample = Assert.Throws<InvalidDataException>(() => AdtFile.Parse(built.WithF32(mcvtData + (10 * 4), float.NaN)));

        Assert.Contains("not finite", position.Message, StringComparison.Ordinal);
        Assert.Contains("not finite", baseHeight.Message, StringComparison.Ordinal);
        Assert.Contains("MCVT", sample.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesAnMcvtOfTheWrongSizeOrAtTheWrongOffset()
    {
        BuiltAdt built = SyntheticTerrain.Adt(new AdtSpec());
        int mcvtStart = built.McnkStarts[3] + 136;

        byte[] shortMcvt = built.WithU32(mcvtStart + 4, 576);
        InvalidDataException size = Assert.Throws<InvalidDataException>(() => AdtFile.Parse(shortMcvt));
        InvalidDataException offset = Assert.Throws<InvalidDataException>(() => AdtFile.Parse(built.WithU32(built.McnkData(3) + 0x14, 8)));

        Assert.Contains("MCVT", size.Message, StringComparison.Ordinal);
        Assert.Contains("MCVT", offset.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesLiquidFlagsThatDisagreeWithTheStoredLayers()
    {
        AdtSpec spec = new();
        spec.Liquid[3] = [new LiquidLayerSpec(AdtLiquidKind.Ocean, 0, 0, (_, _) => 0f, (_, _) => 0)];
        BuiltAdt built = SyntheticTerrain.Adt(spec);
        int flagsAt = built.McnkData(3);
        uint flags = BitConverter.ToUInt32(built.Bytes, flagsAt);

        InvalidDataException extraFlag = Assert.Throws<InvalidDataException>(() => AdtFile.Parse(built.WithU32(flagsAt, flags | AdtLiquidLayer.McnkMagma)));
        InvalidDataException noFlag = Assert.Throws<InvalidDataException>(() => AdtFile.Parse(built.WithU32(flagsAt, 0)));

        Assert.Contains("liquid layer", extraFlag.Message, StringComparison.Ordinal);
        Assert.Contains("liquid layer", noFlag.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesALiquidOffsetThatDoesNotPointAtMclq()
    {
        AdtSpec spec = new();
        spec.Liquid[3] = [new LiquidLayerSpec(AdtLiquidKind.Ocean, 0, 0, (_, _) => 0f, (_, _) => 0)];
        BuiltAdt built = SyntheticTerrain.Adt(spec);

        InvalidDataException e = Assert.Throws<InvalidDataException>(() => AdtFile.Parse(built.WithU32(built.McnkData(3) + 0x60, 136)));
        InvalidDataException size = Assert.Throws<InvalidDataException>(() => AdtFile.Parse(built.WithU32(built.McnkData(3) + 0x64, 8 + 804 + 1)));

        Assert.Contains("MCLQ", e.Message, StringComparison.Ordinal);
        Assert.Contains("MCLQ", size.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesPlacementsThatNameAModelOutsideTheTable()
    {
        AdtSpec spec = new();
        spec.Doodads.Add(new DoodadSpec("a.mdx", 1, default, default, 1024, 0));
        spec.Wmos.Add(new WmoSpec("b.wmo", 2, default, default, default, default, 0, 0, 0));
        BuiltAdt built = SyntheticTerrain.Adt(spec);
        int mddf = built.MhdrData + (int)BitConverter.ToUInt32(built.Bytes, built.MhdrData + (7 * 4)) + 8;
        int modf = built.MhdrData + (int)BitConverter.ToUInt32(built.Bytes, built.MhdrData + (8 * 4)) + 8;

        InvalidDataException doodad = Assert.Throws<InvalidDataException>(() => AdtFile.Parse(built.WithU32(mddf, 5)));
        InvalidDataException wmo = Assert.Throws<InvalidDataException>(() => AdtFile.Parse(built.WithU32(modf, 5)));

        Assert.Contains("MDDF", doodad.Message, StringComparison.Ordinal);
        Assert.Contains("MODF", wmo.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesAnMmidOffsetOutsideMmdx()
    {
        AdtSpec spec = new();
        spec.Doodads.Add(new DoodadSpec("a.mdx", 1, default, default, 1024, 0));
        BuiltAdt built = SyntheticTerrain.Adt(spec);
        int mmid = built.MhdrData + (int)BitConverter.ToUInt32(built.Bytes, built.MhdrData + (4 * 4)) + 8;

        InvalidDataException e = Assert.Throws<InvalidDataException>(() => AdtFile.Parse(built.WithU32(mmid, 4000)));

        Assert.Contains("MMID", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_NeverThrowsAnythingButInvalidDataForTruncatedFiles()
    {
        BuiltAdt built = SyntheticTerrain.Adt(RichSpec());
        int[] lengths = [0, 1, 3, 4, 7, 8, 11, 12, 19, 20, 83, 84, 100, 4200, built.McnkStarts[0] - 1, built.McnkStarts[0], built.McnkStarts[0] + 100, built.McnkStarts[1], built.Bytes.Length / 2, built.Bytes.Length - 1];

        foreach (int length in lengths)
        {
            Assert.Throws<InvalidDataException>(() => AdtFile.Parse(built.Bytes.AsSpan(0, length)));
        }
    }

    [Fact]
    public void Parse_NeverThrowsAnythingButInvalidDataForCorruptedBytes()
    {
        BuiltAdt built = SyntheticTerrain.Adt(RichSpec());
        var random = new XorShift(0x9E3779B97F4A7C15);
        int refused = 0;
        int accepted = 0;
        for (int round = 0; round < 3000; round++)
        {
            byte[] copy = (byte[])built.Bytes.Clone();
            int flips = 1 + random.Next(4);
            for (int i = 0; i < flips; i++)
            {
                // Bias half of the damage toward the structural bytes (MHDR, MCIN, MCNK headers) where the offsets live.
                int at = random.Next(2) == 0
                    ? random.Next(built.McinStart + 8 + 4096)
                    : built.McnkStarts[random.Next(256)] + random.Next(140);
                copy[at] = (byte)random.Next(256);
            }

            try
            {
                AdtFile.Parse(copy);
                accepted++;
            }
            catch (InvalidDataException)
            {
                refused++;
            }
        }

        Assert.True(refused > 100, $"only {refused} of 3000 corrupted files were refused ({accepted} accepted)");
    }

    [Fact]
    public void Accessors_RefuseOutOfRangeIndices()
    {
        BuiltAdt built = SyntheticTerrain.Adt(RichSpec());
        var adt = AdtFile.Parse(built.Bytes);
        AdtChunk chunk = adt.Chunks[40];

        Assert.Throws<ArgumentOutOfRangeException>(() => adt.Chunk(16, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => adt.Chunk(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => chunk.OuterHeight(9, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => chunk.InnerHeight(0, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => chunk.IsHoleSquare(8, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => chunk.LiquidLayers[0].Height(9, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => chunk.LiquidLayers[0].SquareFlags(0, 8));
    }
}

/// <summary>A tiny deterministic generator for corruption tests (System.Random is not used in this codebase).</summary>
internal struct XorShift(ulong seed)
{
    private ulong _state = seed == 0 ? 1 : seed;

    /// <summary>The next value in 0 (inclusive) to <paramref name="exclusiveMax"/> (exclusive).</summary>
    public int Next(int exclusiveMax)
    {
        _state ^= _state << 13;
        _state ^= _state >> 7;
        _state ^= _state << 17;
        return (int)((_state >> 1) % (ulong)exclusiveMax);
    }
}
