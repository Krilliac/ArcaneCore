using System.Diagnostics;
using ArcaneCore.World.Playerbots.Qa;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>#219: a child that prints past the 8 MiB cap must fail at once with the cap error, not sit on a full pipe until the timeout and be reported as a timeout.</summary>
[Trait("Category", "Timing")]
public sealed class PlayerbotQaProcessRunnerOutputCapTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>A child that writes 20 MB to stdout (more than the cap) and would then sleep for a minute; it blocks on the full pipe once the runner stops reading.</summary>
    private static QaProcessRequest ChattyChild()
    {
        var environment = new Dictionary<string, string?>();
        if (OperatingSystem.IsWindows())
        {
            string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            return new QaProcessRequest(powershell, ["-NoProfile", "-NonInteractive", "-Command", "$s = 'x' * 1048576; for ($i = 0; $i -lt 20; $i++) { [Console]::Out.Write($s) }; Start-Sleep -Seconds 60"],
                Path.GetTempPath(), environment, Timeout);
        }

        return new QaProcessRequest("/bin/sh", ["-c", "yes x | head -c 20000000; sleep 60"], Path.GetTempPath(), environment, Timeout);
    }

    [Fact]
    public async Task RealRunner_OutputOverTheCap_ThrowsTheCapError_AsPermanent_WithoutWaitingForTheTimeout()
    {
        var watch = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<PlayerbotQaPublishException>(() => new QaProcessRunner().RunAsync(ChattyChild(), CancellationToken.None));

        watch.Stop();
        Assert.False(ex.Transient);
        Assert.Contains("8 MiB", ex.Message, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < Timeout - TimeSpan.FromSeconds(5), $"the cap failure took {watch.Elapsed}; it must not wait for the {Timeout} timeout");
    }

    [Fact]
    public async Task RealRunner_OutputOverTheCap_LeavesNoUnobservedFaultedReaderTask()
    {
        var unobserved = new List<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            lock (unobserved) unobserved.AddRange(e.Exception.Flatten().InnerExceptions);
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            try
            {
                await new QaProcessRunner().RunAsync(ChattyChild(), CancellationToken.None);
            }
            catch (PlayerbotQaPublishException)
            {
                // the cap error itself is asserted in the test above; here only the leftover reader tasks matter
            }

            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        lock (unobserved) Assert.DoesNotContain(unobserved, e => e is PlayerbotQaPublishException { Message: var m } && m.Contains("8 MiB", StringComparison.Ordinal));
    }
}
