using System.Buffers.Binary;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Text;

// The model probe (tools/ArcaneCore.ChatModelProbe) drives the same engine the in-process chat provider runs.
[assembly: InternalsVisibleTo("ArcaneCore.ChatModelProbe")]

namespace ArcaneCore.World.Playerbots.Chat.Inference;

/// <summary>A GGML tensor element type as a GGUF file stores it (ggml.h <c>enum ggml_type</c>).</summary>
internal enum GgmlType : uint
{
    F32 = 0,
    F16 = 1,
    Q4_0 = 2,
    Q4_1 = 3,
    Q5_0 = 6,
    Q5_1 = 7,
    Q8_0 = 8,
    Q8_1 = 9,
    Q2_K = 10,
    Q3_K = 11,
    Q4_K = 12,
    Q5_K = 13,
    Q6_K = 14,
    Q8_K = 15,
    BF16 = 30,
}

/// <summary>One tensor of a GGUF file: its name, shape (<c>Dims[0]</c> is the row length, the innermost dimension), type and where its data is.</summary>
internal sealed record GgufTensor(string Name, long[] Dims, GgmlType Type, long RelativeOffset, long DataOffset)
{
    /// <summary>Elements per row (<c>Dims[0]</c>).</summary>
    public long RowLength => Dims[0];

    /// <summary>The number of rows: the product of the outer dimensions (1 for a vector).</summary>
    public long Rows
    {
        get
        {
            long rows = 1;
            for (int i = 1; i < Dims.Length; i++) rows *= Dims[i];
            return rows;
        }
    }

    public long Elements => RowLength * Rows;

    /// <summary>Bytes of one row, or -1 when the type is not one this reader can size.</summary>
    public long RowBytes => GgmlQuant.RowBytes(Type, RowLength);

    /// <summary>Bytes of the whole tensor, or -1 when the type is not one this reader can size.</summary>
    public long ByteSize => RowBytes < 0 ? -1 : RowBytes * Rows;

    public string Shape => "[" + string.Join(",", Dims.Select(d => d.ToString(CultureInfo.InvariantCulture))) + "]";
}

/// <summary>
/// A GGUF file (v2 or v3, little-endian; ggml's docs/gguf.md), memory-mapped read-only. The header, the metadata key-values and the
/// tensor table are parsed once; tensor data is never copied: <see cref="Bytes"/> hands out spans over the mapping, so the
/// weights stay in the page cache and a model costs about its file size in memory. Every count, length and offset is checked
/// against the file; a malformed file throws <see cref="InvalidDataException"/> with the reason.
/// </summary>
internal sealed unsafe class GgufFile : IDisposable
{
    public const uint Magic = 0x46554747; // "GGUF" little-endian
    public const int DefaultAlignment = 32;
    internal const int MaxKeyValues = 1 << 16;
    internal const int MaxTensors = 1 << 16;
    internal const int MaxArrayLength = 1 << 24;
    internal const int MaxStringBytes = 1 << 24;
    internal const int MaxDims = 4;

    private readonly MemoryMappedFile? _map;
    private readonly MemoryMappedViewAccessor? _view;
    private byte* _base;
    private readonly long _length;
    private readonly Dictionary<string, GgufTensor> _byName;
    private int _disposed;

    private GgufFile(string path, MemoryMappedFile map, MemoryMappedViewAccessor view, byte* @base, long length)
    {
        Path = path;
        _map = map;
        _view = view;
        _base = @base;
        _length = length;
        try
        {
            var reader = new Reader(new ReadOnlySpan<byte>(@base, (int)Math.Min(length, int.MaxValue)));
            (Version, Metadata, Tensors, Alignment, DataStart) = Parse(ref reader, length);
            _byName = new Dictionary<string, GgufTensor>(StringComparer.Ordinal);
            foreach (GgufTensor tensor in Tensors)
                if (!_byName.TryAdd(tensor.Name, tensor)) throw new InvalidDataException($"the tensor {tensor.Name} is listed twice");
        }
        catch
        {
            Release();
            throw;
        }
    }

    public string Path { get; }

