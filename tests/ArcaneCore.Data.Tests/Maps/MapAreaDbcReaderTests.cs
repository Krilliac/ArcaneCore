using System.Buffers.Binary;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Data.Content.Spells;
using Xunit;

namespace ArcaneCore.Data.Tests.Maps;

public sealed class MapAreaDbcReaderTests
{
    [Fact]
    public void ReadersRefuseNamesThatExceedTheirMappedDatabaseColumns()
    {
        byte[] map0 = new byte[42 * 4];
        byte[] map1 = new byte[42 * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(map0.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(map1.AsSpan(0), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(map1.AsSpan(16), 1);
        byte[] area = new byte[25 * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(area.AsSpan(44), 1);
        string names = "\0" + new string('x', 129) + "\0";
        Assert.Throws<InvalidDataException>(() => MapDbcReader.Read(DbcFile.Parse(Image(42, [map0, map1], names))));
        Assert.Throws<InvalidDataException>(() => AreaTableDbcReader.Read(DbcFile.Parse(Image(25, [area], names))));
    }

    [Fact]
    public void MapReader_SelectsBothCommonContinentsAndUsesSqlDefaults()
    {
        DbcFile file = DbcFile.Parse(MapImage(
            MapRow(0, 0, 77, "Eastern Kingdoms"),
            MapRow(1, 0, 88, "Kalimdor")));

        IReadOnlyList<MapTemplateRow> rows = MapDbcReader.Read(file);

        Assert.Collection(rows,
            row =>
            {
                Assert.Equal(0u, row.Entry); Assert.Equal((byte)0, row.MapType);
                Assert.Equal(77u, row.LinkedZone); Assert.Equal("Eastern Kingdoms", row.MapName);
                Assert.Equal(0u, row.Parent); Assert.Equal(0u, row.PlayerLimit); Assert.Equal(0u, row.ResetDelay);
                Assert.Equal(-1, row.GhostEntranceMap); Assert.Equal(string.Empty, row.ScriptName);
            },
            row => Assert.Equal(1u, row.Entry));
    }

    /// <summary>
    /// Every Map.dbc row becomes a map_template row, not only the continents: the dungeons, raids and battlegrounds keep their instance
    /// type (field 2: 1 dungeon, 2 raid, 3 battleground, as vmangos map_template.map_type) and linked zone (field 19), in id order.
    /// </summary>
    [Fact]
    public void MapReader_ReadsEveryMap_WithItsInstanceTypeAndLinkedZone()
    {
        DbcFile file = DbcFile.Parse(MapImage(
            MapRow(489, 3, 3277, "wrong"),
            MapRow(36, 1, 0, "ignored"),
            MapRow(0, 0, 77, "Eastern Kingdoms"),
            MapRow(249, 2, 2159, "only"),
            MapRow(1, 0, 88, "Kalimdor")));

        IReadOnlyList<MapTemplateRow> rows = MapDbcReader.Read(file);

        Assert.Equal([0u, 1u, 36u, 249u, 489u], rows.Select(r => r.Entry));
        Assert.Equal([(byte)0, (byte)0, (byte)1, (byte)2, (byte)3], rows.Select(r => r.MapType));
        Assert.Equal([77u, 88u, 0u, 2159u, 3277u], rows.Select(r => r.LinkedZone));
        Assert.Equal("ignored", rows[2].MapName);
        Assert.All(rows, r => Assert.Equal((0u, 0u, 0u, -1, ""), (r.Parent, r.PlayerLimit, r.ResetDelay, r.GhostEntranceMap, r.ScriptName)));
    }

    [Fact]
    public void MapReader_RefusesAnUnknownInstanceType_AndADuplicateId()
    {
        Assert.Throws<InvalidDataException>(() => MapDbcReader.Read(DbcFile.Parse(MapImage(
            MapRow(0, 0, 1, "ok"), MapRow(1, 0, 1, "ok"), MapRow(36, 4, 0, "wrong")))));
        Assert.Throws<InvalidDataException>(() => MapDbcReader.Read(DbcFile.Parse(MapImage(
            MapRow(0, 0, 1, "ok"), MapRow(1, 0, 1, "ok"), MapRow(36, 1, 0, "wrong"), MapRow(36, 1, 0, "only")))));
    }

    [Fact]
    public void MapReader_RequiresBothCommonContinentsAndRejectsSelectedWrongType()
    {
        Assert.Throws<InvalidDataException>(() => MapDbcReader.Read(DbcFile.Parse(MapImage(MapRow(0, 0, 1, "only")))));
        Assert.Throws<InvalidDataException>(() => MapDbcReader.Read(DbcFile.Parse(MapImage(
            MapRow(0, 1, 1, "wrong"), MapRow(1, 0, 1, "ok")))));
    }

    [Fact]
    public void AreaReader_PreservesSelectedColumnsAndRejectsDuplicateIds()
    {
        DbcFile file = DbcFile.Parse(AreaImage(AreaRow(42, 3, 4, 5, 6, -7, 8, 9, "West")));
        AreaTemplateRow row = Assert.Single(AreaTableDbcReader.Read(file));
        Assert.Equal(42u, row.Entry); Assert.Equal(3u, row.MapId); Assert.Equal(4u, row.ZoneId);
        Assert.Equal(5u, row.ExploreFlag); Assert.Equal(6u, row.Flags); Assert.Equal(-7, row.AreaLevel);
        Assert.Equal("West", row.Name); Assert.Equal(8u, row.Team); Assert.Equal(9u, row.LiquidTypeId);

        Assert.Throws<InvalidDataException>(() => AreaTableDbcReader.Read(DbcFile.Parse(
            AreaImage(AreaRow(1, 0, 0, 0, 0, 0, 0, 0, "a"), AreaRow(1, 0, 0, 0, 0, 0, 0, 0, "b")))));
    }

    [Fact]
    public void Readers_RejectWrongWidthsAndMalformedStringOffsets()
    {
        Assert.Throws<InvalidDataException>(() => MapDbcReader.Read(DbcFile.Parse(MapImage(fields: 41))));
        Assert.Throws<InvalidDataException>(() => AreaTableDbcReader.Read(DbcFile.Parse(AreaImage(fields: 24))));

        byte[] malformed = AreaImage(AreaRow(1, 0, 0, 0, 0, 0, 0, 0, "ok"));
        BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(20 + 11 * 4), 99);
        Assert.Throws<InvalidDataException>(() => AreaTableDbcReader.Read(DbcFile.Parse(malformed)));
    }

    private static byte[] MapImage(params byte[][] rows) => MapImage(42, rows);
    private static byte[] MapImage(int fields, params byte[][] rows) => Image(fields, rows, "\0Eastern Kingdoms\0Kalimdor\0ignored\0wrong\0only\0ok\0West\0a\0b\0");
    private static byte[] AreaImage(params byte[][] rows) => AreaImage(25, rows);
    private static byte[] AreaImage(int fields, params byte[][] rows) => Image(fields, rows, "\0West\0ok\0a\0b\0");

    private static byte[] MapRow(uint id, uint type, uint linkedZone, string name)
    {
        byte[] row = new byte[42 * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(0), id);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(8), type);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(16), StringOffset(name));
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(19 * 4), linkedZone);
        return row;
    }

