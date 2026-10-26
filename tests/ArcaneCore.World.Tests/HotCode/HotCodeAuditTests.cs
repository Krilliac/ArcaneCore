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
