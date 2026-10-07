using Microsoft.Extensions.Logging;

namespace ArcaneCore.Kernel.Diagnostics;

/// <summary>Where <see cref="CrashHandler"/> writes a report.</summary>
public interface ICrashSink
{
    /// <summary>
    /// True when <see cref="Write"/> has reached its destination before it returns. The console logger
    /// provider queues to a background thread, so a report written only through it is lost when the
    /// process aborts a moment later; <see cref="CrashHandler"/> also writes to standard error before it
    /// terminates the process unless the sink says it is synchronous.
    /// </summary>
    bool IsSynchronous { get; }

    void Write(LogLevel level, string report, Exception? exception);
}

/// <summary>The bootstrap sink: standard error, flushed. Used before the host exists.</summary>
public sealed class StandardErrorCrashSink : ICrashSink
{
    public static StandardErrorCrashSink Instance { get; } = new();

    private StandardErrorCrashSink()
    {
    }

    public bool IsSynchronous => true;

    public void Write(LogLevel level, string report, Exception? exception)
    {
        TextWriter error = Console.Error;
        error.WriteLine(report);
        error.Flush();
    }
}

/// <summary>The hosted sink: the report goes through <see cref="ILogger"/> (the exception attached, so structured sinks keep it).</summary>
public sealed class LoggerCrashSink(ILogger logger) : ICrashSink
{
    public bool IsSynchronous => false;

    public void Write(LogLevel level, string report, Exception? exception)
        => logger.Log(level, exception, "{CrashReport}", report);
}
