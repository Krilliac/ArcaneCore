using System.Collections.Concurrent;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Audit;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Audit;

/// <summary>Edges of the debug write audit (plan 1.5, I3): the length boundary, concurrency, the spec hash value, the log line and the player path.</summary>
public sealed class DebugWriteLogEdgeTests
{
    private sealed class CapturingFactory : ILoggerFactory
    {
        public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Lines { get; } = new();

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => new Capture(categoryName, Lines);

        public void Dispose()
        {
        }

        private sealed class Capture(string category, ConcurrentQueue<(string, LogLevel, string)> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => lines.Enqueue((category, logLevel, formatter(state, exception)));
        }
    }

    [Fact]
    public void Hash_OfAbc_IsTheFirstEightHexDigitsOfTheFips180Vector()
    {
        // FIPS 180-2 appendix B.1: SHA-256("abc") = ba7816bf 8f01cfea 414140de 5dae2223 b00361a3 96177a9c b410ff61 f20015ad.
        Assert.Equal("sha256:ba7816bf", DebugWrite.Hash("abc"));
    }

    [Theory]
    [InlineData(255, false)]
    [InlineData(256, false)]
    [InlineData(257, true)]
    public void TheCutBoundary_IsMaxValueLength(int length, bool cut)
    {
        var log = new DebugWriteLog(2);
        string text = new('y', length);
        DebugWriteEntry entry = log.Record(1, "G", "cmd", "t", "f", text, text, DebugWriteScope.Live, text, true);

        Assert.Equal(cut ? DebugWriteLog.MaxValueLength : length, entry.NewValue.Length);
        Assert.Equal(cut, entry.OldValue.EndsWith("...", StringComparison.Ordinal));
        Assert.Equal(entry.OldValue, entry.Reason);
        Assert.True(entry.Confirmed);
    }

    [Fact]
    public void TheDefaultCapacity_IsOneThousand_AndKeepsTheNewest()
    {
        var log = new DebugWriteLog();
        for (int i = 0; i < 1005; i++) log.Record(1, "G", "cmd", "t", "f", i.ToString(), "", DebugWriteScope.Live);

        Assert.Equal(1000, DebugWriteLog.DefaultCapacity);
        Assert.Equal(1000, log.Tail(5000).Count);
        Assert.Equal("5", log.Tail(5000)[0].OldValue);
        Assert.Equal("1004", log.Tail(1)[0].OldValue);
        Assert.Equal(1005, log.Total);
    }

    [Fact]
    public void ANegativeCapacity_KeepsNothing()
    {
        var log = new DebugWriteLog(-4);
        log.Record(1, "G", "cmd", "t", "f", "a", "b", DebugWriteScope.Live);

        Assert.Empty(log.Tail(1));
        Assert.Equal(1, log.Total);
    }

    [Fact]
    public void Tail_IsASnapshot_UnaffectedByLaterWrites()
    {
        var log = new DebugWriteLog(5);
        log.Record(1, "G", "cmd", "t", "f", "1", "2", DebugWriteScope.Live);
        IReadOnlyList<DebugWriteEntry> snapshot = log.Tail(5);
        log.Record(1, "G", "cmd", "t", "f", "2", "3", DebugWriteScope.Live);

        Assert.Single(snapshot);
        Assert.Equal(2, log.Tail(5).Count);
    }

    [Fact]
    public void ConcurrentWriters_LoseNoCount_AndNeverExceedTheCapacity()
    {
        var log = new DebugWriteLog(100);
        Parallel.For(0, 8, w =>
        {
            for (int i = 0; i < 500; i++) log.Record(w, "G" + w, "cmd", "t", "f", i.ToString(), "", DebugWriteScope.Live);
        });

        Assert.Equal(4000, log.Total);
        Assert.Equal(100, log.Tail(1000).Count);
    }

    [Theory]
    [InlineData(DebugWriteScope.Live)]
    [InlineData(DebugWriteScope.UntilReload)]
    [InlineData(DebugWriteScope.Persisted)]
    [InlineData(DebugWriteScope.Action)]
    public void EveryScope_IsStored(DebugWriteScope scope)
    {
        var log = new DebugWriteLog(2);

        Assert.Equal(scope, log.Record(1, "G", "cmd", "t", "f", "a", "b", scope).Scope);
    }

