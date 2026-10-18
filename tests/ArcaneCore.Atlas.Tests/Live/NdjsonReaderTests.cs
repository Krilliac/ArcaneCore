using System.Text;
using ArcaneCore.Atlas.Live;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Live;

public sealed class NdjsonReaderTests
{
    /// <summary>A stream that returns its bytes in fixed-size pieces, so every buffer boundary is exercised.</summary>
    private sealed class PiecewiseStream(byte[] data, int piece) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Take(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Take(buffer.Span));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Take(Span<byte> destination)
        {
            int n = Math.Min(Math.Min(piece, destination.Length), data.Length - _position);
            data.AsSpan(_position, n).CopyTo(destination);
            _position += n;
            return n;
        }
    }

    private static async Task<List<(NdjsonKind Kind, string Text, long Length)>> ReadAllAsync(byte[] data, int piece, int limit, int bufferSize = 64)
    {
        var reader = new NdjsonReader(new PiecewiseStream(data, piece), limit, bufferSize);
        var items = new List<(NdjsonKind, string, long)>();
        while (true)
        {
            NdjsonItem item = await reader.ReadAsync(CancellationToken.None);
            items.Add((item.Kind, Encoding.UTF8.GetString(item.Line.Span), item.Length));
            if (item.Kind is NdjsonKind.End or NdjsonKind.Truncated)
            {
                Assert.Equal(data.Length, reader.BytesRead);
                return items;
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(64)]
    [InlineData(10_000)]
    public async Task Lines_AreSplitTheSameWhateverTheReadSizes(int piece)
    {
        byte[] data = Encoding.UTF8.GetBytes("{\"a\":1}\n{\"b\":\"héllo\"}\r\n\n  \n{\"c\":3}\n");

        var items = await ReadAllAsync(data, piece, limit: 1024);

        Assert.Equal(
            new[] { "{\"a\":1}", "{\"b\":\"héllo\"}", "  ", "{\"c\":3}" },
            items.Where(i => i.Kind == NdjsonKind.Line).Select(i => i.Text));
        Assert.Equal(NdjsonKind.End, items[^1].Kind);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(100)]
    public async Task ALineAtTheLimitPasses_OneByteOverIsSkippedAndReadingResumes(int piece)
    {
        const int Limit = 40;
        string exact = new('a', Limit);
        string over = new('b', Limit + 1);
        byte[] data = Encoding.UTF8.GetBytes(exact + "\n" + over + "\nafter\n");

        var items = await ReadAllAsync(data, piece, Limit, bufferSize: 16);

        Assert.Equal(NdjsonKind.Line, items[0].Kind);
        Assert.Equal(exact, items[0].Text);
        Assert.Equal(NdjsonKind.Oversize, items[1].Kind);
        Assert.Equal(Limit + 1, items[1].Length);
        Assert.Equal(NdjsonKind.Line, items[2].Kind);
        Assert.Equal("after", items[2].Text);
        Assert.Equal(NdjsonKind.End, items[3].Kind);
    }

    [Fact]
    public async Task AnEndlessLineWithoutNewline_IsDiscardedNotBuffered()
    {
        // 5 MiB of one line against a 1 KiB limit: the reader must report it oversize once the newline arrives and never hold it.
        byte[] junk = new byte[5 * 1024 * 1024];
        Array.Fill(junk, (byte)'x');
        byte[] tail = Encoding.UTF8.GetBytes("\nok\n");
        byte[] data = [.. junk, .. tail];

        var items = await ReadAllAsync(data, piece: 4096, limit: 1024, bufferSize: 8192);

        Assert.Equal(NdjsonKind.Oversize, items[0].Kind);
        Assert.Equal(junk.Length, items[0].Length);
        Assert.Equal("ok", items[1].Text);
    }

    [Fact]
    public async Task ALineCutOffByTheEndOfTheStream_IsReportedTruncatedNeverParsed()
    {
        var items = await ReadAllAsync(Encoding.UTF8.GetBytes("{\"a\":1}\n{\"half\":"), piece: 4, limit: 1024);

        Assert.Equal(NdjsonKind.Line, items[0].Kind);
        Assert.Equal(NdjsonKind.Truncated, items[1].Kind);
        Assert.Equal("{\"half\":".Length, items[1].Length);
    }

    [Fact]
    public async Task AnOversizeLineCutOffByTheEndOfTheStream_IsReportedTruncated()
    {
        byte[] data = new byte[300];
        Array.Fill(data, (byte)'z');

        var items = await ReadAllAsync(data, piece: 50, limit: 100);

        Assert.Equal(NdjsonKind.Truncated, items[0].Kind);
        Assert.Equal(300, items[0].Length);
    }

    [Fact]
    public async Task AnEmptyStream_EndsImmediately()
    {
        var items = await ReadAllAsync([], piece: 8, limit: 100);

        Assert.Equal(NdjsonKind.End, Assert.Single(items).Kind);
    }

    [Fact]
    public void TheDefaultLimitIsEightMebibytes()
    {
        Assert.Equal(8 * 1024 * 1024, LiveOptions.Default.MaxLineBytes);
    }
}
