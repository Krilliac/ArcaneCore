using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.TestSupport;

/// <summary>One captured log call.</summary>
public sealed record CapturedLog(LogLevel Level, string Message);

/// <summary>One captured log call with the category of the logger that took it.</summary>
public sealed record CapturedCategoryLog(string Category, LogLevel Level, string Message);

/// <summary>An <see cref="ILogger"/> that keeps the rendered messages, enabled at every level. Writers lock <see cref="Messages"/>; the list itself is live.</summary>
public sealed class CapturingLogger : ILogger
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Messages)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}

/// <summary>An <see cref="ILogger{TCategoryName}"/> that keeps the level and rendered message of each call, enabled at every level. Single-threaded: the list is not locked.</summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    public List<CapturedLog> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add(new CapturedLog(logLevel, formatter(state, exception)));
}

/// <summary>An <see cref="ILoggerProvider"/> whose loggers feed one queue with the category, level and rendered message of each call, enabled at every level.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedCategoryLog> _entries = new();

    /// <summary>A snapshot of what the loggers took so far, oldest first.</summary>
    public CapturedCategoryLog[] Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<CapturedCategoryLog> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => sink.Enqueue(new CapturedCategoryLog(category, logLevel, formatter(state, exception)));
    }
}

/// <summary>An <see cref="ILogger"/> that keeps each message prefixed with its level ("Warning: text"), thread-safe; <see cref="Entries"/> is a snapshot.</summary>
public sealed class LevelPrefixedCapturingLogger : ILogger
{
    private readonly List<string> _entries = [];

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_entries)
        {
            _entries.Add(logLevel + ": " + formatter(state, exception));
        }
    }
}
