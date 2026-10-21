# Whole-repo mutation testing: Stryker.NET over every source project in batches, resumable, one batch at a time through the build-slot runner.
#   New run:   pwsh -NoProfile -File tools/ops/quality/mutate-all.ps1 [-Projects Cryptography,Kernel] [-Concurrency 2] [-PauseFile D:\mutate.pause]
#   Plan only: pwsh -NoProfile -File tools/ops/quality/mutate-all.ps1 -PlanOnly
#   Resume:    pwsh -NoProfile -File tools/ops/quality/mutate-all.ps1 -Resume [-RunId <id>] [-RetryErrors] [-AllowHeadChange]
#   Report:    pwsh -NoProfile -File tools/ops/quality/mutate-report.ps1 [-RunId <id>]      (also written by this script every few batches and at the end)
# One batch = one source project x one directory (or several small neighbours, or a chunk of one directory's own files), at most -MaxFiles files and
# -MaxLines non-blank lines, run through mutate.ps1 against ONE test project (the project's own <Project>.Tests when it references it, else the
# first test project that references it; World/Playerbots/Protocol: ArcaneCore.World.Tests; -TestOverride @{ Kernel = 'ArcaneCore.World.Tests' } changes it).
# Stryker's default coverage analysis (perTest) is used: each mutant runs only the tests that cover it.
# State: artifacts/mutation/all/<runId>/state.json (batch status pending / running / done / error, report path, git HEAD, counts, reason). A crash or
# reboot loses at most the running batch: -Resume runs it again. -Resume refuses a different git HEAD unless -AllowHeadChange; it skips done batches
# and skips error batches unless -RetryErrors.
# A batch is 'error', never 'done', when mutate.ps1 failed, Stryker judged 0 mutants, found 0 tests, or no test covered any mutant. The aggregate
# (mutate-report.ps1) never counts error batches.
# Exit codes: 0 = every batch done; 1 = finished, some batches are error (or still pending after -MaxBatches); 2 = bad argument / refused resume;
# 3 = stopped early (-MaxConsecutiveErrors errors in a row, or the git HEAD moved during the run).
param(
    [switch]$Resume,
    [string]$RunId,
    [string[]]$Projects,
    [string[]]$BatchFilter,
    [ValidateRange(1, 64)] [int]$Concurrency = 2,
    [ValidateRange(1, 500)] [int]$MaxFiles = 40,
    [ValidateRange(100, 200000)] [int]$MaxLines = 6000,
    [string]$TestFilter,
    [hashtable]$TestOverride,
    [string]$PauseFile,
    [switch]$NoSlot,
    [string]$Lane = 'mutate-all',
    [string]$SlotRunner = 'D:\ArcaneCore-lanes\tools\run-in-slot.ps1',
    [ValidateRange(1, 1440)] [int]$BatchTimeoutMin = 180,
    [switch]$RetryErrors,
    [switch]$AllowHeadChange,
    [switch]$PlanOnly,
    [ValidateRange(0, 100000)] [int]$MaxBatches = 0,
    [ValidateRange(1, 1000)] [int]$MaxConsecutiveErrors = 5,
    [string]$OutputRoot,
    [switch]$NoReport
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'QualityLib.ps1')
. (Join-Path $PSScriptRoot 'MutateAllLib.ps1')

function Stop-MutateAll([string]$Message, [int]$Code = 2) {
    [Console]::Error.WriteLine("mutate-all.ps1: $Message")
    exit $Code
}
trap { Stop-MutateAll "unexpected error: $($_.Exception.Message) at line $($_.InvocationInfo.ScriptLineNumber)" }
function Write-Log([string]$Message) { "[{0:yyyy-MM-dd HH:mm:ss}] {1}" -f (Get-Date), $Message }

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path
$mutateScript = Join-Path $PSScriptRoot 'mutate.ps1'
$reportScript = Join-Path $PSScriptRoot 'mutate-report.ps1'

# The repo pins a .NET 11 preview SDK (global.json) that lives in the user's own dotnet root.
$userDotnet = Join-Path $env:USERPROFILE '.dotnet'
if (Test-Path (Join-Path $userDotnet 'dotnet.exe')) { $env:DOTNET_ROOT = $userDotnet; $env:PATH = "$userDotnet;$env:PATH" }

function Get-Head { $h = (& git -C $repo rev-parse HEAD 2>&1); if ($LASTEXITCODE -ne 0) { Stop-MutateAll "git rev-parse HEAD failed: $h" }; "$h".Trim() }
function Test-Dirty { [bool](@(& git -C $repo status --porcelain -- src tests) | Where-Object { $_ }) }

