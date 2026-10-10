using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ArcaneCore.World.Tests.Ops;

/// <summary>Runs the operator script helpers (tools/ops/OpsLib.ps1) and run-gm-commands.ps1 -DryRun under PowerShell 7.</summary>
/// <remarks>
/// The hand-derived values (largest-remainder counts, base-26 names, the 255-character chat limit) come from the documented rules in
/// OpsLib.ps1 itself. The status line format is the one PlayerbotCommands.Format prints, and the boost lines are the ones
/// CharacterBoostCommand replies with.
/// </remarks>
public sealed class OpsScriptTests
{
    private static readonly string Lib = Path.Combine(RepositorySource.RequireRoot(), "tools", "ops", "OpsLib.ps1");
    private static readonly string RunScript = Path.Combine(RepositorySource.RequireRoot(), "tools", "ops", "run-gm-commands.ps1");

    [PwshFact]
    public void WeightedAssignment_GivesExactCounts_AndInterleaves()
    {
        // 200 over 70/20/10: exactly 140/40/20, no remainder.
        JsonElement r = Eval("""
            $e = ConvertFrom-OpsTeleSpec -Spec 'Westfall=70,Goldshire=20,SentinelHill=10'
            @(Get-OpsWeightedAssignment -Entries $e -Count 200)
            """);
        string[] names = Strings(r);
        Assert.Equal(200, names.Length);
        Assert.Equal(140, names.Count(n => n == "Westfall"));
        Assert.Equal(40, names.Count(n => n == "Goldshire"));
        Assert.Equal(20, names.Count(n => n == "SentinelHill"));
        Assert.Equal(["Westfall", "Goldshire", "SentinelHill", "Westfall", "Goldshire", "SentinelHill"], names.Take(6));
    }

    [PwshFact]
    public void WeightedAssignment_RemainderGoesToLargestFraction_TiesToEarlierEntry()
    {
        // 7 over 5/3/2: exact 3.5/2.1/1.4 -> floors 3/2/1, one left, the largest fraction (.5) is the first entry: 4/2/1.
        // 10 over 1/1/1: 3.33 each -> 3/3/3, one left, a tie, the earlier entry wins: 4/3/3.
        // 4 over 1/2: 1.33/2.67 -> 1/2, one left, the larger fraction (.67) is the second entry: 1/3.
        JsonElement r = Eval("""
            function Tally($spec, $n) {
                $a = Get-OpsWeightedAssignment -Entries (ConvertFrom-OpsTeleSpec -Spec $spec) -Count $n
                [ordered]@{ spec = $spec; counts = @($a | Group-Object | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Count)" }) }
            }
            @((Tally 'A=5,B=3,C=2' 7), (Tally 'A,B,C' 10), (Tally 'A=1,B=2' 4))
            """);
        Assert.Equal(["A=4", "B=2", "C=1"], Strings(r[0].GetProperty("counts")));
        Assert.Equal(["A=4", "B=3", "C=3"], Strings(r[1].GetProperty("counts")));
        Assert.Equal(["A=1", "B=3"], Strings(r[2].GetProperty("counts")));
    }

    [PwshFact]
    public void WeightedAssignment_IsDeterministic_AndZeroCountIsEmpty()
    {
        JsonElement r = Eval("""
            $e = ConvertFrom-OpsTeleSpec -Spec 'Westfall=3,Goldshire=2,Duskwood=1'
            $first = Get-OpsWeightedAssignment -Entries $e -Count 37
            $second = Get-OpsWeightedAssignment -Entries $e -Count 37
            $zero = Get-OpsWeightedAssignment -Entries $e -Count 0
            [ordered]@{ same = (($first -join ',') -eq ($second -join ',')); length = $first.Count; zero = $zero.Count }
            """);
        Assert.True(r.GetProperty("same").GetBoolean());
        Assert.Equal(37, r.GetProperty("length").GetInt32());
        Assert.Equal(0, r.GetProperty("zero").GetInt32());
    }

    [PwshFact]
    public void WeightedAssignment_RefusesNegativeCount()
    {
        Assert.Contains("negative", EvalError("Get-OpsWeightedAssignment -Entries (ConvertFrom-OpsTeleSpec -Spec 'A') -Count -1"));
    }

