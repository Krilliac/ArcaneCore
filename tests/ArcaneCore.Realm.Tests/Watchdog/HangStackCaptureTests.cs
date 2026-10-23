using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops.Watchdog;
using Xunit;

namespace ArcaneCore.Realm.Tests.Watchdog;

/// <summary>The once-per-hung-tick stack capture (Ops:Watchdog:TickMonitor:StackCaptureTool; issues #744/#755).</summary>
public sealed class HangStackCaptureTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-hangstacks-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class FakeRunner : IHangStackRunner
    {
        public List<(string Tool, string[] Arguments)> Calls { get; } = [];

        public Task<(int ExitCode, string Output)> RunAsync(string tool, IReadOnlyList<string> arguments, TimeSpan timeout)
        {
            lock (Calls)
            {
                Calls.Add((tool, [.. arguments]));
            }

            return Task.FromResult((0, "Thread (0x1234):\n  ArcaneCore.Game.Maps.WorldRuntime.RunTick\n"));
        }
    }

    private TickMonitorOptions Options(string tool = "C:/tools/dotnet-stack.exe")
        => new() { HangMs = 2000, StackCaptureTool = tool, StackCaptureDirectory = _directory, StackCaptureIntervalSeconds = 300 };

    [Fact]
    public async Task AHungTick_IsCapturedOnce_WhileItRuns_AndTheIntervalHoldsTheNext()
    {
        var clock = new FakeClock();
        var runner = new FakeRunner();
        TickMonitorOptions options = Options();
        var log = new RecordingLogger<TickMonitor>();
        var capture = new HangStackCapture(options, log, runner, fileExists: _ => true, processId: 4242);
        var monitor = new TickMonitor(options, clock, WatchdogTestSupport.NewRegistry(), log) { StackCapture = capture };
        monitor.StartTicking(50);

        monitor.OnTick(clock.NowMicros);
        clock.AdvanceMs(1500);
        monitor.Check(clock.NowMicros); // under the hang threshold: nothing
        Assert.Empty(runner.Calls);

        clock.AdvanceMs(1000);
        monitor.Check(clock.NowMicros); // 2.5 s: hung, captured
        await capture.Last;
        clock.AdvanceMs(1000);
        monitor.Check(clock.NowMicros); // the same hung tick: not again
        await capture.Last;

        (string tool, string[] arguments) = Assert.Single(runner.Calls);
        Assert.Equal("C:/tools/dotnet-stack.exe", tool);
        Assert.Equal(["report", "-p", "4242"], arguments);
        string file = Assert.Single(Directory.GetFiles(_directory));
        string text = await File.ReadAllTextAsync(file);
        Assert.Contains("world tick running for 2500 ms", text);
        Assert.Contains("WorldRuntime.RunTick", text);

        // A second hang inside the interval is not captured; one after it is.
        monitor.OnTick(clock.NowMicros);
        clock.AdvanceMs(3000);
        monitor.Check(clock.NowMicros);
        await capture.Last;
        Assert.Single(runner.Calls);

        clock.AdvanceMs(300_000);
        monitor.OnTick(clock.NowMicros);
        clock.AdvanceMs(3000);
        monitor.Check(clock.NowMicros);
        await capture.Last;
        Assert.Equal(2, runner.Calls.Count);
    }

    [Fact]
    public void NoToolOrAMissingOne_CapturesNothing_AndSaysSoOnce()
    {
        var runner = new FakeRunner();
        var log = new RecordingLogger<TickMonitor>();
        Assert.False(new HangStackCapture(Options(tool: ""), log, runner, fileExists: _ => true).TryCapture(0, "hang"));
        Assert.Empty(log.Lines);

        var missing = new HangStackCapture(Options(), log, runner, fileExists: _ => false);
        Assert.False(missing.TryCapture(0, "hang"));
        Assert.False(missing.TryCapture(400_000_000, "hang"));
        Assert.Empty(runner.Calls);
        Assert.Single(log.Lines, l => l.Message.Contains("hang stack capture is off", StringComparison.Ordinal));
    }

    [Fact]
    public void TheToolPath_ExpandsEnvironmentVariables()
    {
        var name = "ARCANECORE_TEST_STACKTOOL_DIR_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "D:/x");
        try
        {
            var capture = new HangStackCapture(Options(tool: "%" + name + "%/dotnet-stack.exe"), new RecordingLogger<TickMonitor>());
            Assert.Equal("D:/x/dotnet-stack.exe", capture.Tool);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }
}
