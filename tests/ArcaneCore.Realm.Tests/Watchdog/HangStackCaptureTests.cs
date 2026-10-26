using System.Diagnostics;
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
        var monitor = new TickMonitor(options, clock, WatchdogTestKit.NewRegistry(), log) { StackCapture = capture };
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

    [Fact]
    public void TheStackTool_DoesNotInheritTheCrashDumpVariables_ButKeepsTheRest()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["DOTNET_DbgEnableMiniDump"] = "1", ["DOTNET_DbgMiniDumpType"] = "4", ["DOTNET_DbgMiniDumpName"] = "x.dmp",
            ["DOTNET_CreateDumpDiagnostics"] = "1", ["DOTNET_CreateDumpVerboseDiagnostics"] = "1", ["DOTNET_CreateDumpLogToFile"] = "x.log",
            ["COMPlus_DbgEnableMiniDump"] = "1", ["COMPlus_DbgMiniDumpType"] = "4", ["COMPlus_CreateDumpDiagnostics"] = "1",
            ["dotnet_dbgminidumpname"] = "lower.dmp",
            ["DOTNET_gcServer"] = "0", ["DOTNET_ROOT"] = "D:/dotnet", ["PATH"] = "p",
        };

        ProcessHangStackRunner.RemoveDumpVariables(environment);

        Assert.Equal(["DOTNET_ROOT", "DOTNET_gcServer", "PATH"], environment.Keys.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void TheStackToolStartInfo_HasNoDumpVariables_AndRedirectsWithoutAWindow()
    {
        ProcessStartInfo start = ProcessHangStackRunner.CreateStartInfo("C:/tools/dotnet-stack.exe", ["report", "-p", "42"]);

        Assert.DoesNotContain(start.Environment.Keys, k => k.StartsWith("DOTNET_DbgEnableMiniDump", StringComparison.OrdinalIgnoreCase)
            || k.StartsWith("DOTNET_DbgMiniDump", StringComparison.OrdinalIgnoreCase) || k.StartsWith("DOTNET_CreateDump", StringComparison.OrdinalIgnoreCase)
            || k.StartsWith("COMPlus_DbgMiniDump", StringComparison.OrdinalIgnoreCase) || k.StartsWith("COMPlus_CreateDump", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["report", "-p", "42"], start.ArgumentList.ToArray());
        Assert.True(start.RedirectStandardOutput && start.RedirectStandardError && start.CreateNoWindow && !start.UseShellExecute);
    }

    private sealed class EmptyRunner : IHangStackRunner
    {
        public Task<(int ExitCode, string Output)> RunAsync(string tool, IReadOnlyList<string> arguments, TimeSpan timeout)
            => Task.FromResult((0, string.Empty));
    }

    [Fact]
    public async Task AnEmptyCapture_IsWrittenWithABanner_AndLoggedAsEmpty()
    {
        var log = new RecordingLogger<TickMonitor>();
        var capture = new HangStackCapture(Options(), log, new EmptyRunner(), fileExists: _ => true, processId: 4242);

        Assert.True(capture.TryCapture(0, "hang"));
        await capture.Last;

        string text = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(_directory)));
        Assert.Contains("EMPTY CAPTURE", text);
        LogLine line = Assert.Single(log.Lines);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, line.Level);
        Assert.Contains("empty", line.Message);
        Assert.DoesNotContain("were written", line.Message);
    }

    private sealed class WhitespaceFailedRunner : IHangStackRunner
    {
        public Task<(int ExitCode, string Output)> RunAsync(string tool, IReadOnlyList<string> arguments, TimeSpan timeout)
            => Task.FromResult((1, "  \r\n"));
    }

    [Fact]
    public async Task WhitespaceOnlyOutput_WithANonZeroExit_IsAlsoEmpty()
    {
        var log = new RecordingLogger<TickMonitor>();
        var capture = new HangStackCapture(Options(), log, new WhitespaceFailedRunner(), fileExists: _ => true, processId: 4242);

        Assert.True(capture.TryCapture(0, "hang"));
        await capture.Last;

        string text = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(_directory)));
        Assert.Contains("EMPTY CAPTURE", text);
        Assert.Contains("empty", Assert.Single(log.Lines).Message);
    }

    [Fact]
    public async Task AnUnknownGcPause_IsSaidInTheHeader_NotShownAsZero()
    {
        var capture = new HangStackCapture(Options(), new RecordingLogger<TickMonitor>(), new FakeRunner(), fileExists: _ => true, processId: 4242);

        Assert.True(capture.TryCapture(0, "hang", runningMicros: 63_000_000, gcPauseMicros: HangStackCapture.GcPauseUnknown));
        await capture.Last;

        string text = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(_directory)));
        Assert.Contains("GC pause unknown", text);
        Assert.DoesNotContain("GC-DOMINATED", text);
    }

    [Fact]
    public async Task AGcDominatedFrame_SaysTheStackIsWhereThreadsWereParked()
    {
        var log = new RecordingLogger<TickMonitor>();
        var capture = new HangStackCapture(Options(), log, new FakeRunner(), fileExists: _ => true, processId: 4242);

        Assert.True(capture.TryCapture(0, "hang", runningMicros: 63_000_000, gcPauseMicros: 62_000_000));
        await capture.Last;

        string text = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(_directory)));
        Assert.Contains("GC-DOMINATED", text);
        Assert.DoesNotContain("where the tick was stuck then", text);
    }
}
