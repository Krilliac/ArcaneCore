using System.Diagnostics;
using System.Text.Json;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.World.Tests.Ops;

/// <summary>
/// scripts/dev-runner.ps1 writes start-world.ps1 / start-realm.ps1 by inlining the run directory, the project path and the tee
/// script path into single-quoted PowerShell literals. A path with an apostrophe (a profile named O'Brien) has to survive that.
/// </summary>
/// <remarks>
/// The real New-WindowScript function is lifted out of the script with the PowerShell parser and run in a harness that sits in
/// a directory whose name contains an apostrophe (so $PSScriptRoot, and with it the tee path, carries one too). The generated
/// file is then parsed by the PowerShell parser: it must have no syntax errors and its string literals must hold the paths unchanged.
/// </remarks>
public sealed class DevRunnerWindowScriptTests
{
    private static readonly string DevRunner = Path.Combine(RepositorySource.RequireRoot(), "scripts", "dev-runner.ps1");

    private const string Harness = """
        param([string]$ScriptPath, [string]$Run, [string]$Project)
        $ErrorActionPreference = 'Stop'
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$null, [ref]$null)
        $fn = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'New-WindowScript' }, $true)
        $scriptRoot = $PSScriptRoot
        . ([scriptblock]::Create($fn.Extent.Text.Replace('$PSScriptRoot', '$scriptRoot')))
        $Name = 'dev'
        $environmentName = 'Development'
        $RestartOnRudeEdit = $false
        $run = $Run
        New-Item -ItemType Directory -Force -Path $run | Out-Null
        $generated = New-WindowScript 'world' $Project @() 'world.log'
        $errors = $null
        $tree = [System.Management.Automation.Language.Parser]::ParseFile($generated, [ref]$null, [ref]$errors)
        $strings = @()
        if ($tree) { $strings = @($tree.FindAll({ param($n) $n -is [System.Management.Automation.Language.StringConstantExpressionAst] }, $true) | ForEach-Object { $_.Value }) }
        [ordered]@{ parseErrors = @($errors | ForEach-Object { $_.Message }); strings = $strings; tee = (Join-Path $PSScriptRoot 'dev-runner-tee.ps1') } | ConvertTo-Json -Depth 4 -Compress
        """;

    [PwshFact]
    public void WindowScript_WithApostropheInEveryInlinedPath_ParsesAndKeepsThePathsIntact()
    {
        string box = Path.Combine(Path.GetTempPath(), "arcane-devrunner-" + Guid.NewGuid().ToString("N"), "O'Brien");
        Directory.CreateDirectory(box);
        try
        {
            string harness = Path.Combine(box, "harness.ps1");
            File.WriteAllText(harness, Harness);
            string run = Path.Combine(box, "run'root", "ArcaneCore-dev-dev");
            string project = Path.Combine(box, "repo's", "World.csproj");

            JsonElement result = RunHarness(harness, run, project);

            Assert.Empty(result.GetProperty("parseErrors").EnumerateArray());
            string[] strings = result.GetProperty("strings").EnumerateArray().Select(e => e.GetString()!).ToArray();
            Assert.Contains(run, strings);
            Assert.Contains(result.GetProperty("tee").GetString()!, strings);
            Assert.Contains(strings, s => s.Contains("--project \"" + project + "\"", StringComparison.Ordinal));
            // The script joins these with a hard-coded backslash (it is a Windows-only runner), so build the expectation the same way rather than via Path.Combine, which uses '/' on Linux CI.
            Assert.Contains(run + "\\logs\\world.log", strings);
            Assert.Contains(run + "\\stop.flag", strings);
        }
        finally
        {
            try
            {
                Directory.Delete(Directory.GetParent(box)!.FullName, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [PwshFact]
    public void WindowScript_WithPlainPaths_ParsesAndKeepsThePathsIntact()
    {
        // Control: the same harness with no apostrophe anywhere must pass before and after the fix, so a harness fault cannot masquerade as the bug.
        string box = Path.Combine(Path.GetTempPath(), "arcane-devrunner-" + Guid.NewGuid().ToString("N"), "plain");
        Directory.CreateDirectory(box);
        try
        {
            string harness = Path.Combine(box, "harness.ps1");
            File.WriteAllText(harness, Harness);
            string run = Path.Combine(box, "run", "ArcaneCore-dev-dev");
            string project = Path.Combine(box, "repo", "World.csproj");

            JsonElement result = RunHarness(harness, run, project);

            Assert.Empty(result.GetProperty("parseErrors").EnumerateArray());
            string[] strings = result.GetProperty("strings").EnumerateArray().Select(e => e.GetString()!).ToArray();
            Assert.Contains(run, strings);
            Assert.Contains(strings, s => s.Contains("--project \"" + project + "\"", StringComparison.Ordinal));
        }
        finally
        {
            try
            {
                Directory.Delete(Directory.GetParent(box)!.FullName, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static JsonElement RunHarness(string harness, string run, string project)
    {
        var start = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-File", harness, "-ScriptPath", DevRunner, "-Run", run, "-Project", project })
        {
            start.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(start)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error.Result + output);
        return JsonDocument.Parse(output.Trim()).RootElement.Clone();
    }

    private sealed class PwshFactAttribute : FactAttribute
    {
        public PwshFactAttribute()
        {
            if (!PwshAvailable.Value)
            {
                Skip = "pwsh (PowerShell 7) is not on PATH on this host.";
            }
        }
    }

    private static class PwshAvailable
    {
        public static readonly bool Value = Probe();

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
