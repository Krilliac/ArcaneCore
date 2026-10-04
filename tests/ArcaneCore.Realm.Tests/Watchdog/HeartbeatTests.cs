using System.Net.Sockets;
using System.Text;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops.Watchdog;
using ArcaneCore.Kernel.Ops.Watchdog.Heartbeat;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Realm.Tests.Watchdog;

public sealed class HeartbeatTests
{
    private readonly FakeClock _clock = new();
    private readonly RecordingLogger<HeartbeatWriter> _log = new();

    private sealed class FakeSink : IHeartbeatSink
    {
        public List<string> Calls { get; } = [];

        public bool Fail { get; set; }

        public string Description => "fake";

        public string? LastError => Fail ? "boom" : null;

        public bool Ready(string status)
        {
            Calls.Add("ready:" + status);
            return !Fail;
        }

        public bool Beat()
        {
            Calls.Add("beat");
            return !Fail;
        }

        public bool Stopping(string status)
        {
            Calls.Add("stopping:" + status);
            return !Fail;
        }

        public void Dispose() => Calls.Add("dispose");
    }

    private sealed class Source(string name) : ILivenessSource
    {
        public bool Alive { get; set; } = true;

        public string Name => name;

        public bool IsAlive(long nowMicros, out string? reason)
        {
            reason = Alive ? null : "not responding";
            return Alive;
        }
    }

    private HeartbeatWriter Writer(FakeSink sink, int intervalSeconds, params ILivenessSource[] sources)
        => new(new HeartbeatOptions { WarnIntervalSeconds = 30 }, sources, WatchdogTestSupport.NewRegistry(), _log, () => new HeartbeatPlan(sink, intervalSeconds, null));

    [Fact]
    public void Writer_SendsReadyAtStart_BeatsEveryInterval_AndStoppingAtStop()
    {
        var sink = new FakeSink();
        HeartbeatWriter writer = Writer(sink, 10);
        writer.Start();
        writer.Check(_clock.NowMicros); // first beat
        _clock.AdvanceMs(9_000);
        writer.Check(_clock.NowMicros);
        _clock.AdvanceMs(1_000);
        writer.Check(_clock.NowMicros);
        writer.Stop();
        Assert.Equal(["ready:ArcaneCore started", "beat", "beat", "stopping:ArcaneCore stopping", "dispose"], sink.Calls);
        Assert.Equal(2, writer.Sent);
        Assert.Contains(_log.Lines, l => l.Message.Contains("heartbeat: fake every 10 s", StringComparison.Ordinal));
    }

    [Fact]
    public void Writer_WithholdsTheBeat_WhileAnySourceIsNotAlive_AndWarnsOncePerInterval()
    {
        var sink = new FakeSink();
        var world = new Source("tick");
        var other = new Source("db");
        HeartbeatWriter writer = Writer(sink, 5, world, other);
        writer.Start();
        writer.Check(_clock.NowMicros);
        Assert.Equal(1, writer.Sent);

        world.Alive = false;
        for (int i = 0; i < 4; i++)
        {
            _clock.AdvanceMs(5_000);
            writer.Check(_clock.NowMicros);
        }

        Assert.Equal(1, writer.Sent);
        Assert.Equal(4, writer.Withheld);
        LogLine warning = Assert.Single(_log.Of(WatchdogEvents.HeartbeatWithheld));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("tick reports not responding", warning.Message);

        _clock.AdvanceMs(30_000);
        writer.Check(_clock.NowMicros);
        Assert.Equal(2, _log.CountOf(WatchdogEvents.HeartbeatWithheld));
        Assert.Contains("(3 suppressed)", _log.Of(WatchdogEvents.HeartbeatWithheld).Last().Message);

        world.Alive = true;
        _clock.AdvanceMs(5_000);
        writer.Check(_clock.NowMicros);
        Assert.Equal(2, writer.Sent);
    }

