using System.Diagnostics;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.HotCode;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.World.Tests.HotCode;

/// <summary>The audit writer's failure path: the fault is logged, and a failing logger can never end the writer.</summary>
public sealed class HotCodeAuditTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arcane-audit-" + Guid.NewGuid().ToString("N"));

    public HotCodeAuditTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        HotCodeAudit.FlushAll();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    /// <summary>A path whose parent "directory" is a regular file, so creating the directory fails until the file is removed.</summary>
    private string BlockedPath(out string blocker)
    {
        blocker = Path.Combine(_dir, "blocked");
        File.WriteAllText(blocker, "not a directory");
        return Path.Combine(blocker, "audit.log");
    }

    [Fact]
    public void FailedWrite_IsLoggedThroughTheSuppliedLogger()
    {
        string path = BlockedPath(out _);
        var logger = new CapturingLogger();
        var audit = new HotCodeAudit(path, logger: logger);

        audit.Record("test", "first");
        audit.Flush();

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("hot-code audit write to", StringComparison.Ordinal) && e.Message.Contains(path, StringComparison.Ordinal) && e.Exception is not null);
    }

    [Fact]
    public void ThrowingLogger_DoesNotKillTheWriter()
    {
        string path = BlockedPath(out string blocker);
        var audit = new HotCodeAudit(path, logger: new ThrowingLogger());

        audit.Record("test", "lost");
        audit.Flush(); // the write fails, the logger throws, and the flush still completes

        File.Delete(blocker); // the same path can now be written
        audit.Record("test", "kept");
        audit.Flush();

        Assert.True(File.Exists(path));
        string text = File.ReadAllText(path);
        Assert.Contains("kept", text, StringComparison.Ordinal);
        Assert.DoesNotContain("lost", text, StringComparison.Ordinal);
    }

    private static long Faults(string site) => FaultLedger.Shared.Snapshot().Where(s => s.Site == site).Sum(s => s.Count);

    /// <summary>Wedges the writer of the given path in front of its next disk append until <see cref="WriterGate.Release"/> runs.</summary>
    private sealed class WriterGate : IDisposable
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WriterGate(string path)
        {
            string full = Path.GetFullPath(path);
            Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            HotCodeAudit.BeforeWriteHook = async p =>
            {
                if (string.Equals(p, full, StringComparison.OrdinalIgnoreCase))
                {
                    Entered.TrySetResult();
                    await _release.Task.ConfigureAwait(false);
                }
            };
        }

        public TaskCompletionSource Entered { get; }

        public void Release() => _release.TrySetResult();

        public void Dispose()
        {
            Release();
            HotCodeAudit.BeforeWriteHook = null;
        }
    }

    [Fact]
    public async Task Record_QueuesTheLineWithoutTouchingTheDiskOnTheCallingThread()
    {
        string path = Path.Combine(_dir, "queued.log");
        using var gate = new WriterGate(path);
        var audit = new HotCodeAudit(path);

        audit.Record("test", "first"); // the writer is now wedged in front of the append
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        audit.Record("test", "second");

        Assert.False(File.Exists(path)); // Record returned and nothing reached the disk
        gate.Release();
        audit.Flush();
        string text = File.ReadAllText(path);
        Assert.Contains("first", text, StringComparison.Ordinal);
        Assert.Contains("second", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Flush_BehindAWedgedWriter_GivesUpWithinItsBudgetAndCountsTheTimeout()
    {
        string path = Path.Combine(_dir, "wedged.log");
        using var gate = new WriterGate(path);
        var logger = new CapturingLogger();
        var audit = new HotCodeAudit(path, logger: logger) { FlushTimeout = TimeSpan.FromMilliseconds(300) };
        long before = Faults("hotcode.audit-flush");

        audit.Record("test", "stuck");
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var watch = Stopwatch.StartNew();
        audit.Flush();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), $"flush took {watch.Elapsed}");
        Assert.Equal(1, Faults("hotcode.audit-flush") - before);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("flush", StringComparison.Ordinal) && e.Exception is TimeoutException);
    }

    [Fact]
    public async Task FullQueue_DropsAndCountsLines_NeverBlocksRecord_AndFlushStillGivesUp()
    {
        string path = Path.Combine(_dir, "full.log");
        using var gate = new WriterGate(path);
        var logger = new CapturingLogger();
        var audit = new HotCodeAudit(path, logger: logger) { FlushTimeout = TimeSpan.FromMilliseconds(300) };
        long droppedBefore = Faults("hotcode.audit-dropped");
        long flushBefore = Faults("hotcode.audit-flush");

        audit.Record("test", "wedge");
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        const int Capacity = 4096, Extra = 25;

        // The writer holds the first line; the next Capacity fill the queue; the rest are dropped. Every call must return.
        Task producer = Task.Run(() =>
        {
            for (int i = 0; i < Capacity + Extra; i++)
            {
                audit.Record("test", "line " + i);
            }
        });
        await producer.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(Extra, Faults("hotcode.audit-dropped") - droppedBefore);
        Assert.Contains(logger.Entries, e => e.Message.Contains("queue", StringComparison.Ordinal) && e.Message.Contains("full", StringComparison.Ordinal));

        // The barrier cannot even be queued: the flush must still give up inside its budget and be counted.
        var watch = Stopwatch.StartNew();
        await Task.Run(audit.Flush).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), $"flush took {watch.Elapsed}");
        Assert.Equal(1, Faults("hotcode.audit-flush") - flushBefore);

        gate.Release();
        new HotCodeAudit(path).Flush(); // a later flush (the pending barrier may follow it) completes once the writer is free
        Assert.Contains("wedge", File.ReadAllText(path), StringComparison.Ordinal);
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly object _gate = new();
        private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries
        {
            get
            {
                lock (_gate)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                _entries.Add((logLevel, formatter(state, exception), exception));
            }
        }
    }

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => throw new InvalidOperationException("logger failed");
    }
}
