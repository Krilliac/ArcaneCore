# Aggregates a whole-repo mutation run (mutate-all.ps1) into <run>/SUMMARY.md, survivors.csv and directories.csv. Works on a partial run.
#   pwsh -NoProfile -File tools/ops/quality/mutate-report.ps1 [-RunId <id> | -RunDir <path>] [-OutputRoot <artifacts/mutation/all>] [-Quiet]
# Only batches with status 'done' are counted; error batches are listed with their reason and never counted as covered.
# Exit codes: 0 = written; 2 = no such run.
param([string]$RunId, [string]$RunDir, [string]$OutputRoot, [switch]$Quiet)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'QualityLib.ps1')
. (Join-Path $PSScriptRoot 'MutateAllLib.ps1')
trap { [Console]::Error.WriteLine("mutate-report.ps1: $($_.Exception.Message) at line $($_.InvocationInfo.ScriptLineNumber)"); exit 2 }

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path
$root = if ($OutputRoot) { $OutputRoot } else { Join-Path $repo 'artifacts/mutation/all' }
if (-not $RunDir) {
    if (-not $RunId) {
        $latest = Get-ChildItem $root -Directory -ErrorAction SilentlyContinue | Where-Object { Test-Path (Join-Path $_.FullName 'state.json') } | Sort-Object Name | Select-Object -Last 1
        if (-not $latest) { throw "no run with a state.json under $root" }
        $RunId = $latest.Name
    }
    $RunDir = Join-Path $root $RunId
}
$state = Read-MutateAllState $RunDir
$batches = @($state.batches)

# ---- merge the summaries of the done batches ----
$fileRows = @{}
$survivors = [System.Collections.Generic.List[object]]::new()
foreach ($b in $batches) {
    if ($b.status -ne 'done' -or -not $b.summary -or -not (Test-Path -LiteralPath $b.summary)) { continue }
    $s = Get-Content -LiteralPath $b.summary -Raw | ConvertFrom-Json
    foreach ($f in $s.Files) { if ($f.Killed + $f.Survived + $f.NoCoverage + $f.Timeout + $f.CompileError -gt 0) { $fileRows[$f.File] = $f } }
    foreach ($v in $s.Survivors) { $survivors.Add([pscustomobject]@{ File = $v.File; Line = $v.Line; Column = $v.Column; Status = $v.Status; Mutator = $v.Mutator; Original = $v.Original; Mutated = $v.Mutated; Batch = $b.id }) }
}

function Add-Row($table, [string]$key, $f) {
    if (-not $table.ContainsKey($key)) { $table[$key] = [pscustomobject]@{ Name = $key; Killed = 0; Survived = 0; NoCoverage = 0; Timeout = 0; CompileError = 0 } }
    $r = $table[$key]
    $r.Killed += $f.Killed; $r.Survived += $f.Survived; $r.NoCoverage += $f.NoCoverage; $r.Timeout += $f.Timeout; $r.CompileError += $f.CompileError
}
$byProject = @{}; $byTop = @{}; $byDir = @{}
foreach ($f in $fileRows.Values) {
    $parts = $f.Dir.Split('/')                       # src, ArcaneCore.X, dir...
    $project = if ($parts.Count -ge 2) { $parts[1] } else { $f.Dir }
    $top = if ($parts.Count -ge 3) { "$project/$($parts[2])" } else { "$project/(root)" }
    Add-Row $byProject $project $f; Add-Row $byTop $top $f; Add-Row $byDir $f.Dir $f
}
function Get-Score($r) { Get-MutateAllScore -Killed $r.Killed -Timeout $r.Timeout -Survived $r.Survived -NoCoverage $r.NoCoverage }
function Format-Score($r) { $s = Get-Score $r; if ($null -eq $s) { 'n/a' } else { "$s%" } }
function Add-Table($lines, $rows, [string]$header) {
    $lines.Add("| $header | killed | survived | no-coverage | timeout | compile-error | score |")
    $lines.Add('|---|---:|---:|---:|---:|---:|---:|')
    foreach ($r in $rows) { $lines.Add("| $($r.Name) | $($r.Killed) | $($r.Survived) | $($r.NoCoverage) | $($r.Timeout) | $($r.CompileError) | $(Format-Score $r) |") }
}
$total = [pscustomobject]@{ Name = 'TOTAL'; Killed = 0; Survived = 0; NoCoverage = 0; Timeout = 0; CompileError = 0 }
foreach ($r in $byProject.Values) { $total.Killed += $r.Killed; $total.Survived += $r.Survived; $total.NoCoverage += $r.NoCoverage; $total.Timeout += $r.Timeout; $total.CompileError += $r.CompileError }
$projectOrder = Get-MutateAllProjectOrder
$projectRows = @($byProject.Values | Sort-Object { $i = [array]::IndexOf($projectOrder, $_.Name); if ($i -lt 0) { 99 } else { $i } })