$root = if ($OutputRoot) { $OutputRoot } else { Join-Path $repo 'artifacts/mutation/all' }
$head = Get-Head

# ---- plan or resume -------------------------------------------------------------------------------------------------------------------
if ($Resume) {
    if (-not $RunId) {
        $latest = Get-ChildItem $root -Directory -ErrorAction SilentlyContinue | Where-Object { Test-Path (Join-Path $_.FullName 'state.json') } | Sort-Object Name | Select-Object -Last 1
        if (-not $latest) { Stop-MutateAll "-Resume: no run with a state.json under $root." }
        $RunId = $latest.Name
    }
    $runDir = Join-Path $root $RunId
    if (-not (Test-Path (Join-Path $runDir 'state.json'))) { Stop-MutateAll "-Resume: $runDir has no state.json." }
    $state = Read-MutateAllState $runDir
    if ($state.head -ne $head) {
        if (-not $AllowHeadChange) {
            Stop-MutateAll "-Resume: the run started at git HEAD $($state.head) but HEAD is now $head. Check the old HEAD out again, or pass -AllowHeadChange (batches already done keep the HEAD they ran at)."
        }
        $state.headHistory = @($state.headHistory) + @("$head at $([DateTime]::UtcNow.ToString('s'))Z")
    }
}
else {
    $plan = New-MutateAllPlan -Root $repo -MaxFiles $MaxFiles -MaxLines $MaxLines -Projects $Projects -TestOverride $TestOverride
    $batches = @($plan.Batches)
    if ($BatchFilter) { $batches = @($batches | Where-Object { $b = $_; @($BatchFilter | Where-Object { $b["id"] -like $_ -or $b["name"] -like $_ }).Count -gt 0 }) }
    if ($batches.Count -eq 0) { Stop-MutateAll 'the plan has no batch (check -Projects / -BatchFilter).' }
    if ($PlanOnly) {
        "Batch plan: $($batches.Count) batches, $(($batches | ForEach-Object { $_['lines'] } | Measure-Object -Sum).Sum) non-blank lines, caps $MaxFiles files / $MaxLines lines"
        foreach ($g in ($batches | Group-Object { $_['project'] })) {
            "  {0,-30} {1,4} batches  {2,7} lines  tests: {3}" -f $g.Name, $g.Count, (($g.Group | ForEach-Object { $_['lines'] } | Measure-Object -Sum).Sum), $g.Group[0]['testProject']
        }
        foreach ($b in $batches) { "    {0,-18} {1,3} files {2,6} lines  {3}" -f $b['id'], $b['files'], $b['lines'], $b['name'] }
        if ($plan.Unmapped.Count) { "  no test project reaches: $($plan.Unmapped -join ', ')" }
        "  excluded generated files: $($plan.SkippedFiles.Count)"
        exit 0
    }
    if (-not $RunId) { $RunId = Get-Date -Format 'yyyyMMdd-HHmmss' }
    $runDir = Join-Path $root $RunId
    if (Test-Path (Join-Path $runDir 'state.json')) { Stop-MutateAll "run $RunId already exists; use -Resume." }
    New-Item -ItemType Directory -Force $runDir | Out-Null
    $state = [ordered]@{
        runId = $RunId; createdUtc = [DateTime]::UtcNow.ToString('s') + 'Z'; head = $head; headHistory = @(); repo = $repo
        settings = [ordered]@{ concurrency = $Concurrency; maxFiles = $MaxFiles; maxLines = $MaxLines; testFilter = $TestFilter; batchTimeoutMin = $BatchTimeoutMin; slot = (-not $NoSlot); lane = $Lane }
        unmapped = @($plan.Unmapped); skippedFiles = @($plan.SkippedFiles)
        batches = $batches
    }
    Save-MutateAllState $state $runDir
    Write-Log "new run $RunId at HEAD ${head}: $($batches.Count) batches in $runDir"
}

# ---- single-driver lock -------------------------------------------------------------------------------------------------------------
$lockPath = Join-Path $runDir 'driver.lock'
if (Test-Path $lockPath) {
    $pidText = (Get-Content $lockPath -ErrorAction SilentlyContinue | Select-Object -First 1)
    if ($pidText -match '^\d+$' -and (Get-Process -Id ([int]$pidText) -ErrorAction SilentlyContinue)) { Stop-MutateAll "another driver (pid $pidText) is running this run." }
    Remove-Item $lockPath -Force -ErrorAction SilentlyContinue
}
Set-Content -LiteralPath $lockPath -Value "$PID"

