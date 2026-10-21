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
    public void JitOutput_NeedsAListing_NotJustANonEmptyFile()
    {
        // With DOTNET_JitDisasmSummary=1 the file fills with "JIT compiled" lines whether or not the requested method matched or was
        // inlined; only "; Assembly listing for method" is a disassembly.
        JsonElement r = Eval("""
            $listing = "; Assembly listing for method Ns.Type:PackXYZ(float):int (FullOpts)`n; Emitting BLENDED_CODE for X64`nG_M1_IG01:`n        ret"
            $summary = "JIT compiled Ns.Other:Run():void [Tier0, IL size=5, code size=12]`nJIT compiled Ns.Other:Stop():void [Tier0, IL size=3, code size=7]`n"
            function Row($text) { $c = Test-QualityJitOutput -Text $text -Method 'Ns.Type:PackXYZ'; [ordered]@{ ok = $c.Ok; listings = $c.Listings; summary = $c.SummaryLines; names = @($c.Names); reason = $c.Reason } }
            [ordered]@{ listing = Row $listing; both = Row ($summary + $listing); summaryOnly = Row $summary; empty = Row ''; blank = Row "`n   `n"; nothing = Row $null }
            """);
        Assert.True(r.GetProperty("listing").GetProperty("ok").GetBoolean());
        Assert.Equal(1, r.GetProperty("listing").GetProperty("listings").GetInt32());
        Assert.Equal("Ns.Type:PackXYZ", r.GetProperty("listing").GetProperty("names")[0].GetString());
        Assert.True(r.GetProperty("both").GetProperty("ok").GetBoolean());
        Assert.Equal(2, r.GetProperty("both").GetProperty("summary").GetInt32());
        foreach (string name in new[] { "summaryOnly", "empty", "blank", "nothing" })
        {
            Assert.False(r.GetProperty(name).GetProperty("ok").GetBoolean(), name);
            Assert.Contains("matched no compiled method", r.GetProperty(name).GetProperty("reason").GetString());
        }

        Assert.Contains("compiled 2 method(s)", r.GetProperty("summaryOnly").GetProperty("reason").GetString());
        Assert.Contains("inlined", r.GetProperty("summaryOnly").GetProperty("reason").GetString());
    }

    [PwshFact]
    public void MutationRun_IsNotAResult_WhenNoTestRan_OrEveryMutantIsNoCoverage()
    {
        JsonElement r = Eval("""
            function Run($killed, $timeout, $survived, $noCoverage, $log) {
                $s = [pscustomobject]@{ Killed = $killed; Timeout = $timeout; Survived = $survived; NoCoverage = $noCoverage; Tested = $killed + $timeout + $survived + $noCoverage }
                $v = Test-QualityMutationRun -Summary $s -LogText $log
                [ordered]@{ ok = $v.Ok; reason = $v.Reason }
            }
            [ordered]@{
                killed = Run 2 0 1 3 'Number of tests found: 12'
                timeoutOnly = Run 0 1 0 0 ''
                survivedOnly = Run 0 0 1 0 $null
                allNoCoverage = Run 0 0 0 4 'Number of tests found: 12'
                noTests = Run 0 0 0 4 "Initial testrun`nNumber of tests found: 0`n"
                noTestsEvenWithKills = Run 3 0 0 0 'Number of tests found: 0'
                tenTests = Run 1 0 0 0 'Number of tests found: 10'
                nothingJudged = Run 0 0 0 0 ''
            }
            """);
        foreach (string name in new[] { "killed", "timeoutOnly", "survivedOnly", "tenTests" })
        {
            Assert.True(r.GetProperty(name).GetProperty("ok").GetBoolean(), name);
        }

        Assert.False(r.GetProperty("allNoCoverage").GetProperty("ok").GetBoolean());
        Assert.Contains("NoCoverage", r.GetProperty("allNoCoverage").GetProperty("reason").GetString());
        Assert.False(r.GetProperty("noTests").GetProperty("ok").GetBoolean());
        Assert.Contains("Number of tests found: 0", r.GetProperty("noTests").GetProperty("reason").GetString());
        Assert.False(r.GetProperty("noTestsEvenWithKills").GetProperty("ok").GetBoolean());
        Assert.False(r.GetProperty("nothingJudged").GetProperty("ok").GetBoolean());
        Assert.Contains("no mutant was judged", r.GetProperty("nothingJudged").GetProperty("reason").GetString());
    }

    [PwshFact]
    public void IlBlock_MatchesANestedTypeByItsWholeName_NotByItsShortName()
    {
        // ILSpy writes a nested type inside its outer type's block under its own name; two types can both have an "Inner".
        JsonElement r = Eval("""
            $il = @(
              '.class public auto ansi beforefieldinit Ns.Outer',
              '    extends [System.Runtime]System.Object',
              '{',
              '    .class nested private auto ansi sealed beforefieldinit Inner',
              '        extends [System.Runtime]System.Object',
              '    {',
              '        .method public hidebysig instance void Go () cil managed',
              '        {',
              '            IL_0000: ret',
              '        } // end of method Inner::Go',
              '    } // end of class Inner',
              '    .method public hidebysig instance void Go () cil managed',
              '    {',
              '        IL_0000: nop',
              '    } // end of method Outer::Go',
              '} // end of class Outer',
              '.class public auto ansi beforefieldinit Ns.Other',
              '{',
              '    .class nested private auto ansi Inner',
              '    {',
              '        .method public static void Go () cil managed',
              '        {',
              '            IL_0000: ldnull',
              '        } // end of method Inner::Go',
              '    } // end of class Inner',
              '} // end of class Other',
              ".class private auto ansi sealed beforefieldinit '<>c__DisplayClass0_0'",
              '{',
              '    .method public hidebysig instance void Lambda () cil managed',
              '    {',
              '        IL_0000: ldc.i4.1',
              '    } // end of method <>c__DisplayClass0_0::Lambda',
              '} // end of class <>c__DisplayClass0_0'
            )
            function Cut($type, $method) { $c = Get-QualityIlBlock -Lines $il -Type $type -Method $method; [ordered]@{ typeSeen = $c.TypeSeen; methodSeen = $c.MethodSeen; text = (@($c.Block) -join "`n") } }
            [ordered]@{
                nestedMethod = Cut 'Ns.Outer+Inner' 'Go'
                otherNestedMethod = Cut 'Ns.Other+Inner' 'Go'
                outerMethod = Cut 'Ns.Outer' 'Go'
                nestedWhole = Cut 'Ns.Outer+Inner' $null
                outerWhole = Cut 'Ns.Outer' $null
                shortNameOnly = Cut 'Inner' $null
                noSuchMethod = Cut 'Ns.Outer+Inner' 'Stop'
                generated = Cut '<>c__DisplayClass0_0' 'Lambda'
            }
            """);
        string Text(string name) => r.GetProperty(name).GetProperty("text").GetString()!;
        Assert.Contains("IL_0000: ret", Text("nestedMethod"));
        Assert.DoesNotContain("nop", Text("nestedMethod"));
        Assert.DoesNotContain("ldnull", Text("nestedMethod"));
        Assert.Contains("IL_0000: ldnull", Text("otherNestedMethod"));
        Assert.DoesNotContain("IL_0000: ret", Text("otherNestedMethod"));
        Assert.Contains("IL_0000: nop", Text("outerMethod"));
        Assert.DoesNotContain("IL_0000: ret", Text("outerMethod"));
        Assert.StartsWith(".class nested private", Text("nestedWhole").TrimStart());
        Assert.EndsWith("} // end of class Inner", Text("nestedWhole").TrimEnd());
        Assert.DoesNotContain("nop", Text("nestedWhole"));
        Assert.Contains("IL_0000: ret", Text("outerWhole"));
        Assert.Contains("end of class Inner", Text("outerWhole"));
        Assert.EndsWith("} // end of class Outer", Text("outerWhole").TrimEnd());
        Assert.False(r.GetProperty("shortNameOnly").GetProperty("typeSeen").GetBoolean());
        Assert.True(r.GetProperty("noSuchMethod").GetProperty("typeSeen").GetBoolean());
        Assert.False(r.GetProperty("noSuchMethod").GetProperty("methodSeen").GetBoolean());
        Assert.True(r.GetProperty("generated").GetProperty("methodSeen").GetBoolean());
        Assert.Contains("ldc.i4.1", Text("generated"));
    }

    [PwshFact]
    public void DescendantIds_FollowOrphanedChildren_ButNotARecycledParentId()
    {
        // 100 (the root, gone) started at 10:00:00. 101 and 102 descend from it; 103 has parent id 100 but started an hour earlier (the id was
        // recycled); 104 hangs from an unrelated process.
        JsonElement r = Eval("""
            $t0 = [datetime]'2026-01-01T10:00:00'
            $table = @(
                [pscustomobject]@{ ProcessId = 102; ParentProcessId = 101; Start = $t0.AddSeconds(5) },
                [pscustomobject]@{ ProcessId = 101; ParentProcessId = 100; Start = $t0.AddSeconds(2) },
                [pscustomobject]@{ ProcessId = 103; ParentProcessId = 100; Start = $t0.AddHours(-1) },
                [pscustomobject]@{ ProcessId = 104; ParentProcessId = 999; Start = $t0.AddSeconds(3) },
                [pscustomobject]@{ ProcessId = 100; ParentProcessId = 1; Start = $t0.AddHours(-3) }
            )
            [ordered]@{
                ids = @(Get-QualityDescendantIds -Table $table -RootId 100 -RootStart $t0 | ForEach-Object { $_.ProcessId } | Sort-Object)
                none = @(Get-QualityDescendantIds -Table $table -RootId 555 -RootStart $t0).Count
                empty = @(Get-QualityDescendantIds -Table @() -RootId 100).Count
            }
            """);
        Assert.Equal([101, 102], r.GetProperty("ids").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(0, r.GetProperty("none").GetInt32());
        Assert.Equal(0, r.GetProperty("empty").GetInt32());
    }

    [PwshFact]
    public void InvokeProcess_StopsTheChildAtTheLimit_AndItsDescendantsAfterAnyEnd()
    {
        // A pwsh that starts a sleeping pwsh and then sleeps itself: at -TimeoutSec 3 it is stopped and so is its child. A second parent
        // that exits at once leaves its sleeping child behind (an orphan); that one is stopped too.
        using var temp = new TempDirectory();
        JsonElement r = Eval($$"""
            . '{{Path.Combine(Quality, "QualityLib.ps1").Replace("'", "''")}}'
            . '{{Path.Combine(Quality, "MutateAllLib.ps1").Replace("'", "''")}}'
            $dir = '{{temp.Path.Replace("'", "''")}}'
            $child = 'Start-Sleep 120'
            $parentSleeps = "`$p = Start-Process pwsh -ArgumentList '-NoProfile','-Command','$child' -PassThru; Set-Content '$dir\child1.pid' `$p.Id; Start-Sleep 120"
            $parentExits = "`$p = Start-Process pwsh -ArgumentList '-NoProfile','-Command','$child' -PassThru; Set-Content '$dir\child2.pid' `$p.Id"
            $started = Get-Date
            $timeout = Invoke-QualityProcess -FilePath pwsh -Arguments @('-NoProfile', '-Command', $parentSleeps) -LogFile (Join-Path $dir 'a.log') -TimeoutSec 3
            $elapsed = ((Get-Date) - $started).TotalSeconds
            $normal = Invoke-QualityProcess -FilePath pwsh -Arguments @('-NoProfile', '-Command', $parentExits) -LogFile (Join-Path $dir 'b.log') -TimeoutSec 60
            Start-Sleep -Milliseconds 500
            [ordered]@{
                timedOut = $timeout.TimedOut; exit = $timeout.ExitCode; elapsed = $elapsed
                child1Alive = [bool](Get-Process -Id ([int](Get-Content "$dir\child1.pid")) -ErrorAction SilentlyContinue)
                normalTimedOut = $normal.TimedOut; normalExit = $normal.ExitCode
                child2Alive = [bool](Get-Process -Id ([int](Get-Content "$dir\child2.pid")) -ErrorAction SilentlyContinue)
            }
            """);
        Assert.True(r.GetProperty("timedOut").GetBoolean());
        Assert.Equal(-1, r.GetProperty("exit").GetInt32());
        Assert.True(r.GetProperty("elapsed").GetDouble() < 60, "the limit of 3 s was not honoured");
        Assert.False(r.GetProperty("child1Alive").GetBoolean(), "the descendant of the timed-out process was left running");
        Assert.False(r.GetProperty("normalTimedOut").GetBoolean());
        Assert.Equal(0, r.GetProperty("normalExit").GetInt32());
        Assert.False(r.GetProperty("child2Alive").GetBoolean(), "the orphan of the process that exited was left running");
    }

    [PwshFact]
    public void SharedBuildServers_AreRecognised_AndNeverKilledAsLeftovers()
    {
        JsonElement r = Eval("""
            function Row($name, $cmd) { [pscustomobject]@{ ProcessId = 1; ParentProcessId = 0; Start = [datetime]::MinValue; Name = $name; CommandLine = $cmd } }
            [ordered]@{
                vbcs = Test-QualitySharedBuildServer -Row (Row 'VBCSCompiler.exe' 'C:\sdk\VBCSCompiler.exe')
                node = Test-QualitySharedBuildServer -Row (Row 'dotnet.exe' 'dotnet MSBuild.dll /nodemode:1 /nodeReuse:true')
                vstest = Test-QualitySharedBuildServer -Row (Row 'vstest.console.exe' 'vstest.console.exe --port:5')
                testhost = Test-QualitySharedBuildServer -Row (Row 'testhost.exe' 'testhost.exe --port 5')
                bare = Test-QualitySharedBuildServer -Row ([pscustomobject]@{ ProcessId = 1 })
            }
            """);
        Assert.True(r.GetProperty("vbcs").GetBoolean());
        Assert.True(r.GetProperty("node").GetBoolean());
        Assert.False(r.GetProperty("vstest").GetBoolean());
        Assert.False(r.GetProperty("testhost").GetBoolean());
        Assert.False(r.GetProperty("bare").GetBoolean());
    }

    [PwshFact]
    public void ArgumentString_QuotesWhatNeedsIt()
    {
        JsonElement r = Eval("""
            ConvertTo-QualityArgumentString -Arguments @('test', 'a b', 'x"y', '', 'C:\dir with\', 'FullyQualifiedName~A|FullyQualifiedName~B')
            """);
        Assert.Equal("""test "a b" "x\"y" "" "C:\dir with\\" FullyQualifiedName~A|FullyQualifiedName~B""", r.GetString());
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
    public void Mutate_RefusesACommentedOutTargetFramework_AndOneThatDiffersFromTheCentralValue()
    {
        using var temp = new TempDirectory();
        string commented = Path.Combine(temp.Path, "Commented.csproj");
        File.WriteAllText(commented, "<Project Sdk=\"Microsoft.NET.Sdk\"><!-- <TargetFramework>net11.0</TargetFramework> --></Project>");
        (int code, string output) = RunScript("mutate.ps1", "-Project", commented, "-TestProject", commented, "-Files", "**/X.cs");
        Assert.Equal(2, code);
        Assert.Contains("has no <TargetFramework> of its own", output);

        string different = Path.Combine(temp.Path, "Different.csproj");
        File.WriteAllText(different, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        (code, output) = RunScript("mutate.ps1", "-Project", different, "-TestProject", different, "-Files", "**/X.cs");
        Assert.Equal(2, code);
        Assert.Contains("<TargetFramework>net8.0</TargetFramework> but Directory.Build.props states", output);
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

    [PwshFact]
    public void MutateAllBatchSummary_ExcludesZeroJudgedFiles_FromFilesAndCompileErrorTotals()
    {
        const string report = """
            {"files":{
              "Sub\\Mutated.cs":{"language":"cs","source":"int M(int a, int b)\n{\n    if (a > b) return a;\n    return b;\n}",
                "mutants":[
                  {"id":"1","mutatorName":"Equality","replacement":">=","location":{"start":{"line":3,"column":11},"end":{"line":3,"column":16}},"status":"Killed"},
                  {"id":"2","mutatorName":"Conditional","replacement":"true","location":{"start":{"line":3,"column":9},"end":{"line":3,"column":14}},"status":"Survived"},
                  {"id":"3","mutatorName":"String","replacement":"x","location":{"start":{"line":1,"column":1},"end":{"line":1,"column":4}},"status":"CompileError"},
                  {"id":"4","mutatorName":"String","replacement":"y","location":{"start":{"line":1,"column":1},"end":{"line":1,"column":4}},"status":"Ignored"}]},
              "RealmService.cs":{"language":"cs","source":"class RealmService {}",
                "mutants":[
                  {"id":"5","mutatorName":"String","replacement":"z","location":{"start":{"line":1,"column":1},"end":{"line":1,"column":5}},"status":"CompileError"},
                  {"id":"6","mutatorName":"String","replacement":"w","location":{"start":{"line":1,"column":1},"end":{"line":1,"column":5}},"status":"CompileError"},
                  {"id":"7","mutatorName":"String","replacement":"q","location":{"start":{"line":1,"column":1},"end":{"line":1,"column":5}},"status":"Ignored"}]},
              "Empty.cs":{"language":"cs","source":"class Empty {}","mutants":[]}}}
            """;
        JsonElement r = Eval($$"""
            $report = @'
            {{report}}
            '@ | ConvertFrom-Json
            $s = Get-MutateAllBatchSummary -Report $report -Root 'D:/repo' -ProjectRelativeDirectory 'src/ArcaneCore.Realm'
            $jDone = Get-MutateAllJudgement -Counts $s.Counts
            $jZero = Get-MutateAllJudgement -Counts ([pscustomobject]@{ Tested = 0; Killed = 0; Timeout = 0; Survived = 0; NoCoverage = 0 })
            $jNoCov = Get-MutateAllJudgement -Counts ([pscustomobject]@{ Tested = 3; Killed = 0; Timeout = 0; Survived = 0; NoCoverage = 3 })
            [ordered]@{
                killed = $s.Counts.Killed; survived = $s.Counts.Survived; tested = $s.Counts.Tested
                compileError = $s.Counts.CompileError; ignored = $s.Counts.Ignored
                files = @($s.Files | ForEach-Object { $_.File })
                survivors = @($s.Survivors | ForEach-Object { $_.File })
                doneStatus = $jDone.Status; zeroStatus = $jZero.Status; noCovStatus = $jNoCov.Status
            }
            """);
        Assert.Equal(1, r.GetProperty("killed").GetInt32());
        Assert.Equal(1, r.GetProperty("survived").GetInt32());
        Assert.Equal(2, r.GetProperty("tested").GetInt32());
        Assert.Equal(1, r.GetProperty("compileError").GetInt32());
        Assert.Equal(1, r.GetProperty("ignored").GetInt32());
        Assert.Equal(["src/ArcaneCore.Realm/Sub/Mutated.cs"], r.GetProperty("files").EnumerateArray().Select(e => e.GetString()!));
        Assert.Equal(["src/ArcaneCore.Realm/Sub/Mutated.cs"], r.GetProperty("survivors").EnumerateArray().Select(e => e.GetString()!));
        Assert.Equal("done", r.GetProperty("doneStatus").GetString());
        Assert.Equal("error", r.GetProperty("zeroStatus").GetString());
        Assert.Equal("error", r.GetProperty("noCovStatus").GetString());
    }

    [PwshFact]
    public void MutateAllPlan_SplitsBatches_ExcludesGeneratedFiles_AndScopesTestFilters()
    {
        using var temp = new TempDirectory();
        JsonElement r = Eval($$"""
            $root = '{{temp.Path.Replace("'", "''")}}'
            New-Item -ItemType Directory -Force "$root/src/ArcaneCore.World/Handlers", "$root/tests/ArcaneCore.World.Tests/Handlers" | Out-Null
            Set-Content "$root/src/ArcaneCore.World/ArcaneCore.World.csproj" '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>'
            Set-Content "$root/tests/ArcaneCore.World.Tests/ArcaneCore.World.Tests.csproj" '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" /><ProjectReference Include="..\..\src\ArcaneCore.World\ArcaneCore.World.csproj" /></ItemGroup></Project>'
            Set-Content "$root/src/ArcaneCore.World/WorldHost.cs" ("class WorldHost {`n" + (@(1..10 | ForEach-Object { "    int f$_ = $_;" }) -join "`n") + "`n}")
            Set-Content "$root/src/ArcaneCore.World/A.cs" "class A {}"
            Set-Content "$root/src/ArcaneCore.World/Handlers/ChatHandlers.cs" ("class ChatHandlers {`n" + (@(1..15 | ForEach-Object { "    int c$_ = $_;" }) -join "`n") + "`n}")
            Set-Content "$root/src/ArcaneCore.World/Handlers/MovementHandlers.cs" ("class MovementHandlers {`n" + (@(1..15 | ForEach-Object { "    int m$_ = $_;" }) -join "`n") + "`n}")
            Set-Content "$root/src/ArcaneCore.World/Handlers/Generated.g.cs" "class Generated {}"
            $plan = New-MutateAllPlan -Root $root -MaxFiles 1 -MaxLines 20 -Projects @('World')
            $batches = @($plan.Batches)
            $handlersBatch = [ordered]@{ project = 'ArcaneCore.World'; testProject = 'ArcaneCore.World.Tests'; id = 'World-099'; name = 'Handlers'; parts = @('Handlers'); include = @('Handlers/**/*.cs'); exclude = @('Handlers/Generated.g.cs') }
            $rootOnlyBatch = [ordered]@{ project = 'ArcaneCore.World'; testProject = 'ArcaneCore.World.Tests'; id = 'World-001'; name = '(project root files)'; parts = @('(project root files)'); include = @('WorldHost.cs'); exclude = @() }
            $shortRootBatch = [ordered]@{ project = 'ArcaneCore.World'; testProject = 'ArcaneCore.World.Tests'; id = 'World-002'; name = '(project root files)'; parts = @('(project root files)'); include = @('A.cs'); exclude = @() }
            $splitKids = @(Split-MutateAllBatch -Batch $handlersBatch -Root $root)
            $singleKid = Split-MutateAllBatch -Batch $rootOnlyBatch -Root $root
            [ordered]@{
                skipped = @($plan.SkippedFiles)
                batchNames = @($batches | ForEach-Object { $_.name })
                handlersFilter = Get-MutateAllScopedTestFilter -Batch $handlersBatch -Root $root
                rootFilter = Get-MutateAllScopedTestFilter -Batch $rootOnlyBatch -Root $root
                shortRootFilter = Get-MutateAllScopedTestFilter -Batch $shortRootBatch -Root $root
                splitIds = @($splitKids | ForEach-Object { $_.id })
                splitDepth = $splitKids[0].splitDepth
                splitFrom = $splitKids[0].splitFrom
                splitFilterA = Get-MutateAllScopedTestFilter -Batch $splitKids[0] -Root $root
                singleSplitIsNull = ($null -eq $singleKid)
            }
            """);
        Assert.Equal(["src/ArcaneCore.World/Handlers/Generated.g.cs (generated file name)"], r.GetProperty("skipped").EnumerateArray().Select(e => e.GetString()!));
        Assert.Contains("(project root files #1)", r.GetProperty("batchNames").EnumerateArray().Select(e => e.GetString()!));
        Assert.Equal(
            "FullyQualifiedName~ArcaneCore.World.Tests.Handlers.|FullyQualifiedName~ChatHandlers|FullyQualifiedName~MovementHandlers",
            r.GetProperty("handlersFilter").GetString());
        Assert.Equal("FullyQualifiedName~WorldHost", r.GetProperty("rootFilter").GetString());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("shortRootFilter").ValueKind);
        Assert.Equal(["World-099a", "World-099b"], r.GetProperty("splitIds").EnumerateArray().Select(e => e.GetString()!));
        Assert.Equal(1, r.GetProperty("splitDepth").GetInt32());
        Assert.Equal("World-099", r.GetProperty("splitFrom").GetString());
        Assert.Equal("FullyQualifiedName~ArcaneCore.World.Tests.Handlers.|FullyQualifiedName~ChatHandlers", r.GetProperty("splitFilterA").GetString());
        Assert.True(r.GetProperty("singleSplitIsNull").GetBoolean());
    }

    [PwshFact]
    public void MutateReport_PreservesJudgedRootRows_WhenSubdirectoryBatchVisitsRootWithZeroJudged()
    {
        using var temp = new TempDirectory();
        JsonElement r = Eval($$"""
            $runDir = '{{temp.Path.Replace("'", "''")}}'
            $b1Sum = Join-Path $runDir 'b1-summary.json'
            $b2Sum = Join-Path $runDir 'b2-summary.json'
            Write-MutateAllJson -Path $b1Sum -Object ([ordered]@{
                Counts = [ordered]@{ Killed = 8; Survived = 2; NoCoverage = 0; Timeout = 0; CompileError = 1; Ignored = 0; Other = 0; Tested = 10 }
                Files = @(
                    [ordered]@{ File = 'src/ArcaneCore.Realm/RealmService.cs'; Dir = 'src/ArcaneCore.Realm'; Killed = 8; Survived = 2; NoCoverage = 0; Timeout = 0; CompileError = 1; Ignored = 0; Other = 0 }
                )
                Survivors = @(
                    [ordered]@{ File = 'src/ArcaneCore.Realm/RealmService.cs'; Line = 12; Column = 5; Status = 'Survived'; Mutator = 'Equality'; Original = 'a == b'; Mutated = 'a != b' }
                )
            })
            Write-MutateAllJson -Path $b2Sum -Object ([ordered]@{
                Counts = [ordered]@{ Killed = 6; Survived = 0; NoCoverage = 0; Timeout = 0; CompileError = 0; Ignored = 0; Other = 0; Tested = 6 }
                Files = @(
                    [ordered]@{ File = 'src/ArcaneCore.Realm/RealmService.cs'; Dir = 'src/ArcaneCore.Realm'; Killed = 0; Survived = 0; NoCoverage = 0; Timeout = 0; CompileError = 4; Ignored = 1; Other = 0 },
                    [ordered]@{ File = 'src/ArcaneCore.Realm/UnmutatedRoot.cs'; Dir = 'src/ArcaneCore.Realm'; Killed = 0; Survived = 0; NoCoverage = 0; Timeout = 0; CompileError = 2; Ignored = 0; Other = 0 },
                    [ordered]@{ File = 'src/ArcaneCore.Realm/Auth/AuthSession.cs'; Dir = 'src/ArcaneCore.Realm/Auth'; Killed = 6; Survived = 0; NoCoverage = 0; Timeout = 0; CompileError = 0; Ignored = 0; Other = 0 }
                )
                Survivors = @()
            })
            $state = [ordered]@{
                runId = 'unit-run'; createdUtc = '2026-10-21T00:00:00Z'; head = 'deadbeef'; unmapped = @()
                batches = @(
                    [ordered]@{ id = 'Realm-000'; project = 'ArcaneCore.Realm'; name = 'Auth'; lines = 500; status = 'split'; durationSec = 4200; queueSec = 0; summary = $null },
                    [ordered]@{ id = 'Realm-001'; project = 'ArcaneCore.Realm'; name = '(project root)'; lines = 200; status = 'done'; durationSec = 120; queueSec = 20; summary = $b1Sum },
                    [ordered]@{ id = 'Realm-002'; project = 'ArcaneCore.Realm'; name = 'Auth [half 1/2]'; lines = 250; status = 'done'; durationSec = 150; queueSec = 30; summary = $b2Sum }
                )
            }
            Save-MutateAllState -State $state -RunDirectory $runDir
            & pwsh -NoProfile -File '{{Path.Combine(Quality, "mutate-report.ps1").Replace("'", "''")}}' -RunDir $runDir -Quiet
            $rc = $LASTEXITCODE
            [ordered]@{
                exit = $rc
                md = (Get-Content -LiteralPath (Join-Path $runDir 'SUMMARY.md') -Raw)
                dirs = @(Import-Csv -LiteralPath (Join-Path $runDir 'directories.csv') | ForEach-Object { "$($_.directory):$($_.Killed)/$($_.Survived)/$($_.CompileError)/$($_.score)" })
            }
            """);
        Assert.Equal(0, r.GetProperty("exit").GetInt32());
        string md = r.GetProperty("md").GetString()!;
        Assert.Contains("| ArcaneCore.Realm/(root) | 8 | 2 | 0 | 0 | 1 | 80% |", md);
        Assert.Contains("| ArcaneCore.Realm/Auth | 6 | 0 | 0 | 0 | 0 | 100% |", md);
        Assert.Contains("| ArcaneCore.Realm | 14 | 2 | 0 | 0 | 1 | 87.5% |", md);
        Assert.Contains("1 timed out and were split", md);
        Assert.Equal(
        [
            "src/ArcaneCore.Realm:8/2/1/80",
            "src/ArcaneCore.Realm/Auth:6/0/0/100",
        ], r.GetProperty("dirs").EnumerateArray().Select(e => e.GetString()!));
    }

    [PwshFact]
    public void MutateAll_UsesPortableDefaults_AndRefusesMissingRunOnResume()
    {
        foreach (string file in new[] { "mutate-all.ps1", "mutate-report.ps1", "MutateAllLib.ps1" })
        {
            string text = File.ReadAllText(Path.Combine(Quality, file));
            Assert.DoesNotContain(@"D:\", text, StringComparison.Ordinal);
            Assert.DoesNotContain("D:/", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ArcaneCore-lanes", text, StringComparison.OrdinalIgnoreCase);
        }

        using var temp = new TempDirectory();
        (int codeAll, string outAll) = RunScript("mutate-all.ps1", "-Resume", "-OutputRoot", temp.Path);
        Assert.Equal(2, codeAll);
        Assert.Contains("no run with a state.json", outAll);

        (int codeRep, string outRep) = RunScript("mutate-report.ps1", "-OutputRoot", temp.Path);
        Assert.Equal(2, codeRep);
        Assert.Contains("no run with a state.json", outRep);
    }

    private static JsonElement Eval(string body)
    {
        using var temp = new TempDirectory();
        string script = Path.Combine(temp.Path, "run.ps1");
        File.WriteAllText(script, $$"""
            $ErrorActionPreference = 'Stop'
            . '{{Path.Combine(Quality, "QualityLib.ps1").Replace("'", "''")}}'
            . '{{Path.Combine(Quality, "MutateAllLib.ps1").Replace("'", "''")}}'
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