    [PwshFact]
    public void BotNames_AreDistinctLettersOnly_WithinTheNameLimit()
    {
        // 200 bots need a 2-letter suffix (26 < 200 <= 676): Wfbotaa, Wfbotab, ... index 26 is Wfbotba, index 199 is 7*26+17 = 'h','r'.
        string[] names = Strings(Eval("@(Get-OpsBotNames -Prefix 'wfbot' -Count 200)"));
        Assert.Equal(200, names.Length);
        Assert.Equal(200, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("Wfbotaa", names[0]);
        Assert.Equal("Wfbotab", names[1]);
        Assert.Equal("Wfbotba", names[26]);
        Assert.Equal("Wfbothr", names[199]);
        Assert.All(names, n => Assert.Matches("^[A-Z][a-z]{1,11}$", n));

        // 26 fits one letter, 27 needs two.
        string[] small = Strings(Eval("@(Get-OpsBotNames -Prefix 'Wfbot' -Count 26)"));
        Assert.Equal("Wfbota", small[0]);
        Assert.Equal("Wfbotz", small[25]);
        Assert.Equal("Wfbotaa", Strings(Eval("@(Get-OpsBotNames -Prefix 'Wfbot' -Count 27)"))[0]);
    }

    [PwshFact]
    public void BotNames_LongestAllowedPrefixAtMaximumCount_StaysWithinTwelveLetters()
    {
        string[] names = Strings(Eval("@(Get-OpsBotNames -Prefix 'Abcdefgh' -Count 17576)"));
        Assert.Equal(17576, names.Length);
        Assert.Equal(17576, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(names, n => Assert.True(n.Length <= 12 && n.Length >= 2, n));
        Assert.Equal("Abcdefghaaa", names[0]);
        Assert.Equal("Abcdefghzzz", names[^1]);
    }

    [PwshFact]
    public void BotNames_RefuseBadPrefixAndCount()
    {
        Assert.Contains("2-8 letters", EvalError("Get-OpsBotNames -Prefix 'Abcdefghi' -Count 5"));   // 9 letters
        Assert.Contains("2-8 letters", EvalError("Get-OpsBotNames -Prefix 'A' -Count 5"));
        Assert.Contains("2-8 letters", EvalError("Get-OpsBotNames -Prefix 'Bot1' -Count 5"));
        Assert.Contains("2-8 letters", EvalError("Get-OpsBotNames -Prefix 'Bo t' -Count 5"));
        Assert.Contains("outside 1..17576", EvalError("Get-OpsBotNames -Prefix 'Wfbot' -Count 0"));
        Assert.Contains("outside 1..17576", EvalError("Get-OpsBotNames -Prefix 'Wfbot' -Count 17577"));
    }

    [PwshFact]
    public void CommandLine_AcceptsCommandsCommentsAndWaits_RefusesTheRest()
    {
        string[] lines =
        [
            ".playerbot status",                         // 0 ok
            "# a comment",                               // 1 ok
            "#wait 5",                                   // 2 ok
            "#wait 99999",                               // 3 ok
            "#waiting",                                  // 4 a comment, ok
            "",                                          // 5 empty
            "   ",                                       // 6 blank
            "playerbot status",                          // 7 no leading dot
            "#wait",                                     // 8 bad wait
            "#wait 100000",                              // 9 bad wait
            "#wait abc",                                 // 10 bad wait
            ".say a\tb",                                 // 11 control character
            ".say a\nb",                                 // 12 control character (a second command would follow)
            ".say a\u007fb",                             // 13 DEL
            "." + new string('a', 254),                  // 14 255 characters: the limit, ok
            "." + new string('a', 255),                  // 15 256 characters: refused
        ];
        JsonElement r = EvalWith("""
            @($in.lines | ForEach-Object { [pscustomobject]@{ reason = (Test-OpsCommandLine -Line $_) } })
            """, new { lines });
        string?[] reasons = r.EnumerateArray().Select(e => e.GetProperty("reason").GetString()).ToArray();
        Assert.Equal(lines.Length, reasons.Length);
        foreach (int ok in new[] { 0, 1, 2, 3, 4, 14 })
        {
            Assert.Null(reasons[ok]);
        }
        Assert.Contains("empty", reasons[5]);
        Assert.Contains("empty", reasons[6]);
        Assert.Contains("must start with '.'", reasons[7]);
        Assert.Contains("bad wait directive", reasons[8]);
        Assert.Contains("bad wait directive", reasons[9]);
        Assert.Contains("bad wait directive", reasons[10]);
        Assert.Contains("control character", reasons[11]);
        Assert.Contains("control character", reasons[12]);
        Assert.Contains("control character", reasons[13]);
        Assert.Contains("longer than 255", reasons[15]);
    }

    [PwshFact]
    public void TeleSpec_ParsesNamesAndWeights()
    {
        JsonElement r = Eval("@(ConvertFrom-OpsTeleSpec -Spec \"Westfall=70, Elwynn Forest ,Sentinel-Hill=10,Hall's End=2\")");
        Assert.Equal(4, r.GetArrayLength());
        Assert.Equal(("Westfall", 70), (r[0].GetProperty("Name").GetString(), r[0].GetProperty("Weight").GetInt32()));
        Assert.Equal(("Elwynn Forest", 1), (r[1].GetProperty("Name").GetString(), r[1].GetProperty("Weight").GetInt32()));   // no weight = 1
        Assert.Equal(("Sentinel-Hill", 10), (r[2].GetProperty("Name").GetString(), r[2].GetProperty("Weight").GetInt32()));
        Assert.Equal(("Hall's End", 2), (r[3].GetProperty("Name").GetString(), r[3].GetProperty("Weight").GetInt32()));
    }

    [PwshFact]
    public void TeleSpec_RefusesNamesThatCouldInjectACommandOrAreMalformed()
    {
        (string Spec, string Reason)[] bad =
        [
            ("West;fall", "not a game_tele name"),
            ("Westfall\n.quit", "not a game_tele name"),
            (".quit", "not a game_tele name"),
            ("1Westfall", "not a game_tele name"),
            ("A=1,a=2", "listed twice"),
            ("A,,B", "Empty entry"),
            ("A=0", "not a whole number"),
            ("A=-3", "not a whole number"),
            ("A=abc", "not a whole number"),
            ("A=", "not a whole number"),
            ("A=1000000", "not a whole number"),
            ("A" + new string('b', 60), "not a game_tele name"),   // 61 characters
        ];
        JsonElement r = EvalWith("""
            @($in.specs | ForEach-Object {
                try { $null = ConvertFrom-OpsTeleSpec -Spec $_; [pscustomobject]@{ error = $null } }
                catch { [pscustomobject]@{ error = $_.Exception.Message } } })
            """, new { specs = bad.Select(b => b.Spec).ToArray() });
        for (int i = 0; i < bad.Length; i++)
        {
            string? error = r[i].GetProperty("error").GetString();
            Assert.True(error is not null, $"spec '{bad[i].Spec}' was accepted");
            Assert.Contains(bad[i].Reason, error);
        }

        // 60 characters is the longest accepted name.
        Assert.Equal(60, Eval($"@(ConvertFrom-OpsTeleSpec -Spec '{"A" + new string('b', 59)}')")[0].GetProperty("Name").GetString()!.Length);
    }

    [PwshFact]
    public void ProvisionScript_HasExactLinesInOrder_AndEveryLineIsSendable()
    {
        JsonElement r = Eval("""
            $p = New-OpsProvisionScript -Count 4 -Level 14 -NamePrefix 'Wfbot' -TeleSpec 'Westfall=1,Goldshire=1' -StartWaitSeconds 20 -FinalWaitSeconds 30
            [ordered]@{ lines = @($p.Lines); names = @($p.Names); teleports = @($p.Teleports) }
            """);
        string[] expected =
        [
            "# provision 4 bots at level 14",
            ".playerbot create Wfbota 1 1",
            ".playerbot create Wfbotb 1 2",
            ".playerbot create Wfbotc 1 4",
            ".playerbot create Wfbotd 1 5",
            "# create is asynchronous: let the characters be written before starting them",
            "#wait 5",
            ".playerbot start Wfbota",
            ".playerbot start Wfbotb",
            ".playerbot start Wfbotc",
            ".playerbot start Wfbotd",
            "# a boost needs the character in the world",
            "#wait 20",
            ".character boost Wfbota 14",
            ".character boost Wfbotb 14",
            ".character boost Wfbotc 14",
            ".character boost Wfbotd 14",
            ".tele name Wfbota Westfall",
            ".tele name Wfbotb Goldshire",
            ".tele name Wfbotc Westfall",
            ".tele name Wfbotd Goldshire",
            "#wait 30",
            ".playerbot status",
        ];
        Assert.Equal(expected, Strings(r.GetProperty("lines")));
        Assert.Equal(["Wfbota", "Wfbotb", "Wfbotc", "Wfbotd"], Strings(r.GetProperty("names")));
    }

    [PwshFact]
    public void ProvisionScript_At200Bots_SpreadsTeleportsExactly_AndKeepsEveryLineUnderTheChatLimit()
    {
        JsonElement r = Eval("""
            $p = New-OpsProvisionScript -Count 200 -Level 14 -NamePrefix 'Wfbot' -TeleSpec 'Westfall=70,Goldshire=20,SentinelHill=10'
            [ordered]@{ lines = @($p.Lines); longest = ($p.Lines | Measure-Object -Property Length -Maximum).Maximum }
            """);
        string[] lines = Strings(r.GetProperty("lines"));
        Assert.Equal(140, lines.Count(l => l.StartsWith(".tele name ", StringComparison.Ordinal) && l.EndsWith(" Westfall", StringComparison.Ordinal)));
        Assert.Equal(40, lines.Count(l => l.StartsWith(".tele name ", StringComparison.Ordinal) && l.EndsWith(" Goldshire", StringComparison.Ordinal)));
        Assert.Equal(20, lines.Count(l => l.StartsWith(".tele name ", StringComparison.Ordinal) && l.EndsWith(" SentinelHill", StringComparison.Ordinal)));
        Assert.Equal(200, lines.Count(l => l.StartsWith(".playerbot create ", StringComparison.Ordinal)));
        Assert.Equal(200, lines.Count(l => l.StartsWith(".character boost ", StringComparison.Ordinal)));
        Assert.True(r.GetProperty("longest").GetDouble() <= 255);
    }

    [PwshFact]
    public void ProvisionScript_RefusesInvalidTeleNameLevelNameAndClass()
    {
        Assert.Contains("not a game_tele name", EvalError("New-OpsProvisionScript -Count 2 -Level 14 -NamePrefix 'Wfbot' -TeleSpec 'West;fall'"));
        Assert.Contains("outside 1..255", EvalError("New-OpsProvisionScript -Count 2 -Level 0 -NamePrefix 'Wfbot' -TeleSpec 'Westfall'"));
        Assert.Contains("outside 1..255", EvalError("New-OpsProvisionScript -Count 2 -Level 256 -NamePrefix 'Wfbot' -TeleSpec 'Westfall'"));
        Assert.Contains("2-8 letters", EvalError("New-OpsProvisionScript -Count 2 -Level 14 -NamePrefix 'W' -TeleSpec 'Westfall'"));
        Assert.Contains("numeric ids", EvalError("New-OpsProvisionScript -Count 2 -Level 14 -NamePrefix 'Wfbot' -TeleSpec 'Westfall' -ClassSpec '1:x'"));
        Assert.Contains("outside the 1.12.1 ids", EvalError("New-OpsProvisionScript -Count 2 -Level 14 -NamePrefix 'Wfbot' -TeleSpec 'Westfall' -ClassSpec '12:1'"));
    }

    [PwshFact]
    public void ProvisionLog_ReportsOkOnlyForBotsSeenRunningAndBoosted()
    {
        string[] log =
        [
            "",   // a real session log has blank lines
            "1 Wfbota state=Starting desired=True goal=Idle target=0 quest=0 map=0 health=100 error=none",
            "1 Wfbota state=Running desired=True goal=Idle target=0 quest=0 map=0 health=100 error=none",
            "Boost Wfbota: level 1->14, 120 spells learned, 14 items equipped (0 kept, 3 moved to bags), 24 action buttons set, money 5000.",
            "2 Wfbotb state=Running desired=True goal=Idle target=0 quest=0 map=0 health=100 error=none",
            "Boost refused for Wfbotb: the target is not a character.",
            "3 Wfbotc state=Failed desired=True goal=Idle target=0 quest=0 map=0 health=0 error=login_failed",
            "Boost Wfbotc: level 1->14, 120 spells learned, 14 items equipped (0 kept, 3 moved to bags), 24 action buttons set, money 5000.",
            "4 Wfbotd state=Running desired=True goal=Idle target=0 quest=0 map=0 health=100 error=none",
            "Boost Wfbotd: level 1->13, 120 spells learned, 14 items equipped (0 kept, 3 moved to bags), 24 action buttons set, money 5000.",
            "5 Wfbotae state=Running desired=True goal=Idle target=0 quest=0 map=0 health=100 error=none",   // other bot, must not match Wfbota
        ];
        JsonElement r = EvalWith("""
            @(Test-OpsProvisionLog -LogLines $in.log -Names $in.names -Level 14)
            """, new { log, names = new[] { "Wfbota", "Wfbotb", "Wfbotc", "Wfbotd", "Wfbote" } });
        JsonElement a = r[0], b = r[1], c = r[2], d = r[3], e = r[4];

        Assert.Equal("Running", a.GetProperty("State").GetString());   // the later line wins over Starting
        Assert.True(a.GetProperty("Boosted").GetBoolean());
        Assert.True(a.GetProperty("Ok").GetBoolean());

        Assert.False(b.GetProperty("Ok").GetBoolean());
        Assert.False(b.GetProperty("Boosted").GetBoolean());
        Assert.Equal("the target is not a character", b.GetProperty("Refusal").GetString());

        Assert.Equal("login_failed", c.GetProperty("Error").GetString());   // boosted but errored: not Ok
        Assert.True(c.GetProperty("Boosted").GetBoolean());
        Assert.False(c.GetProperty("Ok").GetBoolean());

        Assert.False(d.GetProperty("Boosted").GetBoolean());   // reached 13, not the requested 14
        Assert.False(d.GetProperty("Ok").GetBoolean());

        Assert.Equal("(missing)", e.GetProperty("State").GetString());   // never seen: never a pass
        Assert.False(e.GetProperty("Ok").GetBoolean());
    }

    [PwshFact]
    public void ProvisionLog_EmptyLog_ReportsEveryBotMissingAndNotOk()
    {
        JsonElement r = EvalWith("@(Test-OpsProvisionLog -LogLines @('') -Names $in.names -Level 14)", new { names = new[] { "Wfbota", "Wfbotb" } });
        Assert.Equal(2, r.GetArrayLength());
        foreach (JsonElement bot in r.EnumerateArray())
        {
            Assert.Equal("(missing)", bot.GetProperty("State").GetString());
            Assert.False(bot.GetProperty("Ok").GetBoolean());
        }
    }

    [PwshFact]
    public void RunGmCommands_DryRun_StartsNoToolAndWritesNothing()
    {
        using var sandbox = new Sandbox();
        string profile = sandbox.Directory("profile");
        string bin = sandbox.Directory("bin");
        string account = sandbox.FakeTool("account-tool");
        string mock = sandbox.FakeTool("mock-tool");

        (int code, string output) = RunScriptFile(profile, bin, account, mock, "-Command", ".playerbot status", "-DryRun");

        Assert.Equal(0, code);
        Assert.Contains("DRY RUN: nothing is run or changed", output);
        Assert.Contains("1 commands", output);
        Assert.Contains(".playerbot status", output);
        Assert.DoesNotContain("NOT FOUND", output);   // both tool paths exist, so the dry run only checked them
        Assert.False(File.Exists(sandbox.MarkerPath), "a tool was started during -DryRun");
        Assert.False(Directory.Exists(Path.Combine(profile, "ops")), "-DryRun created the ops directory");
        Assert.Empty(Directory.GetFileSystemEntries(profile));
    }

    [PwshFact]
    public void RunGmCommands_DryRun_ReportsMissingToolsInsteadOfStartingAnything()
    {
        using var sandbox = new Sandbox();
        string profile = sandbox.Directory("profile");
        string bin = sandbox.Directory("bin");

        (int code, string output) = RunScriptFile(profile, bin, Path.Combine(bin, "nope-a"), Path.Combine(bin, "nope-m"), "-Command", ".playerbot status", "-DryRun");

        Assert.Equal(0, code);
        Assert.Equal(2, output.Split("NOT FOUND").Length - 1);
        Assert.False(Directory.Exists(Path.Combine(profile, "ops")));
    }

    [PwshFact]
    public void RunGmCommands_InvalidLine_IsRefusedWithExitTwo_BeforeAnyToolStarts()
    {
        using var sandbox = new Sandbox();
        string profile = sandbox.Directory("profile");
        string bin = sandbox.Directory("bin");
        string account = sandbox.FakeTool("account-tool");
        string mock = sandbox.FakeTool("mock-tool");

        // Real run (no -DryRun): the line check must refuse before the account tool is ever asked for the security level.
        (int code, string output) = RunScriptFile(profile, bin, account, mock, "-Command", "playerbot status");

        Assert.Equal(2, code);
        Assert.Contains("must start with '.'", output);
        Assert.False(File.Exists(sandbox.MarkerPath), "a tool was started for a refused command line");
        Assert.False(Directory.Exists(Path.Combine(profile, "ops")));
    }

    [PwshFact]
    public void RunGmCommands_OnlyCommentsAndWaits_IsRefused()
    {
        using var sandbox = new Sandbox();
        string profile = sandbox.Directory("profile");
        string bin = sandbox.Directory("bin");

        (int code, string output) = RunScriptFile(profile, bin, sandbox.FakeTool("a"), sandbox.FakeTool("m"), "-Command", "#wait 5", "-DryRun");

        Assert.Equal(2, code);
        Assert.Contains("nothing to send", output);
    }

    // ---- harness -------------------------------------------------------------------------------------------------------

    private static (int Code, string Output) RunScriptFile(string profile, string bin, string accountTool, string mockTool, params string[] extra)
    {
        List<string> args = ["-File", RunScript, "-ProfileDirectory", profile, "-BinDirectory", bin, "-AccountTool", accountTool, "-MockTool", mockTool];
        args.AddRange(extra);
        return Pwsh(args);
    }

    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToArray();

    private static JsonElement Eval(string body) => EvalWith(body, new { });

    private static string EvalError(string body)
    {
        JsonElement result = Run(body, new { });
        Assert.False(result.GetProperty("ok").GetBoolean(), $"expected '{body}' to be refused");
        return result.GetProperty("error").GetString()!;
    }

    private static JsonElement EvalWith(string body, object input)
    {
        JsonElement result = Run(body, input);
        Assert.True(result.GetProperty("ok").GetBoolean(), result.TryGetProperty("error", out JsonElement error) ? error.GetString() : "failed");
        return result.GetProperty("value");
    }

    /// <summary>Dot-sources OpsLib.ps1, runs the body with $in set from <paramref name="input"/>, and returns {ok, value|error} as JSON.</summary>
    private static JsonElement Run(string body, object input)
    {
        string dir = Path.Combine(Path.GetTempPath(), "arcane-ops-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string inputPath = Path.Combine(dir, "in.json");
            File.WriteAllText(inputPath, JsonSerializer.Serialize(input), new UTF8Encoding(false));
            string scriptPath = Path.Combine(dir, "run.ps1");
            File.WriteAllText(scriptPath, $$"""
                $ErrorActionPreference = 'Stop'
                . '{{Lib.Replace("'", "''")}}'
                $in = [System.IO.File]::ReadAllText('{{inputPath.Replace("'", "''")}}') | ConvertFrom-Json
                try {
                    $value = & { {{body}} }
                    [ordered]@{ ok = $true; value = $value } | ConvertTo-Json -Depth 8 -Compress
                } catch {
                    [ordered]@{ ok = $false; error = $_.Exception.Message } | ConvertTo-Json -Depth 8 -Compress
                }
                """, new UTF8Encoding(false));
            (int code, string output) = Pwsh(["-File", scriptPath]);
            Assert.True(code == 0, $"pwsh exited {code}: {output}");
            return JsonDocument.Parse(output.Trim()).RootElement.Clone();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

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

    /// <summary>A temp directory with fake tools that drop a marker file if they are ever executed.</summary>
    private sealed class Sandbox : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "arcane-ops-sandbox-" + Guid.NewGuid().ToString("N"));

        public Sandbox() => System.IO.Directory.CreateDirectory(_root);

        public string MarkerPath => Path.Combine(_root, "tool-was-run");

        public string Directory(string name)
        {
            string path = Path.Combine(_root, name);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        public string FakeTool(string name)
        {
            string toolDir = Directory("tools");
            if (OperatingSystem.IsWindows())
            {
                string cmd = Path.Combine(toolDir, name + ".cmd");
                File.WriteAllText(cmd, $"@echo off\r\necho ran> \"{MarkerPath}\"\r\nexit /b 0\r\n");
                return cmd;
            }

            string sh = Path.Combine(toolDir, name);
            File.WriteAllText(sh, $"#!/bin/sh\necho ran > '{MarkerPath}'\nexit 0\n");
            File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return sh;
        }

        public void Dispose() => System.IO.Directory.Delete(_root, recursive: true);
    }

    /// <summary>Runs only where PowerShell 7 (<c>pwsh</c>) is on PATH; CI (ubuntu-latest) has it.</summary>
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
