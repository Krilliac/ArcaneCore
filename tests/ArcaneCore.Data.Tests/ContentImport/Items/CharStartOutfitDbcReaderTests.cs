using System.Buffers.Binary;
using ArcaneCore.Data.Content.Items;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.Items;

public sealed class CharStartOutfitDbcReaderTests
{
    [Fact]
    public void ReadsCompactFortyOneFieldRecordAndSignedItemSlots()
    {
        byte[] bytes = File(1, (1u) | (1u << 8), [25, -1, 38]);

        var outfit = Assert.Single(CharStartOutfitDbcReader.Read(bytes));
        Assert.Equal((byte)1, outfit.Race);
        Assert.Equal((byte)1, outfit.Class);
        Assert.Equal((byte)0, outfit.Gender);
        Assert.Equal([25, -1, 38], outfit.ItemIds.Take(3));
    }

    [Fact]
    public void RejectsWrongLayoutTruncationDuplicateAndUnmappedRows()
    {
        Assert.Throws<InvalidDataException>(() => CharStartOutfitDbcReader.Read(File(1, 0x0001_0101, [25])[..^1]));
        Assert.Throws<InvalidDataException>(() => CharStartOutfitDbcReader.Read(File(2, 0x0001_0101, [25])));
        Assert.Throws<InvalidDataException>(() => CharStartOutfitDbcReader.Read(File(1, 0x0001_0109, [25])));
    }

    private static byte[] File(int rows, uint key, int[] items)
    {
        byte[] bytes = new byte[20 + (rows * 152) + 1];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x43424457);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)rows);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 41);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 152);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 1);
        for (int row = 0; row < rows; row++)
        {
            Span<byte> record = bytes.AsSpan(20 + (row * 152), 152);
            BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)(100 + row));
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], key);
            for (int i = 0; i < items.Length; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(record[(8 + (i * 4))..], items[i]);
            }
        }

        return bytes;
    }
}
