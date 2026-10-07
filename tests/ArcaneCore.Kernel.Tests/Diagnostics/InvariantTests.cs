using System.Diagnostics;
using System.Reflection;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Ops;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Diagnostics;

/// <summary>
/// <see cref="Invariant"/>: the pass path allocates nothing, a message is formatted only on failure, the
/// counters and the per-site log limit, and the Debug/Release split of <c>Assert</c>. The configuration
/// is process-global, so every test that touches it lives in the <c>Diagnostics</c> collection (xunit
/// runs one collection's tests serially) and restores the defaults.
/// </summary>
[Collection(DiagnosticsCollection.Name)]
public sealed class InvariantTests : IDisposable
{
    public InvariantTests() => Invariant.Configure(new DiagnosticsOptions(), logger: null);

    public void Dispose() => Invariant.Configure(new DiagnosticsOptions(), logger: null);

    [Fact]
    public void PassingChecksAndAsserts_AllocateNothing()
    {
        int value = 42;
        string name = "value";
        var probe = new ToStringProbe();

        // Warm up: tiering, the handler struct, the string literals.
        for (int i = 0; i < 1000; i++)
        {
            Passing(value, name, probe);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100_000; i++)
        {
            Passing(value, name, probe);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"the pass path allocated {allocated} bytes over 100000 iterations");
        Assert.Equal(0, probe.Calls);
    }

    private static void Passing(int value, string name, ToStringProbe probe)
    {
        Invariant.Check(value > 0, "literal message");
        Invariant.Check(value > 0, $"{name} is {value}, probe {probe}");
        Invariant.Assert(value > 0, "literal message");
        Invariant.Assert(value > 0, $"{name} is {value}, probe {probe}");
    }

    [Fact]
    public void FailingCheck_FormatsTheMessageOnce_CountsTheSite_AndReturnsFalse()
    {
        var probe = new ToStringProbe();
        long before = Invariant.FailureCount;

        bool passed = Invariant.Check(probe.Calls == 1, $"the probe says {probe}");

        Assert.False(passed);
        Assert.Equal(1, probe.Calls);
        Assert.Equal(before + 1, Invariant.FailureCount);
        InvariantFailure site = Assert.Single(Invariant.Failures(), f => f.LastMessage == "the probe says probe#1" && f.Member == nameof(FailingCheck_FormatsTheMessageOnce_CountsTheSite_AndReturnsFalse));
        Assert.Equal("InvariantTests.cs", site.File);
        Assert.True(site.Line > 0);
    }

    [Fact]
    public void Capture_SeesOnlyTheFailuresOfItsOwnFlow_WhileTheProcessCounterSeesEveryThread()
    {
        // The flake this guards against: a test snapshots FailureCount, another test class (xunit runs classes
        // in parallel) trips a Check in between, and `before + 1` is off by one. A thread started without
        // ExecutionContext flow stands in for that other test class.
        long before = Invariant.FailureCount;
        using InvariantCapture capture = Invariant.Capture();

        Thread other;
        using (ExecutionContext.SuppressFlow())
        {
            other = new Thread(() => Invariant.Check(false, "an unrelated site failing on another thread"));
            other.Start();
        }

        other.Join();
        Assert.False(Invariant.Check(false, "the site under test"));

        // The process counter saw both (and possibly more, from classes outside this collection); the capture saw one.
        Assert.True(Invariant.FailureCount >= before + 2, $"process-wide count moved by {Invariant.FailureCount - before}, expected at least 2");
        InvariantFailureEvent seen = Assert.Single(capture.Failures);
        Assert.Equal("Check", seen.Kind);
        Assert.Equal("the site under test", seen.Message);
        Assert.Equal(nameof(Capture_SeesOnlyTheFailuresOfItsOwnFlow_WhileTheProcessCounterSeesEveryThread), seen.Member);
        Assert.Equal("InvariantTests.cs", seen.File);
        Assert.True(seen.Line > 0);
    }

    [Fact]
    public async Task Capture_FollowsAwaitedWork_NestsAndEndsOnDispose()
    {
        using InvariantCapture outer = Invariant.Capture();
        await Task.Run(() => Invariant.Check(false, "raised on a pool thread inside the flow"));
        await Task.Yield();
        Invariant.Check(false, "raised after the awaits");
        Assert.Equal(["raised on a pool thread inside the flow", "raised after the awaits"], outer.Failures.Select(f => f.Message));

        using (InvariantCapture inner = Invariant.Capture())
        {
            Invariant.Check(false, "raised in the inner scope");
            Assert.Equal("raised in the inner scope", Assert.Single(inner.Failures).Message);
        }

        Invariant.Check(false, "raised after the inner scope ended");
        Assert.Equal(4, outer.Failures.Count);
        Assert.Equal("raised after the inner scope ended", outer.Failures[^1].Message);

        outer.Dispose();
        Invariant.Check(false, "raised after the outer scope ended");
        Assert.Equal(4, outer.Failures.Count);
    }

