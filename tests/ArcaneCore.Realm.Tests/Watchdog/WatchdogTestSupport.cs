using ArcaneCore.Kernel.Ops.Watchdog;
using ArcaneCore.Kernel.Ops.Watchdog.Counters;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Realm.Tests.Watchdog;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class FakeClock : WatchdogClock
{
    private long _micros = 1_000_000; // never 0: the monitors use long.MinValue, not 0, as "no time", but a non-zero origin keeps the tests honest

    public override long NowMicros => Volatile.Read(ref _micros);

    public void AdvanceMs(long milliseconds) => Interlocked.Add(ref _micros, milliseconds * 1_000);

    public void AdvanceMicros(long micros) => Interlocked.Add(ref _micros, micros);
}

/// <summary>One recorded log line.</summary>
internal sealed record LogLine(LogLevel Level, EventId Event, string Message, Exception? Exception);

/// <summary>Collects every log call (thread-safe) so tests assert on level, event id and text.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly List<LogLine> _lines = [];

    public IReadOnlyList<LogLine> Lines
    {
        get
        {
            lock (_lines)
            {
                return [.. _lines];
            }
        }
    }

    public IEnumerable<LogLine> Of(EventId id) => Lines.Where(l => l.Event.Id == id.Id);

    public int CountOf(EventId id) => Of(id).Count();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_lines)
        {
            _lines.Add(new LogLine(logLevel, eventId, formatter(state, exception), exception));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}

internal static class WatchdogTestSupport
{
    /// <summary>A fresh registry per test, so counters of one test never leak into another.</summary>
    public static CounterRegistry NewRegistry() => new();

    /// <summary>Bytes allocated on this thread by <paramref name="action"/> after one warm-up call (JIT and pool warm-up are excluded).</summary>
    public static long AllocatedBy(Action action, int iterations = 10_000)
    {
        action();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
        {
            action();
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
