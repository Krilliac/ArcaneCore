using System.Buffers.Binary;
using ArcaneCore.Data.Content.ClientEffects;
using ArcaneCore.Data.Content.Creatures;
using ArcaneCore.Data.Content.Names;
using ArcaneCore.Data.ClientData;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// Characterisation of the DBC shape-validation failures that the field-count helper refactor must preserve:
/// exception type, exact message text, and the separate record-limit checks that sit beside the field checks.
/// </summary>
public sealed class DbcFieldCountMessageTests
{
    private static byte[] Image(int fields, int records)
    {
        int recordSize = fields * 4;
        byte[] image = new byte[20 + (records * recordSize) + 1];
        BinaryPrimitives.WriteUInt32LittleEndian(image, 0x43424457);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), (uint)records);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), (uint)fields);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), (uint)recordSize);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), 1);
        return image;
    }

    [Fact]
    public void DbcFileParse_Messages_ArePinned()
    {
        Assert.Equal("not a WDBC file",
            Assert.Throws<InvalidDataException>(() => DbcFile.Parse(new byte[19])).Message);

        byte[] badRecordSize = Image(3, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(badRecordSize.AsSpan(12), 8);
        Assert.Equal("unsupported DBC layout: 3 fields in 8-byte records",
            Assert.Throws<InvalidDataException>(() => DbcFile.Parse(badRecordSize)).Message);

        Assert.Equal("DBC size does not match its header",
            Assert.Throws<InvalidDataException>(() => DbcFile.Parse(Image(3, 0)[..^1])).Message);
    }

    [Theory]
    [InlineData(28)]
    [InlineData(30)]
    public void ClientEffectReader_WrongFieldCount_MessageIsPinned(int fields)
    {
        DbcFile file = DbcFile.Parse(Image(fields, 0));
        var error = Assert.Throws<InvalidDataException>(() => SoundEntriesDbcReader.Read(file));
        Assert.Equal($"SoundEntries.dbc of build 5875 has 29 fields, this file has {fields}", error.Message);
    }

    [Fact]
    public void ClientEffectReader_ExactFieldCount_IsAccepted()
        => Assert.Empty(SoundEntriesDbcReader.Read(DbcFile.Parse(Image(29, 0))).Rows);

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void NamesReader_WrongFieldCount_MessageIsPinned(int fields)
    {
        DbcFile file = DbcFile.Parse(Image(fields, 0));
        var error = Assert.Throws<InvalidDataException>(() => NamesDbcReader.Read(file, "NamesReserved.dbc"));
        Assert.Equal("NamesReserved.dbc requires the build-5875 two-field ds layout", error.Message);
    }

    [Fact]
    public void NamesReader_RecordLimit_BoundaryAndMessageArePinned()
    {
        // The record limit is a separate check after the field check and must survive any helper extraction.
        Assert.Equal(100_000, NamesDbcReader.MaxRecords);
        var error = Assert.Throws<InvalidDataException>(
            () => NamesDbcReader.ReadPatternStrings(DbcFile.Parse(Image(NamesDbcReader.FieldCount, NamesDbcReader.MaxRecords + 1)), "NamesProfanity.dbc"));
        Assert.Equal("NamesProfanity.dbc exceeds the 100000-record limit", error.Message);

        // Field check wins over the record limit when both are violated.
        error = Assert.Throws<InvalidDataException>(
            () => NamesDbcReader.ReadPatternStrings(DbcFile.Parse(Image(3, NamesDbcReader.MaxRecords + 1)), "NamesProfanity.dbc"));
        Assert.Equal("NamesProfanity.dbc requires the build-5875 two-field ds layout", error.Message);
    }

    [Fact]
    public void CreatureDisplayModelReader_WrongFieldCount_MessagesIncludeRecordSize()
    {
        DbcFile goodModels = DbcFile.Parse(Image(CreatureDisplayModelDbcReader.ModelDataFieldCount, 0));
        DbcFile goodDisplays = DbcFile.Parse(Image(CreatureDisplayModelDbcReader.DisplayInfoFieldCount, 0));

        var error = Assert.Throws<InvalidDataException>(
            () => CreatureDisplayModelDbcReader.Read(DbcFile.Parse(Image(11, 0)), goodModels));
        Assert.Equal("CreatureDisplayInfo.dbc requires 12 four-byte fields, found 11/44", error.Message);

        error = Assert.Throws<InvalidDataException>(
            () => CreatureDisplayModelDbcReader.Read(goodDisplays, DbcFile.Parse(Image(17, 0))));
        Assert.Equal("CreatureModelData.dbc requires 16 four-byte fields, found 17/68", error.Message);

        // Display file is validated first.
        error = Assert.Throws<InvalidDataException>(
            () => CreatureDisplayModelDbcReader.Read(DbcFile.Parse(Image(13, 0)), DbcFile.Parse(Image(17, 0))));
        Assert.StartsWith("CreatureDisplayInfo.dbc requires", error.Message);
    }

    [Fact]
    public void CreatureDisplayModelReader_ExactFieldCounts_AreAccepted()
        => Assert.NotNull(CreatureDisplayModelDbcReader.Read(
            DbcFile.Parse(Image(CreatureDisplayModelDbcReader.DisplayInfoFieldCount, 0)),
            DbcFile.Parse(Image(CreatureDisplayModelDbcReader.ModelDataFieldCount, 0))));
}
