using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ArcaneCore.World.Tests.Ops;

/// <summary>
/// Runs the code-quality helpers (tools/ops/quality/QualityLib.ps1) on a hand-made Stryker report, and the three scripts
/// (mutate.ps1, decompile.ps1, jit-disasm.ps1) on arguments they must refuse: each has to exit 2 and say why, never 0.
/// </summary>
public sealed class QualityScriptTests
{
    private static readonly string Quality = Path.Combine(RepositorySource.RequireRoot(), "tools", "ops", "quality");

    [PwshFact]
    public void Summary_CountsStatuses_ScoresDetectedOverJudged_AndListsSurvivors()
    {
        // 2 killed + 1 timeout detected; 1 survived + 1 no-coverage undetected: 3 of 5 judged = 60 %. A compile error and an ignored
        // mutant are not judged. Source line 3 is "    if (a > b) return a;" (columns are 1-based, the end column one past the text).
        const string report = """
            {"files":{"src\\X\\A.cs":{"language":"cs","source":"int M(int a, int b)\n{\n    if (a > b) return a;\n    return b;\n}",
              "mutants":[
                {"id":"1","mutatorName":"Equality","replacement":">=","location":{"start":{"line":3,"column":11},"end":{"line":3,"column":16}},"status":"Killed"},
                {"id":"2","mutatorName":"Equality","replacement":"<","location":{"start":{"line":3,"column":9},"end":{"line":3,"column":16}},"status":"Killed"},
                {"id":"3","mutatorName":"Block removal","replacement":"{}","location":{"start":{"line":3,"column":5},"end":{"line":3,"column":25}},"status":"Timeout"},
                {"id":"4","mutatorName":"Conditional (true)","replacement":"true","location":{"start":{"line":3,"column":9},"end":{"line":3,"column":14}},"status":"Survived"},
                {"id":"5","mutatorName":"Statement","replacement":"","location":{"start":{"line":4,"column":5},"end":{"line":4,"column":14}},"status":"NoCoverage"},
                {"id":"6","mutatorName":"String","replacement":"x","location":{"start":{"line":1,"column":1},"end":{"line":1,"column":4}},"status":"CompileError"},
                {"id":"7","mutatorName":"String","replacement":"y","location":{"start":{"line":1,"column":1},"end":{"line":1,"column":4}},"status":"Ignored"}]}}}
            """;
        JsonElement r = Eval($$"""
            $report = @'
            {{report}}
            '@ | ConvertFrom-Json
            $s = Get-QualityMutationSummary -Report $report
            [ordered]@{ killed = $s.Killed; survived = $s.Survived; noCoverage = $s.NoCoverage; timeout = $s.Timeout; compileError = $s.CompileError
                        ignored = $s.Ignored; tested = $s.Tested; score = $s.Score
                        survivors = @($s.Survivors | ForEach-Object { "$($_.File):$($_.Line):$($_.Column) $($_.Status) $($_.Mutator) [$($_.Original)] -> [$($_.Mutated)]" }) }
            """);
        Assert.Equal(2, r.GetProperty("killed").GetInt32());
        Assert.Equal(1, r.GetProperty("survived").GetInt32());
        Assert.Equal(1, r.GetProperty("noCoverage").GetInt32());
        Assert.Equal(1, r.GetProperty("timeout").GetInt32());
        Assert.Equal(1, r.GetProperty("compileError").GetInt32());
        Assert.Equal(1, r.GetProperty("ignored").GetInt32());
        Assert.Equal(5, r.GetProperty("tested").GetInt32());
        Assert.Equal(60.0, r.GetProperty("score").GetDouble());
        Assert.Equal(
        [
            "src/X/A.cs:3:9 Survived Conditional (true) [a > b] -> [true]",
            "src/X/A.cs:4:5 NoCoverage Statement [return b;] -> []",
        ], r.GetProperty("survivors").EnumerateArray().Select(e => e.GetString()!));
    }

