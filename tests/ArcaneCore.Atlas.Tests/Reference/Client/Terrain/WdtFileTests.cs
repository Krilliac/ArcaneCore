using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Client.Terrain;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Client.Terrain;

public sealed class WdtFileTests
{
    [Fact]
    public void Parse_ListsTilesInServerTileOrder()
    {
        byte[] bytes = SyntheticTerrain.Wdt([(32, 48), (31, 49), (2, 5), (63, 63), (0, 0)]);

        var wdt = WdtFile.Parse(bytes);

        Assert.Equal(5, wdt.TileCount);
        Assert.False(wdt.IsGlobalWmo);
        Assert.True(wdt.HasTile(32, 48));
        Assert.True(wdt.HasTile(31, 49));
        Assert.True(wdt.HasTile(63, 63));
        Assert.True(wdt.HasTile(0, 0));

        // The pair is (tile X, tile Y); the transposed pair is a different tile.
        Assert.False(wdt.HasTile(48, 32));
        Assert.False(wdt.HasTile(5, 2));
        Assert.Equal(
            new[] { new TileKey(7, 0, 0), new TileKey(7, 2, 5), new TileKey(7, 31, 49), new TileKey(7, 32, 48), new TileKey(7, 63, 63) },
            wdt.Tiles(7));
    }

    [Fact]
    public void HasTile_IsFalseOutsideTheGrid()
    {
        var wdt = WdtFile.Parse(SyntheticTerrain.Wdt([(0, 0)]));

        Assert.False(wdt.HasTile(-1, 0));
        Assert.False(wdt.HasTile(0, -1));
        Assert.False(wdt.HasTile(64, 0));
        Assert.False(wdt.HasTile(0, 64));
        Assert.Equal(0u, wdt.TileFlags(64, 64));
        Assert.Equal(1u, wdt.TileFlags(0, 0));
    }

    [Fact]
    public void Parse_ReadsTheGlobalWmo()
    {
        var wmo = new WmoSpec("World\\wmo\\Dungeon\\KL_Deadmines\\Deadmines.wmo", 1, new WorldPoint(-10, 20, 30), new WorldPoint(0, 0, 0), new WorldPoint(-50, -60, -70), new WorldPoint(50, 60, 70), 0, 0, 1);
        byte[] bytes = SyntheticTerrain.Wdt([], WdtFile.GlobalWmoFlag, [wmo]);

        var wdt = WdtFile.Parse(bytes);

        Assert.True(wdt.IsGlobalWmo);
        Assert.Equal(0, wdt.TileCount);
        Assert.Empty(wdt.Tiles(36));
        Assert.Equal(new[] { wmo.Path }, wdt.WmoNames);
        AdtWmoObject placed = Assert.Single(wdt.WmoObjects);
        Assert.Equal(wmo.Path, placed.ModelPath);
        Assert.Equal(wmo.Position, placed.Position);
        Assert.Equal(wmo.Min, placed.BoundsMin);
        Assert.Equal(wmo.Max, placed.BoundsMax);
        Assert.Equal((ushort)1, placed.NameSet);
    }