    public uint Version { get; }

    /// <summary>The metadata: scalars boxed (byte, sbyte, ushort, short, uint, int, float, bool, string, ulong, long, double), arrays as typed arrays (string[], int[], float[], ...; a nested array is object[]).</summary>
    public IReadOnlyDictionary<string, object> Metadata { get; }

    public IReadOnlyList<GgufTensor> Tensors { get; }

    public int Alignment { get; }

    /// <summary>Where the tensor data starts (aligned to <see cref="Alignment"/>).</summary>
    public long DataStart { get; }

    public long FileLength => _length;

    /// <summary>Map <paramref name="path"/> read-only and parse it. Throws <see cref="InvalidDataException"/> (or an I/O error) when it is not a readable GGUF file.</summary>
    public static GgufFile Open(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("the model file does not exist", path);
        if (file.Length < 24) throw new InvalidDataException($"the model file is {file.Length} bytes: too short for a GGUF header");
        MemoryMappedFile map = MemoryMappedFile.CreateFromFile(file.FullName, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        MemoryMappedViewAccessor? view = null;
        try
        {
            view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            byte* pointer = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            pointer += view.PointerOffset;
            return new GgufFile(file.FullName, map, view, pointer, file.Length);
        }
        catch
        {
            view?.Dispose();
            map.Dispose();
            throw;
        }
    }

    public GgufTensor? Tensor(string name) => _byName.TryGetValue(name, out GgufTensor? tensor) ? tensor : null;

    /// <summary>A span over <paramref name="length"/> bytes of the file at <paramref name="offset"/> (checked against the file).</summary>
    public ReadOnlySpan<byte> Bytes(long offset, int length)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (offset < 0 || length < 0 || offset + length > _length) throw new ArgumentOutOfRangeException(nameof(offset), "outside the model file");
        return new ReadOnlySpan<byte>(_base + offset, length);
    }

    /// <summary>The bytes of row <paramref name="row"/> of <paramref name="tensor"/>.</summary>
    public ReadOnlySpan<byte> Row(GgufTensor tensor, long row)
    {
        long rowBytes = tensor.RowBytes;
        if (rowBytes < 0) throw new InvalidDataException($"the tensor {tensor.Name} has the unsupported type {tensor.Type}");
        return Bytes(tensor.DataOffset + (row * rowBytes), checked((int)rowBytes));
    }

    // --- metadata accessors -------------------------------------------------------------------------------------------------

    public string? String(string key) => Metadata.TryGetValue(key, out object? value) && value is string text ? text : null;

    /// <summary>An integer value of any integer type (null when absent, not an integer, or out of the long range).</summary>
    public long? Integer(string key) => Metadata.TryGetValue(key, out object? value) ? value switch
    {
        byte v => v,
        sbyte v => v,
        ushort v => v,
        short v => v,
        uint v => v,
        int v => v,
        ulong v when v <= long.MaxValue => (long)v,
        long v => v,
        _ => null,
    } : null;

    public double? Real(string key) => Metadata.TryGetValue(key, out object? value) ? value switch
    {
        float v => v,
        double v => v,
        _ => Integer(key),
    } : null;

    public bool? Boolean(string key) => Metadata.TryGetValue(key, out object? value) && value is bool flag ? flag : null;

    public T[]? Array<T>(string key) => Metadata.TryGetValue(key, out object? value) ? value as T[] : null;

    /// <summary>An integer array of any integer element type, widened to int (null when absent or not integers).</summary>
    public int[]? IntArray(string key)
    {
        if (!Metadata.TryGetValue(key, out object? value)) return null;
        return value switch
        {
            int[] ints => ints,
            uint[] uints => [.. uints.Select(v => unchecked((int)v))],
            short[] shorts => [.. shorts.Select(v => (int)v)],
            ushort[] ushorts => [.. ushorts.Select(v => (int)v)],
            sbyte[] sbytes => [.. sbytes.Select(v => (int)v)],
            byte[] bytes => [.. bytes.Select(v => (int)v)],
            long[] longs => [.. longs.Select(v => unchecked((int)v))],
            _ => null,
        };
    }

