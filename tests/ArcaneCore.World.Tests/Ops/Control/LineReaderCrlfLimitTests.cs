using System.IO.Pipes;
using System.Text;
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
        var logs = new WorldControlPipeTests.CapturingLoggerProvider();
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
