using Microsoft.Extensions.Logging;

namespace ArcaneCore.Kernel.Logging;

/// <summary>
/// The per-category <see cref="ILogger"/> of <see cref="ArcaneLoggerProvider"/>. Minimum levels are the logger factory's job
/// (the standard <c>Logging:LogLevel</c> rules run before this is called), so <see cref="IsEnabled"/> only rejects
/// <see cref="LogLevel.None"/>. <see cref="Log{TState}"/> renders the message once, stamps the time, and hands the event to every
/// enabled sink; a sink that throws is counted and skipped, never propagated to the caller. Thread affinity: any thread; no
/// state beyond the category.
/// </summary>
public sealed class ArcaneLogger(string category, ArcaneLoggerProvider provider) : ILogger
{
    public string Category => category;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => provider.ScopeProvider?.Push(state);

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.None)
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(formatter);
        string message = formatter(state, exception);
        if (string.IsNullOrEmpty(message) && exception is null)
        {
            return;
        }

        var evt = new LogEvent(provider.Now(), logLevel, category, eventId, message, exception);
        IExternalScopeProvider? scopes = provider.IncludeScopes ? provider.ScopeProvider : null;
        LogSink[] sinks = provider.Sinks;
        for (int i = 0; i < sinks.Length; i++)
        {
            LogSink sink = sinks[i];
            if (!sink.Enabled)
            {
                continue;
            }

            try
            {
                sink.Emit(in evt, state, scopes);
            }
            catch (Exception)
            {
                sink.CountFault();
            }
        }
    }
}