    // --- parsing ------------------------------------------------------------------------------------------------------------

    private static (uint Version, Dictionary<string, object> Metadata, List<GgufTensor> Tensors, int Alignment, long DataStart) Parse(ref Reader reader, long fileLength)
    {
        uint magic = reader.U32();
        if (magic != Magic) throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"not a GGUF file (magic 0x{magic:X8})"));
        uint version = reader.U32();
        if (version is not (2 or 3)) throw new InvalidDataException($"GGUF version {version} is not supported (2 or 3; a big-endian file reads as a huge version)");
        ulong tensorCount = reader.U64();
        ulong kvCount = reader.U64();
        if (tensorCount > MaxTensors) throw new InvalidDataException($"the file claims {tensorCount} tensors (at most {MaxTensors})");
        if (kvCount > MaxKeyValues) throw new InvalidDataException($"the file claims {kvCount} metadata entries (at most {MaxKeyValues})");

        var metadata = new Dictionary<string, object>(StringComparer.Ordinal);
        for (ulong i = 0; i < kvCount; i++)
        {
            string key = reader.String();
            uint type = reader.U32();
            object value = ReadValue(ref reader, type, depth: 0);
            if (!metadata.TryAdd(key, value)) throw new InvalidDataException($"the metadata key {key} is listed twice");
        }

        int alignment = DefaultAlignment;
        if (metadata.TryGetValue("general.alignment", out object? alignValue))
        {
            long a = alignValue switch { uint u => u, int s => s, ulong l when l < int.MaxValue => (long)l, _ => -1 };
            if (a <= 0 || a > 1 << 16 || (a & (a - 1)) != 0) throw new InvalidDataException($"general.alignment {alignValue} is not a power of two up to 65536");
            alignment = (int)a;
        }

        var infos = new List<(string Name, long[] Dims, uint Type, ulong Offset)>((int)tensorCount);
        for (ulong i = 0; i < tensorCount; i++)
        {
            string name = reader.String();
            uint dimCount = reader.U32();
            if (dimCount is < 1 or > MaxDims) throw new InvalidDataException($"the tensor {name} has {dimCount} dimensions (1..{MaxDims})");
            var dims = new long[dimCount];
            for (int d = 0; d < dimCount; d++)
            {
                ulong n = reader.U64();
                if (n is 0 or > int.MaxValue) throw new InvalidDataException($"the tensor {name} has dimension {d} of {n}");
                dims[d] = (long)n;
            }

            infos.Add((name, dims, reader.U32(), reader.U64()));
        }

        long dataStart = Align(reader.Position, alignment);
        if (dataStart > fileLength) throw new InvalidDataException("the tensor data starts past the end of the file");
        var tensors = new List<GgufTensor>(infos.Count);
        foreach ((string name, long[] dims, uint type, ulong offset) in infos)
        {
            if (offset % (ulong)alignment != 0) throw new InvalidDataException($"the tensor {name} is not aligned to {alignment} bytes");
            if (offset > (ulong)(fileLength - dataStart)) throw new InvalidDataException($"the tensor {name} starts past the end of the file");
            long elements = 1;
            foreach (long dim in dims) elements = checked(elements * dim);
            var tensor = new GgufTensor(name, dims, (GgmlType)type, (long)offset, dataStart + (long)offset);
            long size = tensor.ByteSize;
            if (size >= 0 && tensor.DataOffset + size > fileLength)
                throw new InvalidDataException($"the tensor {name} ({tensor.Type} {tensor.Shape}, {size} bytes) runs past the end of the file");
            tensors.Add(tensor);
        }

        return (version, metadata, tensors, alignment, dataStart);
    }

    internal static long Align(long position, int alignment) => (position + alignment - 1) / alignment * alignment;

    private static object ReadValue(ref Reader reader, uint type, int depth)
    {
        switch (type)
        {
            case 0: return reader.U8();
            case 1: return (sbyte)reader.U8();
            case 2: return reader.U16();
            case 3: return (short)reader.U16();
            case 4: return reader.U32();
            case 5: return (int)reader.U32();
            case 6: return BitConverter.Int32BitsToSingle((int)reader.U32());
            case 7:
                byte flag = reader.U8();
                return flag switch { 0 => false, 1 => true, _ => throw new InvalidDataException($"a bool value is {flag}") };
            case 8: return reader.String();
            case 9:
                if (depth > 2) throw new InvalidDataException("metadata arrays are nested too deeply");
                return ReadArray(ref reader, depth);
            case 10: return reader.U64();
            case 11: return (long)reader.U64();
            case 12: return BitConverter.Int64BitsToDouble((long)reader.U64());
            default: throw new InvalidDataException($"unknown metadata value type {type}");
        }
    }

    private static object ReadArray(ref Reader reader, int depth)
    {
        uint type = reader.U32();
        ulong count = reader.U64();
        if (count > MaxArrayLength) throw new InvalidDataException($"a metadata array claims {count} elements (at most {MaxArrayLength})");
        int n = (int)count;
        switch (type)
        {
            case 0: return reader.Raw(n).ToArray();
            case 1: return Fill(ref reader, n, static (ref Reader r) => (sbyte)r.U8());
            case 2: return Fill(ref reader, n, static (ref Reader r) => r.U16());
            case 3: return Fill(ref reader, n, static (ref Reader r) => (short)r.U16());
            case 4: return Fill(ref reader, n, static (ref Reader r) => r.U32());
            case 5: return Fill(ref reader, n, static (ref Reader r) => (int)r.U32());
            case 6: return Fill(ref reader, n, static (ref Reader r) => BitConverter.Int32BitsToSingle((int)r.U32()));
            case 7: return Fill(ref reader, n, static (ref Reader r) => r.U8() != 0);
            case 8: return Fill(ref reader, n, static (ref Reader r) => r.String());
            case 10: return Fill(ref reader, n, static (ref Reader r) => r.U64());
            case 11: return Fill(ref reader, n, static (ref Reader r) => (long)r.U64());
            case 12: return Fill(ref reader, n, static (ref Reader r) => BitConverter.Int64BitsToDouble((long)r.U64()));
            case 9:
                var nested = new object[n];
                for (int i = 0; i < n; i++) nested[i] = ReadValue(ref reader, 9, depth + 1);
                return nested;
            default: throw new InvalidDataException($"unknown metadata array element type {type}");
        }
    }

    private delegate T ReadOne<T>(ref Reader reader);

    private static T[] Fill<T>(ref Reader reader, int count, ReadOne<T> read)
    {
        // Every element takes at least one byte: a count past the remaining bytes is a lie, refused before allocating.
        if (count > reader.Remaining) throw new InvalidDataException($"a metadata array claims {count} elements but {reader.Remaining} bytes remain");
        var values = new T[count];
        for (int i = 0; i < count; i++) values[i] = read(ref reader);
        return values;
    }

    /// <summary>A bounds-checked little-endian cursor over the file's front (the header, metadata and tensor table).</summary>
    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;

        public readonly long Position => _position;

        public readonly int Remaining => _data.Length - _position;

        public ReadOnlySpan<byte> Raw(int count)
        {
            if (count < 0 || count > _data.Length - _position) throw new InvalidDataException("the GGUF header runs past the end of the file");
            ReadOnlySpan<byte> slice = _data.Slice(_position, count);
            _position += count;
            return slice;
        }

        public byte U8() => Raw(1)[0];

        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Raw(2));

        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Raw(4));

        public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Raw(8));

        public string String()
        {
            ulong length = U64();
            if (length > MaxStringBytes) throw new InvalidDataException($"a string claims {length} bytes (at most {MaxStringBytes})");
            return Encoding.UTF8.GetString(Raw((int)length));
        }
    }

    private void Release()
    {
        if (_view is { } view)
        {
            if (_base != null) view.SafeMemoryMappedViewHandle.ReleasePointer();
            _base = null;
            view.Dispose();
        }

        _map?.Dispose();
    }

    /// <summary>Unmap the file. The caller makes sure no thread still reads a span from it (the engine joins its threads first).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Release();
    }
}