    [Fact]
    public async Task Record_LogsOneLine_WithTheFields_AndOnlyTheHashForPersonalValues()
    {
        var factory = new CapturingFactory();
        await using var host = WorldTestHost.Start(configureServices: s => s.AddSingleton<ILoggerFactory>(factory));
        await using WorldTestClient client = await host.EnterWorldAsync("DWEONE", "Dweone", AccountSecurity.Administrator);
        WorldSession session = host.Registry.Find((await host.Accounts.FindByUsernameAsync("DWEONE"))!.Id)!;

        await host.OnWorldAsync(() =>
        {
            CommandContext speed = WriteTestCommands.Bind(new CommandContext(session, session.Player!, ChatCommands.CreateTable()), "alter speed");
            DebugWrite.Record(speed, "self", "speed", "1", "2", DebugWriteScope.Live, "plain", confirmed: true);
            CommandContext kick = WriteTestCommands.Bind(new CommandContext(session, session.Player!, ChatCommands.CreateTable()), "session kick");
            DebugWrite.Record(kick, "session 4", "endpoint", "203.0.113.9:8085", "198.51.100.7:1", DebugWriteScope.Live, sensitive: true);
        });

        var lines = factory.Lines.Where(l => l.Category == DebugWrite.Category).ToArray();
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.Equal(LogLevel.Information, l.Level));
        Assert.Contains("alter speed", lines[0].Message, StringComparison.Ordinal);
        Assert.Contains("Dweone", lines[0].Message, StringComparison.Ordinal);
        Assert.Contains("confirmed True", lines[0].Message, StringComparison.Ordinal);
        Assert.Contains(DebugWrite.Hash("203.0.113.9:8085"), lines[1].Message, StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.9", lines[1].Message, StringComparison.Ordinal);
        Assert.DoesNotContain("198.51.100.7", lines[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Record_WithoutPersonal_KeepsValuesInClear_AndEntriesKeepTheirOrder()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("DWETWO", "Dwetwo", AccountSecurity.GameMaster);
        WorldSession session = host.Registry.Find((await host.Accounts.FindByUsernameAsync("DWETWO"))!.Id)!;
        GmAuditFeature audit = host.WorldServices.GetRequiredService<GmAuditFeature>();

        await host.OnWorldAsync(() =>
        {
            CommandContext a = WriteTestCommands.Bind(new CommandContext(session, session.Player!, ChatCommands.CreateTable()), "a set");
            DebugWrite.Record(a, "t1", "f", "plain-old", "plain-new", DebugWriteScope.Persisted);
            CommandContext b = WriteTestCommands.Bind(new CommandContext(session, session.Player!, ChatCommands.CreateTable()), "b set");
            DebugWrite.Record(b, "t2", "f", null, null, DebugWriteScope.UntilReload);
        });

        DebugWriteEntry[] entries = [.. audit.DebugWrites.Tail(10)];
        Assert.Equal(["a set", "b set"], entries.Select(e => e.Command));
        Assert.Equal(("plain-old", "plain-new"), (entries[0].OldValue, entries[0].NewValue));
        Assert.Equal((string.Empty, string.Empty), (entries[1].OldValue, entries[1].NewValue));
    }

    [Fact]
    public async Task Record_RejectsANullContext()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("DWETHR", "Dwethr", AccountSecurity.Administrator);
        WorldSession session = host.Registry.Find((await host.Accounts.FindByUsernameAsync("DWETHR"))!.Id)!;
        Exception? nullContext = null;

        await host.OnWorldAsync(() =>
        {
            nullContext = Record.Exception(() => DebugWrite.Record(null!, "t", "f", "a", "b", DebugWriteScope.Live));
        });

        Assert.IsType<ArgumentNullException>(nullContext);
    }

    [Fact]
    public async Task Record_ByAnUnflaggedCommand_OutsideStrictMode_LogsOneErrorPerCommand_AndStillRecords()
    {
        var factory = new CapturingFactory();
        await using var host = WorldTestHost.Start(configureServices: s => s.AddSingleton<ILoggerFactory>(factory));
        await using WorldTestClient client = await host.EnterWorldAsync("DWEFLG", "Dweflg", AccountSecurity.Administrator);
        WorldSession session = host.Registry.Find((await host.Accounts.FindByUsernameAsync("DWEFLG"))!.Id)!;
        GmAuditFeature audit = host.WorldServices.GetRequiredService<GmAuditFeature>();
        audit.DebugWrites.Strict = false;

        await host.OnWorldAsync(() =>
        {
            for (int i = 0; i < 3; i++)
            {
                DebugWrite.Record(WriteTestCommands.Bind(CommandContext.ForConsole(session, ChatCommands.CreateTable(), _ => { }), "alter speed", isWrite: false),
                    "t", "f", "a", "b", DebugWriteScope.Live);
            }

            DebugWrite.Record(WriteTestCommands.Bind(CommandContext.ForConsole(session, ChatCommands.CreateTable(), _ => { }), "alter scale", isWrite: false),
                "t", "f", "a", "b", DebugWriteScope.Live);
        });

        var errors = factory.Lines.Where(l => l.Category == DebugWrite.Category && l.Level == LogLevel.Error).ToArray();
        Assert.Equal(2, errors.Length);
        Assert.Contains("alter speed", errors[0].Message, StringComparison.Ordinal);
        Assert.Contains("alter scale", errors[1].Message, StringComparison.Ordinal);
        Assert.Equal(4, audit.DebugWrites.Total);
    }

    [Fact]
    public void TheWriteCategory_IsTheOneThePlanNames()
        => Assert.Equal("ArcaneCore.Gm.Write", DebugWrite.Category);
}
