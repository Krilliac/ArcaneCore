using System.Net.Sockets;
using System.Text;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Ops.ServerConsole;
using ArcaneCore.Kernel.Ops.Watchdog.Counters;
using ArcaneCore.Kernel.Ops.Watchdog.Heartbeat;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Diagnostics;

/// <summary>
/// The wiring around the in-process crash dump: the process-wide one-dump guard across threads, the heartbeat writer's dumping announcement through
/// <see cref="CrashHandler.DumpAnnouncer"/>, the dumping lines of the sinks, and the shared owner-only directory helper. A stub writer is used, so no real dump is written.
/// </summary>
[Collection(DiagnosticsCollection.Name)]
public sealed class CrashDumpWiringTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-crash-wiring-" + Guid.NewGuid().ToString("N"));

    public CrashDumpWiringTests()
    {
        CrashHandler.Current?.Dispose(); // a fresh handler: the one-dump guard is per handler and must start open
    }

    public void Dispose()
    {
        CrashHandler.Current?.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the temp cleaner.
        }
    }

    [Fact]
    public async Task WriteCrashDump_FromTwoThreads_WritesOneDump_AndTheSecondThreadWaitsForTheFirst()
    {
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var writer = new BlockingWriter(entered, release);
        CrashHandler handler = InstallMiniHandler(writer, timeoutSeconds: 30);

        Task<string?> first = Task.Factory.StartNew(() => handler.WriteCrashDump(failFast: false), TaskCreationOptions.LongRunning);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(20)), "the first dump never started");
        Task<string?> second = Task.Factory.StartNew(() => handler.WriteCrashDump(failFast: false), TaskCreationOptions.LongRunning);

        // The second crashing thread must not run on to FailFast (which would end the process mid-dump): it waits for the first.
        await Task.Delay(500);
        Assert.False(second.IsCompleted, "the second thread returned while the first dump was still being written");

        release.Set();
        string? firstNote = await first.WaitAsync(TimeSpan.FromSeconds(30));
        string? secondNote = await second.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, writer.Writes);
        Assert.NotNull(firstNote);
        Assert.Equal(firstNote, secondNote);
    }

    [Fact]
    public void HeartbeatWriter_Start_WiresTheDumpAnnouncer_SoACrashDumpPublishesDumping_AndStopUnwiresIt()
    {
        CrashHandler handler = InstallMiniHandler(new BlockingWriter(null, null), timeoutSeconds: 2);
        var sink = new RecordingSink();
        var writer = new HeartbeatWriter(new HeartbeatOptions { Mode = HeartbeatMode.File }, [], new CounterRegistry(), new CapturingLogger(), () => new HeartbeatPlan(sink, 10, null));

        writer.Start();
        Assert.NotNull(handler.DumpAnnouncer);
        string? note = handler.WriteCrashDump(failFast: false);

        Assert.NotNull(note);
        Assert.Equal(["ready:ArcaneCore started", "dumping:6"], sink.Calls); // 3 x TimeoutSeconds, announced before the dump

        writer.Stop();
        Assert.Null(handler.DumpAnnouncer);
    }

    [Fact]
    public void TextWriterSink_Dumping_WritesTheMaxAndTheSupervisorToken()
    {
        var output = new StringWriter();
        var sink = new TextWriterHeartbeatSink(output, 7, now: () => new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.Zero), supervisorToken: "tok");

        Assert.True(sink.Dumping("dump", 360));

        string line = output.ToString().TrimEnd('\n');
        Assert.Equal("heartbeat 2026-10-29T10:00:00.000Z pid=7 dumping max=360 token=tok", line);
        Assert.True(SupervisorProtocol.TryParseDumping(line, out int max, out string token));
        Assert.Equal(360, max);
        Assert.Equal("tok", token);
    }

    [Fact]
    public void HeartbeatPlan_ForASupervisedChild_CarriesTheSupervisorTokenIntoTheDumpingLine()
    {
        var output = new StringWriter();
        var environment = new Dictionary<string, string>
        {
            [SupervisorProtocol.EnvironmentVariable] = "1",
            [SupervisorProtocol.TokenEnvironmentVariable] = "run-token",
        };

        HeartbeatPlan plan = HeartbeatPlan.Resolve(new HeartbeatOptions { Mode = HeartbeatMode.None }, name => environment.GetValueOrDefault(name), 42, _directory, output);
        Assert.True(plan.Sink.Dumping("dump", 90));

        Assert.Contains(" pid=42 dumping max=90 token=run-token", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SystemdSink_DumpingMessage_ResetsTheWatchdogToTheMaxPlusAMinute()
    {
        Assert.Equal("WATCHDOG=1\nWATCHDOG_USEC=420000000\nSTATUS=a b\n", Encoding.UTF8.GetString(SystemdNotifySink.DumpingMessage("a\nb", 360)));
        Assert.Equal("WATCHDOG=1\nWATCHDOG_USEC=61000000\nSTATUS=x\n", Encoding.UTF8.GetString(SystemdNotifySink.DumpingMessage("x", 0)));
    }

    [Fact]
    public void SystemdSink_Dumping_SendsTheDatagramToTheNotifySocket()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // no unix datagram sockets on Windows; the message itself is covered above
        }

        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "notify.sock");
        using var server = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
        server.Bind(new UnixDomainSocketEndPoint(path));
        server.ReceiveTimeout = 5000;
        using var sink = new SystemdNotifySink(path);

        Assert.True(sink.Dumping("writing a crash dump", 100), sink.LastError);

        byte[] buffer = new byte[512];
        int read = server.Receive(buffer);
        Assert.Equal("WATCHDOG=1\nWATCHDOG_USEC=160000000\nSTATUS=writing a crash dump\n", Encoding.UTF8.GetString(buffer, 0, read));
    }

    [Fact]
    public void EnsureDirectory_CreatesMissingParentsOwnerOnly_AndLeavesAnExistingDirectoryAlone()
    {
        string leaf = Path.Combine(_directory, "a", "dumps");
        var log = new List<string>();

        CrashDumpService.EnsureDirectory(leaf, (level, text) => log.Add(level + " " + text));

        Assert.True(Directory.Exists(leaf));
        if (OperatingSystem.IsWindows())
        {
            Assert.True(new DirectoryInfo(leaf).GetAccessControl().AreAccessRulesProtected);
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(leaf));
        }

        File.WriteAllText(Path.Combine(leaf, "keep.txt"), "x");
        CrashDumpService.EnsureDirectory(leaf, (level, text) => log.Add(level + " " + text));
        Assert.True(File.Exists(Path.Combine(leaf, "keep.txt")));
    }

    private CrashHandler InstallMiniHandler(ICrashDumpWriter writer, int timeoutSeconds)
    {
        DiagnosticsOptions options = DumpFree.Options(o =>
        {
            o.CrashDump.Mode = CrashDumpMode.Mini;
            o.CrashDump.Directory = _directory;
            o.CrashDump.TimeoutSeconds = timeoutSeconds;
        });
        CrashHandler handler = CrashHandler.Install(options, StandardErrorCrashSink.Instance);
        handler.DumpService = new CrashDumpService(writer, static _ => null);
        return handler;
    }

    /// <summary>Counts its writes, optionally signals entry and blocks until released, and writes a small file.</summary>
    private sealed class BlockingWriter(ManualResetEventSlim? entered, ManualResetEventSlim? release) : ICrashDumpWriter
    {
        private int _writes;

        public int Writes => Volatile.Read(ref _writes);

        public void Write(CrashDumpMode mode, string path)
        {
            Interlocked.Increment(ref _writes);
            entered?.Set();
            release?.Wait(TimeSpan.FromSeconds(30));
            File.WriteAllText(path, "dump " + mode);
        }
    }

    private sealed class RecordingSink : IHeartbeatSink
    {
        public List<string> Calls { get; } = [];

        public string Description => "recording";

        public string? LastError => null;

        public bool Ready(string status)
        {
            Calls.Add("ready:" + status);
            return true;
        }

        public bool Beat()
        {
            Calls.Add("beat");
            return true;
        }

        public bool Stopping(string status)
        {
            Calls.Add("stopping:" + status);
            return true;
        }

        public bool Dumping(string status, int maxSeconds)
        {
            Calls.Add("dumping:" + maxSeconds);
            return true;
        }

        public void Dispose()
        {
        }
    }
}
