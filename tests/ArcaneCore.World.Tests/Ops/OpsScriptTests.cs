using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ArcaneCore.TestSupport;
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
    public void OperatorPassword_FitsTheLogonLimit()
    {
        // The 1.12.1 logon takes 1-16 printable ASCII characters (MockClient ProtocolPackets.NormalizePassword); a longer random
        // password made arcane-mock throw before the operator reached the world (live-w19 provisioning, 2026-10-10).
        JsonElement r = Eval("""
            $p = New-OpsPassword
            $all = @(1..50 | ForEach-Object { New-OpsPassword })
            [ordered]@{ length = $p.Length; maxLength = [int]($all | Measure-Object -Property Length -Maximum).Maximum;
                        distinct = @($all | Sort-Object -Unique).Count; ascii = [bool]($all -join '' -match '^[\x21-\x7E]+$') }
            """);
        Assert.Equal(16, r.GetProperty("length").GetInt32());
        Assert.Equal(16, r.GetProperty("maxLength").GetInt32());
        Assert.Equal(50, r.GetProperty("distinct").GetInt32());
        Assert.True(r.GetProperty("ascii").GetBoolean());
        Assert.Contains("16", EvalError("New-OpsPassword -Length 17"));
        Assert.Contains("12", EvalError("New-OpsPassword -Length 11"));
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
            @"#waitfor 10 \bPlayerbot status end: \d+",   // the reply is many chat lines: the runner polls the log for its terminator, not the last bot's line
        ];
        Assert.Equal(expected, Strings(r.GetProperty("lines")));
        Assert.Equal(["Wfbota", "Wfbotb", "Wfbotc", "Wfbotd"], Strings(r.GetProperty("names")));
    }

    [PwshFact]
    public void ProvisionScript_WithHome_AnchorsEveryBotToItsPlace_AfterTheTeleports_AndBeforeTheStatus()
    {
        JsonElement r = Eval("""
            $p = New-OpsProvisionScript -Count 3 -Level 14 -NamePrefix 'Wfbot' -TeleSpec 'Westfall=2,Elwynn Forest=1' -AnchorHome -HomeRadius 450 -StatusWaitSeconds 0
            @($p.Lines)
            """);
        string[] lines = Strings(r);

        string[] tail = [.. lines.SkipWhile(l => !l.StartsWith(".tele name ", StringComparison.Ordinal))];
        Assert.Equal(
        [
            ".tele name Wfbota Westfall",
            ".tele name Wfbotb Elwynn Forest",
            ".tele name Wfbotc Westfall",
            "# anchor every bot to its place: the brain walks back when it strays past the radius",
            ".playerbot home Wfbota Westfall 450",
            ".playerbot home Wfbotb Elwynn Forest 450",
            ".playerbot home Wfbotc Westfall 450",
            "#wait 20",
            ".playerbot status",   // -StatusWaitSeconds 0: no trailing wait
        ], tail);

        // Without the switch there is no home line (the default provision is unchanged).
        JsonElement plain = Eval("@((New-OpsProvisionScript -Count 3 -Level 14 -NamePrefix 'Wfbot' -TeleSpec 'Westfall').Lines)");
        Assert.DoesNotContain(Strings(plain), l => l.StartsWith(".playerbot home", StringComparison.Ordinal));
    }

    [PwshFact]
    public void ProvisionScript_RefusesAHomeRadiusOutsideTheServersBounds_AndANegativeStatusWait()
    {
        Assert.Contains("outside 50..5000", EvalError("New-OpsProvisionScript -Count 2 -Level 14 -NamePrefix 'Wfbot' -TeleSpec 'Westfall' -AnchorHome -HomeRadius 49"));
        Assert.Contains("outside 50..5000", EvalError("New-OpsProvisionScript -Count 2 -Level 14 -NamePrefix 'Wfbot' -TeleSpec 'Westfall' -AnchorHome -HomeRadius 5001"));
        Assert.Contains("outside 0..600", EvalError("New-OpsProvisionScript -Count 2 -Level 14 -NamePrefix 'Wfbot' -TeleSpec 'Westfall' -StatusWaitSeconds -1"));
        // The bounds are the edges: 50 and 5000 are accepted.
        Assert.Contains(".playerbot home Wfbota Westfall 50", Strings(Eval("@((New-OpsProvisionScript -Count 1 -Level 14 -NamePrefix 'Wfbot' -TeleSpec 'Westfall' -AnchorHome -HomeRadius 50).Lines)")));
        Assert.Contains(".playerbot home Wfbota Westfall 5000", Strings(Eval("@((New-OpsProvisionScript -Count 1 -Level 14 -NamePrefix 'Wfbot' -TeleSpec 'Westfall' -AnchorHome -HomeRadius 5000).Lines)")));
    }

    [PwshFact]
    public void BoostRetry_SelectsOnlyBotsNotBoosted_WhoseRefusalIsTransientOrAbsent()
    {
        string[] log =
        [
            "Boost Wfbota: level 1->14, 120 spells learned, 14 items equipped (0 kept, 3 moved to bags), 24 action buttons set, 0 gray quests dropped, money 5000.",
            "Boost refused for Wfbotb: the character is in combat.",
            "Boost refused for Wfbotc: the character is dead.",
            "Boost refused for Wfbotd: the character is level 20, above 14.",          // permanent: never retried
            "Boost refused for Wfbote: the character is settling a quest reward.",
            "Boost refused for Wfbotf: the character is not in the world.",
            "Boost refused for Wfbotg: the character is in combat.",                   // refused first, boosted later
            "Boost Wfbotg: level 1->14, 120 spells learned, 14 items equipped (0 kept, 3 moved to bags), 24 action buttons set, 0 gray quests dropped, money 5000.",
            "Boost refused for Wfbotj: progression is not available.",                 // permanent
        ];
        // Wfboth has no reply at all (a dropped line): it is tried again, and the final verification still decides.
        JsonElement r = EvalWith("@(Get-OpsBoostRetryNames -LogLines $in.log -Names $in.names -Level 14)",
            new { log, names = new[] { "Wfbota", "Wfbotb", "Wfbotc", "Wfbotd", "Wfbote", "Wfbotf", "Wfbotg", "Wfboth", "Wfbotj" } });

        Assert.Equal(["Wfbotb", "Wfbotc", "Wfbote", "Wfbotf", "Wfboth"], Strings(r));
    }

    [PwshFact]
    public void BoostRetry_Script_WaitsFirst_BoostsEachNamedBot_AndEndsWithAStatusAndItsWait()
    {
        JsonElement r = Eval("@(New-OpsBoostRetryScript -Names @('Wfbotb','Wfbotc') -Level 14 -WaitSeconds 45 -StatusWaitSeconds 12)");

        Assert.Equal(
        [
            "# boost retry for 2 bots",
            "#wait 45",
            ".character boost Wfbotb 14",
            ".character boost Wfbotc 14",
            ".playerbot status",
            @"#waitfor 12 \bPlayerbot status end: \d+",
        ], Strings(r));
        Assert.Contains("at least one bot", EvalError("New-OpsBoostRetryScript -Names @() -Level 14"));
        Assert.Contains("outside 1..255", EvalError("New-OpsBoostRetryScript -Names @('Wfbotb') -Level 0"));
    }

    [PwshFact]
    public void BoostRetry_Script_RevivesTheBotsRefusedAsDead_BeforeTheWaitAndTheBoost()
    {
        JsonElement r = Eval("@(New-OpsBoostRetryScript -Names @('Wfbotb','Wfbotc','Wfbotd') -Level 14 -WaitSeconds 45 -StatusWaitSeconds 0 -ReviveNames @('Wfbotc','Wfbotd'))");
        string[] lines = Strings(r);

        Assert.Equal(
        [
            "# boost retry for 3 bots",
            "# 2 bots were refused as dead: revive them first",
            ".revive Wfbotc",
            ".revive Wfbotd",
            "#wait 45",
            ".character boost Wfbotb 14",
            ".character boost Wfbotc 14",
            ".character boost Wfbotd 14",
            ".playerbot status",
        ], lines);
        Assert.True(Array.IndexOf(lines, ".revive Wfbotd") < Array.IndexOf(lines, "#wait 45"));
        Assert.Contains("not one of the bots", EvalError("New-OpsBoostRetryScript -Names @('Wfbotb') -Level 14 -ReviveNames @('Wfbotz')"));

        // The line is sendable as the runner checks it.
        Assert.Equal(JsonValueKind.Null, Eval("Test-OpsCommandLine -Line '.revive Wfbotc'").ValueKind);
    }

    [PwshFact]
    public void BoostDeadNames_AreTheBotsWhoseLastRefusalIsDead_AndNotBoostedLater()
    {
        string[] log =
        [
            "Boost refused for Wfbotb: the character is dead.",
            "Boost refused for Wfbotc: the character is in combat.",
            "Boost refused for Wfbotd: the character is dead.",
            "Boost Wfbotd: level 1->14, 120 spells learned, 14 items equipped (0 kept, 3 moved to bags), 24 action buttons set, 0 gray quests dropped, money 5000.",
        ];
        JsonElement r = EvalWith("@(Get-OpsBoostDeadNames -LogLines $in.log -Names $in.names -Level 14)",
            new { log, names = new[] { "Wfbotb", "Wfbotc", "Wfbotd", "Wfbote" } });

        Assert.Equal("Wfbotb", r.GetString());   // one name: PowerShell unrolls a single-element array
    }

    [PwshFact]
    public void WaitFor_IsAcceptedWithATimeoutAndAValidPattern_RefusedOtherwise()
    {
        string[] lines =
        [
            @"#waitfor 10 \bWfbotd state=",   // 0 ok
            "#waitfor",                       // 1 bad
            "#waitfor 0 x",                   // 2 bad: no zero timeout
            "#waitfor abc x",                 // 3 bad
            "#waitfor 10",                    // 4 bad: no pattern
            "#waitfor 10 (",                  // 5 bad: the pattern does not compile
            "#waitfor 100000 x",              // 6 bad
        ];
        JsonElement r = EvalWith("@($in.lines | ForEach-Object { [pscustomobject]@{ reason = (Test-OpsCommandLine -Line $_) } })", new { lines });
        string?[] reasons = r.EnumerateArray().Select(e => e.GetProperty("reason").GetString()).ToArray();

        Assert.Null(reasons[0]);
        for (int i = 1; i < lines.Length; i++)
        {
            Assert.Contains("waitfor", reasons[i]);
        }
    }

    [PwshFact]
    public void WaitForLog_MatchesOnlyTextAfterTheOffset_AndFailsClosedAtTheTimeout()
    {
        string dir = Path.Combine(Path.GetTempPath(), "arcane-ops-wait-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string log = Path.Combine(dir, "session.log");
            string old = "1 Wfbotd state=Running desired=True goal=none error=none\n";
            File.WriteAllText(log, old);
            object input = new { log, offset = old.Length };

            // The reply from an earlier command is before the offset: it must not satisfy the wait, and the timeout is a $false, not a pass.
            JsonElement stale = EvalWith("Wait-OpsLogMatch -Path $in.log -Pattern '\\bWfbotd state=' -TimeoutSeconds 1 -StartOffset $in.offset -PollMilliseconds 50", input);
            Assert.False(stale.GetBoolean());

            // Text appended afterwards (the writer keeps the file open in the real run) satisfies it.
            File.AppendAllText(log, "2 Wfbotd state=Running desired=True goal=none error=none\n");
            JsonElement fresh = EvalWith("Wait-OpsLogMatch -Path $in.log -Pattern '\\bWfbotd state=' -TimeoutSeconds 5 -StartOffset $in.offset -PollMilliseconds 50", input);
            Assert.True(fresh.GetBoolean());

            // A dead session stops the wait early.
            JsonElement dead = EvalWith("Wait-OpsLogMatch -Path $in.log -Pattern 'never' -TimeoutSeconds 60 -StartOffset 0 -IsAlive { $false } -PollMilliseconds 50", input);
            Assert.False(dead.GetBoolean());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [PwshFact]
    public void WaitForLog_SkippingTheMockEcho_IgnoresTheSentCommand_ButCountsEveryReplyLine()
    {
        // arcane-mock live logs each line it sends as '<time> [<endpoint>] > <command>', so a pattern that is also in the command
        // ('.lookup item Linen', then a wait for 'Linen') matched that echo before the server had answered. The logs are CRLF, as
        // PowerShell writes them on Windows; the echo-only log ends mid-line, as it does while the mock is writing.
        string dir = Path.Combine(Path.GetTempPath(), "arcane-ops-wait-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string echoOnly = Path.Combine(dir, "echo.log");
            File.WriteAllText(echoOnly, "12:00:00.000 [127.0.0.1:50000] > .lookup item Linen\r\n12:00:00.400 [127.0.0.1:50000] < No item found.\r\n"
                + "12:00:01.000 [127.0.0.1:50000] > .lookup item Linen");
            string ipv6Echo = Path.Combine(dir, "echo6.log");
            File.WriteAllText(ipv6Echo, "12:00:00.000 [[::1]:50000] > .lookup item Linen\r\n");
            string reply = Path.Combine(dir, "reply.log");
            File.WriteAllText(reply, "12:00:00.000 [127.0.0.1:50000] > .lookup item Linen\r\n12:00:00.400 [127.0.0.1:50000] < 2996 - [Bolt of Linen Cloth]\r\n");
            string continuation = Path.Combine(dir, "continuation.log");
            File.WriteAllText(continuation, "12:00:00.000 [127.0.0.1:50000] > .lookup item Linen\r\n12:00:00.400 [127.0.0.1:50000] < Items:\r\nItem [Linen] > cloth\r\n");
            object input = new { echoOnly, ipv6Echo, reply, continuation };
            string known = "-SkipLinePattern (Get-OpsMockEchoPattern -Endpoint '127.0.0.1:50000')";
            string any = "-SkipLinePattern (Get-OpsMockEchoPattern)";

            Assert.True(EvalWith(WaitForLinen("echoOnly", ""), input).GetBoolean());           // no skip: the echo matches, as every wait did before
            Assert.False(EvalWith(WaitForLinen("echoOnly", known), input).GetBoolean());
            Assert.False(EvalWith(WaitForLinen("echoOnly", any), input).GetBoolean());         // no 'IN WORLD' endpoint known: any loopback endpoint
            Assert.False(EvalWith(WaitForLinen("ipv6Echo", any), input).GetBoolean());
            Assert.True(EvalWith(WaitForLinen("reply", known), input).GetBoolean());           // the server's reply counts
            Assert.True(EvalWith(WaitForLinen("continuation", known), input).GetBoolean());    // and so does a multi-line reply's raw continuation line
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        static string WaitForLinen(string log, string skip)
            => $"Wait-OpsLogMatch -Path $in.{log} -Pattern 'Linen' -TimeoutSeconds 1 -PollMilliseconds 50 {skip}";
    }

    [PwshFact]
    public void HomeSetCount_SumsTheServersConfirmations_AndIsZeroForAnUnconfirmedRun()
    {
        string[] log =
        [
            "Playerbot home accepted.",   // accepted is not set: only the second kind of reply counts
            "Playerbot home: 1 set (Westfall map 0 at -9461.0,63.0,56.0 radius 600), 0 skipped (not running).",
            "Playerbot home: 1 set (Westfall map 0 at -9461.0,63.0,56.0 radius 600), 0 skipped (not running), 1 failed.",
            "Playerbot home: 0 cleared.",
            "Playerbot home failed: TimeoutException.",
        ];
        Assert.Equal(2, EvalWith("Get-OpsHomeSetCount -LogLines $in.log", new { log }).GetInt32());
        Assert.Equal(0, EvalWith("Get-OpsHomeSetCount -LogLines @('')", new { }).GetInt32());
    }

    [PwshFact]
    public void ProvisionBots_DryRun_AcceptsTheHomeSwitch_AndAddsOneHomeLinePerBot()
    {
        using var sandbox = new Sandbox();
        string profile = sandbox.Directory("profile");
        string bin = sandbox.Directory("bin");
        string account = sandbox.FakeTool("account-tool");
        string mock = sandbox.FakeTool("mock-tool");
        string provision = Path.Combine(RepositorySource.RequireRoot(), "tools", "ops", "provision-bots.ps1");
        string[] common = ["-File", provision, "-ProfileDirectory", profile, "-BinDirectory", bin, "-AccountTool", account, "-MockTool", mock, "-Count", "2", "-DryRun"];

        (int plainCode, string plain) = Pwsh(common);
        (int homeCode, string home) = Pwsh([.. common, "-Home", "-HomeRadius", "300"]);

        Assert.Equal(0, plainCode);
        Assert.Equal(0, homeCode);
        // create 2 + start 2 + boost 2 + tele 2 + status 1 = 9 commands; the switch adds one home line per bot.
        Assert.Contains("9 commands", plain);
        Assert.DoesNotContain("home radius", plain);
        Assert.Contains("11 commands", home);
        Assert.Contains("home radius 300", home);
        Assert.False(File.Exists(sandbox.MarkerPath), "a tool was started during -DryRun");
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
            "Boost Wfbota: level 1->14, 120 spells learned, 14 items equipped (0 kept, 3 moved to bags), 24 action buttons set, 0 gray quests dropped, money 5000.",
            "2 Wfbotb state=Running desired=True goal=Idle target=0 quest=0 map=0 health=100 error=none",
            "Boost refused for Wfbotb: the target is not a character.",
            "3 Wfbotc state=Failed desired=True goal=Idle target=0 quest=0 map=0 health=0 error=login_failed",
            "Boost Wfbotc: level 1->14, 120 spells learned, 14 items equipped (0 kept, 3 moved to bags), 24 action buttons set, 0 gray quests dropped, money 5000.",
            "4 Wfbotd state=Running desired=True goal=Idle target=0 quest=0 map=0 health=100 error=none",
            "Boost Wfbotd: level 1->13, 120 spells learned, 14 items equipped (0 kept, 3 moved to bags), 24 action buttons set, 0 gray quests dropped, money 5000.",
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
    public void ProvisionLog_ALogEndingInTheStatusTerminatorWithoutTheLastBot_ReportsThatBotMissing()
    {
        string[] log =
        [
            "1 Wfbota state=Running desired=True goal=Idle target=0 quest=0 map=0 health=100 error=none",
            "Boost Wfbota: level 1->14, 120 spells learned, 14 items equipped (0 kept, 3 moved to bags), 24 action buttons set, 0 gray quests dropped, money 5000.",
            "Playerbot status end: 1",   // the reply ended; Wfbotb (the last planned bot) was never created
        ];
        JsonElement r = EvalWith("@(Test-OpsProvisionLog -LogLines $in.log -Names $in.names -Level 14)", new { log, names = new[] { "Wfbota", "Wfbotb" } });
        Assert.True(r[0].GetProperty("Ok").GetBoolean());
        Assert.Equal("(missing)", r[1].GetProperty("State").GetString());
        Assert.False(r[1].GetProperty("Ok").GetBoolean());   // per-bot failure (exit 5), not "nothing verified" (exit 1)
        Assert.DoesNotContain("Wfbotb", Eval("Get-OpsStatusWaitLine -Seconds 10").GetString());
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

    [PwshFact]
    public void RunGmCommands_WaitFor_CountsAReplyLoggedDuringThePaceSleep()
    {
        // The fake session answers a sent line within ~20 ms, far inside the 450 ms pace sleep, so the reply is already in the log
        // when the runner reaches '#waitfor'. The wait must scan from before the command was sent, not from the moment it is reached.
        using var sandbox = new Sandbox();
        string profile = sandbox.Directory("profile");
        string bin = sandbox.Directory("bin");
        string toolDir = sandbox.Directory("scripted-tools");
        string account = Path.Combine(toolDir, "account.ps1");
        string mock = Path.Combine(toolDir, "mock.ps1");
        File.WriteAllText(account, """
            if ($args[0] -eq 'list') { Write-Output '1 OPSGM x Player' }
            exit 0
            """);
        File.WriteAllText(mock, """
            function Get-Arg([string]$Name) { $i = [array]::IndexOf($args_, $Name); $args_[$i + 1] }
            $args_ = $args
            $scriptFile = Get-Arg '--script-file'
            $stopFile = Get-Arg '--stop-file'
            $deadline = (Get-Date).AddSeconds([int](Get-Arg '--duration-s'))
            Write-Output 'IN WORLD as Opsgm'
            $seen = 0
            while (-not (Test-Path -LiteralPath $stopFile) -and (Get-Date) -lt $deadline) {
                $sent = @(Get-Content -LiteralPath $scriptFile)
                while ($seen -lt $sent.Count) { Write-Output ('{0} [127.0.0.1:50000] > {1}' -f (Get-Date -Format 'HH:mm:ss.fff'), $sent[$seen]); Write-Output 'Wfbotd state=Running desired=True goal=none error=none'; $seen++ }
                Start-Sleep -Milliseconds 20
            }
            exit 0
            """);

        string commands = Path.Combine(toolDir, "commands.txt");
        File.WriteAllText(commands, ".playerbot status\n#waitfor 5 \\bWfbotd state=\n");

        (int code, string output) = RunScriptFile(profile, bin, account, mock, "-CommandFile", commands, "-PaceMilliseconds", "450");

        Assert.True(code == 0, $"exit {code}: {output}");
        Assert.Contains("sent 1 of 1 commands", output);
        Assert.DoesNotContain("no log line matched", output);
    }

    // A fake arcane-mock live that replays the script file slowly and, like the real one, logs '<time> [<endpoint>] > <command>' per
    // replayed line. MillisecondsPerLine is its replay speed; DeliverAtMost (0 = all) is how many lines it ever sends, after which it
    // ignores the file and exits 1.5 s after its last send.
    private static string WriteCommands(string directory, params string[] lines)
    {
        string path = Path.Combine(directory, "commands.txt");
        File.WriteAllLines(path, lines);
        return path;
    }

    private static string WriteSlowMock(string directory, int millisecondsPerLine, int deliverAtMost, string endpoint = "127.0.0.1:50000", bool continuation = false)
    {
        string mock = Path.Combine(directory, "slow-mock.ps1");
        File.WriteAllText(mock, $$"""
            $args_ = $args
            function Get-Arg([string]$Name) { $i = [array]::IndexOf($args_, $Name); $args_[$i + 1] }
            $scriptFile = Get-Arg '--script-file'
            $stopFile = Get-Arg '--stop-file'
            $deadline = (Get-Date).AddSeconds([int](Get-Arg '--duration-s'))
            Write-Output 'IN WORLD as Opsgm; connection {{endpoint}} -> 127.0.0.1:8085'
            $seen = 0
            $firstAt = $null
            while (-not (Test-Path -LiteralPath $stopFile) -and (Get-Date) -lt $deadline) {
                $lines = @(Get-Content -LiteralPath $scriptFile)
                if ($seen -lt $lines.Count -and ({{deliverAtMost}} -eq 0 -or $seen -lt {{deliverAtMost}})) {
                    # Falling behind: busy for a while before this line goes out; a stop file seen first ends the session unsent.
                    $busyUntil = (Get-Date).AddMilliseconds({{millisecondsPerLine}})
                    while ((Get-Date) -lt $busyUntil) { Start-Sleep -Milliseconds 10 }
                    if (Test-Path -LiteralPath $stopFile) { exit 0 }
                    Write-Output ('{0} [{{endpoint}}] > {1}' -f (Get-Date -Format 'HH:mm:ss.fff'), $lines[$seen])
                    Write-Output ('{0} [{{endpoint}}] < reply' -f (Get-Date -Format 'HH:mm:ss.fff'))
                    if ({{(continuation ? "1" : "0")}} -eq 1) { Write-Output 'Item [Linen] > cloth' }   # a multi-line reply's raw continuation line
                    $seen++
                    if ($null -eq $firstAt) { $firstAt = Get-Date }
                }
                elseif ({{deliverAtMost}} -gt 0 -and $seen -ge {{deliverAtMost}} -and ((Get-Date) - $firstAt).TotalMilliseconds -gt 1500) { exit 0 }
                else { Start-Sleep -Milliseconds 20 }
            }
            exit 0
            """);
        return mock;
    }

    [PwshFact]
    public void RunGmCommands_WaitsForAMockThatFellBehind_AndReportsTheCommandsActuallySent()
    {
        // The mock needs ~1.2 s per line while the runner appends one every 450 ms, so after the last append it is still two lines
        // behind. The runner used to sleep a fixed 1.5 s and then create the stop file: the last command was appended, never sent,
        // and the run still said "sent 3 of 3" and exited 0.
        using var sandbox = new Sandbox();
        string profile = sandbox.Directory("profile");
        string bin = sandbox.Directory("bin");
        string toolDir = sandbox.Directory("scripted-tools");
        string account = Path.Combine(toolDir, "account.ps1");
        File.WriteAllText(account, "if ($args[0] -eq 'list') { Write-Output '1 OPSGM x Player' }\nexit 0\n");
        string mock = WriteSlowMock(toolDir, millisecondsPerLine: 1200, deliverAtMost: 0);
        string log = Path.Combine(toolDir, "session.log");

        (int code, string output) = RunScriptFile(profile, bin, account, mock, "-CommandFile", WriteCommands(toolDir, ".first", ".second", ".third"),
            "-PaceMilliseconds", "450", "-LogPath", log);

        Assert.True(code == 0, $"exit {code}: {output}");
        Assert.Contains("sent 3 of 3 commands", output);
        Assert.Contains("> .third", File.ReadAllText(log));   // RED on the fixed-sleep script: the stop file arrived first
    }

    [PwshFact]
    public void RunGmCommands_FailsWithExitOne_NamingTheFirstUndeliveredCommand_WhenTheMockNeverSendsIt()
    {
        using var sandbox = new Sandbox();
        string profile = sandbox.Directory("profile");
        string bin = sandbox.Directory("bin");
        string toolDir = sandbox.Directory("scripted-tools");
        string account = Path.Combine(toolDir, "account.ps1");
        File.WriteAllText(account, "if ($args[0] -eq 'list') { Write-Output '1 OPSGM x Player' }\nexit 0\n");
        string mock = WriteSlowMock(toolDir, millisecondsPerLine: 20, deliverAtMost: 1);
        string log = Path.Combine(toolDir, "session.log");

        (int code, string output) = RunScriptFile(profile, bin, account, mock, "-CommandFile", WriteCommands(toolDir, ".first", ".second"),
            "-PaceMilliseconds", "450", "-LogPath", log);

        Assert.Equal(1, code);
        Assert.Contains("first undelivered: '.second'", output);
        Assert.Contains("sent 1 of 2 commands", output);
    }

    [PwshFact]
    public void RunGmCommands_DoesNotCountAMultiLineReplysContinuationLineAsADeliveredCommand()
    {
        // The mock writes the 2nd and later lines of a multi-line reply raw, without a timestamp: 'Item [Linen] > cloth' looks like a
        // sent-line to a loose pattern. The mock here sends only the first command, so the run must fail, not count the reply line.
        using var sandbox = new Sandbox();
        string profile = sandbox.Directory("profile");
        string bin = sandbox.Directory("bin");
        string toolDir = sandbox.Directory("scripted-tools");
        string account = Path.Combine(toolDir, "account.ps1");
        File.WriteAllText(account, "if ($args[0] -eq 'list') { Write-Output '1 OPSGM x Player' }\nexit 0\n");
        string mock = WriteSlowMock(toolDir, millisecondsPerLine: 20, deliverAtMost: 1, continuation: true);

        (int code, string output) = RunScriptFile(profile, bin, account, mock, "-CommandFile", WriteCommands(toolDir, ".first", ".second"),
            "-PaceMilliseconds", "450", "-LogPath", Path.Combine(toolDir, "session.log"));

        Assert.Equal(1, code);
        Assert.Contains("first undelivered: '.second'", output);
        Assert.Contains("sent 1 of 2 commands", output);
    }

    [PwshFact]
    public void RunGmCommands_CountsCommandsEchoedOnAnIpv6LoopbackEndpoint()
    {
        using var sandbox = new Sandbox();
        string profile = sandbox.Directory("profile");
        string bin = sandbox.Directory("bin");
        string toolDir = sandbox.Directory("scripted-tools");
        string account = Path.Combine(toolDir, "account.ps1");
        File.WriteAllText(account, "if ($args[0] -eq 'list') { Write-Output '1 OPSGM x Player' }\nexit 0\n");
        string mock = WriteSlowMock(toolDir, millisecondsPerLine: 20, deliverAtMost: 0, endpoint: "[::1]:50000");

        (int code, string output) = RunScriptFile(profile, bin, account, mock, "-CommandFile", WriteCommands(toolDir, ".first", ".second"),
            "-PaceMilliseconds", "450", "-LogPath", Path.Combine(toolDir, "session.log"));

        Assert.True(code == 0, $"exit {code}: {output}");
        Assert.Contains("sent 2 of 2 commands", output);
    }

    [PwshFact]
    public void RunGmCommands_StopFileDuringTheDeliveryWait_IsExitThree_NotAFailure()
    {
        using var sandbox = new Sandbox();
        string profile = sandbox.Directory("profile");
        string bin = sandbox.Directory("bin");
        string toolDir = sandbox.Directory("scripted-tools");
        string account = Path.Combine(toolDir, "account.ps1");
        File.WriteAllText(account, "if ($args[0] -eq 'list') { Write-Output '1 OPSGM x Player' }\nexit 0\n");
        string mock = WriteSlowMock(toolDir, millisecondsPerLine: 6000, deliverAtMost: 0);
        string log = Path.Combine(toolDir, "session.log");
        string stop = Path.Combine(toolDir, "user-stop");

        // The operator's stop file appears once the session is in the world and the runner is waiting for the slow mock.
        Task stopper = Task.Run(() =>
        {
            DateTime give = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < give && !(File.Exists(log) && ReadShared(log).Contains("IN WORLD")))
            {
                Thread.Sleep(50);
            }

            Thread.Sleep(2500);
            File.WriteAllText(stop, "");
        });
        (int code, string output) = RunScriptFile(profile, bin, account, mock, "-CommandFile", WriteCommands(toolDir, ".first", ".second"),
            "-PaceMilliseconds", "450", "-LogPath", log, "-StopFile", stop);
        stopper.Wait();

        Assert.True(code == 3, $"exit {code}: {output}");
        Assert.Contains("stop file seen", output);
        Assert.DoesNotContain("first undelivered", output);
    }

    // A fake arcane-mock live that answers each replayed line with one reply line, ReplyDelayMilliseconds after its echo (the server
    // taking its time). Just before the reply it logs 'appended before the reply: N' when the runner appended N more lines meanwhile.
    private static string WriteReplyingMock(string directory, string reply, int replyDelayMilliseconds)
    {
        string mock = Path.Combine(directory, "replying-mock.ps1");
        File.WriteAllText(mock, $$"""
            $args_ = $args
            function Get-Arg([string]$Name) { $i = [array]::IndexOf($args_, $Name); $args_[$i + 1] }
            $scriptFile = Get-Arg '--script-file'
            $stopFile = Get-Arg '--stop-file'
            $deadline = (Get-Date).AddSeconds([int](Get-Arg '--duration-s'))
            Write-Output 'IN WORLD as Opsgm; connection 127.0.0.1:50000 -> 127.0.0.1:8085'
            $seen = 0
            while (-not (Test-Path -LiteralPath $stopFile) -and (Get-Date) -lt $deadline) {
                $lines = @(Get-Content -LiteralPath $scriptFile)
                if ($seen -lt $lines.Count) {
                    Write-Output ('{0} [127.0.0.1:50000] > {1}' -f (Get-Date -Format 'HH:mm:ss.fff'), $lines[$seen])
                    Start-Sleep -Milliseconds {{replyDelayMilliseconds}}
                    $early = @(Get-Content -LiteralPath $scriptFile).Count - $seen - 1
                    if ($early -gt 0) { Write-Output "appended before the reply: $early" }
                    Write-Output ('{0} [127.0.0.1:50000] < {1}' -f (Get-Date -Format 'HH:mm:ss.fff'), '{{reply}}')
                    $seen++
                }
                else { Start-Sleep -Milliseconds 20 }
            }
            exit 0
            """);
        return mock;
    }

    [PwshFact]
    public void RunGmCommands_WaitFor_IsNotSatisfiedByTheMocksEchoOfTheCommand()
    {
        // The pattern is in the command ('.lookup item Linen') but not in the reply. The mock's echo of the command used to satisfy the
        // wait, so the run passed (exit 0) without the server ever printing 'Linen'.
        using var sandbox = new Sandbox();
        string profile = sandbox.Directory("profile");
        string bin = sandbox.Directory("bin");
        string toolDir = sandbox.Directory("scripted-tools");
        string account = Path.Combine(toolDir, "account.ps1");
        File.WriteAllText(account, "if ($args[0] -eq 'list') { Write-Output '1 OPSGM x Player' }\nexit 0\n");
        string mock = WriteReplyingMock(toolDir, "No item found.", replyDelayMilliseconds: 100);
        string log = Path.Combine(toolDir, "session.log");

        (int code, string output) = RunScriptFile(profile, bin, account, mock, "-CommandFile", WriteCommands(toolDir, ".lookup item Linen", "#waitfor 3 Linen"),
            "-PaceMilliseconds", "450", "-LogPath", log);

        Assert.Contains("[127.0.0.1:50000] > .lookup item Linen", File.ReadAllText(log));   // the echo was there to be matched
        Assert.Contains("< No item found.", File.ReadAllText(log));
        Assert.True(code == 1, $"exit {code}: {output}");
        Assert.Contains("no log line matched 'Linen' within 3 s", output);
    }

    [PwshFact]
    public void RunGmCommands_WaitFor_HoldsTheNextCommandUntilTheReplyArrives()
    {
        // The reply comes 2 s after the echo. A wait satisfied by the echo appended '.second' right after the pace sleep, while the
        // server was still answering the first: the next command went out before the reply it was meant to wait for.
        using var sandbox = new Sandbox();
        string profile = sandbox.Directory("profile");
        string bin = sandbox.Directory("bin");
        string toolDir = sandbox.Directory("scripted-tools");
        string account = Path.Combine(toolDir, "account.ps1");
        File.WriteAllText(account, "if ($args[0] -eq 'list') { Write-Output '1 OPSGM x Player' }\nexit 0\n");
        string mock = WriteReplyingMock(toolDir, "2996 - [Bolt of Linen Cloth]", replyDelayMilliseconds: 2000);
        string log = Path.Combine(toolDir, "session.log");

        (int code, string output) = RunScriptFile(profile, bin, account, mock, "-CommandFile", WriteCommands(toolDir, ".lookup item Linen", "#waitfor 10 Linen", ".second"),
            "-PaceMilliseconds", "450", "-LogPath", log);

        string text = File.ReadAllText(log);
        Assert.True(code == 0, $"exit {code}: {output}");
        Assert.Contains("sent 2 of 2 commands", output);
        Assert.Contains("< 2996 - [Bolt of Linen Cloth]", text);
        Assert.DoesNotContain("appended before the reply", text);
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
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