    [Fact]
    public void Writer_ReportsADeliveryFailure_RateLimited_AndKeepsTrying()
    {
        var sink = new FakeSink { Fail = true };
        HeartbeatWriter writer = Writer(sink, 1);
        writer.Start();
        Assert.Contains(_log.Lines, l => l.Message.Contains("READY could not be delivered", StringComparison.Ordinal));
        for (int i = 0; i < 5; i++)
        {
            writer.Check(_clock.NowMicros);
            _clock.AdvanceMs(1_000);
        }

        Assert.Equal(0, writer.Sent);
        Assert.Single(_log.Lines, l => l.Message.Contains("heartbeat could not be delivered to fake: boom", StringComparison.Ordinal));
        sink.Fail = false;
        writer.Check(_clock.NowMicros);
        Assert.Equal(1, writer.Sent);
    }

    [Fact]
    public void Writer_WithANullPlan_DoesNothingButLogIt()
    {
        var writer = new HeartbeatWriter(new HeartbeatOptions { Mode = HeartbeatMode.None }, [], WatchdogTestSupport.NewRegistry(), _log, () => new HeartbeatPlan(NullHeartbeatSink.Instance, 0, null));
        writer.Start();
        writer.Check(_clock.NowMicros);
        writer.Stop();
        Assert.Contains(_log.Lines, l => l.Message.Contains("heartbeat: none (mode None)", StringComparison.Ordinal));
        Assert.Equal(0, writer.Sent);
    }

    private static Func<string, string?> Env(params (string Key, string Value)[] values) => key => values.FirstOrDefault(v => v.Key == key).Value;

    [Fact]
    public void Plan_Auto_WithoutNotifySocket_IsNone_AndNoneIsNoneEvenWithOne()
    {
        HeartbeatPlan auto = HeartbeatPlan.Resolve(new HeartbeatOptions(), Env(), 1, "/tmp", TextWriter.Null);
        Assert.IsType<NullHeartbeatSink>(auto.Sink);
        Assert.Equal(0, auto.IntervalSeconds);
        Assert.Null(auto.Note);

        HeartbeatPlan none = HeartbeatPlan.Resolve(new HeartbeatOptions { Mode = HeartbeatMode.None }, Env(("NOTIFY_SOCKET", "/run/x")), 1, "/tmp", TextWriter.Null);
        Assert.IsType<NullHeartbeatSink>(none.Sink);
    }

    [Fact]
    public void Plan_SystemdWithoutTheSocket_IsNoneWithANote()
    {
        HeartbeatPlan plan = HeartbeatPlan.Resolve(new HeartbeatOptions { Mode = HeartbeatMode.Systemd }, Env(), 1, "/tmp", TextWriter.Null);
        Assert.IsType<NullHeartbeatSink>(plan.Sink);
        Assert.Contains("NOTIFY_SOCKET is not set", plan.Note);
    }

    [Fact]
    public void SystemdEnvironment_ReadsWatchdogUsec_OnlyWhenThePidMatchesOrIsAbsent()
    {
        Assert.False(SystemdNotifySink.TryReadEnvironment(Env(), 42, out string? socket, out long micros));
        Assert.Null(socket);

        Assert.True(SystemdNotifySink.TryReadEnvironment(Env(("NOTIFY_SOCKET", "/run/systemd/notify"), ("WATCHDOG_USEC", "30000000")), 42, out socket, out micros));
        Assert.Equal("/run/systemd/notify", socket);
        Assert.Equal(30_000_000, micros);

        Assert.True(SystemdNotifySink.TryReadEnvironment(Env(("NOTIFY_SOCKET", "/run/systemd/notify"), ("WATCHDOG_USEC", "30000000"), ("WATCHDOG_PID", "42")), 42, out _, out micros));
        Assert.Equal(30_000_000, micros);

        Assert.True(SystemdNotifySink.TryReadEnvironment(Env(("NOTIFY_SOCKET", "/run/systemd/notify"), ("WATCHDOG_USEC", "30000000"), ("WATCHDOG_PID", "7")), 42, out _, out micros));
        Assert.Equal(0, micros);

        Assert.True(SystemdNotifySink.TryReadEnvironment(Env(("NOTIFY_SOCKET", "@abstract"), ("WATCHDOG_USEC", "garbage")), 42, out socket, out micros));
        Assert.Equal("@abstract", socket);
        Assert.Equal(0, micros);
    }

