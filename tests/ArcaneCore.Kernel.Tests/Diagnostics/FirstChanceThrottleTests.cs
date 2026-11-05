using System.Diagnostics;
using System.Runtime.ExceptionServices;
using ArcaneCore.Kernel.Diagnostics;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Diagnostics;

/// <summary>
/// First-chance crash reports are deduplicated and keep the origin frame (live 2026-10-26: six SQLite busy timeouts, each rethrown through every
/// awaiting frame and wrapped once, became 93 full reports; 51 more were the gate's own wait-slice timeouts whose stack held only the rethrow frame).
/// </summary>
[Collection(DiagnosticsCollection.Name)]
public sealed class FirstChanceThrottleTests
{
    private static readonly AsyncLocal<bool> Watching = new();

    [Fact]
    public void TryMarkNew_IsTrueOnce_PerExceptionObject()
    {
        var throttle = new FirstChanceThrottle(() => 0);
        var exception = new InvalidOperationException("once");

        Assert.True(throttle.TryMarkNew(exception));
        Assert.False(throttle.TryMarkNew(exception)); // a rethrow of the same object
        Assert.True(throttle.TryMarkNew(new InvalidOperationException("once"))); // a new object with the same text is a new exception
    }

    [Fact]
    public void Decide_ReportsASignatureOncePerWindow_AndCountsTheRepeats()
    {
        long now = 1_000;
        var throttle = new FirstChanceThrottle(() => now);

        FirstChanceDecision first = throttle.Decide("T @ A.M");
        Assert.True(first.Report);
        Assert.Equal(0, first.SuppressedRepeats);
        Assert.Equal(-1, first.SinceLastReportMs);

        for (int i = 0; i < 3; i++)
        {
            now += 1_000;
            Assert.False(throttle.Decide("T @ A.M").Report);
        }

        Assert.True(throttle.Decide("T @ B.M").Report); // another origin is another signature

        now = 1_000 + FirstChanceThrottle.WindowMs;
        FirstChanceDecision next = throttle.Decide("T @ A.M");
        Assert.True(next.Report);
        Assert.Equal(3, next.SuppressedRepeats);
        Assert.Equal(FirstChanceThrottle.WindowMs, next.SinceLastReportMs);

        Assert.False(throttle.Decide("T @ A.M").Report); // the count starts over after a report
    }

    [Fact]
    public void Decide_CapsReportsAcrossSignaturesPerWindow_AndNamesTheDropsInTheNextReport()
    {
        long now = 0;
        var throttle = new FirstChanceThrottle(() => now);
        for (int i = 0; i < FirstChanceThrottle.MaxReportsPerWindow; i++)
        {
            Assert.True(throttle.Decide("sig " + i).Report);
        }

        Assert.False(throttle.Decide("one too many").Report);
        Assert.False(throttle.Decide("two too many").Report);

        now = FirstChanceThrottle.WindowMs;
        FirstChanceDecision after = throttle.Decide("one too many");
        Assert.True(after.Report);
        Assert.Equal(2, after.DroppedOverCap);
        Assert.Equal(-1, after.SinceLastReportMs); // it was never reported
    }

    [Fact]
    public async Task Origin_OfAnAwaitedTimeout_IsTheAwaitingMethod_NotTheRethrowFrame()
    {
        // Task.WaitAsync completes its task with a TimeoutException that is never thrown: the first throw is the await's rethrow, whose own stack is
        // ExceptionDispatchInfo.Throw. The thread's stack at that first-chance event still holds the awaiting method.
        string? origin = null;
        string? ownStack = null;
        void OnFirstChance(object? sender, FirstChanceExceptionEventArgs e)
        {
            if (Watching.Value && e.Exception is TimeoutException && origin is null)
            {
                origin = FirstChanceSite.Origin(new StackTrace(1, fNeedFileInfo: false));
                ownStack = e.Exception.StackTrace;
            }
        }

        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
        Watching.Value = true;
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(AwaitANeverCompletingTaskAsync);
        }
        finally
        {
            Watching.Value = false;
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
        }

        Assert.Equal(typeof(FirstChanceThrottleTests).FullName + "." + nameof(AwaitANeverCompletingTaskAsync), origin);
        Assert.DoesNotContain(nameof(AwaitANeverCompletingTaskAsync), ownStack ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void ReportFirstChance_WritesOneReportPerException_WithTheThrowSite_AndSuppressesRepeatsOfTheSameOrigin()
    {
        var log = new CapturingLogger();
        CrashHandler handler = CrashHandler.Install(DumpFree.Options(), new LoggerCrashSink(log));
        try
        {
            var busy = new InvalidOperationException("database is locked");
            Assert.True(handler.ReportFirstChance(busy));
            Assert.False(handler.ReportFirstChance(busy)); // the await in the next frame rethrows the same object
            Assert.False(handler.ReportFirstChance(busy));
            Assert.False(handler.ReportFirstChance(new InvalidOperationException("database is locked again"))); // same type and origin, same window
            Assert.False(handler.ReportFirstChance(new OperationCanceledException())); // cancellation is never reported

            string report = Assert.Single(log.Entries);
            Assert.Contains("first-chance exception (AppDomain.FirstChanceException)", report, StringComparison.Ordinal);
            Assert.Contains("[throw site]\n  origin: " + typeof(FirstChanceThrottleTests).FullName + "."
                + nameof(ReportFirstChance_WritesOneReportPerException_WithTheThrowSite_AndSuppressesRepeatsOfTheSameOrigin) + "\n", report, StringComparison.Ordinal);
            Assert.Contains("  thread stack at the throw:\n", report, StringComparison.Ordinal);
            Assert.Contains("repeats of this type and origin suppressed: 0", report, StringComparison.Ordinal);
            Assert.True(report.IndexOf("[throw site]", StringComparison.Ordinal) < report.IndexOf("[process]", StringComparison.Ordinal));
        }
        finally
        {
            handler.Dispose();
        }
    }

    private static async Task AwaitANeverCompletingTaskAsync()
    {
        await new TaskCompletionSource().Task.WaitAsync(TimeSpan.FromMilliseconds(1)).ConfigureAwait(false);
    }
}
