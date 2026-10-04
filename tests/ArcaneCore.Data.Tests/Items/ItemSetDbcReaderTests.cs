using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Items;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.Data.Tests.Items;

/// <summary>
/// Synthetic build-5875 ItemSet.dbc images. Layout: mangos DBCStructure.h ItemSetEntry / DBCfmt.h ItemSetEntryfmt (45 fields: id, 8 names,
/// name flags, 17 item ids, 8 set spells at 27, 8 thresholds at 35, required skill 43, required skill rank 44).
/// </summary>
public sealed class ItemSetDbcReaderTests
{
    private static uint[] Row(uint id, uint nameOffset, uint[] spells, uint[] thresholds, uint skill, uint rank)
    {
        uint[] row = new uint[ItemSetDbcReader.FieldCount];
        row[0] = id;
        row[1] = nameOffset;
        for (int i = 0; i < 17; i++)
        {
            row[10 + i] = 1000 + (uint)i;   // item ids: ignored by the server
        }

        spells.CopyTo(row, 27);
        thresholds.CopyTo(row, 35);
        row[43] = skill;
        row[44] = rank;
        return row;
    }

    [Fact]
    public void Build_ReadsNameSpellsThresholdsAndSkillRequirement()
    {
        byte[] image = Image(ItemSetDbcReader.FieldCount, "\0Battlegear\0Garb\0",
            Row(81, 1, [16001, 16002, 16003, 0, 0, 0, 0, 0], [2, 4, 6, 0, 0, 0, 0, 0], 164, 200),
            Row(82, 12, [17001, 0, 0, 0, 0, 0, 0, 0], [3, 0, 0, 0, 0, 0, 0, 0], 0, 0));

        ItemSetCatalog catalog = ItemSetDbcReader.Build(DbcFile.Parse(image));

        Assert.Equal(2, catalog.Count);
        ItemSetRecord first = catalog.Find(81)!;
        Assert.Equal("Battlegear", first.Name);
        Assert.Equal([16001u, 16002, 16003, 0, 0, 0, 0, 0], first.SpellIds);
        Assert.Equal([2u, 4, 6, 0, 0, 0, 0, 0], first.Thresholds);
        Assert.Equal(164u, first.RequiredSkill);
        Assert.Equal(200u, first.RequiredSkillRank);
        Assert.Equal("Garb", catalog.Find(82)!.Name);
        Assert.Null(catalog.Find(83));
    }

    [Theory]
    [InlineData(44)]
    [InlineData(46)]
    public void Build_RefusesAnyOtherFieldCount(int fields)
        => Assert.Throws<InvalidDataException>(() => ItemSetDbcReader.Build(DbcFile.Parse(Image(fields, "\0", new uint[fields]))));

    [Fact]
    public void Build_RefusesADuplicateSetId()
    {
        byte[] image = Image(ItemSetDbcReader.FieldCount, "\0",
            Row(5, 0, new uint[8], new uint[8], 0, 0), Row(5, 0, new uint[8], new uint[8], 0, 0));

        Assert.Throws<InvalidDataException>(() => ItemSetDbcReader.Build(DbcFile.Parse(image)));
    }

    [Fact]
    public void Build_RefusesAStringOffsetOutsideTheStringBlock()
    {
        byte[] image = Image(ItemSetDbcReader.FieldCount, "\0", Row(5, 99, new uint[8], new uint[8], 0, 0));

        Assert.Throws<InvalidDataException>(() => ItemSetDbcReader.Build(DbcFile.Parse(image)));
    }

    [Fact]
    public void Load_ReadsAFileFromDisk_AndRefusesAMissingOne()
    {
        string path = Path.Combine(Path.GetTempPath(), $"itemset-{Guid.NewGuid():N}.dbc");
        try
        {
            File.WriteAllBytes(path, Image(ItemSetDbcReader.FieldCount, "\0Set\0", Row(9, 1, [1, 0, 0, 0, 0, 0, 0, 0], [2, 0, 0, 0, 0, 0, 0, 0], 0, 0)));
            Assert.Equal("Set", ItemSetDbcReader.Load(path).Find(9)!.Name);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.ThrowsAny<IOException>(() => ItemSetDbcReader.Load(path));
    }

    [Fact]
    public void ARecordWithAnotherBonusSlotCount_IsRefusedAtConstruction()
        => Assert.Throws<ArgumentException>(() => new ItemSetRecord(1, "x", [1, 2], [1, 2], 0, 0));

    private static byte[] Image(int fields, string strings, params uint[][] records)
    {
        byte[] block = Encoding.UTF8.GetBytes(strings);
        byte[] image = new byte[20 + (records.Length * fields * 4) + block.Length];
        "WDBC"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), (uint)records.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), (uint)fields);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), (uint)fields * 4);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), (uint)block.Length);
        for (int record = 0; record < records.Length; record++)
        {
            for (int field = 0; field < fields; field++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20 + (((record * fields) + field) * 4)), records[record][field]);
            }
        }

        block.CopyTo(image.AsSpan(20 + (records.Length * fields * 4)));
        return image;
    }
}
