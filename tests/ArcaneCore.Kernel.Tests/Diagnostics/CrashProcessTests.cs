using System.Diagnostics;
using ArcaneCore.Kernel.Ops;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Diagnostics;

/// <summary>
/// The paths that end a process, watched from outside: this test assembly is started as a child
/// (<see cref="Program"/>, <c>--diagnostics-probe</c>) and its exit code and standard error are checked.
/// FailFast aborts (134 on Linux, 0x80131623 on Windows), so the assertion is "not 0 and not 70".
/// </summary>
public sealed class CrashProcessTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    [Fact]
    public async Task InvariantCheck_UnderFailFastPolicy_AbortsTheProcess_AfterLoggingTheSite()
    {
        ProbeResult result = await RunProbeAsync("invariant-failfast");

        Assert.NotEqual(0, result.ExitCode);
        Assert.NotEqual(ExitCodes.UnhandledException, result.ExitCode);
        Assert.DoesNotContain(Program.SurvivedMarker, result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Invariant Check failed at Run (Program.cs:", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("probe-invariant-message", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnhandledException_DefaultFailFast_WritesTheReportThenAborts()
    {
        ProbeResult result = await RunProbeAsync("unhandled-failfast");

        Assert.NotEqual(0, result.ExitCode);
        Assert.NotEqual(ExitCodes.UnhandledException, result.ExitCode);
        Assert.DoesNotContain(Program.SurvivedMarker, result.StandardOutput, StringComparison.Ordinal);
        AssertReport(result.StandardError, "unhandled exception (AppDomain.UnhandledException)", "probe-unhandled-message");
        Assert.Contains("name: probe-thrower", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnhandledException_ExitPolicy_WritesTheReportThenExits70()
    {
        ProbeResult result = await RunProbeAsync("unhandled-exit");

        Assert.Equal(ExitCodes.UnhandledException, result.ExitCode);
        Assert.DoesNotContain(Program.SurvivedMarker, result.StandardOutput, StringComparison.Ordinal);
        AssertReport(result.StandardError, "unhandled exception (AppDomain.UnhandledException)", "probe-unhandled-message");
    }

    [Fact]
    public async Task UnobservedTask_DefaultLog_WritesTheReportAndTheProcessGoesOn()
    {
        ProbeResult result = await RunProbeAsync("unobserved-log");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(Program.SurvivedMarker, result.StandardOutput, StringComparison.Ordinal);
        AssertReport(result.StandardError, "unobserved task exception (TaskScheduler.UnobservedTaskException)", "probe-unobserved-message");
    }

    [Fact]
    public async Task UnobservedTask_ExitPolicy_Exits70()
    {
        ProbeResult result = await RunProbeAsync("unobserved-exit");

        Assert.Equal(ExitCodes.UnhandledException, result.ExitCode);
        Assert.DoesNotContain(Program.SurvivedMarker, result.StandardOutput, StringComparison.Ordinal);
        AssertReport(result.StandardError, "unobserved task exception", "probe-unobserved-message");
    }

    [Fact]
    public async Task UnobservedTask_FailFastPolicy_Aborts()
    {
        ProbeResult result = await RunProbeAsync("unobserved-failfast");

        Assert.NotEqual(0, result.ExitCode);
        Assert.NotEqual(ExitCodes.UnhandledException, result.ExitCode);
        Assert.DoesNotContain(Program.SurvivedMarker, result.StandardOutput, StringComparison.Ordinal);
        AssertReport(result.StandardError, "unobserved task exception", "probe-unobserved-message");
    }

    [Fact]
    public async Task UseArcaneDiagnostics_WithAnUnknownPolicyName_Exits78BeforeTheHostIsBuilt()
    {
        ProbeResult result = await RunProbeAsync("hosting-bad-config", "--Diagnostics:OnUnhandled=Bogus");

        Assert.Equal(ExitCodes.InvalidConfiguration, result.ExitCode);
        Assert.DoesNotContain(Program.SurvivedMarker, result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("ERROR Diagnostics:", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Diagnostics:OnUnhandled", result.StandardError, StringComparison.Ordinal); // the binder names the key
        Assert.Contains("UnhandledExceptionPolicy", result.StandardError, StringComparison.Ordinal);
    }

    private static void AssertReport(string stderr, string title, string message)
    {
        Assert.Contains("==== ArcaneCore crash report: " + title, stderr, StringComparison.Ordinal);
        // An unobserved task exception arrives wrapped in an AggregateException; the inner type is in the stack.
        Assert.Contains(title.StartsWith("unobserved", StringComparison.Ordinal) ? "type: System.AggregateException" : "type: System.InvalidOperationException", stderr, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: " + message, stderr, StringComparison.Ordinal);
        Assert.Contains(title.StartsWith("unobserved", StringComparison.Ordinal) ? "inner exceptions: 1" : "message: " + message, stderr, StringComparison.Ordinal);
        Assert.Contains("managed stack:", stderr, StringComparison.Ordinal);
        Assert.Contains("[process]", stderr, StringComparison.Ordinal);
        Assert.Contains("runtime: .NET", stderr, StringComparison.Ordinal);
        Assert.Contains("[memory]", stderr, StringComparison.Ordinal);
        Assert.Contains("working set: ", stderr, StringComparison.Ordinal);
        Assert.Contains("[probe]\n  probe context line: 4711", stderr.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("==== end of crash report ====", stderr, StringComparison.Ordinal);
    }

    private static async Task<ProbeResult> RunProbeAsync(string scenario, params string[] extra)
    {
        var start = new ProcessStartInfo(DotNetHost())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add(Program.ProbeSwitch);
        start.ArgumentList.Add(scenario);
        foreach (string argument in extra)
        {
            start.ArgumentList.Add(argument);
        }

        // No dump on the FailFast paths; the test only wants the exit status and the text.
        start.Environment["DOTNET_DbgEnableMiniDump"] = "0";

        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start the probe process");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(Timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"probe '{scenario}' did not end within {Timeout}");
        }

        return new ProbeResult(process.ExitCode, await stdout, await stderr);
    }

    /// <summary>The dotnet host that runs this test (the SDK sets DOTNET_HOST_PATH; otherwise the muxer on PATH).</summary>
    private static string DotNetHost()
    {
        string? fromSdk = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(fromSdk) && File.Exists(fromSdk))
        {
            return fromSdk;
        }

        string? current = Environment.ProcessPath;
        if (current is not null && string.Equals(Path.GetFileNameWithoutExtension(current), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return current;
        }

        return "dotnet";
    }

    private sealed record ProbeResult(int ExitCode, string StandardOutput, string StandardError);
}