$settings = $state.settings
$settings['lastStartUtc'] = [DateTime]::UtcNow.ToString('s') + 'Z'
if ($Resume -and $TestFilter) { $settings['testFilter'] = $TestFilter }
$batchList = $state.batches
$startedHere = 0; $consecutiveErrors = 0; $exitCode = 0; $sinceReport = 0

# A batch left 'running' by a crash is run again; -RetryErrors puts the error batches back too.
foreach ($b in $batchList) {
    if ($b.status -eq 'running') { $b.status = 'pending'; $b.reason = 'rerun after an interrupted attempt' }
    elseif ($b.status -eq 'error' -and $RetryErrors) { $b.status = 'pending' }
}
Save-MutateAllState $state $runDir

function Invoke-MutateAllBatch($b) {
    $outDir = Join-Path $runDir "batches/$($b.id)"
    if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
    New-Item -ItemType Directory -Force $outDir | Out-Null
    $mutateOut = Join-Path $outDir 'mutate.out'
    $quote = { param($s) "'" + ($s -replace "'", "''") + "'" }
    $mutateArgs = @(
        '-Project', (& $quote "src/$($b.project)/$($b.project).csproj"),
        '-TestProject', (& $quote "tests/$($b.testProject)/$($b.testProject).csproj"),
        '-Files', (& $quote (Get-MutateAllBatchFileArguments $b)),
        '-Concurrency', "$($settings.concurrency)", '-TimeoutMin', "$($settings.batchTimeoutMin)",
        '-OutputRoot', (& $quote $outDir))
    if ($settings.testFilter) { $mutateArgs += @('-TestFilter', (& $quote $settings.testFilter)) }
    $command = "pwsh -NoProfile -File $(& $quote $mutateScript) $($mutateArgs -join ' ') 2>&1 | Tee-Object -FilePath $(& $quote $mutateOut)"
    $slotOut = Join-Path $outDir 'slot.out'
    if ($settings.slot) {
        & pwsh -NoProfile -File $SlotRunner -Lane $Lane -WorkDir $repo -Command $command -TimeoutMin ($settings.batchTimeoutMin + 20) *> $slotOut
        return $LASTEXITCODE
    }
    $script = "Set-Location -LiteralPath '$repo'`n$command`nexit `$LASTEXITCODE"
    $enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
    $p = Start-Process pwsh -ArgumentList '-NoProfile', '-NonInteractive', '-EncodedCommand', $enc -PassThru -NoNewWindow -RedirectStandardOutput $slotOut -RedirectStandardError "$slotOut.err"
    if (-not $p.WaitForExit(($settings.batchTimeoutMin + 20) * 60 * 1000)) { & taskkill.exe /T /F /PID $p.Id | Out-Null; return 124 }
    $p.WaitForExit()
    return $p.ExitCode
}

function Complete-MutateAllBatch($b, [int]$rc) {
    $outDir = Join-Path $runDir "batches/$($b.id)"
    $b.exitCode = $rc
    $b.outDir = $outDir
    $mutateOut = Join-Path $outDir 'mutate.out'
    $reportFile = Get-ChildItem $outDir -Recurse -Filter 'mutation-report.json' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
    $strykerLog = Get-ChildItem $outDir -Recurse -Filter 'stryker.log' -File -ErrorAction SilentlyContinue | Select-Object -Last 1
    $failure = $null
    if ($rc -eq 124) { $failure = "timed out (batch limit $($settings.batchTimeoutMin) min); the Stryker tree was killed" }
    elseif ($rc -ne 0) {
        $lastMessage = $null
        if (Test-Path $mutateOut) { $lastMessage = Select-String -Path $mutateOut -Pattern '^mutate\.ps1:' | Select-Object -Last 1 }
        $failure = if ($lastMessage) { "mutate.ps1 exit ${rc}: $($lastMessage.Line.Substring(12).Trim())" } else { "mutate.ps1 / slot runner exit $rc (see $outDir)" }
    }
    elseif (-not $reportFile) { $failure = 'no mutation-report.json was written' }
    elseif ($strykerLog -and (Select-String -Path $strykerLog.FullName -Pattern 'Number of tests found:\s*0\b' -Quiet)) { $failure = 'Stryker found 0 tests ("Number of tests found: 0")' }

    $b.counts = $null; $b.report = $null; $b.summary = $null
    if ($reportFile) {
        $b.report = $reportFile.FullName
        try {
            $parsed = Get-Content -LiteralPath $reportFile.FullName -Raw | ConvertFrom-Json
            $summary = Get-MutateAllBatchSummary -Report $parsed -Root $repo -ProjectRelativeDirectory "src/$($b.project)"
            $summaryPath = Join-Path $outDir 'batch-summary.json'
            Write-MutateAllJson -Object $summary -Path $summaryPath -Depth 6
            $b.summary = $summaryPath
            $b.counts = [ordered]@{}
            foreach ($p in $summary.Counts.PSObject.Properties) { $b.counts[$p.Name] = $p.Value }
            if (-not $failure) {
                $j = Get-MutateAllJudgement -Counts $summary.Counts
                if ($j.Status -eq 'error') { $failure = $j.Reason }
            }
        }
        catch { if (-not $failure) { $failure = "could not read the report: $($_.Exception.Message)" } }
    }
    if ($failure) { $b.status = 'error'; $b.reason = $failure } else { $b.status = 'done'; $b.reason = $null }
}

