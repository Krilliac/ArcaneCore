using System.Diagnostics;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.World.Tests.Ops;

/// <summary>
/// tools/ops/ci/test-stages.sh is the gate itself (ci.yml and node CI): a defect there reads as a green run. Its self-test drives it
/// with a fake <c>dotnet</c> through a pass, a flaky test, a test that fails twice, a rerun or timing stage that matches 0 tests, a
/// killed and an aborted run, the time budgets (a hard stop that names the slow test, the off switch,
/// the class budget, a filtered run) and concurrent targets, and checks every verdict. Linux only (where CI and node CI run it); bash and coreutils are all it needs.
/// </summary>
public sealed class TestStagesScriptTests
{
    [LinuxBashFact]
    public void TheGateSelfTest_SeesEveryVerdict()
    {
        string script = Path.Combine(RepositorySource.RequireRoot(), "tools", "ops", "ci", "test-stages-selftest.sh");
        var start = new ProcessStartInfo("/bin/bash", [script])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        using Process process = Process.Start(start)!;
        process.StandardInput.Close();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        string stdout = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(300_000), "the self-test did not finish in 5 minutes:\n" + stdout);

        Assert.True(process.ExitCode == 0, $"exit {process.ExitCode}\n{stdout}\n{stderr.Result}");
        Assert.Contains("test-stages self-test: all cases behaved", stdout, StringComparison.Ordinal);
        foreach (string budgetCase in new[] { "hardstop", "hardstop-off", "slowclass", "slowclass-filtered", "concurrent", "stalerc", "norc", "wrapwait", "dup-targets", "knob-text" })
        {
            Assert.Contains("ok   " + budgetCase + ":", stdout, StringComparison.Ordinal);
        }

        Assert.Equal(32, stdout.Split('\n').Count(l => l.StartsWith("ok   ", StringComparison.Ordinal)));
    }
}

/// <summary>Skipped (reported, not passed) off Linux: Windows hosts reach a different bash (Git Bash or WSL) or none.</summary>
public sealed class LinuxBashFactAttribute : FactAttribute
{
    public LinuxBashFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/bin/bash"))
        {
            Skip = "needs Linux /bin/bash (the gate runs there)";
        }
    }
}
