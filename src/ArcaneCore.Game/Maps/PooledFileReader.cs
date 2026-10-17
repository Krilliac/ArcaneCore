using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace ArcaneCore.Game.Maps;

/// <summary>
/// Reads a whole data file (a navmesh tile, 0.1-2 MB) into a pooled buffer and hands the bytes to a parser that copies what it keeps.
/// <see cref="File.ReadAllBytes(string)"/> allocates a fresh array per read, and an array of 85 KB or more is born on the large object
/// heap, which only a gen2 collection frees and nothing compacts by default: the bots' tile reads were the World's main LOH churn
/// (2.3 MB/s on the live server, 2026-10-17, with the LOH at ~1 GB and 40-50% of it free fragments; issues #744/#755).
/// <para>
/// Thread-safe (the pool is). The pool keeps at most <see cref="MaxArraysPerBucket"/> buffers per size class up to
/// <see cref="MaxPooledBytes"/>; a larger file gets a fresh array as before. The parser must not keep the span.
/// </para>
/// </summary>
public static class PooledFileReader
{
    /// <summary>The largest buffer the pool keeps (a file over this is read into a fresh array).</summary>
    public const int MaxPooledBytes = 16 * 1024 * 1024;

    /// <summary>Buffers kept per size class: the concurrent tile readers (prefetch, background reads, the world thread) stay under it.</summary>
    public const int MaxArraysPerBucket = 8;

    private static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Create(MaxPooledBytes, MaxArraysPerBucket);

    /// <summary>Parses the bytes of a file; must copy what it keeps.</summary>
    public delegate T Parser<out T>(ReadOnlySpan<byte> data);

    /// <summary>
    /// Read <paramref name="path"/> and parse it; false (and nothing parsed) when the file or its directory does not exist. Other read errors
    /// (<see cref="IOException"/>, <see cref="UnauthorizedAccessException"/>) and the parser's exceptions propagate.
    /// </summary>
    public static bool TryParse<T>(string path, Parser<T> parse, [MaybeNullWhen(false)] out T result)
    {
        ArgumentNullException.ThrowIfNull(parse);
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            result = default;
            return false;
        }

        using (stream)
        {
            long length = stream.Length;
            if (length > Array.MaxLength)
            {
                throw new IOException($"{path} is {length} bytes, too large to read whole");
            }

            byte[] buffer = Pool.Rent((int)length);
            try
            {
                stream.ReadExactly(buffer, 0, (int)length);
                result = parse(buffer.AsSpan(0, (int)length));
                return true;
            }
            finally
            {
                Pool.Return(buffer);
            }
        }
    }
}
