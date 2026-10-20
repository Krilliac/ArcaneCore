# Mutation testing of chosen source files with Stryker.NET (the pinned dotnet-stryker of .config/dotnet-tools.json).
#   pwsh -NoProfile -File tools/ops/quality/mutate.ps1 -Project src/ArcaneCore.Game/ArcaneCore.Game.csproj `
#        -TestProject tests/ArcaneCore.Game.Tests/ArcaneCore.Game.Tests.csproj -Files 'AntiCheat/SpeedHackDetector.cs' [-Concurrency 2] [-Since main] [-TestFilter 'FullyQualifiedName~SpeedHack']
# -Files is required: there is no whole-project default (a mutation run rebuilds and re-runs the tests once per mutant).
# Each -Files entry is a Stryker glob relative to the project directory ('**/Foo.cs', 'AntiCheat/*.cs', '!Gen/**'); a path written from the
# repository root (src/<Project>/AntiCheat/X.cs) is accepted too.
# Output: artifacts/mutation/<yyyyMMdd-HHmmss>/ (gitignored): the Stryker reports/ folder (mutation-report.html and .json), stryker.log, summary.txt.
# Prints killed / survived / no-coverage / timeout counts, the mutation score and every survivor as file:line mutator  original -> mutated.
# Exit codes: 0 = the run finished and judged at least one mutant; 2 = error (bad argument, tool failure, no mutants, no tests matched).
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

# Stryker's project analysis reads <TargetFramework> from each csproj itself: one that only inherits it from Directory.Build.props
# is "No project found" (Stryker 4.16.0 on .NET 11, checked). Say so up front instead of letting Stryker fail with that message.
foreach ($csproj in $projectPath, $testProjectPath) {
    if ((Get-Content -LiteralPath $csproj -Raw) -notmatch '<TargetFrameworks?>') {
        Stop-Mutate "$csproj has no <TargetFramework> of its own; Stryker's analysis needs one in the csproj (add <TargetFramework>net11.0</TargetFramework> beside the Directory.Build.props value). See docs/ops/tooling.md."
    }
}

$projectDirectory = Split-Path $projectPath -Parent
$projectRelative = [IO.Path]::GetRelativePath($repo, $projectDirectory)
$globs = @($Files | ForEach-Object { ConvertTo-QualityMutateGlob -Glob $_ -ProjectRelativeDirectory $projectRelative })

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$root = if ($OutputRoot) { $OutputRoot } else { Join-Path $repo 'artifacts/mutation' }
$out = Join-Path $root $stamp
New-Item -ItemType Directory -Force $out | Out-Null

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
# reader would then wait for them forever. The wait is on the Stryker process itself, with a limit.
$quoted = $arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }
$stryker = Start-Process dotnet -ArgumentList $quoted -WorkingDirectory (Split-Path $testProjectPath -Parent) -NoNewWindow -PassThru `
    -RedirectStandardOutput $log -RedirectStandardError (Join-Path $out 'stryker.err')
$printed = 0L
$deadline = [DateTime]::UtcNow.AddMinutes($TimeoutMin)
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
while (-not $stryker.WaitForExit(5000)) {
    Write-NewLog
    if ([DateTime]::UtcNow -gt $deadline) {
        $stryker.Kill($true)
        Stop-Mutate "Stryker did not finish within $TimeoutMin minutes and was stopped (log: $log). Name fewer files, or give -TestFilter."
    }
}
Write-NewLog
$strykerExit = $stryker.ExitCode

if ($strykerExit -ne 0) { Stop-Mutate "Stryker exited $strykerExit (log: $log)." }
$reportFile = Get-ChildItem $out -Recurse -Filter 'mutation-report.json' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
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

if ($summary.Tested -eq 0) { Stop-Mutate "no mutant was judged (Stryker found no mutants in the files, or no test ran). Log: $log" }
exit 0