# ---- the loop -------------------------------------------------------------------------------------------------------------------------
try {
    foreach ($b in $batchList) {
        if ($b.status -ne 'pending') { continue }
        if ($MaxBatches -gt 0 -and $startedHere -ge $MaxBatches) { break }
        if ($PauseFile) {
            $announced = $false
            while (Test-Path -LiteralPath $PauseFile) {
                if (-not $announced) { Write-Log "pause file $PauseFile exists: waiting before $($b.id)"; $announced = $true }
                Start-Sleep -Seconds 30
            }
            if ($announced) { Write-Log 'pause file gone: continuing' }
        }
        $now = Get-Head
        if ($now -ne $state.head -and -not $AllowHeadChange) {
            Write-Log "git HEAD moved from $($state.head) to $now during the run: stopping (batches done so far stay valid). Use -Resume -AllowHeadChange to continue."
            $exitCode = 3; break
        }
        $b.head = $now; $b.dirty = Test-Dirty; $b.attempts = [int]$b.attempts + 1
        $b.status = 'running'; $b.startedUtc = [DateTime]::UtcNow.ToString('s') + 'Z'; $b.endedUtc = $null; $b.reason = $null
        Save-MutateAllState $state $runDir
        $startedHere++
        Write-Log "batch $($b.id) [$($b.name)] $($b.files) files, $($b.lines) lines, tests $($b.testProject)$(if ($b.dirty) { ' (working tree has uncommitted changes)' })"
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $rc = 1
        try { $rc = [int](Invoke-MutateAllBatch $b) }
        catch { $rc = 1; Add-Content (Join-Path $runDir 'driver.err') "$($b.id): $($_.Exception.Message)" }
        Complete-MutateAllBatch $b $rc
        $b.durationSec = [math]::Round($watch.Elapsed.TotalSeconds, 1)
        $b.endedUtc = [DateTime]::UtcNow.ToString('s') + 'Z'
        Save-MutateAllState $state $runDir
        if ($b.status -eq 'done') {
            $consecutiveErrors = 0
            Write-Log "  done in $(Format-MutateAllDuration $b.durationSec): killed $($b.counts.Killed) survived $($b.counts.Survived) no-coverage $($b.counts.NoCoverage) timeout $($b.counts.Timeout) compile-error $($b.counts.CompileError)"
        }
        else {
            $consecutiveErrors++
            Write-Log "  ERROR after $(Format-MutateAllDuration $b.durationSec): $($b.reason)"
        }
        $sinceReport++
        if (-not $NoReport -and $sinceReport -ge 5) { $sinceReport = 0; & pwsh -NoProfile -File $reportScript -RunDir $runDir -Quiet }
        if ($consecutiveErrors -ge $MaxConsecutiveErrors) {
            Write-Log "$consecutiveErrors batches in a row ended in error: stopping (the environment is probably broken). Fix it, then -Resume -RetryErrors."
            $exitCode = 3; break
        }
    }
}
finally {
    Remove-Item $lockPath -Force -ErrorAction SilentlyContinue
    Save-MutateAllState $state $runDir
    if (-not $NoReport) { & pwsh -NoProfile -File $reportScript -RunDir $runDir -Quiet }
}

$count = { param($s) @($batchList | Where-Object { $_.status -eq $s }).Count }
Write-Log "run $($state.runId): done $(& $count 'done'), error $(& $count 'error'), pending $(& $count 'pending') of $($batchList.Count). Report: $(Join-Path $runDir 'SUMMARY.md')"
if ($exitCode -ne 0) { exit $exitCode }
if ((& $count 'error') -gt 0 -or (& $count 'pending') -gt 0) { exit 1 }
exit 0
