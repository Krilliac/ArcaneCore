using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.HotCode;

/// <summary>
/// Append-only audit trail of hot-code decisions: one tab-separated line per event
/// (UTC timestamp, kind, detail). A path of null or empty disables the file. The file lives
/// in the same account the server runs as, so it records what happened; it is not tamper-proof.
/// <para>
/// <see cref="Record"/> is called from GM commands and the feature host on the world thread, so it only queues the line; one
/// background writer per file appends the lines in the order they were queued (every instance on the same path shares the writer).
/// <see cref="Flush"/> waits until everything queued so far is on disk, and the queue is flushed when the process exits. A write
/// that fails is counted in the <see cref="FaultLedger"/> (<c>hotcode.audit-write</c>) and its lines are lost; a flush that times out is counted as
/// <c>hotcode.audit-flush</c> (and logged). The queue is bounded (a wedged disk cannot grow it): when it is full <see cref="Record"/> drops the line
/// and counts it as <c>hotcode.audit-dropped</c> instead of blocking the world thread. A failure while reporting a failed write is
/// counted as <c>hotcode.audit-report</c>.
/// </para>
/// </summary>
public sealed class HotCodeAudit(string? path, Func<DateTimeOffset>? clock = null, ILogger? logger = null)
{
    private static readonly ConcurrentDictionary<string, Appender> Appenders = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly TimeSpan DefaultFlushBudget = TimeSpan.FromSeconds(10);

    /// <summary>Test seam: awaited by the writer before each disk append (null in production). Receives the full path.</summary>
    internal static Func<string, Task>? BeforeWriteHook { get; set; }

    /// <summary>How long <see cref="Flush"/> waits (the enqueue of its barrier included); tests shorten it.</summary>
    internal TimeSpan FlushTimeout { get; init; } = DefaultFlushBudget;

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    static HotCodeAudit() => AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushAll();

    public bool HasFile => !string.IsNullOrWhiteSpace(path);

    /// <summary>Queue one audit line; never touches the disk on the calling thread.</summary>
    public void Record(string kind, string detail)
    {
        if (!HasFile)
        {
            return;
        }

        string line = $"{_clock():O}\t{kind}\t{detail.ReplaceLineEndings(" ")}{Environment.NewLine}";
        Appenders.GetOrAdd(Path.GetFullPath(path!), static full => new Appender(full)).Enqueue(line, logger);
    }

    /// <summary>Wait until every line queued so far (by any instance on this path) is written. Not for the world thread.</summary>
    public void Flush()
    {
        if (HasFile && Appenders.TryGetValue(Path.GetFullPath(path!), out Appender? appender))
        {
            if (!appender.FlushAsync().Wait(FlushTimeout))
            {
                RecordFlushTimeout(Path.GetFullPath(path!), FlushTimeout, logger);
            }
        }
    }

    /// <summary>Wait until every line queued so far on every audit file is written (process exit, tests).</summary>
    public static void FlushAll()
    {
        Task[] pending = [.. Appenders.Values.Select(a => a.FlushAsync())];
        if (!Task.WaitAll(pending, DefaultFlushBudget))
        {
            RecordFlushTimeout("all audit files", DefaultFlushBudget, null);
        }
    }

    /// <summary>The flush gave up waiting (a wedged disk): the queued lines may be lost, so the timeout is counted, not silent.</summary>
    private static void RecordFlushTimeout(string target, TimeSpan budget, ILogger? logger)
    {
        var ex = new TimeoutException($"hot-code audit flush of {target} did not finish within {budget.TotalSeconds:0.###} s");
        FaultLedger.Shared.Record("hotcode.audit-flush", ex);
        try
        {
            if (logger is not null)
            {
                logger.LogFault("hotcode-audit-flush", ex, "hot-code audit flush of {Target} timed out; queued lines may be lost", target);
            }
            else
            {
                Console.Error.WriteLine($"hot-code audit flush of {target} timed out; queued lines may be lost: {ex.Message}");
            }
        }
        catch (Exception reportEx) when (reportEx is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("hotcode.audit-report", reportEx);
        }
    }

    private sealed class Appender(string fullPath)
    {
        private const int MaxBatchChars = 64 * 1024;

