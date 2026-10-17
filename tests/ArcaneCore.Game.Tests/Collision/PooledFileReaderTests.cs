using ArcaneCore.Game.Maps;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// The pooled whole-file read behind navmesh tile loads (issues #744/#755: the tile reads' fresh 0.1-2 MB arrays were the live World's
/// main large-object-heap churn). A read hands the parser exactly the file's bytes, a missing file is false, and a repeated read of a
/// large file allocates no new large array.
/// </summary>
public sealed class PooledFileReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-pooledread-" + Guid.NewGuid().ToString("N"));

    public PooledFileReaderTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Read_GivesTheParserExactlyTheFileBytes()
    {
        string path = Path.Combine(_directory, "a.bin");
        byte[] content = [.. Enumerable.Range(0, 300_001).Select(i => (byte)(i * 7))];
        File.WriteAllBytes(path, content);

        Assert.True(PooledFileReader.TryParse(path, static data => data.ToArray(), out byte[]? read));
        Assert.Equal(content, read);

        string empty = Path.Combine(_directory, "empty.bin");
        File.WriteAllBytes(empty, []);
        Assert.True(PooledFileReader.TryParse(empty, static data => data.Length, out int length));
        Assert.Equal(0, length);
    }

    [Fact]
    public void MissingFileOrDirectory_IsFalse_AndParseErrorsPropagate()
    {
        Assert.False(PooledFileReader.TryParse(Path.Combine(_directory, "none.bin"), static data => data.Length, out _));
        Assert.False(PooledFileReader.TryParse(Path.Combine(_directory, "no-dir", "none.bin"), static data => data.Length, out _));

        string path = Path.Combine(_directory, "bad.bin");
        File.WriteAllBytes(path, [1, 2, 3]);
        Assert.Throws<InvalidDataException>(() => PooledFileReader.TryParse<int>(path, static _ => throw new InvalidDataException("bad"), out _));
    }

    [Fact]
    public void RepeatedReadOfALargeFile_AllocatesNoLargeArray()
    {
        const int Size = 1_200_000; // a typical populated .mmtile; anything over 85 KB is a large-object-heap array
        string path = Path.Combine(_directory, "tile.bin");
        File.WriteAllBytes(path, new byte[Size]);
        PooledFileReader.Parser<int> sum = static data => data.Length;

        // The control: the read this replaces allocates the whole file every time.
        long before = GC.GetAllocatedBytesForCurrentThread();
        _ = File.ReadAllBytes(path);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before >= Size);

        Assert.True(PooledFileReader.TryParse(path, sum, out _)); // the pool's first buffer of this size class
        for (int i = 0; i < 5; i++)
        {
            before = GC.GetAllocatedBytesForCurrentThread();
            Assert.True(PooledFileReader.TryParse(path, sum, out int length));
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(Size, length);
            Assert.True(allocated < 32 * 1024, $"a repeated read allocated {allocated} bytes (the file stream only is expected)");
        }
    }
}
