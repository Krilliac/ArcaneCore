using System.Buffers;
using System.Globalization;
using System.Text;

namespace ArcaneCore.Kernel.Logging;

/// <summary>When the active file is rolled and how many rolled segments stay.</summary>
/// <param name="RollSizeBytes">Roll before a write would take the active file past this size; 0 never rolls by size.</param>
/// <param name="RollDaily">Roll at the first write on a new calendar day (in the clock's frame).</param>
/// <param name="Retain">Rolled segments kept; 0 keeps all.</param>
public readonly record struct RollingFilePolicy(long RollSizeBytes, bool RollDaily, int Retain)
{
    public static RollingFilePolicy FromOptions(int rollSizeMb, bool rollDaily, int retain)
        => new(rollSizeMb <= 0 ? 0 : rollSizeMb * 1024L * 1024L, rollDaily, retain);
}

/// <summary>
/// Appends UTF-8 lines to one active file and rolls it by size and/or day. A rolled segment is renamed to
/// <c>name-yyyyMMdd-NNN.ext</c> next to the active file (the date is the day the segment was opened, NNN counts rolls on that
/// day), after which the oldest segments beyond <see cref="RollingFilePolicy.Retain"/> are deleted. On start an existing active
/// file is continued (its size counts toward the size roll) and, with daily rolling, archived first when it is from an earlier day.
/// <para>
/// Not thread-safe: one writer thread (the <see cref="QueuedLineWriter"/>) owns it. Fail-closed on I/O trouble: a failed write
/// closes the file, reports once to <c>diagnostics</c> (stderr by default), drops lines for <see cref="RetryDelay"/>, then tries to
/// reopen; the lines lost meanwhile are counted (<see cref="LostLines"/>) and reported when writing resumes. It never throws into
/// the caller and never blocks longer than the write itself.
/// </para>
/// </summary>
public sealed class RollingFileWriter : ILineWriter
{
    /// <summary>How long writes are dropped after a failure before the file is reopened.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private readonly string _path;
    private readonly string _directory;
    private readonly string _stem;
    private readonly string _extension;
    private readonly RollingFilePolicy _policy;
    private readonly Func<DateTime> _clock;
    private readonly TextWriter _diagnostics;
    private readonly Func<string, FileStream> _open;
    private readonly Encoder _encoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetEncoder();
    private FileStream? _stream;
    private long _size;
    private DateTime _segmentDate;
    private DateTime _retryAt;
    private bool _failing;

    /// <param name="path">The active file; its directory is created on first open.</param>
    /// <param name="clock">The clock daily rolling and segment names use (the provider passes the configured timestamp clock).</param>
    /// <param name="diagnostics">Where open/write failures are reported, once per failure streak; null means stderr.</param>
    /// <param name="open">How the active file is opened for appending (tests inject a stream that fails); null means <see cref="OpenAppend"/>.</param>
    public RollingFileWriter(string path, RollingFilePolicy policy, Func<DateTime>? clock = null, TextWriter? diagnostics = null, Func<string, FileStream>? open = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _directory = Path.GetDirectoryName(_path) ?? Directory.GetCurrentDirectory();
        _stem = Path.GetFileNameWithoutExtension(_path);
        _extension = Path.GetExtension(_path);
        _policy = policy;
        _clock = clock ?? (static () => DateTime.UtcNow);
        _diagnostics = diagnostics ?? Console.Error;
        _open = open ?? OpenAppend;
    }

