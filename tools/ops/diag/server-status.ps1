<#
.SYNOPSIS
  Live status console for an ArcaneCore profile: realm list, Realm/World/Cluster processes and World/Realm metrics. It refreshes every few
  seconds and is read-only.
.DESCRIPTION
  - Realm list: the `realmlist` rows the logon server serves to clients, read from the profile's auth.db with SQLite opened
    read-only (mode=ro). The auth DB path comes from appsettings.json Database:Auth.
  - Processes: who listens on the World, Auth, and Cluster Node ports (19081..19087), their uptime, working set and CPU.
  - World metrics: scraped from Ops:Metrics:PrometheusPrefix (default http://localhost:9464/metrics/) when Ops:Metrics is enabled.
    Shows players, sessions and maps, the save queue, caught faults / invariant failures / unhandled client opcodes, and tick rate, mean and p95 tick time over the refresh window (from the
    arcanecore_world_tick_duration histogram deltas). Without metrics it says so instead of inventing numbers.
  - Realm metrics: scraped from Ops:Metrics:RealmPrometheusPrefix (default http://localhost:9465/metrics/) when Ops:Metrics is enabled.
  Type console commands in the World and Realm windows (docs/areas/server-console.md); this window only displays.
.EXAMPLE
  pwsh -NoExit -File tools/ops/diag/server-status.ps1 -Profile $env:ARCANECORE_LIVE_PROFILE
#>
param(
    [string] $Profile = '',
    [int] $WorldPort = 8085,
    [int] $AuthPort = 3724,
    [int] $IntervalSeconds = 5,
    [string] $MetricsUrl = '',
    [string] $RealmMetricsUrl = '',
    [switch] $Once
)
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'OpsEnv.ps1')
if ([string]::IsNullOrWhiteSpace($Profile)) {
    $Profile = Get-OpsRequiredEnv -Name 'ARCANECORE_LIVE_PROFILE' -Purpose 'the live server profile directory that holds appsettings.json'
}
if (-not $Once) {
    try { $Host.UI.RawUI.WindowTitle = "ArcaneCore status" } catch { }
    # One status console per World port: the newest wins, so each redeploy replaces the old window instead of adding another.
    # Only older pwsh processes running this script via -File for the same port are closed.
    $selfProc = Get-CimInstance Win32_Process -Filter "ProcessId = $PID" -ErrorAction SilentlyContinue
    $parentPid = if ($selfProc) { $selfProc.ParentProcessId } else { 0 }
    $selfStart = (Get-Process -Id $PID).StartTime
    Get-CimInstance Win32_Process -Filter "Name='pwsh.exe' OR Name='powershell.exe'" | Where-Object {
        $_.ProcessId -ne $PID -and $_.ProcessId -ne $parentPid -and
        $_.CommandLine -match '-File\s+["'']?[^"'']*server-status\.ps1' -and
        $(if ($_.CommandLine -match '-WorldPort\s+(\d+)') { [int]$Matches[1] } else { 8085 }) -eq $WorldPort -and
        $_.CreationDate -lt $selfStart
    } | ForEach-Object { Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
}
$ErrorActionPreference = 'Continue'

function Get-JsonProp($obj, [string]$name) {
    if ($null -eq $obj) { return $null }
    $prop = $obj.PSObject.Properties[$name]
    if ($null -eq $prop) { return $null }
    return $prop.Value
}

$settingsPath = Join-Path $Profile 'appsettings.json'
$settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
$opsSection = Get-JsonProp $settings 'Ops'
$metricsSection = Get-JsonProp $opsSection 'Metrics'

if ([string]::IsNullOrWhiteSpace($MetricsUrl)) {
    $prefix = [string](Get-JsonProp $metricsSection 'PrometheusPrefix')
    $MetricsUrl = if (-not [string]::IsNullOrWhiteSpace($prefix)) { $prefix -replace '\+|\*', 'localhost' } else { 'http://localhost:9464/metrics/' }
}
if ([string]::IsNullOrWhiteSpace($RealmMetricsUrl)) {
    $realmPrefix = [string](Get-JsonProp $metricsSection 'RealmPrometheusPrefix')
    $RealmMetricsUrl = if (-not [string]::IsNullOrWhiteSpace($realmPrefix)) { $realmPrefix -replace '\+|\*', 'localhost' } else { 'http://localhost:9465/metrics/' }
}
$metricsOn = [bool](Get-JsonProp $metricsSection 'Enabled')

function AuthDbPath {
    $dbSection = Get-JsonProp $settings 'Database'
    $authSection = Get-JsonProp $dbSection 'Auth'
    $cs = if ($null -ne (Get-JsonProp $authSection 'ConnectionString')) { [string](Get-JsonProp $authSection 'ConnectionString') } else { [string]$authSection }
    if ($cs -match '(?i)Data Source=([^;}]+)') {
        $p = $Matches[1].Trim().Trim('"')
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
    try {
        $c = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction Stop | Select-Object -First 1
        if ($c) { return Get-Process -Id $c.OwningProcess -ErrorAction SilentlyContinue }
    } catch {
        $lines = & netstat -ano 2>$null
        foreach ($line in $lines) {
            if ($line -match "^\s*TCP\s+\S+:$port\s+\S+\s+LISTENING\s+(\d+)\s*$") {
                return Get-Process -Id ([int]$Matches[1]) -ErrorAction SilentlyContinue
            }
        }
    }
    return $null
}

function Scrape([string] $url) {
    try { return (Invoke-WebRequest -Uri $url -TimeoutSec 3 -UseBasicParsing).Content } catch { return $null }
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

$servicesList = @(
    @{ N = 'Realm';            P = $AuthPort;  Role = 'logon' },
    @{ N = 'World';            P = $WorldPort; Role = 'world' },
    @{ N = 'worker:EK';        P = 19081;      Role = 'map 0 (EK)' },
    @{ N = 'worker:Kalimdor';  P = 19082;      Role = 'map 1 (Kalimdor)' },
    @{ N = 'worker:instances'; P = 19083;      Role = '28 instance/BG maps' },
    @{ N = 'gateway';          P = 19084;      Role = 'session router' },
    @{ N = 'social';           P = 19085;      Role = 'presence & chat' },
    @{ N = 'coordinator';      P = 19086;      Role = 'economy & instances' },
    @{ N = 'supervisor';       P = 19087;      Role = 'diagnostics & fence' }
)

$prev = $null
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

    $out.Add('PROCESSES & CLUSTER CONSOLES')
    foreach ($svc in $servicesList) {
        $p = ListenerProc $svc.P
        if ($p) {
            $up = (New-TimeSpan -Start $p.StartTime -End $now)
            $cpuSec = if ($null -ne $p.CPU) { $p.CPU } else { 0.0 }
            $out.Add(('  {0,-16} pid {1,-7} port {2,-5} up {3:d\.hh\:mm\:ss}   mem {4,7:N0} MB   cpu {5,8:N0}s   [{6}]' -f $svc.N, $p.Id, $svc.P, $up, ($p.WorkingSet64 / 1MB), $cpuSec, $svc.Role))
        } else {
            $out.Add(('  {0,-16} NOT LISTENING on port {1,-5} [{2}]' -f $svc.N, $svc.P, $svc.Role))
        }
    }
    $out.Add('')

    $out.Add("WORLD METRICS ($MetricsUrl)")
    if (-not $metricsOn) {
        $out.Add('  Ops:Metrics:Enabled is false in appsettings.json: no metrics (set it true and restart the World).')
    } else {
        $text = Scrape $MetricsUrl
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
                $out.Add(('  faults {0} ({1} distinct)   invariant failures {2}   unhandled client opcodes {3}   (.server faults / .server wiring / .cluster status)' -f `
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

    $out.Add("REALM METRICS ($RealmMetricsUrl)")
    if (-not $metricsOn) {
        $out.Add('  Ops:Metrics:Enabled is false in appsettings.json.')
    } else {
        $realmText = Scrape $RealmMetricsUrl
        if (-not $realmText) { $out.Add('  no answer from the realm scrape endpoint') }
        else {
            $rFaults = Sum $realmText 'arcanecore_faults_total'
            $rDistinct = Sum $realmText 'arcanecore_faults_distinct'
            $rUnhandled = Sum $realmText 'arcanecore_net_opcode_unhandled_total'
            $out.Add(('  endpoint online   faults {0} ({1} distinct)   unhandled logon opcodes {2}' -f `
                $(if ($null -eq $rFaults) { 0 } else { $rFaults }),
                $(if ($null -eq $rDistinct) { 0 } else { $rDistinct }),
                $(if ($null -eq $rUnhandled) { 0 } else { $rUnhandled })))
        }
    }
    $out.Add('')
    $out.Add("Refresh every $IntervalSeconds s. Ctrl+C closes this window; the servers keep running. Type commands in the World/Realm windows.")
    if (-not $Once) {
        Clear-Host
    }
    $out | ForEach-Object { Write-Host $_ }
    if ($Once) {
        break
    }
    Start-Sleep -Seconds $IntervalSeconds
}
