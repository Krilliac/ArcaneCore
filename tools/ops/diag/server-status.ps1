<#
.SYNOPSIS
  Live status console for an ArcaneCore profile: realm list, Realm/World processes and World metrics. It refreshes every few
  seconds and is read-only.
.DESCRIPTION
  - Realm list: the `realmlist` rows the logon server serves to clients, read from the profile's auth.db with SQLite opened
    read-only (mode=ro). The auth DB path comes from appsettings.json Database:Auth.
  - Processes: who listens on the World and Auth ports, their uptime, working set and CPU.
  - World metrics: scraped from Ops:Metrics:PrometheusPrefix (default http://localhost:9464/metrics/) when Ops:Metrics is enabled.
    Shows players, sessions and maps, the save queue, caught faults / invariant failures / unhandled client opcodes, and tick rate, mean and p95 tick time over the refresh window (from the
    arcanecore_world_tick_duration histogram deltas). Without metrics it says so instead of inventing numbers.
  Type console commands in the World and Realm windows (docs/areas/server-console.md); this window only displays.
.EXAMPLE
  pwsh -NoExit -File tools/ops/diag/server-status.ps1 -Profile $env:ARCANECORE_LIVE_PROFILE
#>
param(
    [string] $Profile = '',
    [int] $WorldPort = 8085,
    [int] $AuthPort = 3724,
    [int] $IntervalSeconds = 5,
    [string] $MetricsUrl = ''
)
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'OpsEnv.ps1')
if ([string]::IsNullOrWhiteSpace($Profile)) {
    $Profile = Get-OpsRequiredEnv -Name 'ARCANECORE_LIVE_PROFILE' -Purpose 'the live server profile directory that holds appsettings.json'
}
try { $Host.UI.RawUI.WindowTitle = "ArcaneCore status" } catch { }
# One status console per World port: the newest wins, so each redeploy replaces the old window instead of adding another.
# Only older pwsh processes running this script for the same port are closed.
$selfStart = (Get-Process -Id $PID).StartTime
Get-CimInstance Win32_Process -Filter "Name='pwsh.exe' OR Name='powershell.exe'" | Where-Object {
    $_.ProcessId -ne $PID -and $_.CommandLine -match 'server-status\.ps1' -and
    $(if ($_.CommandLine -match '-WorldPort\s+(\d+)') { [int]$Matches[1] } else { 8085 }) -eq $WorldPort -and
    $_.CreationDate -lt $selfStart
} | ForEach-Object { Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
$ErrorActionPreference = 'Continue'
$settingsPath = Join-Path $Profile 'appsettings.json'
$settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
if (-not $MetricsUrl) {
    $prefix = $settings.Ops.Metrics.PrometheusPrefix
    $MetricsUrl = if ($prefix) { $prefix -replace '\+|\*', 'localhost' } else { 'http://localhost:9464/metrics/' }
}
$metricsOn = [bool]$settings.Ops.Metrics.Enabled

function AuthDbPath {
    $cs = [string]$settings.Database.Auth
    if ($cs -match '(?i)Data Source=([^;]+)') {
        $p = $Matches[1].Trim('"')
        if (-not [System.IO.Path]::IsPathRooted($p)) { $p = Join-Path $Profile $p }
        return $p
    }
    return $null
}

$realmQuery = @'
import sqlite3, sys
c = sqlite3.connect("file:" + sys.argv[1].replace("\\", "/") + "?mode=ro", uri=True)
for r in c.execute("select Id, Name, Address, Type, Flags, Population, Category from realmlist order by Id"):
    print("\t".join(str(x) for x in r))
'@
$realmScript = Join-Path ([System.IO.Path]::GetTempPath()) "arcanecore-status-realmq-$PID.py"
Set-Content -Path $realmScript -Value $realmQuery -Encoding utf8

function ListenerProc([int] $port) {
    $c = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $c) { return $null }
    return Get-Process -Id $c.OwningProcess -ErrorAction SilentlyContinue
}

function Scrape {
    try { return (Invoke-WebRequest -Uri $MetricsUrl -TimeoutSec 3 -UseBasicParsing).Content } catch { return $null }
}

function Sum([string] $text, [string] $name) {
    $total = 0.0; $found = $false
    foreach ($line in ($text -split "`n")) {
        if ($line -match "^$([regex]::Escape($name))(\{[^}]*\})?\s+([0-9.eE+-]+)") { $total += [double]$Matches[2]; $found = $true }
    }
    if ($found) { return $total } else { return $null }
}

