using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Data.ClientData;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.World.Playerbots.Combat;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Combat;

/// <summary>
/// The bot defense call line (<see cref="PlayerbotDefenseText"/>) and the WorldMapArea.dbc reader behind its coordinates. Every number is synthetic
/// and the DBC is built in memory: no client data is read.
/// </summary>
public sealed class PlayerbotDefenseTextTests
{
    // A synthetic rectangle shaped like a large zone: 3470.84 yd wide along Y and 2314.59 yd tall along X.
    private static readonly WorldMapAreaBounds Zone = new(Left: 1535.42f, Right: -1935.42f, Top: -7939.58f, Bottom: -10254.17f);

    private sealed record Row(uint Id, uint Map, uint Area, string Name, float Left, float Right, float Top, float Bottom);

    /// <summary>A WDBC image with <paramref name="fields"/> four-byte fields; the first eight are the WorldMapArea layout (id, map, area, name, left, right, top, bottom).</summary>
    private static byte[] Image(int fields, params Row[] rows)
    {
        var strings = new MemoryStream();
        strings.WriteByte(0);
        var records = new byte[rows.Length * fields * 4];
        for (int i = 0; i < rows.Length; i++)
        {
            Row row = rows[i];
            uint nameOffset = (uint)strings.Position;
            strings.Write(Encoding.UTF8.GetBytes(row.Name));
            strings.WriteByte(0);
            Span<byte> record = records.AsSpan(i * fields * 4, fields * 4);
            BinaryPrimitives.WriteUInt32LittleEndian(record, row.Id);
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], row.Map);
            BinaryPrimitives.WriteUInt32LittleEndian(record[8..], row.Area);
            BinaryPrimitives.WriteUInt32LittleEndian(record[12..], nameOffset);
            BinaryPrimitives.WriteSingleLittleEndian(record[16..], row.Left);
            BinaryPrimitives.WriteSingleLittleEndian(record[20..], row.Right);
            BinaryPrimitives.WriteSingleLittleEndian(record[24..], row.Top);
            BinaryPrimitives.WriteSingleLittleEndian(record[28..], row.Bottom);
        }

        byte[] block = strings.ToArray();
        var image = new byte[20 + records.Length + block.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(image, 0x43424457u);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), (uint)rows.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), (uint)fields);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), (uint)(fields * 4));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), (uint)block.Length);
        records.CopyTo(image, 20);
        block.CopyTo(image, 20 + records.Length);
        return image;
    }

    private static Row ZoneRow(uint id, uint map, uint area, string name = "Zone") => new(id, map, area, name, Zone.Left, Zone.Right, Zone.Top, Zone.Bottom);

    [Fact]
    public void ACornerPointOfTheRectangle_MapsToTheCornersOfTheFrame()
    {
        // Left/Top is the (0, 0) corner; one yard inside Right/Bottom is the (99, 99) corner.
        Assert.Equal((0, 0), PlayerbotDefenseText.Coordinates(Zone, Zone.Top, Zone.Left));
        Assert.Equal((99, 99), PlayerbotDefenseText.Coordinates(Zone, Zone.Bottom + 1f, Zone.Right + 1f));
    }

    [Fact]
    public void AWorldPointInsideTheRectangle_GivesWholePercents_FromTheDocumentedFormula()
    {
        // x = (1535.42 - 47) / 3470.84 * 100 = 42.88 and y = (-7939.58 + 9459) / 2314.59 * 100 = 65.64: cut to 42, 65.
        Assert.Equal((42, 65), PlayerbotDefenseText.Coordinates(Zone, worldX: -9459f, worldY: 47f));
    }

    [Fact]
    public void APointOutsideTheRectangle_OrADegenerateOne_GivesNoCoordinates()
    {
        Assert.Null(PlayerbotDefenseText.Coordinates(Zone, worldX: Zone.Top + 10f, worldY: 0f));
        Assert.Null(PlayerbotDefenseText.Coordinates(Zone, worldX: Zone.Bottom - 10f, worldY: 0f));
        Assert.Null(PlayerbotDefenseText.Coordinates(Zone, worldX: -9000f, worldY: Zone.Left + 10f));
        Assert.Null(PlayerbotDefenseText.Coordinates(Zone, worldX: -9000f, worldY: Zone.Right - 10f));
        Assert.Null(PlayerbotDefenseText.Coordinates(new WorldMapAreaBounds(5f, 5f, 1f, 2f), 1f, 5f));
        Assert.Null(PlayerbotDefenseText.Coordinates(Zone, worldX: float.NaN, worldY: 0f));
    }

    [Fact]
    public void Format_WithCoordinates_AppendsThemAfterTheLine()
        => Assert.Equal("Goldshire is under attack! 42, 65", PlayerbotDefenseText.Format("Goldshire", (42, 65)));

    [Fact]
    public void Format_WithoutCoordinates_IsTheBareLine()
        => Assert.Equal("Goldshire is under attack!", PlayerbotDefenseText.Format("Goldshire", null));

    [Fact]
    public void Format_UsesThePlaceItIsGiven_SoASubzoneNameCanReplaceTheZoneName()
    {
        Assert.Equal("Northshire Valley is under attack! 1, 2", PlayerbotDefenseText.Format("Northshire Valley", (1, 2)));
        Assert.Equal("Elwynn Forest is under attack!", PlayerbotDefenseText.Format("Elwynn Forest", null));
    }

    [Fact]
    public void Reader_KeepsContinentRowsWithAnArea_ByAreaId()
    {
        DbcFile file = DbcFile.Parse(Image(8,
            ZoneRow(1, 0, 12, "Elwynn"),
            ZoneRow(2, 1, 14, "Durotar"),
            ZoneRow(3, 0, 0, "Eastern Kingdoms"),   // the continent overview row: no area
            ZoneRow(4, 30, 2597, "Alterac Valley"), // a battleground map: not a continent
            new Row(5, 0, 12, "Duplicate", 9f, 8f, 7f, 6f)));

        IReadOnlyDictionary<uint, WorldMapAreaBounds> bounds = WorldMapAreaDbcReader.Read(file);

        Assert.Equal(new[] { 12u, 14u }, bounds.Keys.Order());
        Assert.Equal(Zone, bounds[12]);   // the first row for an area wins
        Assert.Equal(Zone, bounds[14]);
    }

    [Fact]
    public void Reader_SkipsRowsWithANonFiniteOrEmptyRectangle()
    {
        DbcFile file = DbcFile.Parse(Image(8,
            new Row(1, 0, 10, "NaN", float.NaN, 0f, 1f, 0f),
            new Row(2, 0, 11, "Infinity", float.PositiveInfinity, 0f, 1f, 0f),
            new Row(3, 0, 12, "Empty width", 4f, 4f, 1f, 0f),
            new Row(4, 0, 13, "Empty height", 4f, 3f, 2f, 2f),
            ZoneRow(5, 0, 14)));

        Assert.Equal(new[] { 14u }, WorldMapAreaDbcReader.Read(file).Keys);
    }

    [Fact]
    public void Reader_RefusesAnotherFieldCount()
    {
        DbcFile file = DbcFile.Parse(Image(9, ZoneRow(1, 0, 12)));

        Assert.Throws<InvalidDataException>(() => WorldMapAreaDbcReader.Read(file));
    }

    [Fact]
    public void Reader_FeedsTheFormatter_EndToEnd()
    {
        IReadOnlyDictionary<uint, WorldMapAreaBounds> bounds = WorldMapAreaDbcReader.Read(DbcFile.Parse(Image(8, ZoneRow(1, 0, 12, "Elwynn Forest"))));

        (int X, int Y)? at = PlayerbotDefenseText.Coordinates(bounds[12], worldX: -9459f, worldY: 47f);

        Assert.Equal("Goldshire is under attack! 42, 65", PlayerbotDefenseText.Format("Goldshire", at));
    }

    [Fact]
    public void TryRead_BlankPath_IsSilentAndEmpty()
    {
        var warnings = new List<string>();

        Assert.Empty(WorldMapAreaDbcReader.TryRead("", warnings.Add));
        Assert.Empty(WorldMapAreaDbcReader.TryRead(null, warnings.Add));
        Assert.Empty(warnings);
    }

    [Fact]
    public void TryRead_MissingOrMalformedFile_WarnsOnceEachAndDropsCoordinates()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcane-defense-text-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var warnings = new List<string>();
            Assert.Empty(WorldMapAreaDbcReader.TryRead(Path.Combine(directory, "missing.dbc"), warnings.Add));
            Assert.Single(warnings);

            string bad = Path.Combine(directory, "bad.dbc");
            File.WriteAllBytes(bad, [1, 2, 3]);
            Assert.Empty(WorldMapAreaDbcReader.TryRead(bad, warnings.Add));
            Assert.Equal(2, warnings.Count);

            string good = Path.Combine(directory, "good.dbc");
            File.WriteAllBytes(good, Image(8, ZoneRow(1, 0, 12)));
            Assert.Equal(new[] { 12u }, WorldMapAreaDbcReader.TryRead(good, warnings.Add).Keys);
            Assert.Equal(2, warnings.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
