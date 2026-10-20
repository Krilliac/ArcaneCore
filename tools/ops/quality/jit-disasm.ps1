# Prints the x64 the JIT generates for a method while a test filter runs (DOTNET_JitDisasm), to check what the machine does against what
# the source says.
#   pwsh -NoProfile -File tools/ops/quality/jit-disasm.ps1 -TestProject tests/ArcaneCore.Game.Tests/ArcaneCore.Game.Tests.csproj `
#        -Filter 'FullyQualifiedName~SpeedHackDetectorTests' -Method 'SpeedHackDetector:Evaluate' [-FinalCode] [-Configuration Release] [-Summary] [-NoBuild]
# -Method is a JitDisasm pattern: 'Class:Method', 'Method', or a '*' wildcard ('*Evaluate*'); several are space separated.
# Only the test host sees the DOTNET_ variables: they travel in a generated .runsettings file, so the build, dotnet test and vstest
# processes do not JIT-dump. -FinalCode sets DOTNET_TieredCompilation=0 so each method is compiled once, fully optimized; without it the
# first (Tier-0, unoptimized) compile of a method is what is printed first and the optimized one appears only after the method ran
# 30 times and the background tier-up finished. -Summary also lists every method the JIT compiled (DOTNET_JitDisasmSummary=1).
# Output is JitStdOutFile in artifacts/jit/<timestamp>/jit.txt, printed here. Exit codes: 0 = disassembly printed, 2 = error (build or
# tests failed, the filter ran no test, or no method matched: nothing was printed).
param(
    [Parameter(Mandatory)] [string]$TestProject,
    [Parameter(Mandatory)] [string]$Filter,
    [Parameter(Mandatory)] [string]$Method,
    [ValidateSet('Debug', 'Release')] [string]$Configuration = 'Debug',
    [switch]$FinalCode,
    [switch]$Summary,
    [switch]$NoBuild,
    [string]$OutputRoot
)
$ErrorActionPreference = 'Stop'

function Stop-Jit([string]$Message) {
    [Console]::Error.WriteLine("jit-disasm.ps1: $Message")
    exit 2
}

trap { Stop-Jit "unexpected error: $($_.Exception.Message) at line $($_.InvocationInfo.ScriptLineNumber)" }

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path
$projectPath = if ([IO.Path]::IsPathRooted($TestProject)) { $TestProject } else { Join-Path $repo $TestProject }
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) { Stop-Jit "-TestProject '$TestProject' does not exist (looked for $projectPath)." }
$projectPath = (Resolve-Path -LiteralPath $projectPath).Path

$out = Join-Path ($(if ($OutputRoot) { $OutputRoot } else { Join-Path $repo 'artifacts/jit' })) (Get-Date -Format 'yyyyMMdd-HHmmss')
New-Item -ItemType Directory -Force $out | Out-Null
$jitFile = Join-Path $out 'jit.txt'
$settings = Join-Path $out 'jit.runsettings'

$variables = [ordered]@{ DOTNET_JitDisasm = $Method; DOTNET_JitStdOutFile = $jitFile }
if ($FinalCode) { $variables['DOTNET_TieredCompilation'] = '0' }
if ($Summary) { $variables['DOTNET_JitDisasmSummary'] = '1' }
$escape = { param($s) [Security.SecurityElement]::Escape($s) }
$xml = "<RunSettings><RunConfiguration><EnvironmentVariables>" +
    (($variables.GetEnumerator() | ForEach-Object { "<$($_.Key)>$(& $escape $_.Value)</$($_.Key)>" }) -join '') +
    "</EnvironmentVariables></RunConfiguration></RunSettings>"
Set-Content -LiteralPath $settings -Value $xml -Encoding utf8

if (-not $NoBuild) {
    & dotnet build $projectPath -c $Configuration -nodeReuse:false -p:UseSharedCompilation=false 2>&1 | Tee-Object -FilePath (Join-Path $out 'build.log') | Select-Object -Last 3
    if ($LASTEXITCODE -ne 0) { Stop-Jit "dotnet build failed (log: $(Join-Path $out 'build.log'))." }
}

$testLog = Join-Path $out 'test.log'
& dotnet test $projectPath -c $Configuration --no-build --filter $Filter --settings $settings --logger 'console;verbosity=minimal' 2>&1 | Tee-Object -FilePath $testLog | Select-Object -Last 6
$testExit = $LASTEXITCODE
if ($testExit -ne 0) { Stop-Jit "dotnet test exited $testExit (log: $testLog); a failing test is not a disassembly run." }
if ((Get-Content -LiteralPath $testLog -Raw) -match 'No test matches the given testcase filter') { Stop-Jit "the filter '$Filter' matched no test (log: $testLog)." }

if (-not (Test-Path -LiteralPath $jitFile) -or (Get-Item -LiteralPath $jitFile).Length -eq 0) {
    Stop-Jit "no JIT output for '$Method' in ${jitFile}: the pattern matched no compiled method (inlined into its caller, precompiled ReadyToRun code, never called by the tests, or a wrong 'Class:Method' name)."
}
"; $(if ($FinalCode) { 'TieredCompilation=0 (final code)' } else { 'tiered: Tier-0 first, optimized after tier-up' }), $Configuration build; file $jitFile"
Get-Content -LiteralPath $jitFile
exit 0