    [Fact]
    public void Parse_RefusesAnEmptyFileAndAWrongVersion()
    {
        InvalidDataException empty = Assert.Throws<InvalidDataException>(() => WdtFile.Parse([]));
        InvalidDataException version = Assert.Throws<InvalidDataException>(() => WdtFile.Parse(SyntheticTerrain.Wdt([(1, 1)], version: 21)));

        Assert.Contains("empty", empty.Message, StringComparison.Ordinal);
        Assert.Contains("MVER", version.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesMissingOrMalformedRequiredChunks()
    {
        byte[] good = SyntheticTerrain.Wdt([(1, 1)]);

        // MVER (12 bytes) | MPHD (8 + 32) | MAIN (8 + 32768)
        byte[] noMain = good[..(12 + 40)];
        byte[] noMphd = [.. good[..12], .. good[(12 + 40)..]];
        byte[] shortMain = (byte[])good.Clone();
        BitConverter.GetBytes(32760u).CopyTo(shortMain, 12 + 40 + 4);
        byte[] shortMphd = (byte[])good.Clone();
        BitConverter.GetBytes(16u).CopyTo(shortMphd, 12 + 4);

        Assert.Contains("MAIN", Assert.Throws<InvalidDataException>(() => WdtFile.Parse(noMain)).Message, StringComparison.Ordinal);
        Assert.Contains("MPHD", Assert.Throws<InvalidDataException>(() => WdtFile.Parse(noMphd)).Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => WdtFile.Parse(shortMain));
        Assert.Contains("MPHD", Assert.Throws<InvalidDataException>(() => WdtFile.Parse(shortMphd)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesADuplicatedChunkAndAModfNameOutsideMwmo()
    {
        byte[] good = SyntheticTerrain.Wdt([(1, 1)]);
        byte[] twoMain = [.. good, .. good[(12 + 40)..]];
        var wmo = new WmoSpec("a.wmo", 1, default, default, default, default, 0, 0, 0);
        byte[] withModf = SyntheticTerrain.Wdt([], 1, [wmo]);

        // MODF is the last chunk: its first u32 (the name index) sits 8 bytes after its header, 64 bytes before the end.
        byte[] badIndex = (byte[])withModf.Clone();
        BitConverter.GetBytes(3u).CopyTo(badIndex, badIndex.Length - 64);

        Assert.Contains("twice", Assert.Throws<InvalidDataException>(() => WdtFile.Parse(twoMain)).Message, StringComparison.Ordinal);
        Assert.Contains("MODF", Assert.Throws<InvalidDataException>(() => WdtFile.Parse(badIndex)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_OnlyThrowsInvalidDataForTruncatedFiles()
    {
        byte[] bytes = SyntheticTerrain.Wdt([(5, 6)], 1, [new WmoSpec("a.wmo", 1, default, default, default, default, 0, 0, 0)]);

        // MVER (12) + MPHD (40) + MAIN (32776) is a complete WDT without a global WMO, and so is the same plus MWMO (8 + 6).
        int[] completePrefixes = [12 + 40 + 32776, 12 + 40 + 32776 + 14];
        int refused = 0;
        for (int length = 0; length < bytes.Length; length += 331)
        {
            if (completePrefixes.Contains(length))
            {
                continue;
            }

            int cut = length;
            Assert.Throws<InvalidDataException>(() => WdtFile.Parse(bytes.AsSpan(0, cut)));
            refused++;
        }

        Assert.True(refused > 90);
        Assert.Throws<InvalidDataException>(() => WdtFile.Parse(bytes.AsSpan(0, bytes.Length - 1)));
    }
}

public sealed class ClientMapPathsTests
{
    [Fact]
    public void WdtPath_IsTheMapFolderAndItsOwnName()
    {
        Assert.Equal("World\\Maps\\Azeroth\\Azeroth.wdt", ClientMapPaths.WdtPath("Azeroth"));
    }

    [Fact]
    public void AdtPath_PutsTileYFirstAndTileXSecond()
    {
        // Goldshire is server tile X 48, Y 32; the client names that file Azeroth_32_48.adt.
        Assert.Equal("World\\Maps\\Azeroth\\Azeroth_32_48.adt", ClientMapPaths.AdtPath("Azeroth", new TileKey(0, 48, 32)));
        Assert.Equal("World\\Maps\\Kalimdor\\Kalimdor_0_63.adt", ClientMapPaths.AdtPath("Kalimdor", new TileKey(1, 63, 0)));
    }

    [Fact]
    public void TileOfAdt_InvertsAdtPath()
    {
        for (int x = 0; x < 64; x += 7)
        {
            for (int y = 0; y < 64; y += 5)
            {
                var tile = new TileKey(3, x, y);
                string path = ClientMapPaths.AdtPath("Maps", tile);
                string[] parts = Path.GetFileNameWithoutExtension(path.Replace('\\', '/')).Split('_');

                Assert.Equal(tile, ClientMapPaths.TileOfAdt(3, int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture)));
            }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a\\b")]
    [InlineData("a/b")]
    [InlineData("C:")]
    public void Paths_RefuseADirectoryThatIsNotOneFolderName(string directory)
    {
        Assert.ThrowsAny<ArgumentException>(() => ClientMapPaths.WdtPath(directory));
        Assert.ThrowsAny<ArgumentException>(() => ClientMapPaths.AdtPath(directory, new TileKey(0, 1, 1)));
    }

    [Fact]
    public void AdtPath_RefusesTilesOutsideTheGrid()
    {
        Assert.Throws<ArgumentException>(() => ClientMapPaths.AdtPath("Azeroth", TileKey.Global(0)));
        Assert.Throws<ArgumentException>(() => ClientMapPaths.AdtPath("Azeroth", new TileKey(0, 64, 0)));
        Assert.Throws<ArgumentException>(() => ClientMapPaths.AdtPath("Azeroth", new TileKey(0, 0, -2)));
    }
}
