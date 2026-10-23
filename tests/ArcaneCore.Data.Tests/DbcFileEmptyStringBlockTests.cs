using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Data.ClientData;
using ArcaneCore.Data.Content.Spells;
using Xunit;

namespace ArcaneCore.Data.Tests;

public sealed class DbcFileEmptyStringBlockTests
{
    [Fact]
    public void GetString_OffsetZero_InEmptyStringBlock_ReturnsEmpty()
    {
        DbcFile dbc = DbcFile.Parse(Image(offset: 0, strings: []));

        Assert.Equal(string.Empty, dbc.GetString(0, 0));
    }

    [Fact]
    public void GetString_NonZeroOffset_InEmptyStringBlock_StillThrows()
    {
        DbcFile dbc = DbcFile.Parse(Image(offset: 1, strings: []));

        Assert.Throws<InvalidDataException>(() => dbc.GetString(0, 0));
    }

    [Fact]
    public void GetString_OffsetPastNonEmptyBlock_StillThrows()
    {
        DbcFile dbc = DbcFile.Parse(Image(offset: 3, strings: "\0a"u8.ToArray()));

        Assert.Throws<InvalidDataException>(() => dbc.GetString(0, 0));
    }

    [Fact]
    public void GetString_OffsetZero_InNonEmptyBlockWithoutLeadingNul_ReturnsStringAtByteZero()
    {
        // Malformed but tolerated today; the fix must not turn this into "".
        DbcFile dbc = DbcFile.Parse(Image(offset: 0, strings: Encoding.UTF8.GetBytes("abc\0")));

        Assert.Equal("abc", dbc.GetString(0, 0));
    }

    [Fact]
    public void GetStringStrict_OffsetZero_InEmptyStringBlock_StillThrows()
    {
        DbcFile dbc = DbcFile.Parse(Image(offset: 0, strings: []));

        Assert.Throws<InvalidDataException>(() => dbc.GetStringStrict(0, 0));
    }

    private static byte[] Image(uint offset, byte[] strings)
    {
        byte[] image = new byte[20 + 4 + strings.Length];
        "WDBC"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), (uint)strings.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20), offset);
        strings.CopyTo(image.AsSpan(24));
        return image;
    }
}
