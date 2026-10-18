using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Atlas.Reference.Dbc;

namespace ArcaneCore.Atlas.Tests.Reference.Mpq;

/// <summary>A scratch directory under the system temp folder, deleted on dispose.</summary>
internal sealed class TestDirectory : IDisposable
{
    public TestDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "arcane-atlas-r1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>The directory's full path.</summary>
    public string Path { get; }

    /// <summary>Writes <paramref name="data"/> to a new file under the directory and returns its full path.</summary>
    public string Write(string relative, byte[] data)
    {
        string full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, data);
        return full;
    }

    /// <summary>A new unused file name under the directory.</summary>
    public string NewName(string extension) => System.IO.Path.Combine(Path, Guid.NewGuid().ToString("N") + extension);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A test that failed with a handle open leaves its folder behind; the temp cleaner collects it.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Builds WDBC images for the DBC reader and map directory tests.</summary>
internal sealed class WdbcTestBuilder(int fieldCount)
{
    private readonly List<uint[]> _records = [];
    private readonly MemoryStream _strings = StringBlock();

    private static MemoryStream StringBlock()
    {
        var block = new MemoryStream();
        block.WriteByte(0);
        return block;
    }

    /// <summary>Appends a string to the string block and returns its offset.</summary>
    public uint String(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        uint offset = (uint)_strings.Length;
        _strings.Seek(0, SeekOrigin.End);
        _strings.Write(Encoding.UTF8.GetBytes(text));
        _strings.WriteByte(0);
        return offset;
    }

    /// <summary>Appends a row; cells not given stay 0.</summary>
    public WdbcTestBuilder Row(params (int Field, uint Value)[] cells)
    {
        var row = new uint[fieldCount];
        foreach ((int field, uint value) in cells)
        {
            row[field] = value;
        }

        _records.Add(row);
        return this;
    }

    /// <summary>A localized string: the enUS slot gets <paramref name="text"/>, or <paramref name="slot"/> when given.</summary>
    public IEnumerable<(int Field, uint Value)> Localized(int firstField, string text, int slot = 0)
    {
        yield return (firstField + slot, String(text));
        yield return (firstField + 8, 0xFFFFFFFF);
    }

    /// <summary>The image.</summary>
    public byte[] Build()
    {
        byte[] strings = _strings.ToArray();
        var image = new byte[20 + (_records.Count * fieldCount * 4) + strings.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(image, WdbcFile.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), (uint)_records.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), (uint)fieldCount);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), (uint)(fieldCount * 4));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), (uint)strings.Length);
        int at = 20;
        foreach (uint[] row in _records)
        {
            foreach (uint cell in row)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at), cell);
                at += 4;
            }
        }

        strings.CopyTo(image, at);
        return image;
    }
}
