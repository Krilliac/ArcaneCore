# Flake hunter: builds a test project once, then runs a filtered test selection N times with `dotnet test --no-build` and counts
# pass/fail PER TEST FROM THE TRX XML, never from console text (a console-scraping loop once reported "failed=0" over a failed run).
#   pwsh -NoProfile -File tools/ops/ci/repeat-test.ps1 -Project tests/ArcaneCore.World.Tests -Filter "FullyQualifiedName~Siege" -Runs 20 [-Load 8] [-Parallel -Concurrency 4] [-StopOnFirstFailure] [-OutDir <dir>] [-Slot [-LanesRoot <dir>]]
# - A run is ERROR (never a pass) when its TRX is missing or unreadable, when it executed 0 tests (the filter matched nothing, or every
#   match was skipped), when dotnet exited non-zero although no test failed (host crash, aborted run), or on timeout.
# - -Load N starts N CPU burner processes for the whole session. Only processes this script started are tracked and killed (process tree),
#   in a finally block, so Ctrl+C and failures clean up. A burner also exits by itself when this script's process is gone.
# - -Parallel runs up to -Concurrency dotnet test processes at once (the tests themselves load the machine). Tests that bind fixed ports
#   or share files will collide; use it for tests that are isolated.
# - -Slot wraps each run in the fleet's run-in-slot.ps1 (the fleet's build-slot cap) when that file exists; else runs directly.
# - Each run gets ARCANECORE_REPEAT_RUN_INDEX=<n> (1-based) in its environment, so a test can vary its seed per run.
# - Writes <OutDir>\summary.json plus per-run run-NNNN\ (TRX, stdout.txt, stderr.txt). Last line: `runs=N ok=A failed=B error=C`.
# Exit code: 0 = every run executed tests and none failed; 1 = a test failed (and no run was ERROR); 2 = any run was ERROR or the build failed.
param(
    [Parameter(Mandatory)] [string]$Project,
    [Parameter(Mandatory)] [string]$Filter,
    [ValidateRange(1, 100000)] [int]$Runs = 10,
    [ValidateRange(0, 256)] [int]$Load = 0,
    [switch]$Parallel,
    [ValidateRange(1, 64)] [int]$Concurrency = 4,
    [switch]$StopOnFirstFailure,
    [string]$OutDir,
    [switch]$Slot,
    [string]$LanesRoot,
    [string]$Configuration = 'Debug',
    [ValidateRange(1, 1440)] [int]$TimeoutMin = 30
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$projectPath = if ([IO.Path]::IsPathRooted($Project)) { $Project } else { Join-Path $repoRoot $Project }
if (-not (Test-Path -LiteralPath $projectPath)) { [Console]::Error.WriteLine("Project not found: $projectPath"); exit 2 }
$projectPath = (Resolve-Path -LiteralPath $projectPath).Path
# Build and run in the checkout that owns the project, not the one this script came from,
# and say which one, so a run against the wrong worktree is visible.
$projectDir = if (Test-Path -LiteralPath $projectPath -PathType Container) { $projectPath } else { Split-Path -Parent $projectPath }
$ownerRoot = (& git -C $projectDir rev-parse --show-toplevel 2>$null)
if ($LASTEXITCODE -eq 0 -and $ownerRoot) { $repoRoot = (Resolve-Path -LiteralPath $ownerRoot).Path }
$ownerHead = (& git -C $repoRoot rev-parse --short HEAD 2>$null)
$ownerDirty = @(& git -C $repoRoot status --porcelain 2>$null).Count
Write-Host "repeat-test: project $projectPath; checkout $repoRoot HEAD $ownerHead dirtyFiles=$ownerDirty"
if (-not $OutDir) { $OutDir = Join-Path ([IO.Path]::GetTempPath()) ('repeat-test-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path -LiteralPath $OutDir).Path

$dotnetRoot = $env:DOTNET_ROOT
$dotnet = if ($dotnetRoot -and (Test-Path (Join-Path $dotnetRoot 'dotnet.exe'))) { Join-Path $dotnetRoot 'dotnet.exe' }
          elseif ($dotnetRoot -and (Test-Path (Join-Path $dotnetRoot 'dotnet'))) { Join-Path $dotnetRoot 'dotnet' }
          else { 'dotnet' }
if (-not $LanesRoot) { $LanesRoot = $env:ARCANECORE_LANES_ROOT }
$slotScript = if ($LanesRoot) { Join-Path $LanesRoot 'tools\run-in-slot.ps1' } else { '' }
$useSlot = $Slot.IsPresent -and $slotScript -and (Test-Path -LiteralPath $slotScript)
if ($Slot -and -not $LanesRoot) { Write-Warning "-Slot requested but no lanes root is set (give -LanesRoot or set ARCANECORE_LANES_ROOT); running dotnet directly." }
elseif ($Slot -and -not $useSlot) { Write-Warning "-Slot requested but $slotScript does not exist; running dotnet directly." }
$concurrency = if ($Parallel) { $Concurrency } else { 1 }
$pwshPath = (Get-Process -Id $PID).Path

function ConvertTo-PwshLiteral([string]$s) { "'" + $s.Replace("'", "''") + "'" }

# Starts dotnet (directly, or through the slot wrapper) with both output streams read asynchronously from the start, so a full pipe
# can never stall the child. Returns a handle; the caller owns the process.
function Start-DotnetProcess {
    param([string[]]$DotnetArgs, [hashtable]$EnvVars, [string]$Tag)
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.WorkingDirectory = $repoRoot
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    if ($useSlot) {
        $assign = ($EnvVars.GetEnumerator() | ForEach-Object { "`$env:$($_.Key)=$(ConvertTo-PwshLiteral ([string]$_.Value)); " }) -join ''
        $cmd = $assign + '& ' + (ConvertTo-PwshLiteral $dotnet) + ' ' + (($DotnetArgs | ForEach-Object { ConvertTo-PwshLiteral $_ }) -join ' ')
        $psi.FileName = $pwshPath
        foreach ($a in @('-NoProfile', '-File', $slotScript, '-Lane', "repeat-$Tag", '-WorkDir', $repoRoot, '-Command', $cmd, '-TimeoutMin', "$TimeoutMin")) { $psi.ArgumentList.Add($a) }
    } else {
        $psi.FileName = $dotnet
        foreach ($a in $DotnetArgs) { $psi.ArgumentList.Add($a) }
        foreach ($kv in $EnvVars.GetEnumerator()) { $psi.Environment[$kv.Key] = [string]$kv.Value }
    }
    if ($dotnetRoot) { $psi.Environment['DOTNET_ROOT'] = $dotnetRoot }
    $p = [Diagnostics.Process]::new()
    $p.StartInfo = $psi
    [void]$p.Start()
    [pscustomobject]@{ Process = $p; Out = $p.StandardOutput.ReadToEndAsync(); Err = $p.StandardError.ReadToEndAsync(); Started = [DateTime]::UtcNow; TimedOut = $false }
}

function Stop-Tree([Diagnostics.Process]$p) {
    try { if (-not $p.HasExited) { $p.Kill($true) } } catch { }
}

# Everything it reports comes from the TRX files in one results directory.
function Read-TrxResults([string]$Dir) {
    $r = [ordered]@{ TrxFound = $false; ParseError = $null; Total = 0; Passed = 0; Failed = 0; Skipped = 0; Tests = @() }
    $files = @(Get-ChildItem -LiteralPath $Dir -Filter '*.trx' -Recurse -File -ErrorAction SilentlyContinue)
    if ($files.Count -eq 0) { return $r }
    $r.TrxFound = $true
    $tests = [Collections.Generic.List[object]]::new()
    foreach ($f in $files) {
        try {
            $doc = [Xml.XmlDocument]::new()
            $doc.Load($f.FullName)
            $ns = [Xml.XmlNamespaceManager]::new($doc.NameTable)
            $ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
            foreach ($n in $doc.SelectNodes('//t:UnitTestResult', $ns)) {
                $msgNode = $n.SelectSingleNode('t:Output/t:ErrorInfo/t:Message', $ns)
                $stkNode = $n.SelectSingleNode('t:Output/t:ErrorInfo/t:StackTrace', $ns)
                $tests.Add([pscustomobject]@{
                    Name = [string]$n.GetAttribute('testName'); Outcome = [string]$n.GetAttribute('outcome')
                    Message = if ($msgNode) { $msgNode.InnerText.Trim() } else { $null }
                    Stack = if ($stkNode) { $stkNode.InnerText.Trim() } else { $null }
                })
            }
        } catch { $r.ParseError = "$($f.Name): $($_.Exception.Message)"; return $r }
    }
    foreach ($t in $tests) {
        $r.Total++
        if ($t.Outcome -eq 'Passed') { $r.Passed++ }
        elseif ($t.Outcome -in @('Failed', 'Error', 'Timeout', 'Aborted')) { $r.Failed++ }
        else { $r.Skipped++ }
    }
    $r.Tests = $tests.ToArray()
    return $r
}

function Get-StackTop([string]$stack) {
    if (-not $stack) { return $null }
    foreach ($line in ($stack -split "`r?`n")) { if ($line.Trim().Length -gt 0) { return $line.Trim() } }
    return $null
}

function Complete-Run($h, [int]$Index, [string]$RunDir) {
    $p = $h.Process
    $p.WaitForExit()
    $stdout = $h.Out.GetAwaiter().GetResult(); $stderr = $h.Err.GetAwaiter().GetResult()
    Set-Content -LiteralPath (Join-Path $RunDir 'stdout.txt') -Value $stdout -Encoding utf8
    Set-Content -LiteralPath (Join-Path $RunDir 'stderr.txt') -Value $stderr -Encoding utf8
    $trx = Read-TrxResults $RunDir
    $reason = $null
    if ($h.TimedOut) { $reason = "timed out after $TimeoutMin min" }
    elseif (-not $trx.TrxFound) { $reason = "no TRX written (exit code $($p.ExitCode); dotnet crashed or never ran tests)" }
    elseif ($trx.ParseError) { $reason = "TRX unreadable: $($trx.ParseError)" }
    elseif (($trx.Passed + $trx.Failed) -eq 0) { $reason = "0 tests executed (total=$($trx.Total) skipped=$($trx.Skipped)); filter matched nothing or every match was skipped" }
    elseif ($p.ExitCode -ne 0 -and $trx.Failed -eq 0) { $reason = "dotnet exited $($p.ExitCode) although the TRX shows no failed test" }
    $status = if ($reason) { 'ERROR' } elseif ($trx.Failed -gt 0) { 'FAILED' } else { 'OK' }
    [pscustomobject]@{
        Run = $Index; Status = $status; Reason = $reason; ExitCode = $p.ExitCode
        Total = $trx.Total; Passed = $trx.Passed; Failed = $trx.Failed; Skipped = $trx.Skipped
        Seconds = [Math]::Round(([DateTime]::UtcNow - $h.Started).TotalSeconds, 1); Dir = $RunDir; Tests = $trx.Tests
    }
}

$burners = [Collections.Generic.List[Diagnostics.Process]]::new()
$active = [Collections.Generic.List[object]]::new()
$results = [Collections.Generic.List[object]]::new()
$launched = 0
try {
    # Build once.
    "BUILD $projectPath ($Configuration)"
    $bh = Start-DotnetProcess -DotnetArgs @('build', $projectPath, '-c', $Configuration, '-nodeReuse:false', '-v:minimal') -EnvVars @{} -Tag 'build'
    $bh.Process.WaitForExit()
    $bout = $bh.Out.GetAwaiter().GetResult() + $bh.Err.GetAwaiter().GetResult()
    Set-Content -LiteralPath (Join-Path $OutDir 'build.log') -Value $bout -Encoding utf8
    if ($bh.Process.ExitCode -ne 0) {
        $bout -split "`r?`n" | Select-Object -Last 30
        [Console]::Error.WriteLine("BUILD FAILED (exit $($bh.Process.ExitCode)); nothing ran. Log: $OutDir\build.log")
        $failLine = 'runs=0 ok=0 failed=0 error=1'
        [ordered]@{ summary = $failLine; exitCode = 2; buildFailed = $true; project = $projectPath; filter = $Filter } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutDir 'summary.json') -Encoding utf8
        $failLine
        exit 2
    }

    # CPU burners: pwsh loops that also stop when this process is gone.
    $burnerCode = "`$pp=$PID; while(`$true){ for(`$i=0;`$i -lt 3000000;`$i++){`$x=`$i*3}; if(-not (Get-Process -Id `$pp -ErrorAction SilentlyContinue)){ exit 0 } }"
    for ($i = 0; $i -lt $Load; $i++) {
        $burners.Add((Start-Process -FilePath $pwshPath -ArgumentList @('-NoProfile', '-NonInteractive', '-Command', $burnerCode) -PassThru -WindowStyle Hidden))
    }
    if ($Load -gt 0) { "LOAD started $($burners.Count) CPU burner(s): PIDs $(($burners | ForEach-Object Id) -join ',')" }

    $nextIndex = 1
    $stop = $false
    while (($nextIndex -le $Runs -and -not $stop) -or $active.Count -gt 0) {
        while ($nextIndex -le $Runs -and -not $stop -and $active.Count -lt $concurrency) {
            $idx = $nextIndex; $nextIndex++
            $name = 'run-{0:D4}' -f $idx
            $runDir = Join-Path $OutDir $name
            New-Item -ItemType Directory -Force $runDir | Out-Null
            $testArgs = @('test', $projectPath, '--no-build', '-c', $Configuration, '--filter', $Filter,
                          '--logger', "trx;LogFileName=$name.trx", '--results-directory', $runDir)
            $h = Start-DotnetProcess -DotnetArgs $testArgs -EnvVars @{ ARCANECORE_REPEAT_RUN_INDEX = "$idx" } -Tag $name
            $launched++
            $active.Add([pscustomobject]@{ Index = $idx; Dir = $runDir; Handle = $h })
        }
        Start-Sleep -Milliseconds 200
        foreach ($a in @($active)) {
            $p = $a.Handle.Process
            if (-not $p.HasExited -and ([DateTime]::UtcNow - $a.Handle.Started).TotalMinutes -gt $TimeoutMin + 1) { $a.Handle.TimedOut = $true; Stop-Tree $p }
            if ($p.HasExited) {
                [void]$active.Remove($a)
                $res = Complete-Run $a.Handle $a.Index $a.Dir
                $results.Add($res)
                $line = 'RUN {0,4} {1,-6} total={2} passed={3} failed={4} skipped={5} exit={6} {7}s' -f $res.Run, $res.Status, $res.Total, $res.Passed, $res.Failed, $res.Skipped, $res.ExitCode, $res.Seconds
                if ($res.Reason) { $line += "  [$($res.Reason)]" }
                $line
                if ($StopOnFirstFailure -and $res.Status -ne 'OK') { $stop = $true }
            }
        }
    }
} finally {
    foreach ($a in $active) { Stop-Tree $a.Handle.Process }
    foreach ($b in $burners) { Stop-Tree $b }
    if ($burners.Count -gt 0) { "LOAD stopped $($burners.Count) burner(s) started by this script" }
}

# Per-test aggregation, keyed by test name.
$perTest = @{}
foreach ($res in ($results | Sort-Object Run)) {
    foreach ($t in $res.Tests) {
        if ($t.Outcome -notin @('Passed', 'Failed', 'Error', 'Timeout', 'Aborted')) { continue }
        if (-not $perTest.ContainsKey($t.Name)) { $perTest[$t.Name] = [ordered]@{ test = $t.Name; runs = 0; fails = 0; failureRate = 0.0; firstFailRun = $null; firstFailMessage = $null; firstFailStackTop = $null } }
        $e = $perTest[$t.Name]
        $e.runs++
        if ($t.Outcome -ne 'Passed') {
            $e.fails++
            if ($null -eq $e.firstFailRun) { $e.firstFailRun = $res.Run; $e.firstFailMessage = $t.Message; $e.firstFailStackTop = Get-StackTop $t.Stack }
        }
    }
}
foreach ($e in $perTest.Values) { $e.failureRate = [Math]::Round($e.fails / $e.runs, 4) }
$testRows = @($perTest.Values | Sort-Object @{ Expression = { $_.failureRate }; Descending = $true }, @{ Expression = { $_.test } })

''
'PER-TEST'
foreach ($e in $testRows) {
    '{0}  runs={1} fails={2} rate={3:P1}' -f $e.test, $e.runs, $e.fails, $e.failureRate
    if ($e.fails -gt 0) {
        "    first failure in run $($e.firstFailRun): $(($e.firstFailMessage -split "`r?`n" | Select-Object -First 3) -join ' | ')"
        "    stack top: $($e.firstFailStackTop)"
    }
}
$errorRuns = @($results | Where-Object { $_.Status -eq 'ERROR' })
foreach ($er in $errorRuns) { "ERROR run $($er.Run): $($er.Reason)" }

$ok = @($results | Where-Object { $_.Status -eq 'OK' }).Count
$failedRuns = @($results | Where-Object { $_.Status -eq 'FAILED' }).Count
$errCount = $errorRuns.Count
$summaryLine = "runs=$($results.Count) ok=$ok failed=$failedRuns error=$errCount"
$exitCode = if ($errCount -gt 0) { 2 } elseif ($failedRuns -gt 0) { 1 } else { 0 }
[ordered]@{
    summary = $summaryLine; exitCode = $exitCode; project = $projectPath; filter = $Filter; configuration = $Configuration
    requestedRuns = $Runs; launchedRuns = $launched; stoppedEarly = ($launched -lt $Runs); load = $Load; parallel = $Parallel.IsPresent; concurrency = $concurrency; slot = $useSlot
    outDir = $OutDir
    runs = @($results | Sort-Object Run | ForEach-Object { [ordered]@{ run = $_.Run; status = $_.Status; reason = $_.Reason; exitCode = $_.ExitCode; total = $_.Total; passed = $_.Passed; failed = $_.Failed; skipped = $_.Skipped; seconds = $_.Seconds; dir = $_.Dir } })
    tests = $testRows
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutDir 'summary.json') -Encoding utf8
$summaryLine
exit $exitCode