# ---- progress and projection ----
$done = @($batches | Where-Object { $_.status -eq 'done' }); $errors = @($batches | Where-Object { $_.status -eq 'error' })
$running = @($batches | Where-Object { $_.status -eq 'running' }); $pending = @($batches | Where-Object { $_.status -eq 'pending' })
$activeSec = [double](($batches | Where-Object { $_.durationSec } | ForEach-Object { [double]$_.durationSec } | Measure-Object -Sum).Sum)
$created = ([DateTime]$state.createdUtc).ToUniversalTime()
$wallSec = ([DateTime]::UtcNow - $created).TotalSeconds
$doneLines = [double](($done | ForEach-Object { [double]$_.lines } | Measure-Object -Sum).Sum)
$doneSec = [double](($done | ForEach-Object { [double]$_.durationSec } | Measure-Object -Sum).Sum)
$globalRate = if ($doneLines -gt 0) { $doneSec / $doneLines } else { $null }
$remainingSec = 0.0; $unprojected = 0
foreach ($b in @($pending) + @($running)) {
    $same = @($done | Where-Object { $_.project -eq $b.project })
    $sl = [double](($same | ForEach-Object { [double]$_.lines } | Measure-Object -Sum).Sum)
    $rate = if ($sl -gt 0) { [double](($same | ForEach-Object { [double]$_.durationSec } | Measure-Object -Sum).Sum) / $sl } else { $globalRate }
    if ($null -eq $rate) { $unprojected++ } else { $remainingSec += $rate * [double]$b.lines }
}
$judged = $total.Killed + $total.Survived + $total.NoCoverage + $total.Timeout

$md = [System.Collections.Generic.List[string]]::new()
$md.Add("# Mutation run $($state.runId)")
$md.Add('')
$md.Add("Git HEAD at start: ``$($state.head)``. Generated $([DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm'))Z. Only batches with status done are counted below.")
$md.Add('')
$md.Add('## Progress')
$md.Add('')
$md.Add("- Batches: **$($done.Count) done** / $($batches.Count) total ($($errors.Count) error, $($running.Count) running, $($pending.Count) pending)")
$md.Add("- Mutants judged so far (killed + survived + no-coverage + timeout): **$judged** ($($total.CompileError) compile-error mutants not counted)")
$md.Add("- Elapsed: $(Format-MutateAllDuration $wallSec) since the run was created; $(Format-MutateAllDuration $activeSec) of batch run time")
if ($pending.Count + $running.Count -eq 0) { $md.Add('- Remaining: nothing pending.') }
elseif ($null -eq $globalRate) { $md.Add('- Projected remaining time: not available yet (no batch is done).') }
else {
    $md.Add("- Projected remaining time: **$(Format-MutateAllDuration $remainingSec)** for $($pending.Count + $running.Count) batches (rate so far: $([math]::Round($globalRate * 1000, 1)) s per 1000 non-blank source lines over $($done.Count) done batches; a project with a done batch uses its own rate, others the global one; the small projects run first, so the early rate understates Game and World)")
    if ($unprojected -gt 0) { $md.Add("- $unprojected pending batches could not be projected.") }
}
$md.Add('')
$md.Add('## Per project')
$md.Add('')
Add-Table $md (@($projectRows) + @($total)) 'project'
$md.Add('')
$md.Add('## Per top-level directory')
$md.Add('')
Add-Table $md @($byTop.Values | Sort-Object Name) 'directory'
$md.Add('')
$md.Add('## Top 50 directories by survivors (survived + no-coverage)')
$md.Add('')
$top50 = @($byDir.Values | Where-Object { $_.Survived + $_.NoCoverage -gt 0 } | Sort-Object @{ Expression = { $_.Survived + $_.NoCoverage }; Descending = $true }, Name | Select-Object -First 50)
Add-Table $md $top50 'directory'
$md.Add('')
$md.Add("Every directory is in directories.csv; the $($survivors.Count) survivors (file, line, mutator, original, mutated) are in survivors.csv.")
$md.Add('')
$md.Add("## Error batches ($($errors.Count))")
$md.Add('')
if ($errors.Count -eq 0) { $md.Add('None.') }
else {
    $md.Add('| batch | name | reason | folder |')
    $md.Add('|---|---|---|---|')
    foreach ($b in $errors) { $md.Add("| $($b.id) | $($b.name -replace '\|', '/') | $((([string]$b.reason) -replace '\|', '/') -replace '\r?\n', ' ') | $($b.outDir) |") }
    $md.Add('')
    $md.Add('Error batches contribute nothing to the tables above. Re-run them with `-Resume -RetryErrors`.')
}
if ($running.Count -gt 0) { $md.Add(''); $md.Add("Running: $(($running | ForEach-Object { $_.id }) -join ', ')") }
if (@($state.unmapped).Count -gt 0) { $md.Add(''); $md.Add("No test project reaches: $(@($state.unmapped) -join ', ') (not mutated).") }
$md | Set-Content -LiteralPath (Join-Path $RunDir 'SUMMARY.md') -Encoding utf8

$survivors | Sort-Object File, Line, Column | Export-Csv -LiteralPath (Join-Path $RunDir 'survivors.csv') -NoTypeInformation -Encoding utf8
$byDir.Values | Sort-Object Name | Select-Object @{n = 'directory'; e = { $_.Name } }, Killed, Survived, NoCoverage, Timeout, CompileError, @{n = 'score'; e = { Get-Score $_ } } |
    Export-Csv -LiteralPath (Join-Path $RunDir 'directories.csv') -NoTypeInformation -Encoding utf8
if (-not $Quiet) { $md }
exit 0
