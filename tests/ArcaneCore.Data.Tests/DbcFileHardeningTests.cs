using System.Buffers.Binary;
using ArcaneCore.Data.Content.Spells;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>Sweep of AC-PI-001's bug class in the DBC reader: header arithmetic must not wrap.</summary>
public sealed class DbcFileHardeningTests
{
    private static byte[] Header(uint records, uint fields, uint recordSize, uint strings, int length)
    {
        byte[] image = new byte[length];
        "WDBC"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), records);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), fields);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), recordSize);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), strings);
        return image;
    }

    [Theory]
    [InlineData(0x40000001u, 4u)]  // fields * 4 wraps to 4 in uint arithmetic
    [InlineData(0x40000000u, 0u)]  // wraps to 0
    [InlineData(0x80000001u, 4u)]
    [InlineData(0xFFFFFFFFu, 0xFFFFFFFCu)]
    public void WrappedRecordSize_IsRejected(uint fields, uint recordSize)
    {
        byte[] image = Header(records: 1, fields, recordSize, strings: 0, length: 24);
        Assert.Throws<InvalidDataException>(() => DbcFile.Parse(image));
    }

    [Fact]
    public void WrappedRecordSizeZero_WithHugeRecordCount_IsRejected()
    {
        byte[] image = Header(records: 0xFFFFFFFFu, fields: 0x40000000u, recordSize: 0, strings: 0, length: 20);
        Assert.Throws<InvalidDataException>(() => DbcFile.Parse(image));
    }

    [Fact]
    public void ValidFile_StillParses()
    {
        byte[] image = Header(records: 2, fields: 3, recordSize: 12, strings: 1, length: 20 + 24 + 1);
        DbcFile file = DbcFile.Parse(image);
        Assert.Equal((2, 3, 12), (file.RecordCount, file.FieldCount, file.RecordSize));
    }
}
