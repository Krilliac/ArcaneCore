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
    public async Task UnhandledException_IsLoggedThroughTheSink_AtCritical_WithTheException()
    {
        ProbeResult result = await RunProbeAsync("unhandled-level");

        Assert.Contains("CRASH-SINK level=Critical exception=System.InvalidOperationException", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnobservedTask_UnderTheDefaultLogPolicy_IsLoggedThroughTheSink_AtCritical()
    {
        ProbeResult result = await RunProbeAsync("unobserved-level");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("CRASH-SINK level=Critical exception=System.AggregateException", result.StandardError, StringComparison.Ordinal);
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

    [Fact]
    public async Task CrashDialog_InAnInteractiveSession_ShowsAfterTheReport_WritesTheReportFile_AndStillExits70()
    {
        using var directory = new TempDirectory();
        ProbeResult result = await RunProbeAsync("unhandled-dialog", directory.Path);

        Assert.Equal(ExitCodes.UnhandledException, result.ExitCode);
        Assert.DoesNotContain(Program.SurvivedMarker, result.StandardOutput, StringComparison.Ordinal);
        AssertReport(result.StandardError, "unhandled exception (AppDomain.UnhandledException)", "probe-unhandled-message");
        string stderr = result.StandardError.Replace("\r\n", "\n", StringComparison.Ordinal);
        int reportEnd = stderr.IndexOf("==== end of crash report ====", StringComparison.Ordinal);
        int dialog = stderr.IndexOf("CRASH-DIALOG title=", StringComparison.Ordinal);
        Assert.True(reportEnd >= 0 && dialog > reportEnd, "the dialog must come after the crash report is written");
        Assert.Contains("CRASH-DIALOG title=ArcaneCore.Kernel.Tests crashed: InvalidOperationException", stderr, StringComparison.Ordinal);
        Assert.Contains("When you close this box the process exits with code 70 (Diagnostics:OnUnhandled = Exit).", stderr, StringComparison.Ordinal);
        Assert.Contains("Fault ledger: site unhandled, signature ", stderr, StringComparison.Ordinal);
        Assert.Contains("Thread: ", stderr, StringComparison.Ordinal);
        Assert.Contains("\"probe-thrower\"", stderr, StringComparison.Ordinal);
        Assert.Contains("System.FormatException: probe-inner-message", stderr[dialog..], StringComparison.Ordinal);

        string file = Assert.Single(Directory.GetFiles(directory.Path, "crash-report.ArcaneCore.Kernel.Tests.*Z.txt"));
        Assert.Contains("Full report: " + file, stderr, StringComparison.Ordinal);
        string written = File.ReadAllText(file);
        Assert.Contains("System.FormatException: probe-inner-message", written, StringComparison.Ordinal);
        Assert.Contains("==== ArcaneCore crash report: unhandled exception", written, StringComparison.Ordinal);
        Assert.Contains("[probe]\n  probe context line: 4711", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrashDialog_UnderTheFailFastPolicy_StillAborts()
    {
        using var directory = new TempDirectory();
        ProbeResult result = await RunProbeAsync("unhandled-dialog-failfast", directory.Path);

        Assert.NotEqual(0, result.ExitCode);
        Assert.NotEqual(ExitCodes.UnhandledException, result.ExitCode);
        Assert.Contains("When you close this box the process aborts (Environment.FailFast; Diagnostics:OnUnhandled = FailFast).", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(Program.SurvivedMarker, result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrashDialog_ThatThrows_IsRecordedAndReported_AndTheCrashStillExits70()
    {
        using var directory = new TempDirectory();
        ProbeResult result = await RunProbeAsync("unhandled-dialog-throws", directory.Path);

        Assert.Equal(ExitCodes.UnhandledException, result.ExitCode);
        AssertReport(result.StandardError, "unhandled exception (AppDomain.UnhandledException)", "probe-unhandled-message");
        Assert.Contains("FAULT-NOTICED site=unhandled type=System.InvalidOperationException", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("FAULT-NOTICED site=diagnostics.crash-dialog type=System.InvalidOperationException", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("crash dialog: the crash dialog failed: InvalidOperationException: probe-dialog-failure", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrashDialog_WithTheRealEnvironment_AndRedirectedStreams_OpensNothing_AndExits70AsBefore()
    {
        using var directory = new TempDirectory();
        ProbeResult result = await RunProbeAsync("unhandled-dialog-live", directory.Path);

        Assert.Equal(ExitCodes.UnhandledException, result.ExitCode);
        AssertReport(result.StandardError, "unhandled exception (AppDomain.UnhandledException)", "probe-unhandled-message");
        Assert.DoesNotContain("crash dialog:", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task CrashDialog_InASupervisedChild_WritesTheReportFile_WithoutABox_AndExits70AsBefore()
    {
        using var directory = new TempDirectory();
        ProbeResult result = await RunProbeAsync(new Dictionary<string, string> { [ArcaneCore.Kernel.Ops.ServerConsole.SupervisedChildDrain.EnvironmentVariable] = "world" }, "unhandled-dialog-live", directory.Path);

        Assert.Equal(ExitCodes.UnhandledException, result.ExitCode);
        AssertReport(result.StandardError, "unhandled exception (AppDomain.UnhandledException)", "probe-unhandled-message");
        Assert.DoesNotContain("crash dialog:", result.StandardError, StringComparison.Ordinal);
        string file = Assert.Single(Directory.GetFiles(directory.Path, "crash-report.*Z.txt"));
        string written = File.ReadAllText(file);
        Assert.Contains("Message: probe-unhandled-message", written, StringComparison.Ordinal);
        Assert.Contains("==== end of crash report ====", written, StringComparison.Ordinal);
    }

    // ---- the in-process crash dump (Diagnostics:CrashDump). Mode=Mini keeps every case small and fast. ----

    [Fact]
    public async Task CrashDump_Mini_UnderFailFast_WritesADumpNamedByDaemonPidAndTime()
    {
        using var directory = new TempDirectory();
        ProbeResult result = await RunProbeAsync("dump", "failfast", "Mini", directory.Path);

        Assert.NotEqual(0, result.ExitCode);
        Assert.NotEqual(ExitCodes.UnhandledException, result.ExitCode);
        string dump = Assert.Single(DumpFiles(directory.Path));
        Assert.Matches(@"^ArcaneCore\.Kernel\.Tests-\d+-\d{8}-\d{6}\.dmp$", Path.GetFileName(dump));
        Assert.True(new FileInfo(dump).Length > 0, "the dump file is empty");
        Assert.Contains("[crash dump]\n  written in-process by ArcaneCore (Diagnostics:CrashDump:Mode = Mini)", result.StandardError.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("crash dump: written " + dump, result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrashDump_Mini_UnderExitPolicy_WritesADump_AndStillExits70()
    {
        using var directory = new TempDirectory();
        ProbeResult result = await RunProbeAsync("dump", "exit", "Mini", directory.Path);

        Assert.Equal(ExitCodes.UnhandledException, result.ExitCode);
        string dump = Assert.Single(DumpFiles(directory.Path));
        Assert.True(new FileInfo(dump).Length > 0, "the dump file is empty");
        Assert.Contains("crash dump: written " + dump, result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrashDump_Mini_ForAnUnobservedTaskThatEndsTheProcess_WritesADump()
    {
        using var directory = new TempDirectory();
        ProbeResult result = await RunProbeAsync("dump", "unobserved-exit", "Mini", directory.Path);

        Assert.Equal(ExitCodes.UnhandledException, result.ExitCode);
        Assert.Single(DumpFiles(directory.Path));
    }

    [Fact]
    public async Task CrashDump_ModeOff_WritesNoDump()
    {
        using var directory = new TempDirectory();
        ProbeResult result = await RunProbeAsync("dump", "exit", "Off", directory.Path);

        Assert.Equal(ExitCodes.UnhandledException, result.ExitCode);
        Assert.Empty(DumpFiles(directory.Path));
        Assert.Contains("[crash dump]\n  none (Diagnostics:CrashDump:Mode = Off", result.StandardError.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrashDump_UnderFailFast_IsSkipped_WhenTheRuntimeWritesItsOwn()
    {
        using var directory = new TempDirectory();
        string runtimeDump = Path.Combine(directory.Path, "runtime.dmp");
        ProbeResult result = await RunProbeAsync(
            new Dictionary<string, string> { ["DOTNET_DbgEnableMiniDump"] = "1", ["DOTNET_DbgMiniDumpType"] = "1", ["DOTNET_DbgMiniDumpName"] = runtimeDump },
            "dump", "failfast", "Mini", directory.Path);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(DumpFiles(directory.Path));
        Assert.Contains("written by the runtime (DOTNET_DbgEnableMiniDump=1", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("crash dump: skipped, the runtime writes one at FailFast", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrashDump_UnderExit_IsStillWritten_EvenWhenTheRuntimeDumpIsEnabled()
    {
        using var directory = new TempDirectory();
        ProbeResult result = await RunProbeAsync(
            new Dictionary<string, string> { ["DOTNET_DbgEnableMiniDump"] = "1", ["DOTNET_DbgMiniDumpName"] = Path.Combine(directory.Path, "runtime.dmp") },
            "dump", "exit", "Mini", directory.Path);

        Assert.Equal(ExitCodes.UnhandledException, result.ExitCode);
        Assert.Single(DumpFiles(directory.Path));
    }

    [Fact]
    public async Task CrashDump_PrunesTheOldestOwnFilesBeyondMaxFiles_AndLeavesOtherFilesAlone()
    {
        using var directory = new TempDirectory();
        DateTime now = DateTime.UtcNow;
        string[] old = ["ArcaneCore.Kernel.Tests-1-20200101-000000.dmp", "ArcaneCore.Kernel.Tests-2-20200102-000000.dmp", "ArcaneCore.Kernel.Tests-3-20200103-000000.dmp"];
        for (int i = 0; i < old.Length; i++)
        {
            string path = Path.Combine(directory.Path, old[i]);
            File.WriteAllText(path, "old");
            File.SetLastWriteTimeUtc(path, now.AddDays(-30 + i));
        }

        string foreign = Path.Combine(directory.Path, "world-99-1234.dmp"); // the launcher's pattern, not ours
        File.WriteAllText(foreign, "launcher");
        File.SetLastWriteTimeUtc(foreign, now.AddDays(-60));

        ProbeResult result = await RunProbeAsync("dump", "exit", "Mini", directory.Path, "2");

        Assert.Equal(ExitCodes.UnhandledException, result.ExitCode);
        string[] kept = [.. DumpFiles(directory.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];
        Assert.Equal(2, kept.Length);
        Assert.Contains(old[2], kept); // the newest old one survives next to the fresh dump
        Assert.DoesNotContain(old[0], kept);
        Assert.DoesNotContain(old[1], kept);
        Assert.True(File.Exists(foreign), "a file outside the pattern must never be pruned");
    }

    private static string[] DumpFiles(string directory) => [.. Directory.GetFiles(directory, "ArcaneCore.Kernel.Tests-*.dmp")];

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "arcanecore-crash-dialog-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A file still held open is left for the temp cleaner.
            }
        }
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

    private static Task<ProbeResult> RunProbeAsync(string scenario, params string[] extra) => RunProbeAsync(null, scenario, extra);

    private static async Task<ProbeResult> RunProbeAsync(IReadOnlyDictionary<string, string>? environment, string scenario, params string[] extra)
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
        foreach ((string key, string value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[key] = value;
        }

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
