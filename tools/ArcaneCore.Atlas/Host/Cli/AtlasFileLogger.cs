using System.Globalization;
using ArcaneCore.Kernel.Logging;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Atlas.Host.Cli;

/// <summary>
/// The <c>--log-file</c> sink: every enabled log entry as one UTF-8 line (<c>2026-10-21 14:03:09.123 [WRN] category: message</c>, then the
/// exception text) through Kernel's <see cref="RollingFileWriter"/> (rolls at 10 MB and daily, keeps five segments), behind a
/// <see cref="QueuedLineWriter"/> so a logging thread never waits on the disk and the single-threaded writer has one owner.
/// Owned by the logger factory that created it, which disposes it (drains the queue and flushes). The file is opened on the first line;
/// an unwritable path is reported once on standard error by the writer and never throws into the caller.
/// </summary>
internal sealed class AtlasFileLoggerProvider : ILoggerProvider
{
    private readonly QueuedLineWriter _writer;

    /// <param name="path">The active file; its directory is created on first write.</param>
    public AtlasFileLoggerProvider(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = new RollingFileWriter(path, RollingFilePolicy.FromOptions(rollSizeMb: 10, rollDaily: true, retain: 5));
        _writer = new QueuedLineWriter(file, capacity: 4096, name: "atlas", dropNotice: static n => Line(DateTime.Now, LogLevel.Warning, "ArcaneCore.Atlas.Log", $"{n} log lines dropped (the queue was full)", null));
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _writer);

    public void Dispose() => _writer.Dispose();

    internal static string Line(DateTime now, LogLevel level, string category, string message, Exception? exception)
    {
        string text = now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " [" + Abbreviation(level) + "] " + category + ": " + message;
        if (exception is not null)
        {
            text += Environment.NewLine + exception;
        }

        return text + Environment.NewLine;
    }

    private static string Abbreviation(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    private sealed class FileLogger(string category, QueuedLineWriter writer) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            writer.Write(Line(DateTime.Now, logLevel, category, formatter(state, exception), exception), priority: logLevel >= LogLevel.Error);
        }
    }
}