    private static uint StringOffset(string value)
    {
        const string strings = "\0Eastern Kingdoms\0Kalimdor\0ignored\0wrong\0only\0ok\0West\0a\0b\0";
        int start = 0;
        foreach (string item in strings.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (item == value) return (uint)(start + 1);
            start += item.Length + 1;
        }

        throw new ArgumentException("test string is not in the synthetic string block", nameof(value));
    }

    private static byte[] AreaRow(uint id, uint map, uint zone, uint explore, uint flags, int level, uint team, uint liquid, string name)
    {
        byte[] row = new byte[25 * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(0), id);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(4), map);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(8), zone);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(12), explore);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(16), flags);
        BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(40), level);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(44), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(80), team);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(96), liquid);
        return row;
    }

    private static byte[] Image(int fields, IReadOnlyList<byte[]> rows, string strings)
    {
        byte[] bytes = new byte[20 + rows.Count * fields * 4 + System.Text.Encoding.UTF8.GetByteCount(strings)];
        "WDBC"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)rows.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)fields);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)(fields * 4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), (uint)System.Text.Encoding.UTF8.GetByteCount(strings));
        for (int i = 0; i < rows.Count; i++) rows[i].CopyTo(bytes, 20 + i * fields * 4);
        System.Text.Encoding.UTF8.GetBytes(strings).CopyTo(bytes, 20 + rows.Count * fields * 4);
        return bytes;
    }
}
