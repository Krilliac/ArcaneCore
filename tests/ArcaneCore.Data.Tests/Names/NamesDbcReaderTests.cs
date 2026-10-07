using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Data.Content.Names;
using ArcaneCore.Data.Content.Spells;
using Xunit;

namespace ArcaneCore.Data.Tests.Names;

public sealed class NamesDbcReaderTests
{
    [Fact]
    public void SyntheticRows_CompileCatalogPatternsAndRejectMalformedWidth()
    {
        byte[] image = Image(["bad", "\\^reserved\\$"]);
        var file = DbcFile.Parse(image);
        var patterns = NamesDbcReader.Read(file, "NamesReserved.dbc");
        Assert.Matches(patterns[0], "not-bad-name");
        Assert.Matches(patterns[1], "Reserved");
        Assert.Throws<InvalidDataException>(() => NamesDbcReader.Read(DbcFile.Parse(Image(["(?<=x)bad"])), "NamesReserved.dbc"));
    }

    [Fact]
    public void ConfiguredPath_WithUnsupportedPatternFailsClosed()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Image([new string('x', NamesDbcReader.MaxPatternLength + 1)]));
            Assert.Throws<InvalidDataException>(() => NamesDbcReader.Read(path, "NamesReserved.dbc", out _));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ConfiguredPath_EnforcesFileAndRecordBounds()
    {
        string path = Path.GetTempFileName();
        try
        {
            using (FileStream stream = File.OpenWrite(path)) stream.SetLength(NamesDbcReader.MaxFileBytes + 1);
            Assert.Throws<InvalidDataException>(() => NamesDbcReader.Read(path, "NamesReserved.dbc", out _));
        }
        finally { File.Delete(path); }
        Assert.Throws<InvalidDataException>(() => NamesDbcReader.Read(DbcFile.Parse(Image(Enumerable.Repeat("", NamesDbcReader.MaxRecords + 1).ToArray())), "NamesReserved.dbc"));
    }

    [Fact]
    public void StrictStringReader_RejectsInvalidUtf8MissingNulAndBadOffset_ButAllowsLiteralReplacementCharacter()
    {
        Assert.Throws<InvalidDataException>(() => NamesDbcReader.Read(DbcFile.Parse(ImageBlock([0, 0xC3, 0x28, 0])), "NamesReserved.dbc"));
        Assert.Throws<InvalidDataException>(() => NamesDbcReader.Read(DbcFile.Parse(ImageBlock([0, (byte)'a'])), "NamesReserved.dbc"));
        byte[] badOffset = Image(["bad"]);
        BinaryPrimitives.WriteUInt32LittleEndian(badOffset.AsSpan(24), 99);
        Assert.Throws<InvalidDataException>(() => NamesDbcReader.Read(DbcFile.Parse(badOffset), "NamesReserved.dbc"));
        Assert.Single(NamesDbcReader.Read(DbcFile.Parse(Image(["\uFFFD"])), "NamesReserved.dbc"));
    }

    private static byte[] Image(string[] patterns)
    {
        var strings = new List<byte> { 0 };
        var offsets = new List<uint>();
        foreach (string pattern in patterns)
        {
            offsets.Add((uint)strings.Count);
            strings.AddRange(Encoding.UTF8.GetBytes(pattern));
            strings.Add(0);
        }
        byte[] image = new byte[20 + patterns.Length * 8 + strings.Count];
        Encoding.ASCII.GetBytes("WDBC").CopyTo(image, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), (uint)patterns.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), (uint)strings.Count);
        for (int i = 0; i < patterns.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20 + (i * 8) + 4), offsets[i]);
        }
        strings.ToArray().CopyTo(image, 20 + patterns.Length * 8);
        return image;
    }

    private static byte[] ImageBlock(byte[] strings)
    {
        byte[] image = new byte[20 + 8 + strings.Length];
        Encoding.ASCII.GetBytes("WDBC").CopyTo(image, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), (uint)strings.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(24), 1);
        strings.CopyTo(image, 28);
        return image;
    }
}
