using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops.ServerConsole;
using ArcaneCore.Kernel.Ops.Watchdog.Heartbeat;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Ops;

/// <summary>A supervised child (<c>ARCANECORE_SUPERVISOR_PROTOCOL=1</c>) beats on stdout beside whatever sink the operator configured.</summary>
public sealed class SupervisorHeartbeatTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arcane-hb-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A late handle on Windows, or never created: the temp directory is not the subject of the test.
        }
    }

    private static Func<string, string?> Env(params (string Key, string Value)[] values) => key => values.FirstOrDefault(v => v.Key == key).Value;

    [Fact]
    public void HeartbeatPlan_SupervisedChild_AddsStdoutBesideConfiguredSink()
    {
        var stdout = new StringWriter();
        string file = Path.Combine(_dir, "alive");
        var options = new HeartbeatOptions { Mode = HeartbeatMode.File, FilePath = file, IntervalSeconds = 7 };

        HeartbeatPlan plan = HeartbeatPlan.Resolve(options, Env((SupervisorProtocol.EnvironmentVariable, "1")), 4242, _dir, stdout);

        Assert.IsType<CompositeHeartbeatSink>(plan.Sink);
        Assert.Equal(7, plan.IntervalSeconds); // the operator's interval is kept
        Assert.True(plan.Sink.Ready("up"));
        Assert.True(plan.Sink.Beat());
        Assert.True(plan.Sink.Stopping("down"));

        Assert.True(File.Exists(file)); // the configured sink still beats
        Assert.Contains("pid=4242 stopping", File.ReadAllText(file), StringComparison.Ordinal);
        string[] lines = stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(3, lines.Length);
        Assert.All(lines, line => Assert.True(SupervisorProtocol.TryParseHeartbeat(line, out _), line));
        Assert.EndsWith("ready", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("alive", lines[1], StringComparison.Ordinal);
        Assert.EndsWith("stopping", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void HeartbeatPlan_SupervisedChild_WithNoConfiguredSink_BeatsOnStdoutEveryTenSeconds()
    {
        var stdout = new StringWriter();

        HeartbeatPlan plan = HeartbeatPlan.Resolve(new HeartbeatOptions(), Env((SupervisorProtocol.EnvironmentVariable, "1")), 9, _dir, stdout);

        Assert.IsNotType<NullHeartbeatSink>(plan.Sink);
        Assert.Equal(10, plan.IntervalSeconds);
        Assert.True(plan.Sink.Beat());
        Assert.Contains("pid=9 alive", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void HeartbeatPlan_SupervisedChild_WithStdoutModeAlready_DoesNotBeatTwice()
    {
        var stdout = new StringWriter();

        HeartbeatPlan plan = HeartbeatPlan.Resolve(new HeartbeatOptions { Mode = HeartbeatMode.Stdout, IntervalSeconds = 5 }, Env((SupervisorProtocol.EnvironmentVariable, "1")), 9, _dir, stdout);
        plan.Sink.Beat();

        Assert.Equal(5, plan.IntervalSeconds);
        Assert.Single(stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void HeartbeatPlan_NotSupervised_Unchanged()
    {
        var stdout = new StringWriter();
        string file = Path.Combine(_dir, "alive");
        var options = new HeartbeatOptions { Mode = HeartbeatMode.File, FilePath = file, IntervalSeconds = 7 };

        HeartbeatPlan plan = HeartbeatPlan.Resolve(options, Env(), 4242, _dir, stdout);
        HeartbeatPlan off = HeartbeatPlan.Resolve(options, Env((SupervisorProtocol.EnvironmentVariable, "0")), 4242, _dir, stdout);
        HeartbeatPlan none = HeartbeatPlan.Resolve(new HeartbeatOptions(), Env(), 4242, _dir, stdout);

        Assert.IsType<FileHeartbeatSink>(plan.Sink);
        Assert.IsType<FileHeartbeatSink>(off.Sink);
        Assert.Equal(7, plan.IntervalSeconds);
        Assert.IsType<NullHeartbeatSink>(none.Sink);
        Assert.Equal(0, none.IntervalSeconds);
        Assert.Equal(string.Empty, stdout.ToString());
    }

    [Fact]
    public void CompositeHeartbeatSink_OneSinkFailing_ReportsFailure_AndStillBeatsTheOther()
    {
        var good = new StringWriter();
        var failing = new FailingSink();
        using var composite = new CompositeHeartbeatSink([failing, new TextWriterHeartbeatSink(good, 1)]);

        bool delivered = composite.Beat();

        Assert.False(delivered);
        Assert.Equal("boom", composite.LastError);
        Assert.Contains("alive", good.ToString(), StringComparison.Ordinal);
        Assert.Contains("fail", composite.Description, StringComparison.Ordinal);
    }

    private sealed class FailingSink : IHeartbeatSink
    {
        public string Description => "fail";

        public string? LastError => "boom";

        public bool Ready(string status) => false;

        public bool Beat() => false;

        public bool Stopping(string status) => false;

        public void Dispose()
        {
        }
    }
}
