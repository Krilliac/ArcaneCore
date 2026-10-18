using Microsoft.Extensions.Logging;

namespace ArcaneCore.Atlas.Server;

/// <summary>
/// Wraps a logger so nothing reaches it above <see cref="LogLevel.Warning"/>. The ArcaneCore.Game readers log a missing or
/// corrupt data file at Error because on a server that is an operator fault; for a visualizer pointed at a data directory
/// that file is an expected input whose state the layer reports itself, so it must not read as an Atlas failure.
/// <para>Thread affinity: any thread (it only forwards to the wrapped logger).</para>
/// </summary>
public class WarningCeilingLogger(ILogger inner) : ILogger
{
    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => inner.BeginScope(state);

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(Clamp(logLevel));

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => inner.Log(Clamp(logLevel), eventId, state, exception, formatter);

    /// <summary>The level actually written: Error and Critical become Warning, everything else is unchanged.</summary>
    public static LogLevel Clamp(LogLevel level) => level is LogLevel.Error or LogLevel.Critical ? LogLevel.Warning : level;
}

/// <summary>The typed form of <see cref="WarningCeilingLogger"/>, for constructors that ask for <c>ILogger&lt;T&gt;</c>.</summary>
public sealed class WarningCeilingLogger<T>(ILogger inner) : WarningCeilingLogger(inner), ILogger<T>;
