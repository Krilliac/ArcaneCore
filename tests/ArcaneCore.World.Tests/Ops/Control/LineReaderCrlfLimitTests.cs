using System.IO.Pipes;
using System.Text;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Ops.Control;
using ArcaneCore.World.Ops.RemoteAdmin;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.World.Tests.Ops.Control;

/// <summary>
/// The line limit counts the content of a line, not its CRLF terminator: a line of exactly the maximum length is accepted whether it ends in
/// LF or CRLF (as MapFeedLineReader does), one byte more is rejected, and a peer that never sends a newline is still cut off.
/// </summary>
public sealed class LineReaderCrlfLimitTests
{
    private const int Max = 8;

    private static RemoteAdminServer.LineReader RaReader(byte[] input) => new(new MemoryStream(input), Max);

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>A stream that returns each chunk from its own ReadAsync call, so a telnet sequence can be split across reads.</summary>
    private sealed class ChunkedStream(params byte[][] chunks) : Stream
    {
        private int _next;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_next >= chunks.Length)
            {
                return ValueTask.FromResult(0);
            }

            byte[] chunk = chunks[_next++];
            chunk.CopyTo(buffer);
            return ValueTask.FromResult(chunk.Length);
        }
    }

    private static async Task<string?> ReadOneLineAsync(params byte[][] chunks)
    {
        var reader = new RemoteAdminServer.LineReader(new ChunkedStream(chunks), 64);
        (string? line, bool tooLong) = await reader.ReadLineAsync(CancellationToken.None);
        Assert.False(tooLong);
        return line;
    }

    [Fact]
    public async Task RemoteAdmin_TelnetNegotiationSplitAfterIac_DoesNotLeakTheOptionBytes()
        => Assert.Equal("user", await ReadOneLineAsync([0xFF], [0xFB, 0x01, (byte)'u', (byte)'s', (byte)'e', (byte)'r', (byte)'\n']));

    [Fact]
    public async Task RemoteAdmin_TelnetNegotiationSplitAfterTheCommand_DoesNotLeakTheOptionByte()
        => Assert.Equal("user", await ReadOneLineAsync([0xFF, 0xFB], [0x01, (byte)'u', (byte)'s', (byte)'e', (byte)'r', (byte)'\n']));

    [Fact]
    public async Task RemoteAdmin_TelnetNegotiationInOneRead_IsDroppedAsBefore()
        => Assert.Equal("user", await ReadOneLineAsync([0xFF, 0xFD, 0x03, (byte)'u', (byte)'s', (byte)'e', (byte)'r', (byte)'\n']));

    [Fact]
    public async Task RemoteAdmin_EscapedIac_IsOneLiteral0xFFByte_AndKeepsTheNextDataByte()
    {
        byte[] wire = [(byte)'a', 0xFF, 0xFF, (byte)'b', (byte)'\n'];
        string? line = await ReadOneLineAsync(wire);
        Assert.Equal(Encoding.UTF8.GetString([(byte)'a', 0xFF, (byte)'b']), line);
    }

    [Fact]
    public async Task RemoteAdmin_Subnegotiation_IsDroppedWholeEvenSplitAcrossReads()
        => Assert.Equal("go", await ReadOneLineAsync(
            [0xFF, 0xFA, 0x1F, 0x00],
            [0x50, 0x00, 0x18, 0xFF],
            [0xF0, (byte)'g', (byte)'o', (byte)'\n']));

    [Theory]
    [InlineData("abcdefgh\n")]
    [InlineData("abcdefgh\r\n")]
    public async Task RemoteAdmin_AcceptsALineOfExactlyTheLimit(string wire)
    {
        var reader = RaReader(Bytes(wire));

        (string? line, bool tooLong) = await reader.ReadLineAsync(CancellationToken.None);

        Assert.False(tooLong);
        Assert.Equal("abcdefgh", line);
    }

    [Theory]
    [InlineData("abcdefghi\n")]
    [InlineData("abcdefghi\r\n")]
    public async Task RemoteAdmin_RejectsALineOneByteOverTheLimit(string wire)
    {
        var reader = RaReader(Bytes(wire));

        (string? line, bool tooLong) = await reader.ReadLineAsync(CancellationToken.None);

        Assert.True(tooLong);
        Assert.Null(line);
    }

    [Fact]
    public async Task RemoteAdmin_RejectsAnUnterminatedLineOneByteOverTheLimitAtEndOfInput()
    {
        var reader = RaReader(Bytes("abcdefghi"));

        (string? line, bool tooLong) = await reader.ReadLineAsync(CancellationToken.None);

        Assert.True(tooLong);
        Assert.Null(line);
    }

    [Fact]
    public async Task RemoteAdmin_StillCutsOffAPeerThatNeverSendsANewline()
    {
        var reader = RaReader(Bytes(new string('x', 100)));

        (string? line, bool tooLong) = await reader.ReadLineAsync(CancellationToken.None);

        Assert.True(tooLong);
        Assert.Null(line);
    }

    [Fact]
    public async Task RemoteAdmin_DoesNotCountTelnetNegotiationOrNulTowardTheLimit()
    {
        byte[] wire = [0xFF, 0xFB, 0x01, .. Bytes("abcd"), 0x00, .. Bytes("efgh"), (byte)'\r', (byte)'\n'];
        var reader = RaReader(wire);

        (string? line, bool tooLong) = await reader.ReadLineAsync(CancellationToken.None);

        Assert.False(tooLong);
        Assert.Equal("abcdefgh", line);
    }

    [WorldControlPipeTests.WindowsFact]
    public async Task ControlPipe_AcceptsACrlfLineOfExactlyTheLimit()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var host = WorldTestHost.Start();
        var logs = new CapturingLoggerProvider();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(logs));
        WorldControlPipe pipe = WorldControlPipeTests.NewPipe(
            host, new WorldControlPipeOptions { Enabled = true, Name = WorldControlPipeTests.UniqueName(), MaxLineLength = 64 }, loggers);
        await pipe.StartAsync(CancellationToken.None);
        try
        {
            // 64 bytes of content, then CRLF.
            string command = "reload status " + new string('x', 50);
            Assert.Equal(64, command.Length);

            await using var client = new NamedPipeClientStream(".", pipe.PipeName!, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(5000);
            await client.WriteAsync(Bytes(command + "\r\n"));
            await client.FlushAsync();

            using var reader = new StreamReader(client, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var replies = new List<string>();
            while (true)
            {
                string? reply = await reader.ReadLineAsync(timeout.Token);
                Assert.NotNull(reply);
                if (reply == WorldControlPipe.DoneMarker)
                {
                    break;
                }

                replies.Add(reply);
            }

            Assert.DoesNotContain(replies, r => r.Contains("line longer than", StringComparison.Ordinal));
        }
        finally
        {
            await pipe.StopAsync(CancellationToken.None);
        }
    }
}
