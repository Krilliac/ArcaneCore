using System.Buffers.Binary;
using ArcaneCore.Data.Content.Creatures;
using ArcaneCore.Data.Content.Spells;
using Xunit;

namespace ArcaneCore.Data.Tests.Creatures;

public sealed class CreatureDisplayModelDbcReaderTests
{
    [Fact]
    public void ConfiguredFiles_AreBoundedAndHashedFromTheLoadedImages()
    {
        string displays = Path.GetTempFileName();
        string models = Path.GetTempFileName();
        try
        {
            byte[] displayImage = Dbc(12, [Row(12, (0, 100u), (1, 1u), (4, 1.5f))]);
            byte[] modelImage = Dbc(16, [Row(16, (0, 1u), (4, 2f), (15, 3f))]);
            File.WriteAllBytes(displays, displayImage);
            File.WriteAllBytes(models, modelImage);
            var content = CreatureDisplayModelDbcReader.Load(displays, models);
            Assert.Contains(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(displayImage)), content.Provenance);
            using (FileStream stream = File.OpenWrite(displays)) stream.SetLength((long)CreatureDisplayModelDbcReader.MaxFileBytes + 1);
            Assert.Throws<InvalidDataException>(() => CreatureDisplayModelDbcReader.Load(displays, models));
        }
        finally { File.Delete(displays); File.Delete(models); }
    }

    [Fact]
    public void Build5875Layouts_ReadScaleAndRetainDisplayWithoutModel()
    {
        DbcFile displays = DbcFile.Parse(Dbc(12, [Row(12, (0, 100u), (1, 1u), (4, 1.5f)), Row(12, (0, 200u), (1, 99u), (4, 2f))]));
        DbcFile models = DbcFile.Parse(Dbc(16, [Row(16, (0, 1u), (4, 2f), (15, 3f))]));

        var content = CreatureDisplayModelDbcReader.Read(displays, models, "test");

        Assert.Equal(3f, content.Find(100)!.NativeScale);
        Assert.Equal(2f, content.Find(200)!.NativeScale);
        Assert.Equal("test", content.Provenance);
    }

    [Fact]
    public void WrongWidthAndNegativeScaleFailClosed()
    {
        DbcFile displays = DbcFile.Parse(Dbc(12, [Row(12, (0, 100u), (1, 1u), (4, 1.5f))]));
        DbcFile models = DbcFile.Parse(Dbc(16, [Row(16, (0, 1u), (4, -1f), (15, 3f))]));

        Assert.Throws<InvalidDataException>(() => CreatureDisplayModelDbcReader.Read(displays, models));
        Assert.Throws<InvalidDataException>(() => CreatureDisplayModelDbcReader.Read(DbcFile.Parse(Dbc(11, [])), models));
    }

    [Fact]
    public void ZeroPlaceholdersUseReferenceRuntimeFallbacks()
    {
        DbcFile displays = DbcFile.Parse(Dbc(12, [Row(12, (0, 100u), (1, 1u), (4, 0f))]));
        DbcFile models = DbcFile.Parse(Dbc(16, [Row(16, (0, 1u), (4, 0f), (15, 0f))]));

        var content = CreatureDisplayModelDbcReader.Read(displays, models, "zero-placeholder");
        var row = content.Find(100)!;

        Assert.Equal(0f, row.DisplayScale);
        Assert.Equal(0f, row.ModelScale);
        Assert.Equal(0f, row.CollisionHeight);
        Assert.Equal(1f, row.NativeScale);
        Assert.True(row.HasModelData);
    }

    [Fact]
    public void NonFiniteDisplayScaleFailsClosed()
    {
        DbcFile displays = DbcFile.Parse(Dbc(12, [Row(12, (0, 100u), (1, 1u), (4, float.NaN))]));
        DbcFile models = DbcFile.Parse(Dbc(16, [Row(16, (0, 1u), (4, 1f), (15, 2f))]));

        Assert.Throws<InvalidDataException>(() => CreatureDisplayModelDbcReader.Read(displays, models));
    }

    private static byte[] Dbc(int fields, IReadOnlyList<byte[]> rows)
    {
        int recordSize = fields * 4;
        byte[] bytes = new byte[20 + (rows.Count * recordSize) + 1];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x43424457);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)rows.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)fields);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)recordSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 1);
        for (int i = 0; i < rows.Count; i++) rows[i].CopyTo(bytes, 20 + i * recordSize);
        return bytes;
    }

    private static byte[] Row(int fields, params (int Field, object Value)[] values)
    {
        byte[] row = new byte[fields * 4];
        foreach ((int field, object value) in values)
        {
            if (value is uint u) BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(field * 4), u);
            else BinaryPrimitives.WriteSingleLittleEndian(row.AsSpan(field * 4), (float)value);
        }
        return row;
    }
}
