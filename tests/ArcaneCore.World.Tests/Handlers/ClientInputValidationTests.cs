using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Net;
using ArcaneCore.Kernel.WorldData.Chat;
using ArcaneCore.Protocol;
using ArcaneCore.World.AntiCheat;
using ArcaneCore.World.Chat;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Handlers;

/// <summary>
/// Client-input validation in the world handlers: the interact slack clamp, AFK/DND text sanitising, chat length caps,
/// bug-report log escaping, account data write throttling, client-triggerable warning throttles and the pre-auth ping budget.
/// </summary>
public sealed class ClientInputValidationTests
{
    [Fact]
    public void InteractSlack_ClampsTheClientReportedLatency_LikeMovement()
    {
        var options = new AntiCheatOptions();
        float clamped = AntiCheatFeature.InteractSlack(7f, 60_000, options);
        Assert.True(clamped <= 20f + (7f * options.MaxLatencySlackMs / 1000f), $"slack {clamped} yd");
        Assert.Equal(20f + (7f * 2f * 100 / 1000f), AntiCheatFeature.InteractSlack(7f, 100, options));
        Assert.Equal(20f, AntiCheatFeature.InteractSlack(7f, -5, options));
    }

    [Fact]
    public void AwayText_IsStrippedLinkCheckedFilteredAndCapped()
    {
        var options = new ChatOptions { FakeMessagePreventing = true, StrictLinkSeverity = 2, WordFilter = true, MaxAwayMessageLength = 12 };
        var filter = new ChatWordFilter([new ChatWordFilterRule(1, "dang", ChatWordFilterScope.Chat, ChatWordFilterAction.Censor)]);

        Assert.Equal("be right back", ChatHandlers.SanitizeAwayText("be  \n right back", new ChatOptions { FakeMessagePreventing = true }, null));
        Assert.Null(ChatHandlers.SanitizeAwayText("away |Zbogus", options, filter));
        Assert.Equal("****", ChatHandlers.SanitizeAwayText("dang", options, filter));
        Assert.Equal(12, ChatHandlers.SanitizeAwayText(new string('x', 400), options, filter)!.Length);
        Assert.Equal(string.Empty, ChatHandlers.SanitizeAwayText(string.Empty, options, filter));
    }

    [Fact]
    public void TruncateAtSafeBoundary_NeverSplitsASurrogatePairALinkOrAnEscapedPipe()
    {
        const string link = "|cffffffff|Hitem:1|h[Sword]|h|r"; // 31 units
        Assert.Equal(31, link.Length);

        Assert.Equal("short", ChatSanitizer.TruncateAtSafeBoundary("short", 5));
        Assert.Equal("abc", ChatSanitizer.TruncateAtSafeBoundary("abcdef", 3));
        Assert.Equal(string.Empty, ChatSanitizer.TruncateAtSafeBoundary("abc", 0));

        // A surrogate pair ("\U0001F600") is kept whole or dropped whole.
        Assert.Equal("ab", ChatSanitizer.TruncateAtSafeBoundary("ab\U0001F600cd", 3));
        Assert.Equal("ab\U0001F600", ChatSanitizer.TruncateAtSafeBoundary("ab\U0001F600cd", 4));

        // A colour/hyperlink sequence is kept whole or dropped whole, wherever the cut falls inside it.
        string text = "xx" + link + "tail";
        for (int max = 2; max < 2 + link.Length; max++)
        {
            Assert.Equal("xx", ChatSanitizer.TruncateAtSafeBoundary(text, max));
        }

        Assert.Equal("xx" + link, ChatSanitizer.TruncateAtSafeBoundary(text, 2 + link.Length));
        Assert.Equal("xx" + link + "t", ChatSanitizer.TruncateAtSafeBoundary(text, 2 + link.Length + 1));

        // A cut between the two pipes of an escaped pipe, or right after a lone pipe, backs off the pipe.
        Assert.Equal("abc", ChatSanitizer.TruncateAtSafeBoundary("abc||def", 4));
        Assert.Equal("abc||", ChatSanitizer.TruncateAtSafeBoundary("abc||def", 5));

        // Whatever the cut, the result still passes the link check at the strictest grammar severity.
        for (int max = 0; max <= text.Length; max++)
        {
            string cut = ChatSanitizer.TruncateAtSafeBoundary(text, max);
            Assert.True(cut.Length <= max);
            Assert.True(ChatSanitizer.IsValidChatMessage(cut, 2), $"cut at {max}: {cut}");
            Assert.False(cut.Length > 0 && char.IsHighSurrogate(cut[^1]));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => ChatSanitizer.TruncateAtSafeBoundary("abc", -1));
    }