    [Fact]
    public void Plan_Systemd_UsesHalfOfWatchdogUsec_OrReadyOnlyWithoutIt_OrTheConfiguredInterval()
    {
        // Resolving a systemd plan opens the datagram socket (not connected: nothing is sent until Ready/Beat), so a
        // platform without unix datagram sockets falls back to none with a note. Both outcomes are asserted.
        string socketPath = Path.Combine(Path.GetTempPath(), "arcane-hb-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
        HeartbeatPlan half = HeartbeatPlan.Resolve(new HeartbeatOptions { Mode = HeartbeatMode.Systemd }, Env(("NOTIFY_SOCKET", socketPath), ("WATCHDOG_USEC", "30000000")), Environment.ProcessId, "/tmp", TextWriter.Null);
        if (half.Sink is NullHeartbeatSink)
        {
            Assert.Contains("cannot be opened on this platform", half.Note);
            return;
        }

        Assert.IsType<SystemdNotifySink>(half.Sink);
        Assert.Equal(15, half.IntervalSeconds);
        Assert.Null(half.Note);
        half.Sink.Dispose();

        HeartbeatPlan readyOnly = HeartbeatPlan.Resolve(new HeartbeatOptions { Mode = HeartbeatMode.Auto }, Env(("NOTIFY_SOCKET", socketPath)), Environment.ProcessId, "/tmp", TextWriter.Null);
        Assert.IsType<SystemdNotifySink>(readyOnly.Sink);
        Assert.Equal(0, readyOnly.IntervalSeconds);
        Assert.Contains("READY and STOPPING only", readyOnly.Note);
        readyOnly.Sink.Dispose();

        HeartbeatPlan configured = HeartbeatPlan.Resolve(new HeartbeatOptions { Mode = HeartbeatMode.Systemd, IntervalSeconds = 4 }, Env(("NOTIFY_SOCKET", socketPath), ("WATCHDOG_USEC", "30000000")), Environment.ProcessId, "/tmp", TextWriter.Null);
        Assert.Equal(4, configured.IntervalSeconds);
        configured.Sink.Dispose();

        // Short watchdog: at least one second between beats.
        HeartbeatPlan tiny = HeartbeatPlan.Resolve(new HeartbeatOptions { Mode = HeartbeatMode.Systemd }, Env(("NOTIFY_SOCKET", socketPath), ("WATCHDOG_USEC", "500000")), Environment.ProcessId, "/tmp", TextWriter.Null);
        Assert.Equal(1, tiny.IntervalSeconds);
        tiny.Sink.Dispose();
    }

    [Fact]
    public void SystemdSink_SendsSdNotifyDatagrams_WhereUnixDatagramSocketsExist()
    {
        string socketPath = Path.Combine(Path.GetTempPath(), "arcane-hb-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
        Socket server;
        try
        {
            server = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
            server.Bind(new UnixDomainSocketEndPoint(socketPath));
        }
        catch (SocketException)
        {
            // No unix datagram sockets (Windows): the sink must degrade to a note, never throw past the plan.
            HeartbeatPlan plan = HeartbeatPlan.Resolve(new HeartbeatOptions { Mode = HeartbeatMode.Systemd }, Env(("NOTIFY_SOCKET", socketPath)), 1, "/tmp", TextWriter.Null);
            Assert.IsType<NullHeartbeatSink>(plan.Sink);
            Assert.NotNull(plan.Note);
            return;
        }

        using (server)
        {
            server.ReceiveTimeout = 5_000;
            using var sink = new SystemdNotifySink(socketPath);
            Assert.Equal("systemd NOTIFY_SOCKET " + socketPath, sink.Description);
            Assert.True(sink.Ready("up\nand running"));
            Assert.True(sink.Beat());
            Assert.True(sink.Stopping("bye"));
            Assert.True(sink.Notify("STATUS=custom"));

            byte[] buffer = new byte[256];
            Assert.Equal("READY=1\nSTATUS=up and running\n", Receive(server, buffer));
            Assert.Equal("WATCHDOG=1\n", Receive(server, buffer));
            Assert.Equal("STOPPING=1\nSTATUS=bye\n", Receive(server, buffer));
            Assert.Equal("STATUS=custom\n", Receive(server, buffer));
            Assert.Null(sink.LastError);

            // The beat itself does not allocate (one warm-up beat plus eight measured ones, then drained: the kernel's
            // unix datagram queue is short, and the sink is non-blocking so a full queue fails a beat instead of parking the thread).
            Assert.Equal(0, WatchdogTestSupport.AllocatedBy(() => sink.Beat(), 8));
            for (int i = 0; i < 9; i++)
            {
                Assert.Equal("WATCHDOG=1\n", Receive(server, buffer));
            }

            // A reader that stopped draining: the beats fail (EAGAIN) and are reported, and the sender never blocks.
            bool anyFailed = false;
            for (int i = 0; i < 2000 && !anyFailed; i++)
            {
                anyFailed = !sink.Beat();
            }

            Assert.True(anyFailed, "a full datagram queue must fail the beat instead of blocking");
            Assert.NotNull(sink.LastError);
        }

        File.Delete(socketPath);

        // Nothing listens any more: the failure is reported, not thrown.
        using var orphan = new SystemdNotifySink(socketPath);
        Assert.False(orphan.Beat());
        Assert.NotNull(orphan.LastError);
    }

    private static string Receive(Socket server, byte[] buffer)
    {
        int n = server.Receive(buffer);
        return Encoding.UTF8.GetString(buffer, 0, n);
    }

    [Fact]
    public void FileSink_WritesTimestampPidAndState_Atomically_AndFormatsInvariantUtc()
    {
        string dir = Path.Combine(Path.GetTempPath(), "arcane-hb-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "sub", "alive");
        var now = new DateTimeOffset(2026, 10, 4, 12, 30, 15, 250, TimeSpan.FromHours(2));
        try
        {
            using var sink = new FileHeartbeatSink(path, 4242, () => now);
            Assert.True(sink.Ready("x"));
            Assert.Equal("2026-10-04T10:30:15.250Z pid=4242 ready\n", File.ReadAllText(path));
            now = now.AddSeconds(10);
            Assert.True(sink.Beat());
            Assert.Equal("2026-10-04T10:30:25.250Z pid=4242 alive\n", File.ReadAllText(path));
            Assert.True(sink.Stopping("x"));
            Assert.EndsWith("stopping\n", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".tmp"));
            Assert.Null(sink.LastError);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Plan_File_ResolvesRelativePathsUnderTheContentRoot_AndNeedsAPath()
    {
        HeartbeatPlan empty = HeartbeatPlan.Resolve(new HeartbeatOptions { Mode = HeartbeatMode.File }, Env(), 1, "/srv/arcane", TextWriter.Null);
        Assert.IsType<NullHeartbeatSink>(empty.Sink);
        Assert.Contains("FilePath is empty", empty.Note);

        string root = Path.GetTempPath();
        HeartbeatPlan relative = HeartbeatPlan.Resolve(new HeartbeatOptions { Mode = HeartbeatMode.File, FilePath = "var/alive", IntervalSeconds = 7 }, Env(), 1, root, TextWriter.Null);
        Assert.Equal("file " + Path.GetFullPath(Path.Combine(root, "var", "alive")), relative.Sink.Description);
        Assert.Equal(7, relative.IntervalSeconds);
        relative.Sink.Dispose();
    }

    [Fact]
    public void StdoutSink_WritesOneLinePerBeat()
    {
        var output = new StringWriter();
        var now = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        HeartbeatPlan plan = HeartbeatPlan.Resolve(new HeartbeatOptions { Mode = HeartbeatMode.Stdout }, Env(), 99, "/tmp", output);
        Assert.Equal(10, plan.IntervalSeconds);
        using var sink = new TextWriterHeartbeatSink(output, 99, now: () => now);
        Assert.True(sink.Ready("s"));
        Assert.True(sink.Beat());
        Assert.Equal("heartbeat 2026-01-02T03:04:05.000Z pid=99 ready\nheartbeat 2026-01-02T03:04:05.000Z pid=99 alive\n", output.ToString());
        plan.Sink.Dispose();
    }
}
