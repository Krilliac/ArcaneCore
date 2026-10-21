# Prints the x64 the JIT generates for a method while a test filter runs (DOTNET_JitDisasm), to check what the machine does against what
# the source says.
#   pwsh -NoProfile -File tools/ops/quality/jit-disasm.ps1 -TestProject tests/ArcaneCore.Game.Tests/ArcaneCore.Game.Tests.csproj `
#        -Filter 'FullyQualifiedName~SpeedHackDetectorTests' -Method 'SpeedHackDetector:Feed' [-FinalCode] [-Configuration Release] [-Summary] [-NoBuild]
# -Method is a JitDisasm pattern: 'Class:Method', 'Method', or a '*' wildcard ('*Feed*'); several are space separated.
# Only the test host sees the DOTNET_ variables: they travel in a generated .runsettings file, so the build, dotnet test and vstest
# processes do not JIT-dump. -FinalCode sets DOTNET_TieredCompilation=0 so each method is compiled once and not recompiled later (fully optimized for a Release build; a Debug build, the default, is compiled MinOpts and debuggable whatever the tiering, so use -Configuration Release to judge optimized code); without it the
# first (Tier-0, unoptimized) compile of a method is what is printed first and the optimized one appears only after the method ran
# 30 times and the background tier-up finished. -Summary also lists every method the JIT compiled (DOTNET_JitDisasmSummary=1).
# -NoInline sets DOTNET_JitNoInline=1, so a small method the JIT would inline into its caller is compiled on its own and gets a listing.
# Output is JitStdOutFile in artifacts/jit/<timestamp>-<pid>/jit.txt, printed here. -TimeoutMin (default 30) bounds the build and the test
# run together; the processes they started are stopped when they end or the limit is reached.
# Exit codes: 0 = a listing ('; Assembly listing for method') of the requested method is in jit.txt and printed, 2 = error (build or tests
# failed or timed out, the filter ran no test, or the JIT wrote no listing for the pattern: no match, or the method was inlined; -Summary
# lines alone do not count).
param(
    [Parameter(Mandatory)] [string]$TestProject,
    [Parameter(Mandatory)] [string]$Filter,
    [Parameter(Mandatory)] [string]$Method,
    [ValidateSet('Debug', 'Release')] [string]$Configuration = 'Debug',
    [switch]$FinalCode,
    [switch]$Summary,
    [switch]$NoBuild,
    [switch]$NoInline,
    [ValidateRange(1, 1440)] [int]$TimeoutMin = 30,
    [string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'QualityLib.ps1')

function Stop-Jit([string]$Message) {
    [Console]::Error.WriteLine("jit-disasm.ps1: $Message")
    exit 2
}

trap { Stop-Jit "unexpected error: $($_.Exception.Message) at line $($_.InvocationInfo.ScriptLineNumber)" }

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path
$projectPath = if ([IO.Path]::IsPathRooted($TestProject)) { $TestProject } else { Join-Path $repo $TestProject }
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) { Stop-Jit "-TestProject '$TestProject' does not exist (looked for $projectPath)." }
$projectPath = (Resolve-Path -LiteralPath $projectPath).Path

$out = Join-Path ($(if ($OutputRoot) { $OutputRoot } else { Join-Path $repo 'artifacts/jit' })) "$(Get-Date -Format 'yyyyMMdd-HHmmss')-$PID"
if (Test-Path -LiteralPath $out) { Stop-Jit "the output folder $out exists already; run again." }
New-Item -ItemType Directory $out | Out-Null
$jitFile = Join-Path $out 'jit.txt'
$settings = Join-Path $out 'jit.runsettings'

$variables = [ordered]@{ DOTNET_JitDisasm = $Method; DOTNET_JitStdOutFile = $jitFile }
if ($FinalCode) { $variables['DOTNET_TieredCompilation'] = '0' }
if ($Summary) { $variables['DOTNET_JitDisasmSummary'] = '1' }
if ($NoInline) { $variables['DOTNET_JitNoInline'] = '1' }
$escape = { param($s) [Security.SecurityElement]::Escape($s) }
$xml = "<RunSettings><RunConfiguration><EnvironmentVariables>" +
    (($variables.GetEnumerator() | ForEach-Object { "<$($_.Key)>$(& $escape $_.Value)</$($_.Key)>" }) -join '') +
    "</EnvironmentVariables></RunConfiguration></RunSettings>"
Set-Content -LiteralPath $settings -Value $xml -Encoding utf8

$deadline = [DateTime]::UtcNow.AddMinutes($TimeoutMin)
function Get-RemainingSeconds { [math]::Max(1, [int]($deadline - [DateTime]::UtcNow).TotalSeconds) }
function Show-Tail([string]$File, [int]$Count) {
    foreach ($f in $File, "$File.err") { if (Test-Path -LiteralPath $f) { Get-Content -LiteralPath $f -Tail $Count } }
}

if (-not $NoBuild) {
    $buildLog = Join-Path $out 'build.log'
    $build = Invoke-QualityProcess -FilePath 'dotnet' -Arguments @('build', $projectPath, '-c', $Configuration, '-nodeReuse:false', '-p:UseSharedCompilation=false') `
        -LogFile $buildLog -TimeoutSec (Get-RemainingSeconds) -WorkingDirectory $repo
    Show-Tail $buildLog 3
    if ($build.TimedOut) { Stop-Jit "dotnet build did not finish within $TimeoutMin minutes and was stopped (log: $buildLog)." }
    if ($build.ExitCode -ne 0) { Stop-Jit "dotnet build failed with exit code $($build.ExitCode) (log: $buildLog)." }
}

$testLog = Join-Path $out 'test.log'
$test = Invoke-QualityProcess -FilePath 'dotnet' -Arguments @('test', $projectPath, '-c', $Configuration, '--no-build', '--filter', $Filter, '--settings', $settings, '--logger', 'console;verbosity=minimal') `
    -LogFile $testLog -TimeoutSec (Get-RemainingSeconds) -WorkingDirectory $repo
Show-Tail $testLog 6
if ($test.TimedOut) { Stop-Jit "dotnet test did not finish within $TimeoutMin minutes and was stopped (log: $testLog)." }
if ($test.ExitCode -ne 0) { Stop-Jit "dotnet test exited $($test.ExitCode) (log: $testLog); a failing test is not a disassembly run." }
if ((Get-Content -LiteralPath $testLog -Raw) -match 'No test matches the given testcase filter') { Stop-Jit "the filter '$Filter' matched no test (log: $testLog)." }

# A non-empty jit.txt is not a disassembly: with -Summary it holds a 'JIT compiled ...' line per method whether or not the requested one
# matched or was inlined. Success needs a listing.
$jitText = if (Test-Path -LiteralPath $jitFile) { Get-Content -LiteralPath $jitFile -Raw } else { '' }
$check = Test-QualityJitOutput -Text $jitText -Method $Method
if (-not $check.Ok) { Stop-Jit "$($check.Reason) Output: $jitFile" }
"; $(if ($FinalCode) { 'TieredCompilation=0 (final code)' } else { 'tiered: Tier-0 first, optimized after tier-up' }), $Configuration build, $($check.Listings) listing(s): $($check.Names -join ', '); file $jitFile"
$jitText
exit 0
