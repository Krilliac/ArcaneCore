using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Text;

namespace ArcaneCore.World.Cluster.Supervision;

/// <summary>
/// Multiplexes stdout and stderr lines from supervised child processes into:
/// <list type="bullet">
/// <item><description>Each child's own dedicated log file (<see cref="SupervisedProcessConfig.LogFilePath"/>), unconditionally.</description></item>
/// <item><description>The supervisor's merged console output prefixed with <c>[&lt;name&gt;]</c> when <see cref="SupervisedProcessConfig.Console"/> is <see cref="SupervisedConsoleMode.Merged"/>.</description></item>
/// <item><description>The supervisor's merged console output <b>unconditionally</b> for any <c>stderr</c> line or error/exception line, even when <see cref="SupervisedProcessConfig.Console"/> is <see cref="SupervisedConsoleMode.Window"/> or <see cref="SupervisedConsoleMode.None"/>.</description></item>
/// </list>
/// </summary>
public sealed class SupervisedOutputMultiplexer : IDisposable
{
    private static readonly string[] ErrorMarkers =
    [
        "ERROR",
        "FATAL",
        "CRIT",
        "[ERR]",
        "[FTL]",
        "Unhandled exception",
        "Exception:",
        "fail:",
    ];

    private readonly Lock _gate = new();
    private readonly Action<string> _mergedConsoleSink;
    private readonly Dictionary<string, StreamWriter> _logWriters = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>Where a failed child-log write is logged (through <see cref="FaultLedgerLogging.LogFault(ILogger, string, Exception, string, object?[])"/>); without one the fault is only recorded in the ledger.</summary>
    public ILogger? Logger { get; init; }

    public SupervisedOutputMultiplexer(Action<string>? mergedConsoleSink = null)
    {
        _mergedConsoleSink = mergedConsoleSink ?? Console.Out.WriteLine;
    }

    public SupervisedOutputMultiplexer(TextWriter mergedConsoleWriter)
    {
        ArgumentNullException.ThrowIfNull(mergedConsoleWriter);
        _mergedConsoleSink = mergedConsoleWriter.WriteLine;
    }

    /// <summary>
    /// Formats a raw child output line with its role/process tag prefix: <c>[&lt;processName&gt;] &lt;line&gt;</c>.
    /// </summary>
    public static string FormatLine(string processName, string line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        return $"[{processName}] {line}";
    }

    /// <summary>
    /// Determines whether a child output line represents an error or exception that must always
    /// be surfaced in the supervisor's merged console regardless of <see cref="SupervisedConsoleMode"/>.
    /// </summary>
    public static bool IsErrorOrExceptionLine(string line, bool isStdErr)
    {
        if (isStdErr)
            return true;

        if (string.IsNullOrEmpty(line))
            return false;

        foreach (string marker in ErrorMarkers)
        {
            if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Routes a single output line from <paramref name="config"/> to its per-process log file
    /// and (when in <see cref="SupervisedConsoleMode.Merged"/> mode or when the line is an error/exception)
    /// to the supervisor's merged console.
    /// </summary>
    public void RouteLine(SupervisedProcessConfig config, string line, bool isStdErr = false)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (line is null)
            return;

        string formatted = FormatLine(config.Name, line);
        bool shouldWriteToMergedConsole = config.Console == SupervisedConsoleMode.Merged
            || IsErrorOrExceptionLine(line, isStdErr);

        lock (_gate)
        {
            if (_disposed)
                return;

            if (!string.IsNullOrWhiteSpace(config.LogFilePath))
            {
                // A log file that cannot be opened or written (disk full, sharing violation, read-only directory) must not unwind the caller:
                // that is the child's stdout pump, and a pump that stops leaves the child blocked on a full pipe. Record it and keep going to the console.
                try
                {
                    StreamWriter writer = GetOrCreateLogWriter(config.LogFilePath);
                    writer.WriteLine(formatted);
                    writer.Flush();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException or ArgumentException)
                {
                    RecordLogFault(config, ex);
                }
            }

            if (shouldWriteToMergedConsole)
            {
                _mergedConsoleSink(formatted);
            }
        }
    }

    private void RecordLogFault(SupervisedProcessConfig config, Exception ex)
    {
        const string site = "cluster.supervisor.log-file";
        try
        {
            if (Logger is { } logger)
                logger.LogFault(site, ex, "Supervised process {Process} log file {Path} cannot be written; its output continues on the console only ({Type})", config.Name, config.LogFilePath, ex.GetType().Name);
            else
                FaultLedger.Shared.Record(site, ex);
        }
        catch (Exception sinkEx) when (sinkEx is not OutOfMemoryException)
        {
            // A broken log sink must not stop the pump either; the fault is already in the ledger.
        }
    }

    private StreamWriter GetOrCreateLogWriter(string logFilePath)
    {
        string fullPath = Path.GetFullPath(logFilePath);
        if (_logWriters.TryGetValue(fullPath, out StreamWriter? existing))
            return existing;

        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
        _logWriters[fullPath] = writer;
        return writer;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            foreach (StreamWriter writer in _logWriters.Values)
            {
                try
                {
                    writer.Flush();
                    writer.Dispose();
                }
                catch
                {
                    // Best-effort flush on supervisor shutdown.
                }
            }

            _logWriters.Clear();
        }
    }
}
