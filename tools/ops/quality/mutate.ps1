# Mutation testing of chosen source files with Stryker.NET (the pinned dotnet-stryker of .config/dotnet-tools.json).
#   pwsh -NoProfile -File tools/ops/quality/mutate.ps1 -Project src/ArcaneCore.Game/ArcaneCore.Game.csproj `
#        -TestProject tests/ArcaneCore.Game.Tests/ArcaneCore.Game.Tests.csproj -Files 'AntiCheat/SpeedHackDetector.cs' [-Concurrency 2] [-Since main] [-TestFilter 'FullyQualifiedName~SpeedHack']
# -Files is required: there is no whole-project default (a mutation run rebuilds and re-runs the tests once per mutant).
# Each -Files entry is a Stryker glob relative to the project directory ('**/Foo.cs', 'AntiCheat/*.cs', '!Gen/**'); a path written from the
# repository root (src/<Project>/AntiCheat/X.cs) is accepted too.
# Output: artifacts/mutation/<yyyyMMdd-HHmmss>-<pid>/ (gitignored): the Stryker reports/ folder (mutation-report.html and .json), stryker.log, summary.txt.
# The summary is read from this run's own report (the folder is new for every run), never from an older one.
# Prints killed / survived / no-coverage / timeout counts, the mutation score and every survivor as file:line mutator  original -> mutated.
# Exit codes: 0 = the run finished and killed, timed out or let survive at least one mutant; 2 = error (bad argument, tool failure, no
# mutants, no tests found ("Number of tests found: 0" in stryker.log), or every judged mutant NoCoverage: no test reached the code).
# When Stryker ends (or the -TimeoutMin limit is hit) the vstest / testhost / build processes it started are stopped too.
# Survivors do not change the exit code: they are the findings, not a failure of the tool.
param(
    [Parameter(Mandatory)] [string]$Project,
    [Parameter(Mandatory)] [string]$TestProject,
    [Parameter(Mandatory)] [string[]]$Files,
    [ValidateRange(1, 64)] [int]$Concurrency = 2,
    [string]$Since,
    [string]$TestFilter,
    [ValidateRange(1, 1440)] [int]$TimeoutMin = 180,
    [string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'QualityLib.ps1')

function Stop-Mutate([string]$Message) {
    [Console]::Error.WriteLine("mutate.ps1: $Message")
    exit 2
}

trap { Stop-Mutate "unexpected error: $($_.Exception.Message) at line $($_.InvocationInfo.ScriptLineNumber)" }

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path
function Resolve-RepoFile([string]$Path, [string]$What) {
    $full = if ([IO.Path]::IsPathRooted($Path)) { $Path } else { Join-Path $repo $Path }
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { Stop-Mutate "$What '$Path' does not exist (looked for $full)." }
    (Resolve-Path -LiteralPath $full).Path
}

$projectPath = Resolve-RepoFile $Project '-Project'
$testProjectPath = Resolve-RepoFile $TestProject '-TestProject'
if ($projectPath -notmatch '\.csproj$') { Stop-Mutate "-Project must be a .csproj, got '$Project'." }
if ($testProjectPath -notmatch '\.csproj$') { Stop-Mutate "-TestProject must be a .csproj, got '$TestProject'." }
$Files = @($Files | ForEach-Object { $_ -split ',' } | Where-Object { $_.Trim() } | ForEach-Object { $_.Trim() })
if ($Files.Count -eq 0) { Stop-Mutate '-Files is empty: name the files to mutate (a glob relative to the project directory).' }

# Stryker's project analysis reads <TargetFramework> from each csproj itself: one that only inherits it from Directory.Build.props is
# "No project found" (Stryker 4.16.0 on .NET 11, checked; its target-framework setting does not help: same message). So the csproj must
# state it, and the same value as Directory.Build.props: a project that differs would be analysed for another framework than it builds for.
$central = Get-QualityTargetFramework (Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw)
if (-not $central) { Stop-Mutate 'Directory.Build.props states no <TargetFramework>.' }
foreach ($csproj in $projectPath, $testProjectPath) {
    $local = Get-QualityTargetFramework (Get-Content -LiteralPath $csproj -Raw)
    if (-not $local) {
        Stop-Mutate "$csproj has no <TargetFramework> of its own; Stryker's analysis needs one in the csproj (add <TargetFramework>$central</TargetFramework>, the Directory.Build.props value). See docs/ops/tooling.md."
    }
    if ($local -ne $central) {
        Stop-Mutate "$csproj states <TargetFramework>$local</TargetFramework> but Directory.Build.props states $central; they must be equal. See docs/ops/tooling.md."
    }
}

$projectDirectory = Split-Path $projectPath -Parent
$projectRelative = [IO.Path]::GetRelativePath($repo, $projectDirectory)
$globs = @($Files | ForEach-Object { ConvertTo-QualityMutateGlob -Glob $_ -ProjectRelativeDirectory $projectRelative })

$stamp = "$(Get-Date -Format 'yyyyMMdd-HHmmss')-$PID"
$root = if ($OutputRoot) { $OutputRoot } else { Join-Path $repo 'artifacts/mutation' }
$out = Join-Path $root $stamp
if (Test-Path -LiteralPath $out) { Stop-Mutate "the output folder $out exists already; run again." }
New-Item -ItemType Directory $out | Out-Null

$arguments = [System.Collections.Generic.List[string]]::new()
$arguments.AddRange([string[]]@('tool', 'run', 'dotnet-stryker', '--',
        '--project', (Split-Path $projectPath -Leaf), '--concurrency', "$Concurrency",
        '--output', $out, '--reporter', 'html', '--reporter', 'json', '--break-at', '0', '--skip-version-check'))
foreach ($glob in $globs) { $arguments.Add('--mutate'); $arguments.Add($glob) }
if ($Since) { $arguments.Add("--since:$Since") }
if ($TestFilter) {
    # Stryker 4.16.0 has no --test-case-filter option on the command line; its configuration file takes the same setting.
    $config = Join-Path $out 'stryker-config.json'
    [ordered]@{ 'stryker-config' = [ordered]@{ 'test-case-filter' = $TestFilter } } | ConvertTo-Json | Set-Content -LiteralPath $config -Encoding utf8
    $arguments.Add('--config-file'); $arguments.Add($config)
}

$log = Join-Path $out 'stryker.log'
"mutate.ps1: stryker in $(Split-Path $testProjectPath -Parent), project $(Split-Path $projectPath -Leaf), globs: $($globs -join ', '), concurrency $Concurrency"
"mutate.ps1: output $out"
# Stryker does not restore: without project.assets.json its analysis finds no project. The repo's packages.lock.json files are honoured.
& dotnet restore $testProjectPath --locked-mode 2>&1 | Tee-Object -FilePath (Join-Path $out 'restore.log') | Out-Null
if ($LASTEXITCODE -ne 0) { Stop-Mutate "dotnet restore --locked-mode of $TestProject failed (log: $(Join-Path $out 'restore.log'))." }
# Stryker's output goes to a file, not a pipe: the vstest.console processes it starts can outlive it holding the pipe open, and a pipe
# reader would then wait for them forever. Invoke-QualityProcess waits on the Stryker process itself, with a limit, and afterwards stops
# the vstest / testhost processes that descended from it (found while it ran: once it has exited they are orphans). The builds Stryker
# starts get MSBUILDDISABLENODEREUSE=1 and UseSharedCompilation=false, so they leave no MSBuild node or VBCSCompiler behind, and those
# two kinds of server are never killed here (another slot's build may be using one).
$printed = 0L
$runStartedUtc = [DateTime]::UtcNow
function Write-NewLog {
    $stream = [IO.File]::Open($log, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        [void]$stream.Seek($script:printed, [IO.SeekOrigin]::Begin)
        $text = [IO.StreamReader]::new($stream).ReadToEnd()
        $script:printed = $stream.Position
        if ($text) { [Console]::Out.Write($text) }
    }
    finally { $stream.Dispose() }
}
$run = Invoke-QualityProcess -FilePath 'dotnet' -Arguments $arguments.ToArray() -LogFile $log -TimeoutSec ($TimeoutMin * 60) `
    -WorkingDirectory (Split-Path $testProjectPath -Parent) -OnTick { Write-NewLog } `
    -Environment @{ MSBUILDDISABLENODEREUSE = '1'; UseSharedCompilation = 'false' }
if ($run.TimedOut) { Stop-Mutate "Stryker did not finish within $TimeoutMin minutes and was stopped with its child processes (log: $log). Name fewer files, or give -TestFilter." }
$strykerExit = $run.ExitCode
if ($run.Stopped -gt 0) { "mutate.ps1: stopped $($run.Stopped) process(es) Stryker left running (vstest.console or testhost)." }

# Stryker itself exits 1 when the filter leaves no test ("Number of tests found: 0", then "No test result reported"); say why.
if ((Get-Content -LiteralPath $log -Raw) -match 'Number of tests found:\s*0(?!\d)') {
    Stop-Mutate "Stryker found no tests (log: 'Number of tests found: 0'; Stryker exit code $strykerExit): the -TestFilter matched no test, or the test project has none. Log: $log"
}
if ($strykerExit -ne 0) { Stop-Mutate "Stryker exited $strykerExit (log: $log)." }
# $out is new for this run, and the report must have been written since the run began: an older report is never this run's result.
$reportFile = Get-ChildItem $out -Recurse -Filter 'mutation-report.json' -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTimeUtc -ge $runStartedUtc.AddSeconds(-2) } | Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $reportFile) { Stop-Mutate "Stryker wrote no mutation-report.json (no file matched the globs, or nothing changed since '$Since'). Log: $log" }

