using Microsoft.Extensions.Logging;

namespace ArcaneCore.Kernel.Ops.ServerConsole;

/// <summary>
/// Reads console lines on one dedicated background thread and hands each trimmed, non-empty line to a handler (the
/// mangosd CliRunnable loop, cmangos mangos-classic CliRunnable.cpp). End of input (null from <c>ReadLine</c>) ends the
/// reader only; the server keeps running. <see cref="Stop"/> never joins: a blocked <c>ReadLine</c> cannot be
/// interrupted, and a background thread does not keep the process alive.
/// <para>Thread affinity: <see cref="Start"/> and <see cref="Stop"/> are called by the host thread; the handler runs on the reader thread.</para>
/// </summary>
public sealed class ConsoleLineReader(TextReader input, string threadName)
{
    private readonly object _gate = new();
    private volatile bool _stopped;

    /// <summary>The reader thread; null before <see cref="Start"/>. Exposed for tests.</summary>
    public Thread? Thread { get; private set; }

    /// <summary>Start the reader thread. Throws when already started.</summary>
    public void Start(Action<string> onLine, ILogger logger) => Start(onLine, logger, onEndOfInput: null);

    /// <summary>
    /// Start the reader thread; <paramref name="onEndOfInput"/> (optional) runs on the reader thread when the input ends or closes
    /// (a supervised child begins its graceful stop then). Throws when already started.
    /// </summary>
    public void Start(Action<string> onLine, ILogger logger, Action? onEndOfInput)
    {
        ArgumentNullException.ThrowIfNull(onLine);
        ArgumentNullException.ThrowIfNull(logger);
        lock (_gate)
        {
            if (Thread is not null)
            {
                throw new InvalidOperationException("The console reader is already started.");
            }

            Thread = new Thread(() => Run(onLine, logger, onEndOfInput)) { IsBackground = true, Name = threadName };
            Thread.Start();
        }
    }

    /// <summary>Ask the reader to stop at the next line; returns at once.</summary>
    public void Stop() => _stopped = true;

    private static void EndOfInput(Action? onEndOfInput, ILogger logger)
    {
        try
        {
            onEndOfInput?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Diagnostics.FaultLedger.Shared.Record("console.line-reader", ex);
            logger.LogError(ex, "console end-of-input handler failed");
        }
    }

    private void Run(Action<string> onLine, ILogger logger, Action? onEndOfInput)
    {
        while (!_stopped)
        {
            string? line;
            try
            {
                line = input.ReadLine();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                logger.LogInformation("console input closed ({Reason})", ex.GetType().Name);
                EndOfInput(onEndOfInput, logger);
                return;
            }

            if (line is null)
            {
                logger.LogInformation(onEndOfInput is null ? "console input closed; the server keeps running" : "console input closed");
                EndOfInput(onEndOfInput, logger);
                return;
            }

            string trimmed = line.Trim();
            if (trimmed.Length == 0 || _stopped)
            {
                continue;
            }

            try
            {
                onLine(trimmed);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Diagnostics.FaultLedger.Shared.Record("console.line-reader", ex);
                logger.LogError(ex, "console command handler failed");
            }
        }
    }
}
