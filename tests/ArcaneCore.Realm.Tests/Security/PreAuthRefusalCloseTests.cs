using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ArcaneCore.Kernel;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Net;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.Realm.Net;
using ArcaneCore.Realm.Protocol;
using ArcaneCore.TestSupport;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Realm.Tests.Security;

/// <summary>
/// Pre-auth refusals close the connection. A realm list requested before authentication and a logon challenge
/// that fails to parse were refused but left the socket open, so one connection could repeat either without end
/// (the realm-list case wrote an unthrottled warning per request). The reference authservers end the connection
/// in both cases: a command sent in the wrong state, or a handler that refuses, closes the socket. After the fix
/// each refusal is answered (or not) exactly as before, then the session closes, so it is logged once.
/// </summary>
public sealed class PreAuthRefusalCloseTests
{
    private const string RealmListWarning = "realm list requested before authentication";

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(6);

    [Fact]
    public async Task RealmListBeforeAuth_ClosesTheConnection()
    {
        await using Harness h = Harness.Start();
        await h.Client.WriteAsync(RealmListRequest());

        Assert.True(await ReadsNothingThenClosesAsync(h.Client), "a realm list before authentication must close the connection without a reply");
    }

    [Fact]
    public async Task RepeatedRealmListBeforeAuth_LogsOneWarning()
    {
        const int Requests = 50;
        await using Harness h = Harness.Start();
        byte[] burst = new byte[Requests * 5];
        for (int i = 0; i < Requests; i++)
        {
            RealmListRequest().CopyTo(burst, i * 5);
        }

        await h.Client.WriteAsync(burst);

        // Give the session time to work through every request it is willing to read: it either closes
        // (the fix) or keeps reading and logging (the bug) until the budget runs out.
        await Task.WhenAny(h.SessionTask, Task.Delay(TimeSpan.FromSeconds(2)));

        int warnings = h.Log.Count(LogLevel.Warning, RealmListWarning);
        Assert.True(warnings == 1, $"expected exactly one pre-auth realm-list warning for {Requests} requests, got {warnings}");
    }

    [Fact]
    public async Task MalformedChallenge_RepliesThenClosesTheConnection()
    {
        await using Harness h = Harness.Start();

        // A frame that passes the size window, username-length cap and locale checks but fails
        // LogonChallengeRequest.TryParse: username_len 5 reaches past the 31-byte body (30 + 5 > 31).
        await h.Client.WriteAsync(UnparseableChallenge());

        byte[] reply = new byte[3];
        using (var cts = new CancellationTokenSource(Budget))
        {
            await h.Client.ReadExactlyAsync(reply, cts.Token);
        }

        Assert.Equal((byte)AuthCommand.LogonChallenge, reply[0]);
        Assert.Equal((byte)AuthResult.UnknownAccount, reply[2]);
        Assert.True(await ReadsNothingThenClosesAsync(h.Client), "a malformed logon challenge must close the connection after its failure reply");
    }

    [Fact]
    public async Task MalformedChallenge_IsLoggedOnce_AndTheSessionEnds()
    {
        await using Harness h = Harness.Start();

        // One challenge only: a second one written after the server closed would leave unread bytes behind, and the
        // resulting reset can discard the reply on a loaded host.
        await h.Client.WriteAsync(UnparseableChallenge());

        byte[] reply = new byte[3];
        using (var cts = new CancellationTokenSource(Budget))
        {
            await h.Client.ReadExactlyAsync(reply, cts.Token);
        }

        Assert.Equal((byte)AuthResult.UnknownAccount, reply[2]);
        Assert.True(await ReadsNothingThenClosesAsync(h.Client), "the connection must close after the refusal");
        Assert.True(await Task.WhenAny(h.SessionTask, Task.Delay(Budget)) == h.SessionTask, "the session must end after a malformed challenge");
        Assert.Equal(1, h.Log.Count(LogLevel.Warning, "malformed logon challenge"));
    }