$summary = Get-QualityMutationSummary -Root $repo -Report (Get-Content -LiteralPath $reportFile.FullName -Raw | ConvertFrom-Json)
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("Mutation summary ($($globs -join ', '))")
$lines.Add("  killed $($summary.Killed)  survived $($summary.Survived)  no-coverage $($summary.NoCoverage)  timeout $($summary.Timeout)  (compile-error $($summary.CompileError), ignored $($summary.Ignored), other $($summary.Other) not counted)")
$lines.Add($(if ($null -eq $summary.Score) { '  score n/a (no mutant was judged)' } else { "  score $($summary.Score)% ($($summary.Killed + $summary.Timeout) of $($summary.Tested) judged mutants detected)" }))
foreach ($f in $summary.PerFile) { $lines.Add("  $($f.File): killed $($f.Killed + $f.Timeout), survived $($f.Survived), no-coverage $($f.NoCoverage), score $($f.Score)%") }
$lines.Add("  survivors ($($summary.Survivors.Count)):")
foreach ($s in $summary.Survivors) { $lines.Add("    $($s.File):$($s.Line):$($s.Column) [$($s.Status)] $($s.Mutator)  $($s.Original)  ->  $($s.Mutated)") }
$lines.Add("  report: $(Join-Path $reportFile.DirectoryName 'mutation-report.html')")
$lines | Set-Content -LiteralPath (Join-Path $out 'summary.txt') -Encoding utf8
$lines | ForEach-Object { $_ }

$verdict = Test-QualityMutationRun -Summary $summary -LogText (Get-Content -LiteralPath $log -Raw)
if (-not $verdict.Ok) { Stop-Mutate "$($verdict.Reason) Log: $log" }
exit 0
