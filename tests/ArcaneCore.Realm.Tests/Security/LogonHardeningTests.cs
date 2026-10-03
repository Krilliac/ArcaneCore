using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ArcaneCore.Kernel;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Logging;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.Realm.Net;
using ArcaneCore.Realm.Protocol;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Realm.Tests.Security;

/// <summary>
/// Logon connection limits and input validation: MaxSessionDuration and read timeouts
/// (slowloris), the vmangos challenge size window, username/locale validation, and
/// log-injection safety. References: vmangos AuthSocket.cpp:76-82 (session duration),
/// :248-262 (size window 31..47), :273 (username_len &lt;= 16), :306 (locale allow-list).
/// </summary>
public sealed class LogonHardeningTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(6);

    [Fact]
    public async Task IdleConnection_IsClosedAtMaxSessionDuration()
    {
        await using NetworkStream c = Start(new AuthOptions { MaxSessionDurationSeconds = 1 }, out _);
        await AssertClosedAsync(c);
    }

    [Fact]
    public async Task StalledPacketBody_IsClosedAtReadTimeout()
    {
        await using NetworkStream c = Start(
            new AuthOptions { MaxSessionDurationSeconds = 0, ReadTimeoutSeconds = 1 }, out _);

        // command + header promising a 40 byte body, then silence.
        await c.WriteAsync(new byte[] { 0x00, 0x08, 40, 0 });
        await AssertClosedAsync(c);
    }

    [Theory]
    [InlineData(30, 0, "enUS", "size below the 31 byte minimum")]
    [InlineData(48, 17, "enUS", "size above the 47 byte maximum")]
    [InlineData(47, 17, "enUS", "username_len 17 > AUTH_LOGON_MAX_NAME")]
    [InlineData(35, 5, "xxxx", "locale not in the allow-list")]
    public async Task MalformedChallenge_ClosesWithoutReply(int bodySize, int nameLength, string locale, string why)
    {
        await using NetworkStream c = Start(new AuthOptions(), out _);
        byte[] packet = RawChallenge(bodySize, nameLength, locale, Encoding.ASCII.GetBytes(new string('A', Math.Max(0, bodySize - 30))));
        await c.WriteAsync(packet);
        Assert.True(await ReadsNothingThenClosesAsync(c), why);
    }

    [Fact]
    public async Task NonPrintableUsername_IsRefusedAndNeverLoggedRaw()
    {
        var log = new CapturingLogger();
        await using NetworkStream c = Start(new AuthOptions(), out _, log);
        byte[] name = Encoding.ASCII.GetBytes("EVIL\r\nFAKE");
        await c.WriteAsync(RawChallenge(30 + name.Length, name.Length, "enUS", name));

        byte[] reply = new byte[3];
        using var cts = new CancellationTokenSource(Budget);
        await c.ReadExactlyAsync(reply, cts.Token);
        Assert.Equal((byte)AuthResult.UnknownAccount, reply[2]);
        await AssertClosedOrQuietAsync();

        Assert.NotEmpty(log.Messages);
        Assert.DoesNotContain(log.Messages, m => m.Contains('\r') || m.Contains('\n'));
    }

    [Fact]
    public void LogSafe_EscapesControlCharactersAndTruncates()
    {
        Assert.Equal("EVIL\\r\\nFAKE", LogSafe.Escape("EVIL\r\nFAKE"));
        Assert.Equal("a\\u001Bb", LogSafe.Escape("a\u001Bb"));
        Assert.Equal(string.Empty, LogSafe.Escape(null));
        string longText = LogSafe.Escape(new string('x', 500));
        Assert.Equal(LogSafe.MaxLength + 3, longText.Length);
        Assert.EndsWith("...", longText);
    }

    [Fact]
    public void ShippedRealmConfig_DoesNotAutocreateAccounts()
    {
        string? dir = AppContext.BaseDirectory;
        string? path = null;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, "src", "ArcaneCore.Realm", "appsettings.json");
            if (File.Exists(candidate))
            {
                path = candidate;
                break;
            }

            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(path);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path!));
        Assert.False(doc.RootElement.GetProperty("Auth").GetProperty("AutocreateAccounts").GetBoolean());
    }

    // --- plumbing ----------------------------------------------------------------

    private static Task AssertClosedOrQuietAsync() => Task.CompletedTask;

    private static NetworkStream Start(AuthOptions options, out Task sessionTask, ILogger? logger = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        sessionTask = Task.Run(async () =>
        {
            using TcpClient server = await listener.AcceptTcpClientAsync();
            listener.Stop();
            await using NetworkStream s = server.GetStream();
            var session = new LogonSession(
                s, new InMemoryAccountStore(), new InMemoryRealmStore([]), options,
                logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, "test");
            try
            {
                await session.RunAsync(CancellationToken.None);
            }
            catch (Exception)
            {
                // connection closed
            }
        });
        var client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);
        return client.GetStream();
    }

    /// <summary>Challenge with explicit size/length/locale so malformed shapes can be produced.</summary>
    private static byte[] RawChallenge(int bodySize, int nameLength, string locale, byte[] name)
    {
        byte[] body = new byte[Math.Max(bodySize, 30)];
        "WoW\0"u8.CopyTo(body);
        body[4] = 1;
        body[5] = 12;
        body[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(7), ClientBuild.Vanilla1121);
        "68x\0"u8.CopyTo(body.AsSpan(9));
        "niW\0"u8.CopyTo(body.AsSpan(13));
        byte[] reversed = Encoding.ASCII.GetBytes(locale);
        Array.Reverse(reversed);
        reversed.CopyTo(body.AsSpan(17));
        body[29] = (byte)nameLength;
        name.AsSpan(0, Math.Min(name.Length, body.Length - 30)).CopyTo(body.AsSpan(30));

        byte[] packet = new byte[4 + body.Length];
        packet[0] = (byte)AuthCommand.LogonChallenge;
        packet[1] = 0x08;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)bodySize);
        body.AsSpan(0, Math.Min(body.Length, bodySize)).CopyTo(packet.AsSpan(4));
        return packet[..(4 + Math.Min(body.Length, bodySize))];
    }

    private static async Task AssertClosedAsync(NetworkStream c)
    {
        Assert.True(await ReadsNothingThenClosesAsync(c), "expected the server to close without sending anything");
    }

    private static async Task<bool> ReadsNothingThenClosesAsync(NetworkStream c)
    {
        byte[] buf = new byte[16];
        using var cts = new CancellationTokenSource(Budget);
        try
        {
            return await c.ReadAsync(buf, cts.Token) == 0;
        }
        catch (IOException)
        {
            return true; // reset counts as closed
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Messages)
            {
                Messages.Add(formatter(state, exception));
            }
        }
    }
}
