<#
.SYNOPSIS
  Capture a full memory dump of a running ArcaneCore daemon and write a heap summary next to it.

.DESCRIPTION
  Uses dotnet-dump (dotnet tool install -g dotnet-dump). Only --type Full works on the live
  world (verified 2026-10-11): dotnet-gcdump fails on heaps this size ("lossy buffering"), and
  --type Heap fails with E_INVALIDARG from MiniDumpWriteDump. A full dump suspends the process
  for roughly 1 minute and is about the size of its working set (7.4 GB at a 6 GB working set).
  Dumps contain session keys and passwords in memory: keep them local, never upload them.

  The summary (summary.txt) holds: eeheap -gc, the whole heap by type (top 40 by size),
  the large-object heap by type (live and all), so that LOH fragmentation and churn are visible.

.PARAMETER ProcessId   The daemon's pid (World: the dotnet.exe running ArcaneCore.World.dll).
.PARAMETER OutDir      Directory to write into. Default: <$env:ARCANECORE_LANES_ROOT>\_diag\<yyyyMMdd>.
.PARAMETER SummaryOnly Skip the capture and summarise an existing dump given by -Dump.
#>
param(
    [int]$ProcessId,
    [string]$OutDir = '',
    [string]$Dump,
    [switch]$SummaryOnly
)
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'OpsEnv.ps1')

if ([string]::IsNullOrWhiteSpace($OutDir)) {
    if ($SummaryOnly -and $Dump) {
        $OutDir = Split-Path -Parent $Dump
    } else {
        $lanesRoot = Get-OpsRequiredEnv -Name 'ARCANECORE_LANES_ROOT' -Purpose 'the directory that holds _diag'
        $OutDir = Join-Path (Join-Path $lanesRoot '_diag') (Get-Date -Format 'yyyyMMdd')
    }
}

$tool = Join-Path $env:USERPROFILE '.dotnet\tools\dotnet-dump.exe'
if (-not (Test-Path $tool)) { throw "dotnet-dump not found at $tool (dotnet tool install -g dotnet-dump)" }
New-Item -ItemType Directory -Force $OutDir | Out-Null

if (-not $SummaryOnly) {
    if (-not $ProcessId) { throw '-ProcessId is required' }
    $p = Get-Process -Id $ProcessId
    $driveFree = (Get-PSDrive ($OutDir.Substring(0, 1))).Free
    if ($driveFree -lt 2 * $p.WorkingSet64) {
        throw ("not enough disk: {0:N1} GB free, need {1:N1} GB" -f ($driveFree / 1GB), (2 * $p.WorkingSet64 / 1GB))
    }
    $stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
    $Dump = Join-Path $OutDir "$($p.ProcessName)-$ProcessId-$stamp.dmp"
    Write-Host ("dumping pid {0} (working set {1:N1} GB) to {2}; the process is suspended while this runs" -f $ProcessId, ($p.WorkingSet64 / 1GB), $Dump)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    & $tool collect -p $ProcessId --type Full -o $Dump
    if ($LASTEXITCODE -ne 0) { throw "dotnet-dump collect exited $LASTEXITCODE" }
    Write-Host ("dump written in {0:N0} s, {1:N2} GB" -f $sw.Elapsed.TotalSeconds, ((Get-Item $Dump).Length / 1GB))
}
if (-not $Dump -or -not (Test-Path $Dump)) { throw "no dump to summarise: $Dump" }

$summary = [IO.Path]::ChangeExtension($Dump, '.summary.txt')
$raw = [IO.Path]::ChangeExtension($Dump, '.analyze.txt')
& $tool analyze $Dump -c 'eeheap -gc' -c 'dumpheap -stat' -c 'dumpheap -stat -min 85000' -c 'dumpheap -stat -live -min 85000' -c 'exit' *> $raw
if ($LASTEXITCODE -ne 0) { throw "dotnet-dump analyze exited $LASTEXITCODE (raw output: $raw)" }

# Each dumpheap -stat block ends with "Total N objects, B bytes"; keep the 40 largest rows of each.
$lines = Get-Content $raw
$out = [Collections.Generic.List[string]]::new()
$titles = @('whole heap by type', 'objects >= 85000 bytes (LOH), live and dead', 'objects >= 85000 bytes (LOH), live only')
$block = -1; $rows = [Collections.Generic.List[string]]::new()
foreach ($l in $lines) {
    if ($l -match '^\s*MT\s+Count\s+TotalSize') { $block++; $rows.Clear(); continue }
    if ($block -ge 0 -and $l -match '^Total ') {
        $out.Add("== $($titles[$block])"); $out.Add($l)
        $rows | Select-Object -Last 40 | ForEach-Object { $out.Add($_) }
        $out.Add(''); continue
    }
    if ($block -ge 0 -and $l -match '^[0-9a-f]{8,}\s') { $rows.Add($l) }
}
$gc = $lines | Select-String -Pattern 'GC Allocated Heap Size|GC Committed Heap Size' | Select-Object -First 12
@("dump: $Dump", '== eeheap -gc (totals)') + ($gc | ForEach-Object { $_.Line.Trim() }) + @('') + $out | Set-Content $summary
Write-Host "summary: $summary"
Write-Host "raw:     $raw"