    [PwshFact]
    public void Summary_OfARunThatJudgedNothing_HasNoScore()
    {
        JsonElement r = Eval("""
            $report = '{"files":{"A.cs":{"source":"x","mutants":[{"id":"1","mutatorName":"M","replacement":"y","location":{"start":{"line":1,"column":1},"end":{"line":1,"column":2}},"status":"CompileError"}]}}}' | ConvertFrom-Json
            $s = Get-QualityMutationSummary -Report $report
            [ordered]@{ tested = $s.Tested; score = $s.Score; survivors = $s.Survivors.Count }
            """);
        Assert.Equal(0, r.GetProperty("tested").GetInt32());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("score").ValueKind);
        Assert.Equal(0, r.GetProperty("survivors").GetInt32());
    }

    [PwshFact]
    public void MutateGlob_DropsTheProjectDirectoryPrefix_AndKeepsExcludes()
    {
        JsonElement r = Eval("""
            @(
              (ConvertTo-QualityMutateGlob -Glob 'src/ArcaneCore.Game/AntiCheat/X.cs' -ProjectRelativeDirectory 'src/ArcaneCore.Game'),
              (ConvertTo-QualityMutateGlob -Glob '!src\ArcaneCore.Game\Gen\**' -ProjectRelativeDirectory 'src/ArcaneCore.Game'),
              (ConvertTo-QualityMutateGlob -Glob '**/Foo.cs' -ProjectRelativeDirectory 'src/ArcaneCore.Game')
            )
            """);
        Assert.Equal(["AntiCheat/X.cs", "!Gen/**", "**/Foo.cs"], r.EnumerateArray().Select(e => e.GetString()!));
    }

    [PwshFact]
    public void Mutate_RefusesAMissingProject_WithExitCode2()
    {
        (int code, string output) = RunScript("mutate.ps1", "-Project", "src/NoSuch/NoSuch.csproj", "-TestProject", "tests/NoSuch.Tests/NoSuch.Tests.csproj", "-Files", "**/X.cs");
        Assert.Equal(2, code);
        Assert.Contains("-Project 'src/NoSuch/NoSuch.csproj' does not exist", output);
    }

    [PwshFact]
    public void Mutate_RefusesAProjectWithoutItsOwnTargetFramework_WithExitCode2()
    {
        using var temp = new TempDirectory();
        string csproj = Path.Combine(temp.Path, "Lib.csproj");
        File.WriteAllText(csproj, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        (int code, string output) = RunScript("mutate.ps1", "-Project", csproj, "-TestProject", csproj, "-Files", "**/X.cs");
        Assert.Equal(2, code);
        Assert.Contains("has no <TargetFramework> of its own", output);
    }

    [PwshFact]
    public void Decompile_RefusesAMissingAssembly_WithExitCode2()
    {
        (int code, string output) = RunScript("decompile.ps1", "-Assembly", "NoSuch.Assembly.Anywhere", "-Type", "A.B");
        Assert.Equal(2, code);
        Assert.Contains("was not found", output);
    }

    [PwshFact]
    public void JitDisasm_RefusesAMissingTestProject_WithExitCode2()
    {
        (int code, string output) = RunScript("jit-disasm.ps1", "-TestProject", "tests/NoSuch/NoSuch.csproj", "-Filter", "x", "-Method", "A:B");
        Assert.Equal(2, code);
        Assert.Contains("does not exist", output);
    }

    private static JsonElement Eval(string body)
    {
        using var temp = new TempDirectory();
        string script = Path.Combine(temp.Path, "run.ps1");
        File.WriteAllText(script, $$"""
            $ErrorActionPreference = 'Stop'
            . '{{Path.Combine(Quality, "QualityLib.ps1").Replace("'", "''")}}'
            $value = & { {{body}} }
            $value | ConvertTo-Json -Depth 8 -Compress
            """, new UTF8Encoding(false));
        (int code, string output) = Pwsh(["-File", script]);
        Assert.True(code == 0, $"pwsh exited {code}: {output}");
        return JsonDocument.Parse(output.Trim()).RootElement.Clone();
    }

    private static (int Code, string Output) RunScript(string name, params string[] arguments)
        => Pwsh(["-File", Path.Combine(Quality, name), .. arguments]);

    private static (int Code, string Output) Pwsh(IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in new[] { "-NoProfile", "-NonInteractive" }.Concat(arguments))
        {
            start.ArgumentList.Add(a);
        }

        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("pwsh did not finish within 120 s");
        }

        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "arcane-quality-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    /// <summary>Runs only where PowerShell 7 (<c>pwsh</c>) is on PATH; CI (ubuntu-latest) has it.</summary>
    private sealed class PwshFactAttribute : FactAttribute
    {
        private static readonly Lazy<bool> Available = new(Probe);

        public PwshFactAttribute()
        {
            if (!Available.Value)
            {
                Skip = "pwsh (PowerShell 7) is not on PATH on this host.";
            }
        }

        private static bool Probe()
        {
            try
            {
                var start = new ProcessStartInfo("pwsh", ["-NoProfile", "-NonInteractive", "-Command", "$PSVersionTable.PSVersion.Major"])
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using Process process = Process.Start(start)!;
                string text = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                return process.ExitCode == 0 && int.TryParse(text, out int major) && major >= 7;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }
    }
}
