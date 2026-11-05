using ArcaneCore.World.Cluster.Supervision;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Supervision;

/// <summary>
/// The per-child log file retry backoff: a log path that cannot be opened is tried again once per backoff window, not once per child line.
/// The proof is the file system, not the fault ledger (which is process-wide, shared with parallel tests and throttles its own log lines): the
/// path is blocked by a regular file where its directory should be, the block is lifted inside the window, and the file must not appear
/// until the window has passed.
/// </summary>
public sealed class SupervisedOutputMultiplexerTests : IDisposable
{
    private readonly string _tempDir;

    public SupervisedOutputMultiplexerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ArcaneCore.MuxTests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // A late handle on Windows: the temp directory is not the subject of the test.
        }
    }

    [Fact]
    public void RouteLine_WhenTheLogPathFails_RetriesOncePerBackoffWindow_NotOncePerLine()
    {
        string blocker = Path.Combine(_tempDir, "logs");
        string logPath = Path.Combine(blocker, "world.log");
        File.WriteAllText(blocker, "a file where the log directory should be");
        var clock = new ManualClock();
        var console = new StringWriter();
        using var mux = new SupervisedOutputMultiplexer(console) { TimeProvider = clock, LogRetryBackoff = TimeSpan.FromSeconds(30) };
        var config = new SupervisedProcessConfig { Name = "world", ExecutablePath = "x", LogFilePath = logPath };

        mux.RouteLine(config, "line-1"); // fails: CreateDirectory finds a file, and the failure starts the window
        File.Delete(blocker);
        Directory.CreateDirectory(blocker); // the path is writable from here on

        for (int i = 2; i <= 6; i++)
            mux.RouteLine(config, "line-" + i); // inside the window: no attempt, so no file

        Assert.False(File.Exists(logPath), "a line inside the backoff window must not touch the failed log path");

        clock.Advance(TimeSpan.FromSeconds(31));
        mux.RouteLine(config, "line-7"); // the window is over: one retry, which now succeeds

        Assert.True(File.Exists(logPath), "the first line after the window must retry the open");
        mux.Dispose();
        string written = File.ReadAllText(logPath);
        Assert.Equal("[world] line-7" + Environment.NewLine, written);

        // The console got every line throughout (the stdout pump never loses output to a log fault); only stderr/error lines go there in non-merged
        // modes, so the default Merged mode of this config is what shows all seven.
        string consoleText = console.ToString();
        for (int i = 1; i <= 7; i++)
            Assert.Contains("[world] line-" + i, consoleText, StringComparison.Ordinal);
    }

    [Fact]
    public void RouteLine_AfterASuccessfulRetry_WritesEveryLineWithoutABackoff()
    {
        string blocker = Path.Combine(_tempDir, "logs2");
        string logPath = Path.Combine(blocker, "realm.log");
        File.WriteAllText(blocker, "blocked");
        var clock = new ManualClock();
        using var mux = new SupervisedOutputMultiplexer(new StringWriter()) { TimeProvider = clock, LogRetryBackoff = TimeSpan.FromSeconds(10) };
        var config = new SupervisedProcessConfig { Name = "realm", ExecutablePath = "x", LogFilePath = logPath };

        mux.RouteLine(config, "lost");
        File.Delete(blocker);
        Directory.CreateDirectory(blocker);
        clock.Advance(TimeSpan.FromSeconds(11));
        mux.RouteLine(config, "kept-1");
        mux.RouteLine(config, "kept-2"); // the success cleared the failure: no new window
        mux.Dispose();

        Assert.Equal("[realm] kept-1" + Environment.NewLine + "[realm] kept-2" + Environment.NewLine, File.ReadAllText(logPath));
    }
}
