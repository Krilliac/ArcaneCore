using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.Kernel.Ops.ServerConsole;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Net;
using ArcaneCore.World.Ops.Lifecycle;
using ArcaneCore.World.Ops.ServerConsole;
using ArcaneCore.World.Tests.Docs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

// The namespace is ServerConsole, not Console: a namespace named Console would hide System.Console from the ArcaneCore.* code below it
// (the same reason the production namespace is ArcaneCore.*.Ops.ServerConsole).
namespace ArcaneCore.World.Tests.Ops.ServerConsole;

/// <summary>
/// The world daemon's console (docs/areas/server-console.md): typed GM commands run on the world thread as the console invoker
/// (Administrator, no character) against a real in-process world, real sessions and in-memory stores.
/// </summary>
[Collection(ExitCodesCollection.Name)]
public sealed class WorldConsoleTests
{
    private const string Prefix = WorldConsoleExecutor.Prefix;

    /// <summary>A sink that records every line and whether it was written on the world thread.</summary>
    private sealed class RecordingWriter(WorldRuntime world) : TextWriter
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = [];
        private readonly List<bool> _onWorldThread = [];

        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            lock (_gate)
            {
                _lines.Add(value ?? string.Empty);
                _onWorldThread.Add(world.IsWorldThread);
            }
        }

        public string[] Lines
        {
            get
            {
                lock (_gate)
                {
                    return [.. _lines];
                }
            }
        }

        public bool AllOnWorldThread
        {
            get
            {
                lock (_gate)
                {
                    return _onWorldThread.Count > 0 && _onWorldThread.All(b => b);
                }
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _lines.Clear();
                _onWorldThread.Clear();
            }
        }
    }

    /// <summary>An executor over the host's world, with its own DI scope and recording sink.</summary>
    private sealed class ExecutorRig : IAsyncDisposable
    {
        private readonly WorldTestHost _host;
        private readonly AsyncServiceScope _scope;

        public ExecutorRig(WorldTestHost host)
        {
            _host = host;
            Output = new RecordingWriter(host.World);
            _scope = host.WorldServices.CreateAsyncScope();
            Executor = WorldConsoleExecutor.Create(
                _scope.ServiceProvider, host.Opcodes, host.World, host.Registry, new WorldSessionOptions(), NullLogger.Instance, Output);
        }

        public RecordingWriter Output { get; }

        public WorldConsoleExecutor Executor { get; }

        /// <summary>Run <paramref name="line"/> on the world thread and return the lines it printed.</summary>
        public async Task<string[]> RunAsync(string line)
        {
            Output.Clear();
            await _host.World.InvokeAsync(() =>
            {
                Executor.Execute(line);
                return 0;
            });
            return Output.Lines;
        }

        public ValueTask DisposeAsync() => _scope.DisposeAsync();
    }

    /// <summary>A reader whose ReadLine blocks until a line is posted (null ends the input), like a console.</summary>
    private sealed class ScriptedReader : TextReader
    {
        private readonly BlockingCollection<string?> _lines = [];
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public void Post(string? line) => _lines.Add(line);

        public override string? ReadLine()
        {
            Interlocked.Increment(ref _reads);
            return _lines.Take();
        }
    }

    /// <summary>Records the ban requests that reach the store, then forwards to the in-memory store.</summary>
    private sealed class RecordingBanStore(IBanStore inner) : IBanStore
    {
        public ConcurrentQueue<BanRequest> AccountBans { get; } = new();

        public Task BanAccountAsync(BanRequest request, CancellationToken cancellationToken = default)
        {
            AccountBans.Enqueue(request);
            return inner.BanAccountAsync(request, cancellationToken);
        }

        public Task<AccountBanRecord?> GetActiveAccountBanAsync(int accountId, CancellationToken cancellationToken = default)
            => inner.GetActiveAccountBanAsync(accountId, cancellationToken);

        public Task<IpBanRecord?> GetActiveIpBanAsync(string ip, CancellationToken cancellationToken = default)
            => inner.GetActiveIpBanAsync(ip, cancellationToken);

        public Task<IReadOnlySet<int>> FindBannedAccountsAsync(IReadOnlyCollection<int> accountIds, CancellationToken cancellationToken = default)
            => inner.FindBannedAccountsAsync(accountIds, cancellationToken);

        public Task<IReadOnlySet<string>> FindBannedIpsAsync(IReadOnlyCollection<string> ips, CancellationToken cancellationToken = default)
            => inner.FindBannedIpsAsync(ips, cancellationToken);

        public Task<bool> BanIpAsync(IpBanRequest request, CancellationToken cancellationToken = default)
            => inner.BanIpAsync(request, cancellationToken);

        public Task<bool> UnbanAccountAsync(int accountId, string source, string message, CancellationToken cancellationToken = default)
            => inner.UnbanAccountAsync(accountId, source, message, cancellationToken);

        public Task<bool> UnbanIpAsync(string ip, CancellationToken cancellationToken = default)
            => inner.UnbanIpAsync(ip, cancellationToken);

        public Task<IReadOnlyList<AccountBanRecord>> GetHistoryAsync(int accountId, CancellationToken cancellationToken = default)
            => inner.GetHistoryAsync(accountId, cancellationToken);

        public Task<IReadOnlySet<int>> FindAccountsWithHistoryAsync(IReadOnlyCollection<int> accountIds, CancellationToken cancellationToken = default)
            => inner.FindAccountsWithHistoryAsync(accountIds, cancellationToken);

        public Task<IReadOnlyList<AccountBanRecord>> ListActiveAccountBansAsync(CancellationToken cancellationToken = default)
            => inner.ListActiveAccountBansAsync(cancellationToken);

        public Task<IReadOnlyList<IpBanRecord>> ListIpBansAsync(string prefix, CancellationToken cancellationToken = default)
            => inner.ListIpBansAsync(prefix, cancellationToken);

        public Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
            => inner.PurgeExpiredAsync(cancellationToken);
    }

    private static WorldConsole NewConsole(
        WorldTestHost host, ConsoleInputSource source, ILoggerFactory? loggers = null, bool enabled = true)
        => new(
            Options.Create(new WorldConsoleOptions { Enabled = enabled }), source, host.WorldServices.GetRequiredService<IServiceScopeFactory>(),
            host.Opcodes, host.World, host.Registry, Options.Create(new WorldSessionOptions()), loggers ?? NullLoggerFactory.Instance);

    private static async Task<bool> JoinAsync(Thread thread, TimeSpan timeout)
        => await Task.Run(() => thread.Join(timeout));

    // ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Console_NamedTargetCommand_RunsOnTheWorldThread_AndReturnsItsOutput()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient target = await host.EnterWorldAsync("TARGET", "Target");
        await using var rig = new ExecutorRig(host);
        int before = await host.PlayerStateAsync("Target", p => p.Level);

        string[] lines = await rig.RunAsync(".levelup Target 2");

        Assert.Equal(before + 2, await host.PlayerStateAsync("Target", p => p.Level));
        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.StartsWith(Prefix, l, StringComparison.Ordinal));
        Assert.True(rig.Output.AllOnWorldThread, "the sink must be written on the world thread (the command runs there)");
    }

    [Fact]
    public async Task Console_SelfOrSelectionCommand_FailsClosed_WithTheNeedsAPlayerMessage()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient other = await host.EnterWorldAsync("OTHER", "Other");
        await using var rig = new ExecutorRig(host);
        int before = await host.PlayerStateAsync("Other", p => p.Level);

        string[] levelup = await rig.RunAsync(".levelup 2");
        string[] gps = await rig.RunAsync(".gps");

        Assert.Equal([Prefix + GmStrings.ConsoleNeedsPlayer], levelup);
        Assert.Equal([Prefix + GmStrings.ConsoleNeedsPlayer], gps);
        Assert.Equal(before, await host.PlayerStateAsync("Other", p => p.Level));
    }

    [Theory]
    [InlineData("server info", ".server info")]
    [InlineData("server info", "!server info")]
    [InlineData("help server", ".help server")]
    public async Task Console_LeadingDot_IsOptional(string bare, string prefixed)
    {
        await using var host = WorldTestHost.Start();
        await using var rig = new ExecutorRig(host);

        string[] a = await rig.RunAsync(bare);
        string[] b = await rig.RunAsync(prefixed);

        Assert.NotEmpty(a);
        Assert.NotEqual([Prefix + GmStrings.NoSuchCommand], a);
        Assert.Equal(a.Length, b.Length);

        // Uptime and diagnostics lines carry a clock; every other line must be identical.
        static string[] Stable(string[] lines) => [.. lines.Where(l => !l.Contains("Uptime", StringComparison.OrdinalIgnoreCase) && !l.Contains("diagnostics", StringComparison.OrdinalIgnoreCase))];
        Assert.Equal(Stable(a), Stable(b));
        if (bare == "server info")
        {
            Assert.Contains(a, l => l.StartsWith(Prefix + "Core revision: ArcaneCore", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Console_UnknownCommand_RepliesNoSuchCommand()
    {
        await using var host = WorldTestHost.Start();
        await using var rig = new ExecutorRig(host);

        Assert.Equal([Prefix + GmStrings.NoSuchCommand], await rig.RunAsync("frobnicate now"));
        Assert.Equal([Prefix + GmStrings.NoSuchCommand], await rig.RunAsync(".frobnicate"));
    }

    [Fact]
    public async Task Console_Help_ListsAdministratorCommands()
    {
        await using var host = WorldTestHost.Start();
        await using var rig = new ExecutorRig(host);

        string[] lines = await rig.RunAsync("help");
        string all = string.Join("\n", lines);

        // 'server' is Player level, 'account' (login security) is Administrator only: the console is listed as an Administrator.
        Assert.Contains("server", all, StringComparison.Ordinal);
        Assert.Contains("account", all, StringComparison.Ordinal);
        Assert.Contains("levelup", all, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Console_Commands_KeepsTheFullVerticalList_AndPagesWhenGivenAPageOrText()
    {
        await using var host = WorldTestHost.Start();
        await using var rig = new ExecutorRig(host);

        // A bare .commands is the complete, unpaged retail list: no scrollback problem on a console.
        string[] full = await rig.RunAsync("commands");
        Assert.Contains(full, l => l.EndsWith(GmStrings.CommandsAvailable, StringComparison.Ordinal));
        Assert.Contains(full, l => l.EndsWith("    server ...", StringComparison.Ordinal));
        Assert.Contains(full, l => l.EndsWith("    levelup", StringComparison.Ordinal));
        Assert.DoesNotContain(full, l => l.Contains("(page ", StringComparison.Ordinal));
        Assert.True(full.Length > CommandListing.PageLines, $"{full.Length} lines");

        // With an argument it pages like everywhere else.
        string[] filtered = await rig.RunAsync("commands e 2");
        Assert.Contains("Commands matching 'e' (page 2/", filtered[0], StringComparison.Ordinal);
        Assert.True(filtered.Length <= CommandListing.PageLines);
        Assert.Contains("Commands (page 1/", (await rig.RunAsync("commands 1"))[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Console_Kick_And_Announce_ByName_Work()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient victim = await host.EnterWorldAsync("VICTIM", "Victim");
        await using var rig = new ExecutorRig(host);

        await rig.RunAsync("announce server maintenance soon");
        ChatMessage announced = await victim.ReadChatAsync();
        Assert.Equal(GmStrings.SystemMessage("server maintenance soon"), announced.Text);

        string[] kick = await rig.RunAsync("kick victim");

        Assert.Equal([Prefix + GmStrings.CommandKickMessage(GmStrings.PlayerLink("Victim"))], kick);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Victim") is null, "the kicked player leaves the world");
        Assert.Equal([Prefix + "Player not found!"], await rig.RunAsync("kick victim"));
    }

    [Fact]
    public async Task Console_Ban_RecordsConsoleAsTheAuthor()
    {
        var recorder = new RecordingBanStore(new Bans.InMemoryBanStore());
        await using var host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IBanStore>(recorder));
        await using WorldTestClient victim = await host.EnterWorldAsync("VICTIM", "Victim");
        await using var rig = new ExecutorRig(host);
        int accountId = (await host.Accounts.FindByUsernameAsync("VICTIM"))!.Id;

        await rig.RunAsync("ban account victim 1d spam");
        await WorldTestHost.WaitForAsync(() => !recorder.AccountBans.IsEmpty, "the console ban reaches the store");

        BanRequest request = Assert.Single(recorder.AccountBans);
        Assert.Equal(CommandContext.ConsoleName, request.Author);
        Assert.Equal(0, request.AuthorAccountId);
        Assert.Equal(accountId, request.AccountId);
        AccountBanRecord row = Assert.Single(await recorder.GetHistoryAsync(accountId));
        Assert.Equal(CommandContext.ConsoleName, row.BannedBy);
        Assert.Equal("spam", row.Reason);
        await WorldTestHost.WaitForAsync(() => rig.Output.Lines.Any(l => l.Contains("is banned for 1d", StringComparison.Ordinal)), "the ban reply reaches the console");
    }

    [Fact]
    public async Task Console_Quit_RequestsTheGracefulShutdown()
    {
        await using var host = WorldTestHost.Start();
        await using var rig = new ExecutorRig(host);
        ServerLifecycleFeature feature = host.WorldServices.GetRequiredService<ServerLifecycleFeature>();
        var stops = new ConcurrentQueue<int>();
        feature.StopRequested += stops.Enqueue;
        try
        {
            // A countdown starts draining but does not stop; 'shutdown cancel' ends it.
            await rig.RunAsync("shutdown 30");
            Assert.True(feature.IsDraining);
            Assert.Empty(stops);
            await rig.RunAsync("shutdown cancel");
            Assert.False(feature.IsDraining);
            Assert.Empty(stops);

            await rig.RunAsync("quit");

            Assert.Equal([ExitCodes.Success], stops.ToArray());
            Assert.Equal(ExitCodes.Success, ExitCodes.Current);
        }
        finally
        {
            ExitCodes.Current = ExitCodes.Success;
        }
    }

    [Fact]
    public async Task Console_RedirectedInput_StartsNoReaderThread()
    {
        await using var host = WorldTestHost.Start();
        var reader = new ScriptedReader();
        var output = new StringWriter();
        var console = NewConsole(host, new ConsoleInputSource(reader, output, IsInputRedirected: true, IsUserInteractive: true));

        await console.StartAsync(CancellationToken.None);

        Assert.Null(console.ReaderThread);
        await Task.Delay(50);
        Assert.Equal(0, reader.Reads);
        Assert.Equal(string.Empty, output.ToString());
        await console.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Console_NotInteractiveSession_StartsNoReaderThread()
    {
        await using var host = WorldTestHost.Start();
        var reader = new ScriptedReader();
        var console = NewConsole(host, new ConsoleInputSource(reader, new StringWriter(), IsInputRedirected: false, IsUserInteractive: false));

        await console.StartAsync(CancellationToken.None);

        Assert.Null(console.ReaderThread);
        Assert.Equal(0, reader.Reads);
        await console.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Console_Disabled_StartsNoReaderThread()
    {
        await using var host = WorldTestHost.Start();
        var reader = new ScriptedReader();
        var console = NewConsole(host, new ConsoleInputSource(reader, new StringWriter(), IsInputRedirected: false, IsUserInteractive: true), enabled: false);

        await console.StartAsync(CancellationToken.None);

        Assert.Null(console.ReaderThread);
        await Task.Delay(50);
        Assert.Equal(0, reader.Reads);
        await console.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Console_EndOfInput_EndsTheReader_WithoutStoppingTheServer()
    {
        await using var host = WorldTestHost.Start();
        ServerLifecycleFeature feature = host.WorldServices.GetRequiredService<ServerLifecycleFeature>();
        var stops = new ConcurrentQueue<int>();
        feature.StopRequested += stops.Enqueue;
        var reader = new ScriptedReader();
        var console = NewConsole(host, new ConsoleInputSource(reader, new StringWriter(), IsInputRedirected: false, IsUserInteractive: true));
        await console.StartAsync(CancellationToken.None);
        Thread thread = Assert.IsType<Thread>(console.ReaderThread);

        reader.Post(null); // Ctrl+Z / closed pipe

        Assert.True(await JoinAsync(thread, TimeSpan.FromSeconds(10)), "the reader must end at end of input");
        Assert.Empty(stops);
        Assert.False(feature.IsDraining);
        Assert.Equal(7, await host.World.InvokeAsync(() => 7)); // the world thread is still serving
        await console.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Console_ReaderThread_IsBackground_AndStopDoesNotBlock()
    {
        await using var host = WorldTestHost.Start();
        var reader = new ScriptedReader(); // ReadLine blocks and never completes until released below
        var console = NewConsole(host, new ConsoleInputSource(reader, new StringWriter(), IsInputRedirected: false, IsUserInteractive: true));
        await console.StartAsync(CancellationToken.None);
        Thread thread = Assert.IsType<Thread>(console.ReaderThread);
        await WorldTestHost.WaitForAsync(() => reader.Reads == 1, "the reader blocks in ReadLine");

        try
        {
            Assert.True(thread.IsBackground, "a background thread cannot keep the process alive");
            var stopwatch = Stopwatch.StartNew();
            await console.StopAsync(CancellationToken.None);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"StopAsync took {stopwatch.Elapsed}");
            Assert.True(thread.IsAlive, "the blocked ReadLine cannot be interrupted, so Stop must not have joined it");
        }
        finally
        {
            reader.Post(null); // release the thread so the test leaves nothing behind
            await JoinAsync(thread, TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task Console_TypedLine_RunsThroughTheReaderOnTheWorldThread()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient target = await host.EnterWorldAsync("TARGET", "Target");
        var reader = new ScriptedReader();
        var output = new RecordingWriter(host.World);
        var console = NewConsole(host, new ConsoleInputSource(reader, output, IsInputRedirected: false, IsUserInteractive: true));
        await console.StartAsync(CancellationToken.None);
        int before = await host.PlayerStateAsync("Target", p => p.Level);
        try
        {
            reader.Post("   ");                 // blank lines are ignored
            reader.Post("  levelup Target 3  "); // trimmed

            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Target")!.Level == before + 3, "the typed command ran");
            Assert.Contains(output.Lines, l => l.StartsWith("[console] Console ready", StringComparison.Ordinal));
        }
        finally
        {
            reader.Post(null);
            await console.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Console_CommandsAreLogged_AsConsole_WithSecretsRedacted()
    {
        var provider = new CapturingLoggerProvider();
        using ILoggerFactory loggers = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(provider));
        await using var host = WorldTestHost.Start(configureServices: s => s.AddSingleton(loggers));
        var reader = new ScriptedReader();
        var console = NewConsole(host, new ConsoleInputSource(reader, new StringWriter(), IsInputRedirected: false, IsUserInteractive: true), loggers);
        await console.StartAsync(CancellationToken.None);
        try
        {
            reader.Post("announce password hunter2");

            await WorldTestHost.WaitForAsync(
                () => provider.Entries.Any(e => e.Category == GmCommandLog.Category && e.Message.Contains("[Console]", StringComparison.Ordinal)),
                "the GM audit line of the console command");
        }
        finally
        {
            reader.Post(null);
            await console.StopAsync(CancellationToken.None);
        }

        CapturedCategoryLog[] entries = provider.Entries;
        CapturedCategoryLog typed = Assert.Single(entries, e => e.Category == WorldConsole.LogCategory && e.Message.StartsWith("Console command:", StringComparison.Ordinal));
        Assert.Equal("Console command: announce password ***", typed.Message);
        CapturedCategoryLog audit = Assert.Single(entries, e => e.Category == GmCommandLog.Category);
        Assert.Contains("[Console]", audit.Message, StringComparison.Ordinal);
        Assert.Contains("password ***", audit.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(entries, e => e.Message.Contains("hunter2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Console_EveryCommandWithoutArguments_NeverThrowsUnhandled()
    {
        var provider = new CapturingLoggerProvider();
        using ILoggerFactory loggers = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(provider));
        await using var host = WorldTestHost.Start(configureServices: s => s.AddSingleton(loggers));
        await using WorldTestClient bystander = await host.EnterWorldAsync("BYSTANDER", "Bystander");
        var output = new RecordingWriter(host.World);
        await using AsyncServiceScope scope = host.WorldServices.CreateAsyncScope();
        WorldConsoleExecutor executor = WorldConsoleExecutor.Create(
            scope.ServiceProvider, host.Opcodes, host.World, host.Registry, new WorldSessionOptions(), loggers.CreateLogger<WorldSession>(), output);

        // The commands that stop or reconfigure the process are covered by their own tests; hot code is development-only.
        static bool Excluded(string path)
            => path.StartsWith("server shutdown", StringComparison.Ordinal) || path.StartsWith("server restart", StringComparison.Ordinal)
               || path.StartsWith("server idle", StringComparison.Ordinal) || path.StartsWith("hotcode", StringComparison.Ordinal)
               || path.StartsWith("hotmodule", StringComparison.Ordinal) || path.StartsWith("reload", StringComparison.Ordinal);

        string[] paths = [.. CommandReferenceRenderer.Nodes(reloadEnabled: false).Where(n => n.HasHandler && !Excluded(n.Path)).Select(n => n.Path)];
        Assert.True(paths.Length > 100, "the sweep must cover the command table; found " + paths.Length);

        var failed = new List<string>();
        try
        {
            foreach (string path in paths)
            {
                output.Clear();
                await host.World.InvokeAsync(() =>
                {
                    executor.Execute(path); // must never throw; a missing player is a reply
                    return 0;
                });
                if (output.Lines.Any(l => l == Prefix + "The command failed; see the server log."))
                {
                    failed.Add(path);
                }
            }
        }
        finally
        {
            ExitCodes.Current = ExitCodes.Success;
        }

        Assert.True(failed.Count == 0, "commands that threw an unexpected exception at the console: " + string.Join(", ", failed));
        Assert.DoesNotContain(provider.Entries, e => e.Level >= LogLevel.Error && e.Message.StartsWith("console command failed", StringComparison.Ordinal));
        _ = bystander;
    }
}