    [Fact]
    public void LogLimit_StopsLoggingButKeepsCounting()
    {
        var log = new CapturingLogger();
        Invariant.Configure(new DiagnosticsOptions { InvariantLogLimit = 2 }, log);
        long before = Invariant.FailureCount;

        for (int i = 0; i < 5; i++)
        {
            Invariant.Check(false, "repeated failure");
        }

        Assert.Equal(before + 5, Invariant.FailureCount);
        Assert.Equal(2, log.Entries.Count(e => e.Contains("repeated failure", StringComparison.Ordinal)));
        Assert.Contains(log.Entries, e => e.Contains("further failures of this site are counted, not logged", StringComparison.Ordinal));
        InvariantFailure site = Assert.Single(Invariant.Failures(), f => f.Member == nameof(LogLimit_StopsLoggingButKeepsCounting));
        Assert.Equal(5, site.Count);
    }

    [Fact]
    public void LogLimitZero_CountsOnly()
    {
        var log = new CapturingLogger();
        Invariant.Configure(new DiagnosticsOptions { InvariantLogLimit = 0 }, log);

        Invariant.Check(false, "never logged");

        Assert.Empty(log.Entries);
        Assert.Contains(Invariant.Failures(), f => f.LastMessage == "never logged");
    }

    [Fact]
    public void Assert_IsConditionalOnDebug_AndCheckIsNot()
    {
        foreach (MethodInfo assert in typeof(Invariant).GetMethods().Where(m => m.Name == nameof(Invariant.Assert)))
        {
            ConditionalAttribute attribute = Assert.IsType<ConditionalAttribute>(Assert.Single(assert.GetCustomAttributes(typeof(ConditionalAttribute), inherit: false)));
            Assert.Equal("DEBUG", attribute.ConditionString);
        }

        foreach (MethodInfo check in typeof(Invariant).GetMethods().Where(m => m.Name == nameof(Invariant.Check)))
        {
            Assert.Empty(check.GetCustomAttributes(typeof(ConditionalAttribute), inherit: false));
        }
    }

    [Fact]
    public void Assert_EvaluatesAndThrowsInDebugBuilds_AndIsRemovedFromReleaseBuilds()
    {
        // This test is compiled in both configurations (CI runs Release, developers Debug): the same call
        // site must evaluate its condition and throw in one and not even evaluate it in the other.
        bool evaluated = false;
        bool Condition()
        {
            evaluated = true;
            return false;
        }

        InvariantViolationException? thrown = null;
        try
        {
            Invariant.Assert(Condition(), "debug-only probe");
        }
        catch (InvariantViolationException ex)
        {
            thrown = ex;
        }

#if DEBUG
        Assert.True(evaluated, "a Debug build must evaluate the Assert condition");
        Assert.NotNull(thrown);
        Assert.Contains("debug-only probe", thrown.Message, StringComparison.Ordinal);
        Assert.Equal("InvariantTests.cs", thrown.File);
        Assert.Equal(nameof(Assert_EvaluatesAndThrowsInDebugBuilds_AndIsRemovedFromReleaseBuilds), thrown.Member);
        Assert.Equal("Debug", CrashReport.BuildConfiguration);
#else
        Assert.False(evaluated, "a Release build must not evaluate the Assert condition");
        Assert.Null(thrown);
        Assert.Equal("Release", CrashReport.BuildConfiguration);
#endif
    }

    [Fact]
    public void Check_RunsInEveryConfiguration()
    {
        bool evaluated = false;
        bool Condition()
        {
            evaluated = true;
            return false;
        }

        Assert.False(Invariant.Check(Condition(), "always-on probe"));
        Assert.True(evaluated);
    }

    [Fact]
    public void DebugBreak_IsANoOp_WithoutAnAttachedDebugger()
    {
        Invariant.Configure(new DiagnosticsOptions { BreakOnInvariant = true }, logger: null);
        if (!Debugger.IsAttached)
        {
            Invariant.DebugBreak(); // must return
        }
    }

    [Fact]
    public void ExitCodeOutsideTheRequestableRange_IsCountedAsAnInvariantFailure_ButStillStored()
    {
        int previous = ExitCodes.Current;
        try
        {
            long before = Invariant.FailureCount;
            ExitCodes.Current = ExitCodes.MaxRequested + 1;
            Assert.Equal(ExitCodes.MaxRequested + 1, ExitCodes.Current);
            Assert.Equal(before + 1, Invariant.FailureCount);

            ExitCodes.Current = ExitCodes.Restart;
            Assert.Equal(before + 1, Invariant.FailureCount);
        }
        finally
        {
            ExitCodes.Current = previous;
        }
    }

    private sealed class ToStringProbe
    {
        public int Calls;

        public override string ToString()
        {
            Calls++;
            return "probe#" + Calls;
        }
    }
}
