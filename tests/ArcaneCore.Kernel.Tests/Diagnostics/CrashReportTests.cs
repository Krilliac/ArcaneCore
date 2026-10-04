using System.Text;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Diagnostics;

/// <summary>The report text, and the handler's install/reconfigure/dispose contract (no process is ended here; see <see cref="CrashProcessTests"/>).</summary>
[Collection(DiagnosticsCollection.Name)]
public sealed class CrashReportTests
{
    [Fact]
    public void Render_CarriesTheException_Stack_ProcessRuntimeThreadAndMemoryFacts_AndTheProviders()
    {
        Exception exception;
        try
        {
            ThrowDeep();
            throw new InvalidOperationException("unreachable");
        }
        catch (InvalidOperationException ex)
        {
            exception = ex;
        }

        string report = CrashReport.Render(CrashKind.Unhandled, exception, [new FixedProvider(), new ThrowingProvider()], new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc));

        Assert.Contains("unhandled exception (AppDomain.UnhandledException) at 2026-10-04 12:00:00.000 UTC", report, StringComparison.Ordinal);
        Assert.Contains("type: System.InvalidOperationException", report, StringComparison.Ordinal);
        Assert.Contains("message: deep failure", report, StringComparison.Ordinal);
        Assert.Contains(nameof(ThrowDeep), report, StringComparison.Ordinal); // the managed stack
        Assert.Contains("[process]", report, StringComparison.Ordinal);
        Assert.Contains("pid: " + Environment.ProcessId, report, StringComparison.Ordinal);
        Assert.Contains("runtime: .NET", report, StringComparison.Ordinal);
        Assert.Contains("kernel build: " + CrashReport.BuildConfiguration, report, StringComparison.Ordinal);
        Assert.Contains("[thread]", report, StringComparison.Ordinal);
        Assert.Contains("managed id: " + Environment.CurrentManagedThreadId, report, StringComparison.Ordinal);
        Assert.Contains("[memory]", report, StringComparison.Ordinal);
        Assert.Contains("working set: ", report, StringComparison.Ordinal);
        Assert.Contains("gc heap: ", report, StringComparison.Ordinal);
        Assert.Contains("gc collections gen0/gen1/gen2: ", report, StringComparison.Ordinal);
        Assert.Contains("[invariants]", report, StringComparison.Ordinal);
        Assert.Contains("[fixed]\n  answer: 42\n", report, StringComparison.Ordinal);
        Assert.Contains("[throwing]\n  throwing failed to describe itself: NotSupportedException: no", report, StringComparison.Ordinal);
        Assert.DoesNotContain("--Database", report, StringComparison.Ordinal); // never the command line
        Assert.EndsWith("==== end of crash report ====", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_WithoutAnException_AndWithAnAggregate_AndAnInvariantViolation()
    {
        string none = CrashReport.Render(CrashKind.UnobservedTask, null, [], DateTime.UtcNow);
        Assert.Contains("(no managed exception object)", none, StringComparison.Ordinal);

        var aggregate = new AggregateException(new InvalidOperationException("a"), new TimeoutException("b"));
        string aggregated = CrashReport.Render(CrashKind.UnobservedTask, aggregate, [], DateTime.UtcNow);
        Assert.Contains("inner exceptions: 2", aggregated, StringComparison.Ordinal);
        Assert.Contains("TimeoutException: b", aggregated, StringComparison.Ordinal);

        var violation = new InvariantViolationException("broken", "Member", "File.cs", 12);
        string invariant = CrashReport.Render(CrashKind.Unhandled, violation, [], DateTime.UtcNow);
        Assert.Contains("invariant site: Member (File.cs:12)", invariant, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ListsTheInvariantCounters()
    {
        Invariant.Configure(new DiagnosticsOptions { InvariantLogLimit = 0 }, logger: null);
        try
        {
            Invariant.Check(false, "counted for the report");
            string report = CrashReport.Render(CrashKind.Unhandled, null, [], DateTime.UtcNow);
            Assert.Contains(nameof(Render_ListsTheInvariantCounters), report, StringComparison.Ordinal);
            Assert.Contains("counted for the report", report, StringComparison.Ordinal);
        }
        finally
        {
            Invariant.Configure(new DiagnosticsOptions(), logger: null);
        }
    }

    [Fact]
    public void Install_IsIdempotent_ReconfigureSwapsSinkAndProviders_DisposeDetaches()
    {
        Assert.Null(CrashHandler.Current);
        var first = new DiagnosticsOptions { OnUnhandled = UnhandledExceptionPolicy.Exit };
        CrashHandler handler = CrashHandler.Install(first, StandardErrorCrashSink.Instance);
        try
        {
            Assert.Same(handler, CrashHandler.Current);
            Assert.Same(first, handler.Options);
            Assert.Same(StandardErrorCrashSink.Instance, handler.Sink);

            var log = new CapturingLogger();
            var second = new DiagnosticsOptions { OnUnhandled = UnhandledExceptionPolicy.FailFast };
            CrashHandler again = CrashHandler.Install(second, new LoggerCrashSink(log), [new FixedProvider()]);
            Assert.Same(handler, again);
            Assert.Same(second, handler.Options);
            Assert.IsType<LoggerCrashSink>(handler.Sink);
            Assert.Contains("[fixed]\n  answer: 42\n", handler.Render(CrashKind.Unhandled, new InvalidOperationException("x")), StringComparison.Ordinal);
        }
        finally
        {
            handler.Dispose();
        }

        Assert.Null(CrashHandler.Current);
        handler.Dispose(); // idempotent
    }

    [Fact]
    public void LoggerSink_WritesTheReportAtTheGivenLevel_WithTheException()
    {
        var log = new CapturingLogger();
        var sink = new LoggerCrashSink(log);
        var exception = new InvalidOperationException("sunk");

        sink.Write(LogLevel.Critical, "the report", exception);

        Assert.False(sink.IsSynchronous);
        Assert.True(StandardErrorCrashSink.Instance.IsSynchronous);
        Assert.Equal("Critical: the report", Assert.Single(log.Entries));
    }

    [Fact]
    public void FirstChanceHook_FollowsTheKernelBuild()
    {
        CrashHandler handler = CrashHandler.Install(new DiagnosticsOptions { FirstChanceExceptions = true }, StandardErrorCrashSink.Instance);
        try
        {
            Assert.Equal(CrashReport.BuildConfiguration == "Debug", handler.FirstChanceHooked);
            handler.Reconfigure(new DiagnosticsOptions { FirstChanceExceptions = false }, StandardErrorCrashSink.Instance, null);
            Assert.False(handler.FirstChanceHooked);
        }
        finally
        {
            handler.Dispose();
        }
    }

    private static void ThrowDeep() => throw new InvalidOperationException("deep failure");

    private sealed class FixedProvider : ICrashContextProvider
    {
        public string Name => "fixed";

        public void Describe(StringBuilder report) => report.Append("  answer: 42\n");
    }

    private sealed class ThrowingProvider : ICrashContextProvider
    {
        public string Name => "throwing";

        public void Describe(StringBuilder report) => throw new NotSupportedException("no");
    }
}
