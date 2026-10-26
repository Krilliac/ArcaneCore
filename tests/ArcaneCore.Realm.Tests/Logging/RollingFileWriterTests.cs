using ArcaneCore.Kernel.Logging;
using Xunit;

namespace ArcaneCore.Realm.Tests.Logging;

/// <summary>Size and daily rolling, retention pruning, continuation of an existing file and fail-closed behaviour of the file sink.</summary>
public sealed class RollingFileWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arcanecore-logtests-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 10, 4, 10, 0, 0, DateTimeKind.Utc);

    private string LogPath => Path.Combine(_dir, "world.log");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private RollingFileWriter Writer(long rollSizeBytes, bool daily, int retain, TextWriter? diagnostics = null)
        => new(LogPath, new RollingFilePolicy(rollSizeBytes, daily, retain), () => _now, diagnostics ?? TextWriter.Null);

    // The writer keeps the live file open for writing (FileShare.ReadWrite). File.ReadAllText/ReadAllBytes ask for
    // FileShare.Read, which Windows refuses while a writer holds the file (Linux has no such lock), so read the way a
    // log tailer does: share read and write.
    private static byte[] ReadSharedBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static string ReadShared(string path) => new System.Text.UTF8Encoding(false).GetString(ReadSharedBytes(path));

    private static string Line(int n) => $"line {n:000}\n"; // 9 bytes

    /// <summary>
    /// Two processes (the realm and world daemons) append to one shared debug log: every line of both must survive. A plain append
    /// stream seeks to the end once at open and then writes at its own offset, so the two overwrote each other's lines (the 2026-10-16
    /// live log lost the world's startup lines, "Bot chat provider" among them). Two streams in one process show the same race.
    /// </summary>
    [Fact]
    public void TwoAppenders_OnOneFile_KeepEveryLine()
    {
        // The fix is the Windows append-only open (FILE_APPEND_DATA); .NET on Unix has no O_APPEND FileStream, so there the race remains
        // and this test proves nothing. The servers run on Windows.
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_dir);
        using (FileStream a = RollingFileWriter.OpenAppend(LogPath))
        using (FileStream b = RollingFileWriter.OpenAppend(LogPath))
        {
            for (int i = 0; i < 5; i++)
            {
                a.Write(System.Text.Encoding.ASCII.GetBytes($"A{i}\n"));
                a.Flush();
                b.Write(System.Text.Encoding.ASCII.GetBytes($"B{i}\n"));
                b.Flush();
            }
        }

        string[] lines = ReadShared(LogPath).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(10, lines.Length);
        for (int i = 0; i < 5; i++)
        {
            Assert.Contains($"A{i}", lines);
            Assert.Contains($"B{i}", lines);
        }
    }

    /// <summary>
    /// Two writers configured with one path (the realm and the world reading the same profile appsettings.json, live 2026-10-16:
    /// torn and interleaved JSON lines in world.debug.jsonl, and each process rolling the shared file on its own count so one kept
    /// writing into the segment the other had just renamed) must never share a file: the first owns the configured path, the second
    /// falls back to a per-process name, says so once, and every file holds the lines of exactly one writer.
    /// </summary>
    [Fact]
    public void TwoWriters_OnOneConfiguredPath_NeverShareAFile()
    {
        var diagnostics = new StringWriter();
        using (RollingFileWriter first = Writer(rollSizeBytes: 0, daily: false, retain: 0))
        using (RollingFileWriter second = Writer(rollSizeBytes: 0, daily: false, retain: 0, diagnostics))
        {
            for (int i = 0; i < 5; i++)
            {
                first.Write($"A{i}\n");
                first.Flush();
                second.Write($"B{i}\n");
                second.Flush();
            }

            Assert.Equal(LogPath, first.ActivePath);
            Assert.NotEqual(first.ActivePath, second.ActivePath);
            Assert.Equal(_dir, Path.GetDirectoryName(second.ActivePath));
            Assert.Contains("in use by another writer", diagnostics.ToString(), StringComparison.Ordinal);
            Assert.Equal(0, first.LostLines + second.LostLines);
        }

        string[] logs = Directory.GetFiles(_dir, "*.log");
        Assert.Equal(2, logs.Length);
        foreach (string log in logs)
        {
            string[] lines = ReadShared(log).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(5, lines.Length);
            Assert.Single(lines.Select(l => l[0]).Distinct());
        }
    }

    /// <summary>The configured path is taken again once its owner is disposed (a daemon restart keeps its file name).</summary>
    [Fact]
    public void ConfiguredPath_IsReleasedOnDispose()
    {
        using (RollingFileWriter first = Writer(rollSizeBytes: 0, daily: false, retain: 0))
        {
            first.Write("one\n");
        }

        using RollingFileWriter again = Writer(rollSizeBytes: 0, daily: false, retain: 0);
        again.Write("two\n");
        again.Flush();
        Assert.Equal(LogPath, again.ActivePath);
        Assert.Equal("one\ntwo\n", ReadShared(LogPath));
    }

    /// <summary>A per-process fallback file rolls under its own name; the owner of the configured path never counts or prunes its segments.</summary>
    [Fact]
    public void FallbackWriter_RollsItsOwnSegments_WithoutTouchingTheOwners()
    {
        using RollingFileWriter first = Writer(rollSizeBytes: 10, daily: false, retain: 1);
        using RollingFileWriter second = Writer(rollSizeBytes: 10, daily: false, retain: 1);
        for (int i = 0; i < 3; i++)
        {
            first.Write(Line(i));
            second.Write(Line(i));
        }

        Assert.Equal(2, first.Rolls);
        Assert.Equal(2, second.Rolls);
        Assert.Equal(["world-20261004-002.log"], first.RolledSegments().Select(f => Path.GetFileName(f)).ToArray());
        string secondStem = Path.GetFileNameWithoutExtension(second.ActivePath);
        Assert.Equal([secondStem + "-20261004-002.log"], second.RolledSegments().Select(f => Path.GetFileName(f)).ToArray());
    }

    /// <summary>
    /// A lock file this writer may not open (access denied: a read-only file here; on Windows also a lock whose delete-on-close is still
    /// pending when the next writer opens it) is a name it cannot own, like one held by another writer: it falls back instead of
    /// failing and dropping lines.
    /// </summary>
    [NotPrivilegedFact]
    public void AccessDeniedOnTheLock_IsTreatedAsHeld_AndTheWriterFallsBack()
    {
        Directory.CreateDirectory(_dir);
        string lockPath = LogPath + ".lock";
        File.WriteAllText(lockPath, string.Empty);
        File.SetAttributes(lockPath, FileAttributes.ReadOnly);
        try
        {
            var diagnostics = new StringWriter();
            using RollingFileWriter writer = Writer(rollSizeBytes: 0, daily: false, retain: 0, diagnostics);
            writer.Write("one\n");
            writer.Flush();

            Assert.Equal(0, writer.Failures);
            Assert.NotEqual(LogPath, writer.ActivePath);
            Assert.Equal("one\n", ReadShared(writer.ActivePath));
            Assert.Contains("in use by another writer", diagnostics.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            // A writer that wrongly took the lock deletes it on dispose (delete-on-close); do not let this cleanup mask the assertion.
            if (File.Exists(lockPath))
            {
                File.SetAttributes(lockPath, FileAttributes.Normal);
            }
        }
    }

    /// <summary>
    /// The read-only attribute is a permission the test relies on being enforced. Root bypasses
    /// permission checks on Unix, so the writer legitimately opens the "denied" lock, owns the name, and deletes the lock when it is disposed.
    /// </summary>
    private sealed class NotPrivilegedFactAttribute : FactAttribute
    {
        public NotPrivilegedFactAttribute()
        {
            if (!OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess)
            {
                Skip = "Running as root (effective uid 0): permission checks are bypassed, so a read-only lock file is not access-denied and the writer opens it.";
            }
        }
    }

    /// <summary>With the configured name and both fallback names held elsewhere, the writer fails closed: no file is written under any of them.</summary>
    [Fact]
    public void AllThreeNamesHeld_WriterFailsClosed_WithoutWritingAnyOfThem()
    {
        Directory.CreateDirectory(_dir);
        string process = new([.. (Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? string.Empty)
            .Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' ? c : '_')]);
        if (process.Length == 0)
        {
            process = "process";
        }

        string[] names =
        [
            LogPath,
            Path.Combine(_dir, $"world.{process}.log"),
            Path.Combine(_dir, $"world.{process}.{Environment.ProcessId}.log"),
        ];
        var held = names.Select(n => new FileStream(n + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)).ToList();
        try
        {
            var diagnostics = new StringWriter();
            using RollingFileWriter writer = Writer(rollSizeBytes: 0, daily: false, retain: 0, diagnostics);
            writer.Write("one\n");
            writer.Flush();

            Assert.Equal(1, writer.Failures);
            Assert.All(names, n => Assert.False(File.Exists(n), n + " was written"));
            Assert.Contains("ArcaneCore logging:", diagnostics.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            held.ForEach(s => s.Dispose());
        }
    }

    [Fact]
    public void RollsAtSize_AndNamesSegmentsByDateAndSequence()
    {
        using (RollingFileWriter writer = Writer(rollSizeBytes: 20, daily: false, retain: 0))
        {
            for (int i = 0; i < 5; i++)
            {
                writer.Write(Line(i)); // 2 lines fit (18 bytes); the third would reach 27 > 20 and rolls
            }

            Assert.Equal(2, writer.Rolls);
            Assert.Equal(9, writer.CurrentSize);
        }

        string[] files = Directory.GetFiles(_dir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;
        Assert.Equal(["world-20261004-001.log", "world-20261004-002.log", "world.log"], files);
        Assert.Equal("line 000\nline 001\n", ReadShared(Path.Combine(_dir, "world-20261004-001.log")));
        Assert.Equal("line 002\nline 003\n", ReadShared(Path.Combine(_dir, "world-20261004-002.log")));
        Assert.Equal("line 004\n", ReadShared(LogPath));
    }

    [Fact]
    public void RollsDaily_OnTheFirstLineOfANewDay()
    {
        using RollingFileWriter writer = Writer(rollSizeBytes: 0, daily: true, retain: 0);
        writer.Write(Line(1));
        _now = _now.AddHours(13); // 23:00 same day
        writer.Write(Line(2));
        Assert.Equal(0, writer.Rolls);
        _now = _now.AddHours(2); // 01:00 next day
        writer.Write(Line(3));
        writer.Flush();

        Assert.Equal(1, writer.Rolls);
        Assert.Equal("line 001\nline 002\n", ReadShared(Path.Combine(_dir, "world-20261004-001.log")));
        Assert.Equal("line 003\n", ReadShared(LogPath));
    }

    [Fact]
    public void Retention_DeletesTheOldestSegmentsBeyondRetain()
    {
        using RollingFileWriter writer = Writer(rollSizeBytes: 10, daily: false, retain: 2);
        for (int i = 0; i < 6; i++)
        {
            writer.Write(Line(i)); // every line rolls the previous one
        }

        Assert.Equal(5, writer.Rolls);
        IReadOnlyList<string> kept = writer.RolledSegments();
        Assert.Equal(2, kept.Count);
        Assert.Equal(["world-20261004-005.log", "world-20261004-004.log"], kept.Select(f => Path.GetFileName(f)).ToArray());
        Assert.Equal("line 004\n", ReadShared(kept[0]));
    }

    [Fact]
    public void RetainZero_KeepsEverySegment()
    {
        using RollingFileWriter writer = Writer(rollSizeBytes: 10, daily: false, retain: 0);
        for (int i = 0; i < 6; i++)
        {
            writer.Write(Line(i));
        }

        Assert.Equal(5, writer.RolledSegments().Count);
    }

    [Fact]
    public void ExistingFile_IsContinued_AndCountsTowardTheSizeRoll()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(LogPath, "old content 15b\n"); // 16 bytes
        File.SetLastWriteTimeUtc(LogPath, _now);
        using RollingFileWriter writer = Writer(rollSizeBytes: 20, daily: true, retain: 0);
        writer.Write(Line(1)); // 16 + 9 > 20: roll first
        writer.Flush();

        Assert.Equal(1, writer.Rolls);
        Assert.Equal("old content 15b\n", ReadShared(Path.Combine(_dir, "world-20261004-001.log")));
        Assert.Equal("line 001\n", ReadShared(LogPath));
    }

    [Fact]
    public void ExistingFileFromAnEarlierDay_IsArchivedBeforeTheFirstWrite()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(LogPath, "yesterday\n");
        File.SetLastWriteTimeUtc(LogPath, _now.AddDays(-1));
        using RollingFileWriter writer = Writer(rollSizeBytes: 0, daily: true, retain: 0);
        writer.Write(Line(1));
        writer.Flush();

        Assert.Equal("yesterday\n", ReadShared(Path.Combine(_dir, "world-20261003-001.log")));
        Assert.Equal("line 001\n", ReadShared(LogPath));
    }

    [Fact]
    public void Utf8_IsWrittenWithoutBom_AndSizeCountsBytes()
    {
        using RollingFileWriter writer = Writer(rollSizeBytes: 0, daily: false, retain: 0);
        writer.Write("héllo\n"); // 7 bytes
        writer.Flush();
        Assert.Equal(7, writer.CurrentSize);
        byte[] bytes = ReadSharedBytes(LogPath);
        Assert.Equal(7, bytes.Length);
        Assert.NotEqual(0xEF, bytes[0]);
    }

    [Fact]
    public void UnwritablePath_ReportsOnce_DropsAndCounts_NeverThrows()
    {
        Directory.CreateDirectory(_dir);
        string blocker = Path.Combine(_dir, "blocked");
        Directory.CreateDirectory(blocker); // a directory where the file should be: every open fails on every OS
        var diagnostics = new StringWriter();
        using var writer = new RollingFileWriter(blocker, new RollingFilePolicy(0, false, 0), () => _now, diagnostics);
        writer.Write("one\n");
        writer.Write("two\n"); // inside RetryDelay: dropped without touching the disk
        _now += RollingFileWriter.RetryDelay;
        writer.Write("three\n"); // retried, fails again, still one report (same streak)

        Assert.Equal(3, writer.LostLines);
        Assert.Equal(2, writer.Failures);
        Assert.Single(diagnostics.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("cannot write", diagnostics.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A failure on an already open file (ENOSPC/EIO at flush time, the buffered write itself having succeeded): disposing the dirty
    /// stream re-flushes and throws again. That second exception must not escape <c>Fail()</c>, or the writer keeps a closed stream
    /// and every later call dies with ObjectDisposedException instead of dropping, retrying and reporting the lost count.
    /// </summary>
    [Fact]
    public void FlushFailureOnAnOpenFile_ReportsOnce_DropsForRetryDelay_ThenReopensAndReportsLost()
    {
        var diagnostics = new StringWriter();
        var disk = new FailingDisk();
        using var writer = new RollingFileWriter(LogPath, new RollingFilePolicy(0, false, 0), () => _now, diagnostics, disk.Open);

        writer.Write("one\n"); // buffered: succeeds
        writer.Flush();        // the disk is full: Flush throws, and so does the flush inside Dispose

        Assert.Equal(1, writer.Failures);
        Assert.Equal(1, writer.LostLines);
        Assert.Contains("cannot write", diagnostics.ToString(), StringComparison.Ordinal);

        writer.Write("two\n"); // inside RetryDelay: dropped, no disk access, no exception
        writer.Flush();
        Assert.Equal(2, writer.LostLines);
        Assert.Equal(1, disk.Opens);

        disk.Full = false;
        _now += RollingFileWriter.RetryDelay;
        writer.Write("three\n"); // reopened on a working disk
        writer.Flush();

        Assert.Equal(2, disk.Opens);
        Assert.Equal(2, writer.LostLines);
        Assert.Equal(1, writer.Failures);
        string[] lines = diagnostics.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("resumed; 2 line(s) were lost", lines[1], StringComparison.Ordinal);
        Assert.Equal("three\n", ReadShared(LogPath));
    }

    [Fact]
    public void RollBlockedByExternalReaderWithoutDeleteShare_ContinuesOnActiveFileWithoutDroppingLines()
    {
        var diagnostics = new StringWriter();
        using var writer = Writer(rollSizeBytes: 20, daily: false, retain: 0, diagnostics);
        writer.Write(Line(1));
        writer.Write(Line(2));
        writer.Flush();

        // Hold the active file open without FileShare.Delete so File.Move fails on Windows when the third line triggers a roll.
        using (var lockStream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            writer.Write(Line(3));
            writer.Write(Line(4));
            writer.Flush();
        }

        Assert.Equal(0, writer.LostLines);
        Assert.Contains("line 003\nline 004\n", ReadShared(LogPath), StringComparison.Ordinal);
    }

    /// <summary>Opens real files, but while <see cref="Full"/> every flush, including the one <see cref="Stream.Dispose()"/> performs on a dirty buffer, fails with ENOSPC.</summary>
    private sealed class FailingDisk
    {
        public bool Full { get; set; } = true;

        public int Opens { get; private set; }

        public FileStream Open(string path)
        {
            Opens++;
            return new FullStream(this, path);
        }

        private sealed class FullStream(FailingDisk disk, string path) : FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, bufferSize: 4096)
        {
            private bool _dirty;

            public override void Write(byte[] buffer, int offset, int count)
            {
                _dirty = true;
                if (!disk.Full)
                {
                    base.Write(buffer, offset, count);
                }
            }

            public override void Flush()
            {
                if (disk.Full && _dirty)
                {
                    throw new IOException("No space left on device");
                }

                base.Flush();
            }

            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);
                if (disposing && disk.Full && _dirty)
                {
                    _dirty = false;
                    throw new IOException("No space left on device");
                }
            }
        }
    }
}
