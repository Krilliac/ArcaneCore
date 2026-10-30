using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Diagnostics;

/// <summary>The fallback chain, the timeout, the free-space rule and the naming of <see cref="CrashDumpService"/>, with a fake writer so no real dump is written.</summary>
public sealed class CrashDumpServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-crash-dump-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Left for the temp cleaner.
        }
    }

    [Fact]
    public void Run_WhenFullFails_FallsBackToHeapThenMini_AndLogsEachFailure()
    {
        var writer = new FakeWriter(CrashDumpMode.Full, CrashDumpMode.Heap);
        var service = new CrashDumpService(writer, static _ => null);

        CrashDumpOutcome? outcome = Run(service, new CrashDumpOptions { Mode = CrashDumpMode.Full });

        Assert.NotNull(outcome);
        Assert.Equal(CrashDumpMode.Mini, outcome.Mode);
        Assert.Equal([CrashDumpMode.Full, CrashDumpMode.Heap, CrashDumpMode.Mini], writer.Attempts);
        Assert.True(File.Exists(outcome.Path));
        Assert.Contains(_log, l => l.Contains("the Full dump failed", StringComparison.Ordinal));
        Assert.Contains(_log, l => l.Contains("the Heap dump failed", StringComparison.Ordinal));
        Assert.Contains(_log, l => l.Contains("fell back from Full", StringComparison.Ordinal));
        Assert.Contains("fell back from Full", outcome.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_WhenEveryTypeFails_ReportsFailure_AndLeavesNoFile()
    {
        var writer = new FakeWriter(CrashDumpMode.Full, CrashDumpMode.Heap, CrashDumpMode.Mini);

        CrashDumpOutcome? outcome = Run(new CrashDumpService(writer, static _ => null), new CrashDumpOptions { Mode = CrashDumpMode.Full });

        Assert.NotNull(outcome);
        Assert.Null(outcome.Path);
        Assert.Contains("failed", outcome.Note, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public void Run_BelowFullMinFreeGB_StartsAtHeap()
    {
        var writer = new FakeWriter();
        var service = new CrashDumpService(writer, static _ => 1L * 1024 * 1024 * 1024);

        CrashDumpOutcome? outcome = Run(service, new CrashDumpOptions { Mode = CrashDumpMode.Full, FullMinFreeGB = 16 });

        Assert.Equal([CrashDumpMode.Heap], writer.Attempts);
        Assert.Equal(CrashDumpMode.Heap, outcome!.Mode);
        Assert.Contains(_log, l => l.Contains("Full falls back to Heap", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_WithEnoughFreeSpace_WritesFull()
    {
        var writer = new FakeWriter();

        Run(new CrashDumpService(writer, static _ => 100L * 1024 * 1024 * 1024), new CrashDumpOptions { Mode = CrashDumpMode.Full, FullMinFreeGB = 16 });

        Assert.Equal([CrashDumpMode.Full], writer.Attempts);
    }

    [Fact]
    public void Run_WhenTheWriterHangs_GivesUpAfterTheTimeout_AndDoesNotRetry()
    {
        using var release = new ManualResetEventSlim();
        var writer = new FakeWriter { Block = release };
        var service = new CrashDumpService(writer, static _ => null);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        CrashDumpOutcome? outcome = Run(service, new CrashDumpOptions { Mode = CrashDumpMode.Full, TimeoutSeconds = 1 });
        clock.Stop();
        release.Set();

        Assert.NotNull(outcome);
        Assert.Null(outcome.Path);
        Assert.Single(writer.Attempts);
        Assert.InRange(clock.Elapsed.TotalSeconds, 0.8, 20);
        Assert.Contains(_log, l => l.Contains("giving up after the timeout", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_ModeOff_DoesNothing()
    {
        var writer = new FakeWriter();

        Assert.Null(Run(new CrashDumpService(writer, static _ => null), new CrashDumpOptions { Mode = CrashDumpMode.Off }));
        Assert.Empty(writer.Attempts);
    }

    [Fact]
    public void FileNameFor_IsDaemonPidAndUtcTime()
    {
        Assert.Equal("ArcaneCore.World-4242-20261029-153045.dmp", CrashDumpService.FileNameFor("ArcaneCore.World", 4242, new DateTime(2026, 10, 29, 15, 30, 45, DateTimeKind.Utc)));
        Assert.Equal("a_b-1-20261029-000000.dmp", CrashDumpService.FileNameFor("a/b", 1, new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void ResolveDirectory_PrefersTheConfiguredOne_ThenDumpsBesideTheLog_ThenTheDaemonDirectory()
    {
        Assert.Equal(Path.GetFullPath("custom"), CrashDumpService.ResolveDirectory(new CrashDumpOptions { Directory = "custom" }, "logs/a.log"));
        Assert.Equal(Path.Combine(Path.GetDirectoryName(Path.GetFullPath("logs/a.log"))!, "dumps"), CrashDumpService.ResolveDirectory(new CrashDumpOptions(), "logs/a.log"));
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "dumps"), CrashDumpService.ResolveDirectory(new CrashDumpOptions(), null));
    }

    [Fact]
    public void Defaults_AreFullMode_KeepFive_SixteenGiBForFull()
    {
        var options = new DiagnosticsOptions();

        Assert.Equal(CrashDumpMode.Full, options.CrashDump.Mode);
        Assert.Equal(5, options.CrashDump.MaxFiles);
        Assert.Equal(16, options.CrashDump.FullMinFreeGB);
        Assert.Equal(string.Empty, options.CrashDump.Directory);
    }

    [Fact]
    public void Prune_ZeroKeepsEverything()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "D-1-20200101-000000.dmp"), "x");
        File.WriteAllText(Path.Combine(_directory, "D-2-20200102-000000.dmp"), "x");

        Assert.Equal(0, CrashDumpService.Prune(new CrashDumpOptions { MaxFiles = 0 }, "D", _directory, Record));
        Assert.Equal(2, Directory.GetFiles(_directory).Length);
    }

    private CrashDumpOutcome? Run(CrashDumpService service, CrashDumpOptions options)
        => service.Run(options, failFast: false, "ArcaneCore.Kernel.Tests", _directory, DateTime.UtcNow, Record);

    private void Record(LogLevel level, string text)
    {
        lock (_log)
        {
            _log.Add(level + " " + text);
        }
    }

    /// <summary>Writes a small file per attempt, throws for the listed modes, and can block to model a hung runtime.</summary>
    private sealed class FakeWriter(params CrashDumpMode[] failing) : ICrashDumpWriter
    {
        private readonly List<CrashDumpMode> _attempts = [];

        public ManualResetEventSlim? Block { get; init; }

        public List<CrashDumpMode> Attempts
        {
            get
            {
                lock (_attempts)
                {
                    return [.. _attempts];
                }
            }
        }

        public void Write(CrashDumpMode mode, string path)
        {
            lock (_attempts)
            {
                _attempts.Add(mode);
            }

            Block?.Wait(TimeSpan.FromSeconds(30));
            if (failing.Contains(mode))
            {
                File.WriteAllText(path, "partial");
                throw new InvalidOperationException("E_INVALIDARG " + mode);
            }

            File.WriteAllText(path, "dump " + mode);
        }
    }
}
