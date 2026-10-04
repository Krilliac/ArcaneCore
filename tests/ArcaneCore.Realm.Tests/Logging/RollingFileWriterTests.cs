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

    private static string Line(int n) => $"line {n:000}\n"; // 9 bytes

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
        Assert.Equal("line 000\nline 001\n", File.ReadAllText(Path.Combine(_dir, "world-20261004-001.log")));
        Assert.Equal("line 002\nline 003\n", File.ReadAllText(Path.Combine(_dir, "world-20261004-002.log")));
        Assert.Equal("line 004\n", File.ReadAllText(LogPath));
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
        Assert.Equal("line 001\nline 002\n", File.ReadAllText(Path.Combine(_dir, "world-20261004-001.log")));
        Assert.Equal("line 003\n", File.ReadAllText(LogPath));
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
        Assert.Equal("line 004\n", File.ReadAllText(kept[0]));
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
        Assert.Equal("old content 15b\n", File.ReadAllText(Path.Combine(_dir, "world-20261004-001.log")));
        Assert.Equal("line 001\n", File.ReadAllText(LogPath));
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

        Assert.Equal("yesterday\n", File.ReadAllText(Path.Combine(_dir, "world-20261003-001.log")));
        Assert.Equal("line 001\n", File.ReadAllText(LogPath));
    }

    [Fact]
    public void Utf8_IsWrittenWithoutBom_AndSizeCountsBytes()
    {
        using RollingFileWriter writer = Writer(rollSizeBytes: 0, daily: false, retain: 0);
        writer.Write("héllo\n"); // 7 bytes
        writer.Flush();
        Assert.Equal(7, writer.CurrentSize);
        byte[] bytes = File.ReadAllBytes(LogPath);
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
        Assert.Equal("three\n", File.ReadAllText(LogPath));
    }

    /// <summary>Opens real files, but while <see cref="Full"/> every flush, including the one <see cref="FileStream.Dispose()"/> performs on a dirty buffer, fails with ENOSPC.</summary>
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