    [Fact]
    public void AwayText_CutInsideALink_DropsTheWholeLink()
    {
        var options = new ChatOptions { StrictLinkSeverity = 2, MaxAwayMessageLength = 12 };
        string cut = ChatHandlers.SanitizeAwayText("back |cffffffff|Hitem:1|h[Sword]|h|r soon", options, null)!;
        Assert.Equal("back ", cut);
    }

    [Fact]
    public async Task AwayText_OverTheGeneralCap_IsCutByTheAwayCap_NotDropped()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("AFKCAP", "Afkcap");
        await client.CollectAsync();

        ChatOptions options = host.WorldServices.GetRequiredService<ChatFeature>().Options;
        Assert.Equal(options.MaxMessageLength, options.MaxAwayMessageLength); // the shipped defaults: both 1024
        await client.SendChatAsync(ChatType.Afk, Language.Universal, new string('a', options.MaxMessageLength + 400));

        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Afkcap")!.IsAfk, "the player to go AFK");
        string stored = await host.PlayerStateAsync("Afkcap", p => p.AfkMessage);
        Assert.Equal(options.MaxAwayMessageLength, stored.Length);
    }

    [Theory]
    [InlineData(ChatType.Afk)]
    [InlineData(ChatType.Dnd)]
    public async Task AwayText_WithABadLink_StillTogglesTheState_WithAnEmptyText(ChatType type)
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("AFKLINK" + (int)type, "Afklink" + (type == ChatType.Afk ? "a" : "d"));
        await client.CollectAsync();
        string name = "Afklink" + (type == ChatType.Afk ? "a" : "d");

        ChatOptions options = host.WorldServices.GetRequiredService<ChatFeature>().Options;
        options.StrictLinkSeverity = 2;
        await client.SendChatAsync(type, Language.Universal, "gone |Zbogus");

        await host.WaitForWorldAsync(() => type == ChatType.Afk
            ? host.World.FindOnlinePlayer(name)!.IsAfk
            : host.World.FindOnlinePlayer(name)!.IsDnd, "the state to be set although the text was refused");
        string stored = await host.PlayerStateAsync(name, p => type == ChatType.Afk ? p.AfkMessage : p.DndMessage);
        Assert.Equal(string.Empty, stored);
    }

    [Theory]
    [InlineData(ChatType.Afk)]
    [InlineData(ChatType.Dnd)]
    public async Task AwayText_Refused_WhileAlreadyAway_KeepsTheStateAndTheOldText(ChatType type)
    {
        string name = type == ChatType.Afk ? "Keepafk" : "Keepdnd";
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("KEEP" + (int)type, name);
        await client.CollectAsync();

        await client.SendChatAsync(type, Language.Universal, "back soon");
        await host.WaitForWorldAsync(() => type == ChatType.Afk ? host.World.FindOnlinePlayer(name)!.IsAfk : host.World.FindOnlinePlayer(name)!.IsDnd, "the state to be set");

        host.WorldServices.GetRequiredService<ChatFeature>().Options.StrictLinkSeverity = 2;
        await client.SendChatAsync(type, Language.Universal, "gone |Zbogus");
        await client.SendChatAsync(ChatType.Say, Language.Common, "marker"); // handled after the refused text, on the same world thread queue
        Assert.Equal("marker", (await client.ReadChatAsync()).Text);

        (bool away, string text) = await host.PlayerStateAsync(name, p => type == ChatType.Afk ? (p.IsAfk, p.AfkMessage) : (p.IsDnd, p.DndMessage));
        Assert.True(away);
        Assert.Equal("back soon", text);
    }

    [Fact]
    public async Task ChatMessage_OverTheConfiguredCap_IsDropped()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("CHATCAP", "Chatcap");
        await client.CollectAsync();

        await client.SendChatAsync(ChatType.Say, Language.Common, new string('a', 1100));
        await client.SendChatAsync(ChatType.Say, Language.Common, "short");
        Assert.Equal("short", (await client.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task BugReport_EscapesControlCharacters_InTheLogLine()
    {
        var lines = new ConcurrentQueue<string>();
        await using var host = WorldTestHost.Start(configureServices: c => c.AddSingleton<ILoggerFactory>(new LineLoggerFactory(lines)));
        await using WorldTestClient client = await host.EnterWorldAsync("BUGLOG", "Buglog");

        await client.SendAsync(WorldOpcode.CmsgBug, Bug("first\r\nFORGED server line", "Map\n"));
        await client.SendAsync(WorldOpcode.MsgLookingForGroup, []);
        await client.ReadUntilAsync(WorldOpcode.MsgLookingForGroup);

        string line = Assert.Single(lines, l => l.Contains("reported a", StringComparison.Ordinal));
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.Contains("first\\r\\nFORGED server line", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AccountData_UnchangedIsNotWritten_AndABurstIsCoalescedToTheLatestValue()
    {
        var store = new CountingAccountDataStore();
        await using var host = WorldTestHost.Start(configureServices: c => c.AddSingleton<IAccountDataStore>(store));
        WorldTestClient client = await AuthenticateAsync(host, "ACCDATA");
        await using (client)
        {
            byte[] v1 = Encoding.ASCII.GetBytes("SET a 1"), v2 = Encoding.ASCII.GetBytes("SET a 2"), v3 = Encoding.ASCII.GetBytes("SET a 3");
            await client.SendAsync(WorldOpcode.CmsgUpdateAccountData, AccountData(0, v1));
            await RoundTripAsync(client);
            await WorldTestHost.WaitForAsync(() => store.DataWrites == 1, "the first change to be written at once");

            await client.SendAsync(WorldOpcode.CmsgUpdateAccountData, AccountData(0, v1)); // unchanged: no write
            await client.SendAsync(WorldOpcode.CmsgUpdateAccountData, AccountData(0, v2));
            await client.SendAsync(WorldOpcode.CmsgUpdateAccountData, AccountData(0, v3));
            await RoundTripAsync(client);

            await WorldTestHost.WaitForAsync(() => store.Latest(0) is { } latest && latest.SequenceEqual(v3), "the latest blob to be written");
            await Task.Delay(300);
            Assert.Equal(2, store.DataWrites);
        }
    }

    [Fact]
    public async Task AccountData_OutOfRangeWarnings_AreThrottled()
    {
        var log = new LineLogger();
        await using var host = WorldTestHost.Start(sessionLogger: log);
        WorldTestClient client = await AuthenticateAsync(host, "ACCWARN");
        await using (client)
        {
            for (int i = 0; i < 6; i++)
            {
                await client.SendAsync(WorldOpcode.CmsgUpdateAccountData, AccountData(99, []));
            }

            await RoundTripAsync(client);
            Assert.Equal(1, log.Count("out of range"));
        }
    }

    [Fact]
    public async Task ActiveMoverMismatch_Warning_IsThrottled()
    {
        var log = new LineLogger();
        await using var host = WorldTestHost.Start(sessionLogger: log);
        await using WorldTestClient client = await host.EnterWorldAsync("MOVERWARN", "Moverwarn");
        for (int i = 0; i < 6; i++)
        {
            await client.SendAsync(WorldOpcode.CmsgSetActiveMover, BitConverter.GetBytes(0xF130000000DEAD00UL));
        }

        await client.SendAsync(WorldOpcode.MsgLookingForGroup, []);
        await client.ReadUntilAsync(WorldOpcode.MsgLookingForGroup);
        Assert.Equal(1, log.Count("is not the mover"));
    }

    [Fact]
    public async Task ForeignCharacterLogin_Warning_IsThrottled()
    {
        var log = new LineLogger();
        await using var host = WorldTestHost.Start(sessionLogger: log);
        WorldTestClient client = await AuthenticateAsync(host, "LOGINWARN");
        await using (client)
        {
            for (int i = 0; i < 4; i++)
            {
                await client.SendAsync(WorldOpcode.CmsgPlayerLogin, BitConverter.GetBytes(999_999UL));
                await client.ReadUntilAsync(WorldOpcode.SmsgCharacterLoginFailed);
            }

            Assert.Equal(1, log.Count("not owned"));
        }
    }

    [Fact]
    public async Task PingBeforeAuthentication_IsAnswered_ButUnderTheNetguardBudget()
    {
        await using var host = WorldTestHost.Start();
        var guard = new NetGuard(new NetProtectionOptions { WorldOpcodeBurst = 2, WorldOpcodeRefillPerSecond = 0, WorldPacketsPerSecond = 0, WorldFloodPacketsPerSecond = 0 },
            () => 0, () => 0, NullLogger.Instance);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = new CancellationTokenSource();
        Task session = Task.Run(async () =>
        {
            using TcpClient server = await listener.AcceptTcpClientAsync(stop.Token);
            await using NetworkStream stream = server.GetStream();
            await using AsyncServiceScope scope = host.WorldServices.CreateAsyncScope();
            var worldSession = new WorldSession(stream, "127.0.0.1:41001", scope.ServiceProvider, host.Opcodes, host.World, host.Registry,
                new WorldSessionOptions(), NullLogger.Instance, guard);
            await worldSession.RunAsync(stop.Token);
        });

        var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var client = new WorldTestClient(socket);
        try
        {
            await client.ReadUntilAsync(WorldOpcode.SmsgAuthChallenge);
            for (uint i = 1; i <= 5; i++)
            {
                byte[] ping = new byte[8];
                BinaryPrimitives.WriteUInt32LittleEndian(ping, i);
                await client.SendAsync(WorldOpcode.CmsgPing, ping);
            }

            await client.ReadUntilAsync(WorldOpcode.SmsgPong);
            List<(WorldOpcode Opcode, byte[] Payload)> rest = await client.CollectAsync(TimeSpan.FromMilliseconds(500));
            Assert.Equal(1, rest.Count(p => p.Opcode == WorldOpcode.SmsgPong));
            Assert.Equal(3, guard.DroppedPackets);
        }
        finally
        {
            await client.DisposeAsync();
            stop.Cancel();
            listener.Stop();
            await session.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    [Fact]
    public async Task PingAfterAuthentication_IsNotUnderThePreAuthNetguardBudget()
    {
        await using var host = WorldTestHost.Start();
        byte[] key = await host.AddAccountAsync("PINGAUTH");
        var guard = new NetGuard(new NetProtectionOptions { WorldOpcodeBurst = 2, WorldOpcodeRefillPerSecond = 0, WorldPacketsPerSecond = 0, WorldFloodPacketsPerSecond = 0 },
            () => 0, () => 0, NullLogger.Instance);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = new CancellationTokenSource();
        Task session = Task.Run(async () =>
        {
            using TcpClient server = await listener.AcceptTcpClientAsync(stop.Token);
            await using NetworkStream stream = server.GetStream();
            await using AsyncServiceScope scope = host.WorldServices.CreateAsyncScope();
            var worldSession = new WorldSession(stream, "127.0.0.1:41003", scope.ServiceProvider, host.Opcodes, host.World, host.Registry,
                new WorldSessionOptions(), NullLogger.Instance, guard);
            await worldSession.RunAsync(stop.Token);
        });

        var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var client = new WorldTestClient(socket);
        try
        {
            await client.AuthenticateAsync("PINGAUTH", key);
            await client.CollectAsync();
            const int pings = 6; // three times the budget of two
            for (uint i = 1; i <= pings; i++)
            {
                byte[] ping = new byte[8];
                BinaryPrimitives.WriteUInt32LittleEndian(ping, i);
                await client.SendAsync(WorldOpcode.CmsgPing, ping);
            }

            var pongs = new List<byte[]>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (pongs.Count < pings && !timeout.IsCancellationRequested)
            {
                pongs.Add(await client.ReadUntilAsync(WorldOpcode.SmsgPong).WaitAsync(TimeSpan.FromSeconds(5)));
            }

            Assert.Equal(pings, pongs.Count);
            Assert.Equal(0, guard.DroppedPackets);
        }
        finally
        {
            await client.DisposeAsync();
            stop.Cancel();
            listener.Stop();
            await session.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    private static async Task<WorldTestClient> AuthenticateAsync(WorldTestHost host, string account)
    {
        byte[] key = await host.AddAccountAsync(account);
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(account, key);
        await client.CollectAsync();
        return client;
    }

    /// <summary>CMSG_REQUEST_ACCOUNT_DATA runs on the session task after everything sent before it.</summary>
    private static async Task RoundTripAsync(WorldTestClient client)
    {
        await client.SendAsync(WorldOpcode.CmsgRequestAccountData, BitConverter.GetBytes(1u));
        await client.ReadUntilAsync(WorldOpcode.SmsgUpdateAccountData);
    }

    private static byte[] AccountData(uint type, byte[] data)
    {
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes(type));
        bytes.AddRange(BitConverter.GetBytes((uint)data.Length));
        if (data.Length > 0)
        {
            using var buffer = new MemoryStream();
            using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(data);
            }

            bytes.AddRange(buffer.ToArray());
        }

        return [.. bytes];
    }

    private static byte[] Bug(string content, string type)
    {
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes(0u));
        bytes.AddRange(BitConverter.GetBytes((uint)content.Length + 1));
        bytes.AddRange(Encoding.UTF8.GetBytes(content));
        bytes.Add(0);
        bytes.AddRange(BitConverter.GetBytes((uint)type.Length + 1));
        bytes.AddRange(Encoding.UTF8.GetBytes(type));
        bytes.Add(0);
        return [.. bytes];
    }

    private sealed class CountingAccountDataStore : IAccountDataStore
    {
        private readonly ConcurrentDictionary<int, byte[]> _latest = new();
        private int _dataWrites;

        public int DataWrites => Volatile.Read(ref _dataWrites);

        public byte[]? Latest(int type) => _latest.GetValueOrDefault(type);

        public Task<AccountSettings> GetAsync(int accountId, CancellationToken cancellationToken = default) => Task.FromResult(new AccountSettings());

        public Task SaveDataAsync(int accountId, int type, AccountDataEntry entry, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _dataWrites);
            _latest[type] = entry.Data;
            return Task.CompletedTask;
        }

        public Task SaveTutorialsAsync(int accountId, IReadOnlyList<uint> tutorials, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class LineLogger(ConcurrentQueue<string>? lines = null) : ILogger
    {
        private readonly ConcurrentQueue<string> _lines = lines ?? new();

        public int Count(string fragment) => _lines.Count(l => l.Contains(fragment, StringComparison.Ordinal));

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _lines.Enqueue(formatter(state, exception));
    }

    private sealed class LineLoggerFactory(ConcurrentQueue<string> lines) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => new LineLogger(lines);

        public void Dispose()
        {
        }
    }
}