    /// <summary>The production open: append, shared read/write (tail -f and a second daemon on the same file work), 64 KiB buffer.</summary>
    public static FileStream OpenAppend(string path)
        => new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, bufferSize: 64 * 1024);

    /// <summary>Bytes in the active file.</summary>
    public long CurrentSize => _size;

    /// <summary>Segments rolled since start.</summary>
    public int Rolls { get; private set; }

    /// <summary>Lines dropped while the file could not be written.</summary>
    public long LostLines { get; private set; }

    /// <summary>Open or write failures since start.</summary>
    public long Failures { get; private set; }

    /// <summary>The active file's full path.</summary>
    public string ActivePath => _path;

    /// <summary>Rolled segments next to the active file, newest first.</summary>
    public IReadOnlyList<string> RolledSegments()
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        string[] files = Directory.GetFiles(_directory, _stem + "-*" + _extension);
        Array.Sort(files, StringComparer.Ordinal);
        Array.Reverse(files);
        return files;
    }

    public void Write(ReadOnlySpan<char> line)
    {
        if (line.IsEmpty)
        {
            return;
        }

        if (_failing && _clock() < _retryAt)
        {
            LostLines++;
            return;
        }

        byte[] bytes = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(line.Length));
        try
        {
            _encoder.Convert(line, bytes, flush: true, out int charsUsed, out int byteCount, out bool completed);
            System.Diagnostics.Debug.Assert(completed && charsUsed == line.Length, "the buffer is sized for the whole line");

            DateTime now = _clock();
            if (_stream is null)
            {
                Open(now);
            }

            if ((_policy.RollDaily && now.Date != _segmentDate) || (_policy.RollSizeBytes > 0 && _size > 0 && _size + byteCount > _policy.RollSizeBytes))
            {
                Roll(now);
            }

            _stream!.Write(bytes, 0, byteCount);
            _size += byteCount;
            if (_failing)
            {
                _failing = false;
                _diagnostics.WriteLine($"ArcaneCore logging: writing to {_path} resumed; {LostLines.ToString(CultureInfo.InvariantCulture)} line(s) were lost.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Fail(ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    public void Flush()
    {
        try
        {
            _stream?.Flush();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail(ex);
        }
    }

    public void Dispose()
    {
        Flush(); // a failed flush has already closed and released the stream through Fail()
        FileStream? stream = _stream;
        _stream = null;
        try
        {
            stream?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Failures++;
            _diagnostics.WriteLine($"ArcaneCore logging: cannot close {_path}: {ex.Message}.");
        }
    }

    private void Open(DateTime now)
    {
        Directory.CreateDirectory(_directory);
        var info = new FileInfo(_path);
        if (info.Exists && info.Length > 0)
        {
            _size = info.Length;
            DateTime written = now.Kind == DateTimeKind.Utc ? info.LastWriteTimeUtc : info.LastWriteTime;
            _segmentDate = written.Date;
            if (_policy.RollDaily && _segmentDate != now.Date)
            {
                Roll(now);
                return;
            }
        }
        else
        {
            _size = 0;
            _segmentDate = now.Date;
        }

        _stream = _open(_path);
    }

    private void Roll(DateTime now)
    {
        _stream?.Dispose();
        _stream = null;
        if (File.Exists(_path))
        {
            string date = _segmentDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            string prefix = _stem + "-" + date + "-";
            int sequence = 1;
            foreach (string existing in Directory.GetFiles(_directory, prefix + "*" + _extension))
            {
                string name = Path.GetFileNameWithoutExtension(existing);
                if (name.Length > prefix.Length && int.TryParse(name.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= sequence)
                {
                    sequence = n + 1;
                }
            }

            string target = Path.Combine(_directory, prefix + sequence.ToString("000", CultureInfo.InvariantCulture) + _extension);
            File.Move(_path, target, overwrite: false);
            Rolls++;
            Prune();
        }

        _size = 0;
        _segmentDate = now.Date;
        _stream = _open(_path);
    }

    private void Prune()
    {
        if (_policy.Retain <= 0)
        {
            return;
        }

        IReadOnlyList<string> segments = RolledSegments();
        for (int i = _policy.Retain; i < segments.Count; i++)
        {
            try
            {
                File.Delete(segments[i]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Failures++;
                _diagnostics.WriteLine($"ArcaneCore logging: cannot delete old log segment {segments[i]}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Enters the failure streak: the state (<c>_stream</c> released, <c>_retryAt</c>, <c>_failing</c>) is set before anything that can
    /// throw, because disposing a <see cref="FileStream"/> with a dirty buffer re-flushes and raises the same IOException again; that
    /// second exception is swallowed here (the file is closed either way) so it can never leave the writer holding a closed stream.
    /// </summary>
    private void Fail(Exception ex)
    {
        Failures++;
        LostLines++;
        FileStream? stream = _stream;
        _stream = null;
        _retryAt = _clock() + RetryDelay;
        bool first = !_failing;
        _failing = true;
        try
        {
            stream?.Dispose();
        }
        catch (Exception disposeEx) when (disposeEx is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // the dirty buffer could not be flushed on close; the lines in it are what LostLines already counts
        }

        if (first)
        {
            _diagnostics.WriteLine($"ArcaneCore logging: cannot write {_path}: {ex.Message}. Lines are dropped until the file can be reopened (retry every {RetryDelay.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s).");
        }
    }
}
