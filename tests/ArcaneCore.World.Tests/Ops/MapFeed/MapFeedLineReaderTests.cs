using System.Text;
using ArcaneCore.World.Ops.MapFeed;
using Xunit;

namespace ArcaneCore.World.Tests.Ops.MapFeed;

/// <summary>
/// The bounded line reader under every connection: a peer that never sends a newline costs at most one buffer, lines split across reads
/// come out whole, and the limit is exact.
/// </summary>
public sealed class MapFeedLineReaderTests
{
    private const int Max = 64;

    /// <summary>A stream that hands out at most <c>chunk</c> bytes per read, then end of input.</summary>
    private sealed class ChunkedStream(byte[] data, int chunk) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int count = Math.Min(Math.Min(chunk, buffer.Length), data.Length - _position);
            data.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return ValueTask.FromResult(count);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A stream of endless 'x' that counts what was read.</summary>
    private sealed class EndlessStream : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            buffer.Span.Fill((byte)'x');
            BytesRead += buffer.Length;
            return ValueTask.FromResult(buffer.Length);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<List<string>> ReadAllAsync(Stream stream)
    {
        var reader = new MapFeedLineReader(stream, Max);
        var lines = new List<string>();
        while (true)
        {
            LineRead read = await reader.ReadLineAsync(CancellationToken.None);
            switch (read.Status)
            {
                case LineStatus.Line:
                    lines.Add(Encoding.UTF8.GetString(read.Line.Span));
                    break;
                case LineStatus.EndOfInput:
                    return lines;
                default:
                    lines.Add("<too long>");
                    return lines;
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task LinesSplitAcrossReads_ComeOutWhole_WithTheCarriageReturnDropped(int chunk)
    {
        byte[] data = Encoding.UTF8.GetBytes("alpha\nbeta\r\n\ngamma é世\nlast without newline");
        List<string> lines = await ReadAllAsync(new ChunkedStream(data, chunk));
        Assert.Equal(["alpha", "beta", "", "gamma é世", "last without newline"], lines);
    }

    [Fact]
    public async Task ALineOfExactlyTheLimit_IsServed_AndOneByteMoreIsTooLong()
    {
        string exact = new('a', Max);
        Assert.Equal([exact, "after"], await ReadAllAsync(new ChunkedStream(Encoding.UTF8.GetBytes(exact + "\nafter\n"), 5)));

        // A '\r' before the newline is not part of the line, so the limit counts the line without it.
        Assert.Equal([exact], await ReadAllAsync(new ChunkedStream(Encoding.UTF8.GetBytes(exact + "\r\n"), 5)));

        string over = new('a', Max + 1);
        Assert.Equal(["<too long>"], await ReadAllAsync(new ChunkedStream(Encoding.UTF8.GetBytes(over + "\n"), 5)));
        Assert.Equal(["<too long>"], await ReadAllAsync(new ChunkedStream(Encoding.UTF8.GetBytes(over + "\r\n"), 5)));
    }

    [Fact]
    public async Task APeerThatNeverSendsANewline_IsCutOff_AfterOneBuffer()
    {
        var endless = new EndlessStream();
        var reader = new MapFeedLineReader(endless, Max);

        LineRead read = await reader.ReadLineAsync(CancellationToken.None);

        Assert.Equal(LineStatus.TooLong, read.Status);
        // The buffer is the limit, one byte of slack and one read chunk: the memory a hostile peer can make the daemon hold.
        Assert.True(endless.BytesRead <= Max + 1 + 1024, $"{endless.BytesRead} bytes were read before the cut");
        Assert.True(endless.BytesRead > Max, "the reader gave up before the limit");
    }

    [Fact]
    public async Task AnEmptyStream_IsEndOfInput()
        => Assert.Empty(await ReadAllAsync(new MemoryStream()));

    [Fact]
    public async Task APeerThatClosesInTheMiddleOfALine_GetsThatLineServed_ThenEndOfInput()
    {
        var reader = new MapFeedLineReader(new MemoryStream(Encoding.UTF8.GetBytes("one\ntwo")), Max);

        // A line is a view into the reader's buffer, valid until the next call: read each before asking for the next.
        LineRead first = await reader.ReadLineAsync(CancellationToken.None);
        Assert.Equal(LineStatus.Line, first.Status);
        Assert.Equal("one", Encoding.UTF8.GetString(first.Line.Span));
        LineRead second = await reader.ReadLineAsync(CancellationToken.None);
        Assert.Equal(LineStatus.Line, second.Status);
        Assert.Equal("two", Encoding.UTF8.GetString(second.Line.Span));
        LineRead third = await reader.ReadLineAsync(CancellationToken.None);
        Assert.Equal(LineStatus.EndOfInput, third.Status);
    }

    /// <summary>A stream whose reads never complete until they are cancelled (a client that is connected and silent).</summary>
    private sealed class SilentStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task AReadThatIsCancelled_ThrowsOperationCancelled()
    {
        var reader = new MapFeedLineReader(new SilentStream(), Max);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.ReadLineAsync(cancel.Token));
    }
}
