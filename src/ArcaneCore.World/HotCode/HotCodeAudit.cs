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
/// <c>hotcode.audit-flush</c>.
/// </para>
/// </summary>
public sealed class HotCodeAudit(string? path, Func<DateTimeOffset>? clock = null, ILogger? logger = null)
{
    private static readonly ConcurrentDictionary<string, Appender> Appenders = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly TimeSpan FlushBudget = TimeSpan.FromSeconds(10);

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
            if (!appender.FlushAsync().Wait(FlushBudget))
            {
                RecordFlushTimeout(Path.GetFullPath(path!));
            }
        }
    }

    /// <summary>Wait until every line queued so far on every audit file is written (process exit, tests).</summary>
    public static void FlushAll()
    {
        Task[] pending = [.. Appenders.Values.Select(a => a.FlushAsync())];
        if (!Task.WaitAll(pending, FlushBudget))
        {
            RecordFlushTimeout("all audit files");
        }
    }

    /// <summary>The flush gave up waiting (a wedged disk): the queued lines may be lost, so the timeout is counted, not silent.</summary>
    private static void RecordFlushTimeout(string target)
        => FaultLedger.Shared.Record("hotcode.audit-flush", new TimeoutException($"hot-code audit flush of {target} did not finish within {FlushBudget.TotalSeconds:0} s"));

    private sealed class Appender(string fullPath)
    {
        private const int MaxBatchChars = 64 * 1024;

        // Bounded so a wedged disk cannot grow the queue without limit; a full queue makes the caller wait (an audit line is not dropped).
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

            Write(new Entry(line, null));
        }

        public Task FlushAsync()
        {
            if (Volatile.Read(ref _started) == 0)
            {
                return Task.CompletedTask;
            }

            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Write(new Entry(null, barrier));
            return barrier.Task;
        }

        private void Write(Entry entry)
        {
            if (Interlocked.Exchange(ref _started, 1) == 0)
            {
                _ = Task.Run(RunAsync);
            }

            if (!_queue.Writer.TryWrite(entry))
            {
                _queue.Writer.WriteAsync(entry).AsTask().GetAwaiter().GetResult();
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

                        await File.AppendAllTextAsync(fullPath, text.ToString()).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        FaultLedger.Shared.Record("hotcode.audit-write", ex);
                        if (Volatile.Read(ref _logger) is { } log)
                        {
                            log.LogFault("hotcode-audit-write", ex, "hot-code audit write to {Path} failed; its queued lines are lost", fullPath);
                        }
                        else
                        {
                            Console.Error.WriteLine($"hot-code audit write to {fullPath} failed; its queued lines are lost: {ex.Message}");
                        }
                    }
                }

                foreach (TaskCompletionSource barrier in barriers)
                {
                    barrier.TrySetResult();
                }
            }
        }

        private readonly record struct Entry(string? Line, TaskCompletionSource? Barrier);
    }
}
