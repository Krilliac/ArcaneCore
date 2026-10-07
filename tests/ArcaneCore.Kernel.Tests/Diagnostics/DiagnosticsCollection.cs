using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Diagnostics;

/// <summary>Invariant and CrashHandler state is process-global; these tests must not run in parallel with each other.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DiagnosticsCollection
{
    public const string Name = "Diagnostics";
}

/// <summary>An <see cref="ILogger"/> that keeps the rendered messages (thread-safe).</summary>
public sealed class CapturingLogger : ILogger
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
