using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Tests.Cluster.Social;

/// <summary>A logger that keeps every formatted line with its level, for asserting what an outage logs.</summary>
internal sealed class CapturingLogger : ILogger
{
    public ConcurrentQueue<(LogLevel Level, string Text)> Lines { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Lines.Enqueue((logLevel, formatter(state, exception)));

    /// <summary>The lines of at least <paramref name="minimum"/> (Debug lines are normally noise).</summary>
    public (LogLevel Level, string Text)[] AtLeast(LogLevel minimum) => [.. Lines.Where(l => l.Level >= minimum)];
}

/// <summary>A clock that only moves when a test advances it (<see cref="TimeProvider.GetTimestamp"/>).</summary>
internal sealed class SteppedTimeProvider : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}
