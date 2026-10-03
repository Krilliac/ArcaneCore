using System.Buffers.Binary;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Npc;
using ArcaneCore.Kernel.Npc;
using Xunit;

namespace ArcaneCore.Data.Tests;

public sealed class FactionTemplateDbcTests
{
    [Fact]
    public void SyntheticVanillaRecord_DecodesEveryField_AndEnemiesPrecedeFriends()
    {
        // Independent file-order vector from pinned DBCStructure.h, not an exported catalog.
        FactionTemplateCatalog catalog = FactionTemplateDbcReader.Read(DbcFile.Parse(Image(14,
            [7, 0, 0, 8, 2, 1, 42, 43, 44, 45, 42, 47, 48, 49])));
        Assert.Equal(new FactionTemplateRecord(7, 0, 0, 8, 2, 1, 42, 43, 44, 45, 42, 47, 48, 49), catalog.Find(7));
        Assert.True(catalog.Find(7)!.IsHostileTo(new(1, 42, 0, 0, 0, 0)));
        Assert.False(catalog.Find(7)!.IsHostileTo(new(1, 47, 0, 1, 0, 0)));
        Assert.True(catalog.Find(7)!.IsHostileTo(new(1, 50, 0, 1, 0, 0)));
    }

    [Theory]
    [InlineData(13)]
    [InlineData(15)]
    public void OtherClientLayouts_AreRejected(int fields)
        => Assert.Throws<InvalidDataException>(() => FactionTemplateDbcReader.Read(DbcFile.Parse(Image(fields, new uint[fields]))));

    [Fact]
    public void DuplicateAndZeroIds_AreRejected()
    {
        Assert.Throws<InvalidDataException>(() => FactionTemplateDbcReader.Read(DbcFile.Parse(Image(14, new uint[14]))));
        uint[] row = [1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Throws<InvalidDataException>(() => FactionTemplateDbcReader.Read(DbcFile.Parse(Image(14, row, row))));
    }

    private static byte[] Image(int fields, params uint[][] records)
    {
        byte[] image = new byte[20 + records.Length * fields * 4 + 1];
        "WDBC"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), (uint)records.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), (uint)fields);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), (uint)fields * 4);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), 1);
        for (int record = 0; record < records.Length; record++)
        {
            for (int field = 0; field < fields; field++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20 + (record * fields + field) * 4), records[record][field]);
            }
        }

        return image;
    }
}
