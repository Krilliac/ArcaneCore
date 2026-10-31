using ArcaneCore.Kernel.Ops.Watchdog;
using ArcaneCore.World.Cluster.Supervision;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Supervision;

public sealed class SystemSupervisedHangCaptureTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ac-hang").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Stacks_PassesTheConfiguredToolTimeoutToTheRunner()
    {
        var runner = new RecordingRunner();
        var capture = new SystemSupervisedHangCapture(runner);

        await capture.CaptureAsync(
            new SupervisedHangCaptureRequest("world", 4242, SupervisedHangCaptureMode.Stacks, _dir, null, TimeSpan.FromSeconds(300)),
            CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(300), runner.Timeout);
        Assert.Equal("dotnet-stack", runner.Tool);
    }

    [Fact]
    public async Task Stacks_WithoutAConfiguredTimeout_KeepsTheTwoMinuteDefault()
    {
        var runner = new RecordingRunner();
        var capture = new SystemSupervisedHangCapture(runner);

        await capture.CaptureAsync(new SupervisedHangCaptureRequest("world", 4242, SupervisedHangCaptureMode.Stacks, _dir, null), CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(120), runner.Timeout);
    }

    private sealed class RecordingRunner : IHangStackRunner
    {
        public TimeSpan Timeout { get; private set; }

        public string? Tool { get; private set; }

        public Task<(int ExitCode, string Output)> RunAsync(string tool, IReadOnlyList<string> arguments, TimeSpan timeout)
        {
            Tool = tool;
            Timeout = timeout;
            return Task.FromResult((0, "stacks"));
        }
    }
}
