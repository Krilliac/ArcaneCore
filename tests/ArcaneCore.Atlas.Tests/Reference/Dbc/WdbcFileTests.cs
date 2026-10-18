using System.Buffers.Binary;
using ArcaneCore.Atlas.Reference.Dbc;
using ArcaneCore.Atlas.Tests.Reference.Mpq;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Dbc;

/// <summary>The WDBC reader over tables built in memory (the header layout is cited in WdbcFile).</summary>
public sealed class WdbcFileTests
{
    [Fact]
    public void Parse_ReadsHeaderCellsAndStrings()
    {
        var builder = new WdbcTestBuilder(4);
        builder.Row((0, 7), (1, builder.String("Azeroth")), (2, 0xFFFFFFFF));
        builder.Row((0, 9), (3, 42));
        WdbcFile table = WdbcFile.Parse(builder.Build());

        Assert.Equal(2, table.RecordCount);
        Assert.Equal(4, table.FieldCount);
        Assert.Equal(16, table.RecordSize);
        Assert.Equal(9, table.StringBlockSize);
        Assert.Equal(7u, table.GetUInt32(0, 0));
        Assert.Equal("Azeroth", table.GetString(0, 1));
        Assert.Equal(0xFFFFFFFFu, table.GetUInt32(0, 2));
        Assert.Equal(42u, table.GetUInt32(1, 3));
        Assert.Equal(string.Empty, table.GetString(1, 1));
    }

    [Fact]
    public void LocalizedString_PrefersEnUS_ThenTheFirstFilledSlot()
    {
        var builder = new WdbcTestBuilder(10);
        builder.Row([(0, 1), .. builder.Localized(1, "Elwynn Forest")]);
        builder.Row([(0, 2), .. builder.Localized(1, "Forêt d'Elwynn", slot: 2)]);
        builder.Row((0, 3));
        WdbcFile table = WdbcFile.Parse(builder.Build());

        Assert.Equal("Elwynn Forest", table.GetLocalizedString(0, 1));
        Assert.Equal("Forêt d'Elwynn", table.GetLocalizedString(1, 1));
        Assert.Equal(string.Empty, table.GetLocalizedString(2, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.GetLocalizedString(0, 2));
    }

    [Fact]
    public void Parse_RefusesAWrongMagic()
    {
        byte[] image = new WdbcTestBuilder(2).Row((0, 1)).Build();
        image[0] = (byte)'X';
        Assert.Contains("Not a WDBC", Assert.Throws<InvalidDataException>(() => WdbcFile.Parse(image)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesALengthThatDisagreesWithTheHeader()
    {
        byte[] image = new WdbcTestBuilder(2).Row((0, 1)).Build();
        Assert.Throws<InvalidDataException>(() => WdbcFile.Parse(image[..^1]));
        Assert.Throws<InvalidDataException>(() => WdbcFile.Parse([.. image, 0]));
        Assert.Throws<InvalidDataException>(() => WdbcFile.Parse(image[..12]));
    }

    [Fact]
    public void Parse_RefusesRecordsThatAreNotFourBytesPerField()
    {
        byte[] image = new WdbcTestBuilder(2).Row((0, 1)).Build();
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), 12);
        Assert.Contains("Unsupported DBC layout", Assert.Throws<InvalidDataException>(() => WdbcFile.Parse(image)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Strings_OutsideTheBlockOrUnterminated_AreRefused()
    {
        var builder = new WdbcTestBuilder(2);
        builder.Row((0, 1), (1, 500));
        WdbcFile outside = WdbcFile.Parse(builder.Build());
        Assert.Throws<InvalidDataException>(() => outside.GetString(0, 1));

        var unterminated = new WdbcTestBuilder(2);
        unterminated.Row((0, 1), (1, unterminated.String("abc")));
        byte[] image = unterminated.Build();
        image[^1] = (byte)'d';
        Assert.Throws<InvalidDataException>(() => WdbcFile.Parse(image).GetString(0, 1));
    }

    [Fact]
    public void Cells_OutsideTheTable_AreRefused()
    {
        WdbcFile table = WdbcFile.Parse(new WdbcTestBuilder(2).Row((0, 1)).Build());
        Assert.Throws<ArgumentOutOfRangeException>(() => table.GetUInt32(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.GetUInt32(0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.GetUInt32(-1, 0));
    }
}
