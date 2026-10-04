using Microsoft.Extensions.Logging;

namespace ArcaneCore.Kernel.Logging;

/// <summary>
/// One log event as the sinks see it: the already-rendered message plus its metadata. A plain struct passed by <c>in</c>; the
/// typed state travels separately (generic, unboxed) so the text sinks never box it.
/// </summary>
public readonly struct LogEvent(DateTime timestamp, LogLevel level, string category, EventId eventId, string message, Exception? exception)
{
    /// <summary>The event time, in the configured clock (<see cref="TimestampKind"/>); its <see cref="DateTime.Kind"/> says which.</summary>
    public DateTime Timestamp { get; } = timestamp;

    public LogLevel Level { get; } = level;

    public string Category { get; } = category;

    public EventId EventId { get; } = eventId;

    public string Message { get; } = message;

    public Exception? Exception { get; } = exception;
}