    [Fact]
    public async Task RealmListBeforeAuth_ChargesTheAddressFailureBudget()
    {
        // Frozen clock: the budget refills with time, so a real clock would drift the exact token count.
        var guard = new NetGuard(
            new NetProtectionOptions { AuthFailureBurstPerIp = 5, AuthFailuresPerMinutePerIp = 1 }, () => 0, () => 0, new CapturingLogger(), clock: () => 0);
        IpKey key = IpKey.From(IPAddress.Loopback);
        Assert.Equal(5.0, guard.Table.TokensOf(key, RateBucket.AuthFailures));

        await using Harness h = Harness.Start(guard, "127.0.0.1:50000");
        await h.Client.WriteAsync(RealmListRequest());

        Assert.True(await ReadsNothingThenClosesAsync(h.Client), "a realm list before authentication must close the connection");
        Assert.True(await Task.WhenAny(h.SessionTask, Task.Delay(Budget)) == h.SessionTask, "the session must end");
        Assert.Equal(4.0, guard.Table.TokensOf(key, RateBucket.AuthFailures));
    }

    // --- plumbing ----------------------------------------------------------------

    private static byte[] RealmListRequest() => [(byte)AuthCommand.RealmList, 0, 0, 0, 0];

    /// <summary>
    /// A 31-byte body (inside the 31..47 window) whose username_len of 5 reaches past the body, so the size, length-cap
    /// and locale checks pass and <see cref="LogonChallengeRequest.TryParse"/> returns false.
    /// </summary>
    private static byte[] UnparseableChallenge()
    {
        byte[] packet = Challenge(Encoding.ASCII.GetBytes("A"));
        packet[4 + 29] = 5; // username_len beyond the body: 30 + 5 > 31
        return packet;
    }

    private static byte[] Challenge(byte[] name)
    {
        int bodySize = 30 + name.Length;
        byte[] body = new byte[bodySize];
        "WoW\0"u8.CopyTo(body);
        body[4] = 1;
        body[5] = 12;
        body[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(7), ClientBuild.Vanilla1121);
        "68x\0"u8.CopyTo(body.AsSpan(9));
        "niW\0"u8.CopyTo(body.AsSpan(13));
        "SUne"u8.CopyTo(body.AsSpan(17)); // enUS, reversed on the wire
        body[29] = (byte)name.Length;
        name.CopyTo(body.AsSpan(30));

        byte[] packet = new byte[4 + bodySize];
        packet[0] = (byte)AuthCommand.LogonChallenge;
        packet[1] = 0x08;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)bodySize);
        body.CopyTo(packet.AsSpan(4));
        return packet;
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
            return true; // a reset counts as closed
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly TcpClient _client;

        private Harness(TcpClient client, Task sessionTask, LevelLogger log)
        {
            _client = client;
            SessionTask = sessionTask;
            Log = log;
            Client = client.GetStream();
        }

        public NetworkStream Client { get; }

        public Task SessionTask { get; }

        public LevelLogger Log { get; }

        public static Harness Start(NetGuard? guard = null, string endpoint = "test")
        {
            var log = new LevelLogger();
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task sessionTask = Task.Run(async () =>
            {
                using TcpClient server = await listener.AcceptTcpClientAsync();
                listener.Stop();
                await using NetworkStream s = server.GetStream();
                var session = new LogonSession(
                    s, new InMemoryAccountStore(), new InMemoryRealmStore([]),
                    new AuthOptions { MaxSessionDurationSeconds = 0 }, log, endpoint, banStore: null, guard);
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
            return new Harness(client, sessionTask, log);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            _client.Dispose();
        }
    }

    private sealed class LevelLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public int Count(LogLevel level, string fragment)
        {
            lock (_entries)
            {
                return _entries.Count(e => e.Level == level && e.Message.Contains(fragment, StringComparison.Ordinal));
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
