using ArcaneCore.Kernel.Configuration;
using ArcaneCore.World.Ops.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Ops.OpsDiagnostics;

/// <summary><c>.debug dump</c>'s gate and runner with a fake writer: no real dump is ever written here.</summary>
public sealed class ProcessDumpServiceTests : IDisposable
{
    private const long Gib = 1024L * 1024 * 1024;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arcane-dump-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ms;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => Volatile.Read(ref _ms);

        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero).AddMilliseconds(Volatile.Read(ref _ms));

        public void AdvanceSeconds(int seconds) => Interlocked.Add(ref _ms, seconds * 1000L);
    }

    private sealed class FakeWriter : IProcessDumpWriter
    {
        public List<(DumpKind Kind, string Path)> Writes { get; } = [];

        public int? ThreadId { get; private set; }

        public Exception? Fail { get; set; }

        public ManualResetEventSlim? Gate { get; set; }

        public ManualResetEventSlim Entered { get; } = new(false);

        public void Write(DumpKind kind, string path)
        {
            ThreadId = Environment.CurrentManagedThreadId;
            lock (Writes)
            {
                Writes.Add((kind, path));
            }

            Entered.Set();
            Gate?.Wait(TimeSpan.FromSeconds(20));
            if (Fail is not null)
            {
                throw Fail;
            }

            File.WriteAllBytes(path, new byte[4096]);
        }
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines)
            {
                Lines.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    private ProcessDumpService Create(IProcessDumpWriter writer, bool enabled = true, int minInterval = 600, long? free = 100 * Gib, long workingSet = 7 * Gib, ILogger? logger = null)
        => new(new DumpDiagnosticsOptions { Enabled = enabled, MinIntervalSeconds = minInterval }, _dir, logger ?? NullLogger.Instance, writer, _time, _ => free, () => workingSet);

    [Fact]
    public void Disabled_IsRefused_AndNothingIsWrittenOrCreated()
    {
        var writer = new FakeWriter();
        DumpRequestResult result = Create(writer, enabled: false).Request(DumpKind.Full);

        Assert.False(result.Started);
        Assert.Null(result.Completion);
        Assert.Contains("disabled", result.Message);
        Assert.Contains("Ops:Diagnostics:Dumps:Enabled", result.Message);
        Assert.Empty(writer.Writes);
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public async Task Enabled_CreatesTheDirectoryOwnerOnly_ThroughTheCrashDumpHelper()
    {
        // A dump holds every secret in memory: the directory is made by the same helper the crash dump uses (protected DACL on Windows, mode 700 on Unix).
        var writer = new FakeWriter();
        DumpRequestResult result = Create(writer).Request(DumpKind.Mini);
        Assert.True(result.Started);
        await result.Completion!.WaitAsync(TimeSpan.FromSeconds(20));

        if (OperatingSystem.IsWindows())
        {
            Assert.True(new DirectoryInfo(_dir).GetAccessControl().AreAccessRulesProtected, "the dump directory must not inherit its parent's ACL");
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_dir));
        }
    }

    [Fact]
    public async Task Enabled_WritesOffTheCallingThread_AndReportsPathSizeAndDuration()
    {
        var writer = new FakeWriter();
        var log = new ListLogger();
        DumpRequestResult result = Create(writer, logger: log).Request(DumpKind.Heap);

        Assert.True(result.Started);
        Assert.Contains("Dump started", result.Message);
        Assert.Contains("roughly 1 minute", result.Message);
        Assert.StartsWith(_dir, result.Path);
        Assert.EndsWith(".dmp", result.Path);
        Assert.Contains("heap", Path.GetFileName(result.Path!));
        DumpOutcome outcome = await result.Completion!.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(outcome.Success);
        Assert.Equal(4096, outcome.Bytes);
        Assert.Equal(result.Path, outcome.Path);
        Assert.Equal((DumpKind.Heap, result.Path!), Assert.Single(writer.Writes));
        Assert.NotEqual(Environment.CurrentManagedThreadId, writer.ThreadId);
        Assert.Contains(log.Lines, l => l.Message.Contains("process dump written") && l.Message.Contains(result.Path!));
        Assert.Contains("Dump written", ProcessDumpService.DescribeOutcome(outcome));
    }

    [Fact]
    public async Task AFailedDump_IsLoggedAsAnError_AndDoesNotBlockARetry()
    {
        var writer = new FakeWriter { Fail = new InvalidOperationException("E_INVALIDARG") };
        var log = new ListLogger();
        ProcessDumpService service = Create(writer, logger: log);

        DumpOutcome outcome = await service.Request(DumpKind.Heap).Completion!.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False(outcome.Success);
        Assert.Contains("E_INVALIDARG", outcome.Error);
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Error && l.Message.Contains("failed"));
        Assert.Contains("try full", ProcessDumpService.DescribeOutcome(outcome));

        writer.Fail = null;
        DumpRequestResult retry = service.Request(DumpKind.Full); // inside the 600 s interval, but the first attempt failed
        Assert.True(retry.Started);
        Assert.True((await retry.Completion!.WaitAsync(TimeSpan.FromSeconds(20))).Success);
    }

    [Fact]
    public async Task AFailedDump_DeletesThePartialFile()
    {
        var writer = new PartialWriter();
        DumpRequestResult result = Create(writer).Request(DumpKind.Full);
        DumpOutcome outcome = await result.Completion!.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False(outcome.Success);
        Assert.True(writer.Wrote);
        Assert.False(File.Exists(result.Path));
    }

    private sealed class PartialWriter : IProcessDumpWriter
    {
        public bool Wrote { get; private set; }

        public void Write(DumpKind kind, string path)
        {
            File.WriteAllBytes(path, new byte[1024]);
            Wrote = File.Exists(path);
            throw new IOException("disk full");
        }
    }

    [Theory]
    [InlineData("/mnt/data", "/mnt/data/dumps", true)]
    [InlineData("/mnt/data", "/mnt/database/dumps", false)]
    [InlineData("/mnt/data", "/mnt/data", true)]
    [InlineData("/", "/mnt/data", true)]
    public void IsUnder_MatchesWholePathSegments(string root, string path, bool expected)
        => Assert.Equal(expected, ProcessDumpService.IsUnder(path.Replace('/', Path.DirectorySeparatorChar), root.Replace('/', Path.DirectorySeparatorChar), StringComparison.Ordinal));

    [Fact]
    public async Task TheMinimumInterval_IsEnforcedBetweenSuccessfulDumps()
    {
        var writer = new FakeWriter();
        ProcessDumpService service = Create(writer, minInterval: 600);
        await service.Request(DumpKind.Full).Completion!;

        _time.AdvanceSeconds(599);
        DumpRequestResult early = service.Request(DumpKind.Full);
        Assert.False(early.Started);
        Assert.Contains("minimum interval is 600 s", early.Message);

        _time.AdvanceSeconds(2);
        DumpRequestResult later = service.Request(DumpKind.Full);
        Assert.True(later.Started);
        await later.Completion!;
        Assert.Equal(2, writer.Writes.Count);
        Assert.NotEqual(writer.Writes[0].Path, writer.Writes[1].Path);
    }

    [Fact]
    public async Task AZeroInterval_DisablesTheLimit()
    {
        var writer = new FakeWriter();
        ProcessDumpService service = Create(writer, minInterval: 0);
        await service.Request(DumpKind.Mini).Completion!;
        DumpRequestResult second = service.Request(DumpKind.Mini);
        Assert.True(second.Started);
        await second.Completion!;
    }

    [Fact]
    public async Task OnlyOneDumpRunsAtATime()
    {
        using var gate = new ManualResetEventSlim(false);
        var writer = new FakeWriter { Gate = gate };
        ProcessDumpService service = Create(writer, minInterval: 0);
        DumpRequestResult first = service.Request(DumpKind.Full);
        Assert.True(writer.Entered.Wait(TimeSpan.FromSeconds(20)));

        DumpRequestResult second = service.Request(DumpKind.Full);
        Assert.False(second.Started);
        Assert.Contains("already being written", second.Message);

        gate.Set();
        await first.Completion!.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Single(writer.Writes);
    }

    [Fact]
    public async Task LessThanTwiceTheWorkingSetFree_IsRefused_ExactlyTwiceIsAllowed()
    {
        var writer = new FakeWriter();
        DumpRequestResult tight = Create(writer, free: 14 * Gib - 1, workingSet: 7 * Gib).Request(DumpKind.Full);
        Assert.False(tight.Started);
        Assert.Contains("Not enough free disk space", tight.Message);
        Assert.Contains("2x the working set", tight.Message);
        Assert.Empty(writer.Writes);

        DumpRequestResult exact = Create(writer, free: 14 * Gib, workingSet: 7 * Gib).Request(DumpKind.Full);
        Assert.True(exact.Started);
        await exact.Completion!.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void UnknownFreeSpace_IsRefused()
    {
        DumpRequestResult result = Create(new FakeWriter(), free: null).Request(DumpKind.Full);
        Assert.False(result.Started);
        Assert.Contains("cannot be determined", result.Message);
    }

    [Fact]
    public void ResolveDirectory_UsesTheConfiguredDirectory_ElseDumpsBesideTheLogFile()
    {
        string configured = Path.Combine(Path.GetTempPath(), "somewhere");
        Assert.Equal(Path.GetFullPath(configured), ProcessDumpService.ResolveDirectory(new DumpDiagnosticsOptions { Directory = configured }, "logs/x.log"));
        Assert.Equal(Path.Combine(Path.GetFullPath("var/log"), "dumps"), ProcessDumpService.ResolveDirectory(new DumpDiagnosticsOptions(), "var/log/arcanecore.log"));
        Assert.Equal(Path.Combine(Path.GetFullPath("logs"), "dumps"), ProcessDumpService.ResolveDirectory(new DumpDiagnosticsOptions(), string.Empty));
    }

    [Fact]
    public void TheOptionsDefaults_AreDisabled_Interval600_AndLogsDumps()
    {
        var options = new OpsDiagnosticsOptions();
        Assert.False(options.Dumps.Enabled);
        Assert.Equal(600, options.Dumps.MinIntervalSeconds);
        Assert.Equal(string.Empty, options.Dumps.Directory);
    }

    [Fact]
    public void TheRealFreeSpaceProbe_FindsTheTempVolume()
    {
        long? free = ProcessDumpService.FreeBytesOnVolumeOf(Path.GetTempPath());
        Assert.NotNull(free);
        Assert.True(free > 0);
    }
}