function Buckets([string] $text, [string] $name) {
    $b = @{}
    foreach ($line in ($text -split "`n")) {
        if ($line -match "^$([regex]::Escape($name))_bucket\{[^}]*le=`"([^`"]+)`"[^}]*\}\s+([0-9.eE+-]+)") {
            $le = $Matches[1]; $v = [double]$Matches[2]
            if ($b.ContainsKey($le)) { $b[$le] += $v } else { $b[$le] = $v }
        }
    }
    return $b
}

function FindName([string] $text, [string] $stem) {
    foreach ($line in ($text -split "`n")) {
        if ($line -match "^($([regex]::Escape($stem))[a-z_]*?)(_bucket|_sum|_count)?(\{|\s)") { return $Matches[1] }
    }
    return $null
}

$prev = $null; $prevAt = $null
while ($true) {
    $now = Get-Date
    $out = [System.Collections.Generic.List[string]]::new()
    $out.Add("ArcaneCore status   $($now.ToString('yyyy-MM-dd HH:mm:ss'))   profile: $Profile")
    $out.Add('')

    $out.Add('REALM LIST (served to clients)')
    $db = AuthDbPath
    if ($db -and (Test-Path $db)) {
        $rows = & python -I $realmScript $db 2>&1
        $out.Add('  Id  Name                              Address               Type Flags Pop  Cat')
        foreach ($r in $rows) {
            $f = "$r" -split "`t"
            if ($f.Count -ge 7) { $out.Add(('  {0,-3} {1,-33} {2,-21} {3,-4} {4,-5} {5,-4} {6}' -f $f[0], $f[1], $f[2], $f[3], $f[4], $f[5], $f[6])) }
            else { $out.Add("  $r") }
        }
    } else { $out.Add("  auth DB not found ($db)") }
    $out.Add('')

    $out.Add('PROCESSES')
    foreach ($svc in @(@{ N = 'Realm'; P = $AuthPort }, @{ N = 'World'; P = $WorldPort })) {
        $p = ListenerProc $svc.P
        if ($p) {
            $up = (New-TimeSpan -Start $p.StartTime -End $now)
            $out.Add(('  {0,-6} pid {1,-7} port {2,-5} up {3:d\.hh\:mm\:ss}   mem {4,7:N0} MB   cpu {5,8:N0}s' -f $svc.N, $p.Id, $svc.P, $up, ($p.WorkingSet64 / 1MB), $p.CPU))
        } else { $out.Add(('  {0,-6} NOT LISTENING on port {1}' -f $svc.N, $svc.P)) }
    }
    $out.Add('')

    $out.Add("WORLD METRICS ($MetricsUrl)")
    if (-not $metricsOn) {
        $out.Add('  Ops:Metrics:Enabled is false in appsettings.json: no metrics (set it true and restart the World).')
    } else {
        $text = Scrape
        if (-not $text) { $out.Add('  no answer from the scrape endpoint') }
        else {
            $players = Sum $text 'arcanecore_world_players_online'
            $sessions = Sum $text 'arcanecore_world_sessions'
            $maps = Sum $text 'arcanecore_world_maps_loaded'
            $saveq = Sum $text 'arcanecore_db_save_queue_depth'
            $out.Add(('  players {0}   sessions {1}   maps loaded {2}   save queue {3}' -f $players, $sessions, $maps, $saveq))
            # Fault ledger and wiring audit (train 13+). Absent on older builds: say so rather than show zeros.
            $faults = Sum $text 'arcanecore_faults_total'
            $distinct = Sum $text 'arcanecore_faults_distinct'
            $invariants = Sum $text 'arcanecore_invariant_failures_total'
            $unhandled = Sum $text 'arcanecore_net_opcode_unhandled_total'
            if ($null -eq $distinct) { $out.Add('  faults: not exported by this build') }
            else {
                $out.Add(('  faults {0} ({1} distinct)   invariant failures {2}   unhandled client opcodes {3}   (.server faults / .server wiring)' -f `
                    $(if ($null -eq $faults) { 0 } else { $faults }), $distinct,
                    $(if ($null -eq $invariants) { 'n/a' } else { $invariants }), $(if ($null -eq $unhandled) { 0 } else { $unhandled })))
            }
            $tickName = FindName $text 'arcanecore_world_tick_duration'
            $netOut = Sum $text 'arcanecore_net_bytes_out_total'; if ($null -eq $netOut) { $netOut = Sum $text 'arcanecore_net_bytes_out' }
            $cur = @{ at = $now; sum = $(if ($tickName) { Sum $text "${tickName}_sum" }); count = $(if ($tickName) { Sum $text "${tickName}_count" });
                      buckets = $(if ($tickName) { Buckets $text $tickName } else { @{} }); netOut = $netOut }
            if ($prev -and $tickName -and $null -ne $cur.count -and $null -ne $prev.count) {
                $secs = ($now - $prev.at).TotalSeconds
                $dc = $cur.count - $prev.count; $ds = $cur.sum - $prev.sum
                if ($dc -gt 0) {
                    $mean = $ds / $dc
                    $edges = $cur.buckets.Keys | Where-Object { $_ -ne '+Inf' } | Sort-Object { [double]$_ }
                    $target = 0.95 * $dc; $p95 = '>' + ($edges | Select-Object -Last 1)
                    foreach ($e in $edges) { if (($cur.buckets[$e] - [double]$prev.buckets[$e]) -ge $target) { $p95 = "<=$e"; break } }
                    $out.Add(('  ticks/s {0:N1}   tick mean {1:N2}   tick p95 {2}   ({3}, unit as exported)' -f ($dc / $secs), $mean, $p95, $tickName))
                } else { $out.Add('  no ticks in the last window') }
                if ($null -ne $cur.netOut -and $null -ne $prev.netOut) { $out.Add(('  net out {0:N1} KB/s' -f (($cur.netOut - $prev.netOut) / 1KB / $secs))) }
            } else { $out.Add('  tick rate: waiting for a second sample') }
            $prev = $cur
        }
    }
    $out.Add('')
    $out.Add("Refresh every $IntervalSeconds s. Ctrl+C closes this window; the servers keep running. Type commands in the World/Realm windows.")
    Clear-Host
    $out | ForEach-Object { Write-Host $_ }
    Start-Sleep -Seconds $IntervalSeconds
}
