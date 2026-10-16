using System.Text;
using ArcaneCore.World.Playerbots.Chat.Inference;

namespace ArcaneCore.World.Tests.Playerbots.Chat.Inference;

/// <summary>Writes small synthetic GGUF v3 files (little-endian, ggml docs/gguf.md) for the reader and engine tests.</summary>
internal sealed class GgufBuilder
{
    private readonly List<(string Key, uint Type, Action<BinaryWriter> Write)> _kv = [];
    private readonly List<(string Name, long[] Dims, GgmlType Type, byte[] Data)> _tensors = [];

    public int Alignment { get; set; } = 32;

    public uint Version { get; set; } = 3;

    public GgufBuilder String(string key, string value) => Add(key, 8, w => WriteString(w, value));

    public GgufBuilder U32(string key, uint value) => Add(key, 4, w => w.Write(value));

    public GgufBuilder I32(string key, int value) => Add(key, 5, w => w.Write(value));

    public GgufBuilder F32(string key, float value) => Add(key, 6, w => w.Write(value));

    public GgufBuilder Bool(string key, bool value) => Add(key, 7, w => w.Write((byte)(value ? 1 : 0)));

    public GgufBuilder U64(string key, ulong value) => Add(key, 10, w => w.Write(value));

    public GgufBuilder Strings(string key, IReadOnlyList<string> values) => Add(key, 9, w =>
    {
        w.Write(8u);
        w.Write((ulong)values.Count);
        foreach (string value in values) WriteString(w, value);
    });

    public GgufBuilder Ints(string key, IReadOnlyList<int> values) => Add(key, 9, w =>
    {
        w.Write(5u);
        w.Write((ulong)values.Count);
        foreach (int value in values) w.Write(value);
    });

    public GgufBuilder Floats(string key, IReadOnlyList<float> values) => Add(key, 9, w =>
    {
        w.Write(6u);
        w.Write((ulong)values.Count);
        foreach (float value in values) w.Write(value);
    });

    public GgufBuilder Tensor(string name, GgmlType type, byte[] data, params long[] dims)
    {
        _tensors.Add((name, dims, type, data));
        return this;
    }

    public GgufBuilder TensorF32(string name, float[] values, params long[] dims)
    {
        var data = new byte[values.Length * 4];
        Buffer.BlockCopy(values, 0, data, 0, data.Length);
        return Tensor(name, GgmlType.F32, data, dims);
    }

    private GgufBuilder Add(string key, uint type, Action<BinaryWriter> write)
    {
        _kv.Add((key, type, write));
        return this;
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write((ulong)bytes.Length);
        writer.Write(bytes);
    }

    /// <summary>The file's bytes: header, metadata (general.alignment added when not 32), tensor table, padding, data (each tensor aligned).</summary>
    public byte[] Build()
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            var kv = new List<(string Key, uint Type, Action<BinaryWriter> Write)>(_kv);
            if (Alignment != 32) kv.Insert(0, ("general.alignment", 4, x => x.Write((uint)Alignment)));
            w.Write(GgufFile.Magic);
            w.Write(Version);
            w.Write((ulong)_tensors.Count);
            w.Write((ulong)kv.Count);
            foreach ((string key, uint type, Action<BinaryWriter> write) in kv)
            {
                WriteString(w, key);
                w.Write(type);
                write(w);
            }

            long offset = 0;
            var offsets = new List<long>();
            foreach ((string name, long[] dims, GgmlType type, byte[] data) in _tensors)
            {
                WriteString(w, name);
                w.Write((uint)dims.Length);
                foreach (long dim in dims) w.Write((ulong)dim);
                w.Write((uint)type);
                w.Write((ulong)offset);
                offsets.Add(offset);
                offset = Align(offset + data.Length);
            }

            w.Flush();
            Pad(w, stream);
            long dataStart = stream.Position;
            for (int i = 0; i < _tensors.Count; i++)
            {
                stream.Position = dataStart + offsets[i];
                w.Write(_tensors[i].Data);
                w.Flush();
            }

            Pad(w, stream);
        }

        return stream.ToArray();
    }

    private long Align(long position) => (position + Alignment - 1) / Alignment * Alignment;

    private void Pad(BinaryWriter writer, MemoryStream stream)
    {
        writer.Flush();
        long target = Align(stream.Length);
        stream.Position = stream.Length;
        while (stream.Length < target) writer.Write((byte)0);
        writer.Flush();
    }

    /// <summary>Write the file to a fresh temp path (the caller deletes it).</summary>
    public string WriteTemp() => WriteTemp(Build());

    public static string WriteTemp(byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), "arcanecore-gguf-" + Guid.NewGuid().ToString("N") + ".gguf");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Delete a temp model file, ignoring a file still mapped by a model a failed test did not dispose.</summary>
    public static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
