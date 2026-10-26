using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.World.HotCode;

/// <summary>
/// Append-only audit trail of hot-code decisions: one tab-separated line per event
/// (UTC timestamp, kind, detail). A path of null or empty disables the file. The file lives
/// in the same account the server runs as, so it records what happened; it is not tamper-proof.
/// <para>
/// <see cref="Record"/> is called from GM commands and the feature host on the world thread, so it only queues the line; one
/// background writer per file appends the lines in the order they were queued (every instance on the same path shares the writer).
/// <see cref="Flush"/> waits until everything queued so far is on disk, and the queue is flushed when the process exits. A write
/// that fails is counted in the <see cref="FaultLedger"/> (<c>hotcode.audit-write</c>) and its lines are lost.
/// </para>
/// </summary>
public sealed class HotCodeAudit(string? path, Func<DateTimeOffset>? clock = null)
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

        string line = $"{_clock():O}	{kind}	{detail.ReplaceLineEndings(" ")}{Environment.NewLine}";
        Appenders.GetOrAdd(Path.GetFullPath(path!), static full => new Appender(full)).Enqueue(line);
    }

    /// <summary>Wait until every line queued so far (by any instance on this path) is written. Not for the world thread.</summary>
    public void Flush()
    {
        if (HasFile && Appenders.TryGetValue(Path.GetFullPath(path!), out Appender? appender))
        {
            appender.FlushAsync().Wait(FlushBudget);
        }
    }

    /// <summary>Wait until every line queued so far on every audit file is written (process exit, tests).</summary>
    public static void FlushAll()
    {
        Task[] pending = [.. Appenders.Values.Select(a => a.FlushAsync())];
        Task.WaitAll(pending, FlushBudget);
    }

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

        public void Enqueue(string line) => Write(new Entry(line, null));

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