        // Bounded so a wedged disk cannot grow the queue without limit. A full queue drops the line (Record never blocks the world thread);
        // only a flush barrier waits for room, asynchronously, inside the flush budget.
        private readonly Channel<Entry> _queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(4096)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        private int _started;
        private ILogger? _logger; // the latest instance that has one supplies it (every instance on the path shares this writer)

        public void Enqueue(string line, ILogger? logger)
        {
            if (logger is not null)
            {
                Volatile.Write(ref _logger, logger);
            }

            EnsureStarted();
            if (!_queue.Writer.TryWrite(new Entry(line, null)))
            {
                ReportDrop();
            }
        }

        /// <summary>Queue a barrier and complete when the writer has passed it. Never blocks the caller: a full queue is waited for asynchronously.</summary>
        public Task FlushAsync()
        {
            if (Volatile.Read(ref _started) == 0)
            {
                return Task.CompletedTask;
            }

            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entry = new Entry(null, barrier);
            if (_queue.Writer.TryWrite(entry))
            {
                return barrier.Task;
            }

            return WaitForRoomThenAwaitAsync(entry, barrier.Task);
        }

        private async Task WaitForRoomThenAwaitAsync(Entry entry, Task barrier)
        {
            await _queue.Writer.WriteAsync(entry).ConfigureAwait(false);
            await barrier.ConfigureAwait(false);
        }

        private void EnsureStarted()
        {
            if (Interlocked.Exchange(ref _started, 1) == 0)
            {
                _ = Task.Run(RunAsync);
            }
        }

        /// <summary>The queue is full (the writer is wedged or far behind): the line is lost, but counted and logged (throttled), never silent.</summary>
        private void ReportDrop()
        {
            var ex = new InvalidOperationException($"hot-code audit queue for {fullPath} is full; an audit line was dropped");
            FaultLedger.Shared.Record("hotcode.audit-dropped", ex);
            try
            {
                if (Volatile.Read(ref _logger) is { } log)
                {
                    log.LogFault("hotcode-audit-dropped", ex, "hot-code audit queue for {Path} is full; an audit line was dropped", fullPath);
                }
            }
            catch (Exception reportEx) when (reportEx is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("hotcode.audit-report", reportEx);
            }
        }

        private async Task RunAsync()
        {
            var text = new StringBuilder();
            var barriers = new List<TaskCompletionSource>();
            ChannelReader<Entry> reader = _queue.Reader;
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                text.Clear();
                barriers.Clear();
                while (text.Length < MaxBatchChars && reader.TryRead(out Entry entry))
                {
                    if (entry.Line is not null)
                    {
                        text.Append(entry.Line);
                    }
                    else
                    {
                        barriers.Add(entry.Barrier!);
                    }
                }

                if (text.Length > 0)
                {
                    try
                    {
                        if (Path.GetDirectoryName(fullPath) is { Length: > 0 } directory)
                        {
                            Directory.CreateDirectory(directory);
                        }

                        if (BeforeWriteHook is { } hook)
                        {
                            await hook(fullPath).ConfigureAwait(false);
                        }

                        await File.AppendAllTextAsync(fullPath, text.ToString()).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        ReportWriteFailure(ex);
                    }
                }

                foreach (TaskCompletionSource barrier in barriers)
                {
                    barrier.TrySetResult();
                }
            }
        }

        /// <summary>Count and log a failed write. Reporting must never end the writer loop: a throwing logger or console is swallowed (the fault is already counted).</summary>
        private void ReportWriteFailure(Exception ex)
        {
            FaultLedger.Shared.Record("hotcode.audit-write", ex);
            try
            {
                if (Volatile.Read(ref _logger) is { } log)
                {
                    log.LogFault("hotcode-audit-write", ex, "hot-code audit write to {Path} failed; its queued lines are lost", fullPath);
                }
                else
                {
                    Console.Error.WriteLine($"hot-code audit write to {fullPath} failed; its queued lines are lost: {ex.Message}");
                }
            }
            catch (Exception reportEx) when (reportEx is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("hotcode.audit-report", reportEx);
            }
        }

        private readonly record struct Entry(string? Line, TaskCompletionSource? Barrier);
    }
}
