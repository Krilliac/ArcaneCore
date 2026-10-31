<#
.SYNOPSIS
  Live status console (and optional auto-recovery supervisor) for an ArcaneCore profile: realm list, Realm/World/Cluster processes,
  watchdog heartbeat and World/Realm metrics.
.DESCRIPTION
  - Realm list: the `realmlist` rows the logon server serves to clients, read from the profile's auth.db with SQLite opened
    read-only (mode=ro). The auth DB path comes from appsettings.json Database:Auth.
  - Processes: who listens on the World, Auth, and Cluster Node ports (19081..19087), their uptime, working set and CPU.
  - Watchdog & Supervisor (-Supervise): monitors `Ops:Watchdog:Heartbeat:FilePath` (default `logs/world.heartbeat`) and the
    Realm/World processes recorded in `standard-processes.json`. Automatically restarts a crashed or `.server restart` (exit code 2)
    daemon, kills and restarts a World process whose heartbeat file stays stale past `-HangKillSeconds` (default 90 s), refuses to
    restart intentional `.server shutdown` (exit code 0 / `stopping` state) or config errors (exit code 78), pauses while
    `deploying.lock` exists, and trips a circuit breaker after 5 restarts within 180 s.
  - World metrics: scraped from Ops:Metrics:PrometheusPrefix (default http://localhost:9464/metrics/) when Ops:Metrics is enabled.
    Shows players, sessions and maps, the save queue, caught faults / invariant failures / unhandled client opcodes, and tick rate, mean and p95 tick time over the refresh window (from the
    arcanecore_world_tick_duration histogram deltas). Without metrics it says so instead of inventing numbers.
  - Realm metrics: scraped from Ops:Metrics:RealmPrometheusPrefix (default http://localhost:9465/metrics/) when Ops:Metrics is enabled.
  The World executable also has a built-in opt-in supervisor with the same recovery rules (Cluster:Supervisor:Enabled; see docs/CLUSTERING_DESIGN.md);
  this script remains the supervisor for the live profile.
  Type console commands in the World and Realm windows (docs/areas/server-console.md); this window displays status (and supervises when -Supervise is passed).
  Under the cluster host supervisor (tools/ops/live/start-cluster.ps1) pass -HeartbeatPath, the liveness file the supervised world writes
  (it need not be the profile's Ops:Watchdog:Heartbeat:FilePath), and -WorldPort/-AuthPort of the supervised children; do not pass
  -Supervise there, the cluster supervisor owns restarts. -WindowTitle replaces the console title.
.EXAMPLE
  pwsh -NoExit -File tools/ops/diag/server-status.ps1 -Profile $env:ARCANECORE_LIVE_PROFILE -Supervise
#>
param(
    [Alias('Profile')][string] $ProfileDir = '',
    [int] $WorldPort = 8085,
    [int] $AuthPort = 3724,
    [int] $IntervalSeconds = 5,
    [string] $MetricsUrl = '',
    [string] $RealmMetricsUrl = '',
    [switch] $Supervise,
    [int] $HangKillSeconds = 90,
    [switch] $Once,
    [string] $HeartbeatPath = '',
    [string] $WindowTitle = ''
)
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'OpsEnv.ps1')
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'live\LiveEnv.ps1')
if ([string]::IsNullOrWhiteSpace($ProfileDir)) {
    $ProfileDir = Get-OpsRequiredEnv -Name 'ARCANECORE_LIVE_PROFILE' -Purpose 'the live server profile directory that holds appsettings.json'
}
if (-not $Once) {
    try { $Host.UI.RawUI.WindowTitle = if ($WindowTitle) { $WindowTitle } elseif ($Supervise) { "ArcaneCore status [SUPERVISOR]" } else { "ArcaneCore status" } } catch { }
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

$settingsPath = Join-Path $ProfileDir 'appsettings.json'
$settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
$opsSection = Get-JsonProp $settings 'Ops'
$metricsSection = Get-JsonProp $opsSection 'Metrics'
$watchdogSection = Get-JsonProp $opsSection 'Watchdog'
$hbSection = Get-JsonProp $watchdogSection 'Heartbeat'
$hbRelPath = [string](Get-JsonProp $hbSection 'FilePath')
if ([string]::IsNullOrWhiteSpace($hbRelPath)) { $hbRelPath = 'logs/world.heartbeat' }
$heartbeatPath = if ($HeartbeatPath) { $HeartbeatPath } elseif ([System.IO.Path]::IsPathRooted($hbRelPath)) { $hbRelPath } else { Join-Path $ProfileDir $hbRelPath }
$supervisorLogPath = Join-Path $ProfileDir 'logs\supervisor.log'
$deployLockPath = Join-Path $ProfileDir 'deploying.lock'
$metaPath = Join-Path $ProfileDir 'standard-processes.json'

if ([string]::IsNullOrWhiteSpace($MetricsUrl)) {
    $prefix = [string](Get-JsonProp $metricsSection 'PrometheusPrefix')
    $MetricsUrl = if (-not [string]::IsNullOrWhiteSpace($prefix)) { $prefix -replace '\+|\*', 'localhost' } else { 'http://localhost:9464/metrics/' }
}
if ([string]::IsNullOrWhiteSpace($RealmMetricsUrl)) {
    $realmPrefix = [string](Get-JsonProp $metricsSection 'RealmPrometheusPrefix')
    $RealmMetricsUrl = if (-not [string]::IsNullOrWhiteSpace($realmPrefix)) { $realmPrefix -replace '\+|\*', 'localhost' } else { 'http://localhost:9465/metrics/' }
}
$metricsOn = [bool](Get-JsonProp $metricsSection 'Enabled')

function Write-SupervisorLog([string] $msg) {
    $stamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
    $line = "$stamp $msg"
    $script:lastSupervisorNote = $line
    try {
        $dir = Split-Path -Parent $supervisorLogPath
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        Add-Content -Path $supervisorLogPath -Value $line -Encoding utf8
    } catch { }
}

function Read-HeartbeatInfo {
    if (-not (Test-Path $heartbeatPath)) { return $null }
    try {
        $item = Get-Item -LiteralPath $heartbeatPath -ErrorAction Stop
        $raw = (Get-Content -LiteralPath $heartbeatPath -Raw -ErrorAction Stop).Trim()
        $ageSec = [Math]::Max(0.0, ((Get-Date).ToUniversalTime() - $item.LastWriteTimeUtc).TotalSeconds)
        if ($raw -match '^(\S+)\s+pid=(\d+)\s+(\S+)$') {
            return @{ Stamp = $Matches[1]; Pid = [int]$Matches[2]; State = $Matches[3]; AgeSec = $ageSec; Raw = $raw }
        }
        return @{ Stamp = '?'; Pid = 0; State = $raw; AgeSec = $ageSec; Raw = $raw }
    } catch {
        return $null
    }
}

$watchedProcs = @{}
$intentionalStop = @{ Realm = $false; World = $false }
$restartHistory = @{
    Realm = [System.Collections.Generic.List[datetime]]::new()
    World = [System.Collections.Generic.List[datetime]]::new()
}
$lastSupervisorNote = 'none'

function Restart-SupervisedService([string] $svcName, [string] $reason) {
    $nowUtc = (Get-Date).ToUniversalTime()
    $hist = $restartHistory[$svcName]
    for ($i = $hist.Count - 1; $i -ge 0; $i--) {
        if (($nowUtc - $hist[$i]).TotalSeconds -gt 180.0) { $hist.RemoveAt($i) }
    }
    if ($hist.Count -ge 5) {
        Write-SupervisorLog "CIRCUIT BREAKER OPEN for ${svcName}: 5 restarts in 180s (last reason: $reason)"
        return
    }
    if (-not (Test-Path $metaPath)) {
        Write-SupervisorLog "cannot restart ${svcName} ($reason): $metaPath not found"
        return
    }
    try {
        $meta = Get-Content -LiteralPath $metaPath -Raw | ConvertFrom-Json
        $svcMeta = $meta.services.$svcName
        $dll = [string]$svcMeta.dll
        if ([string]::IsNullOrWhiteSpace($dll) -or -not (Test-Path -LiteralPath $dll)) {
            Write-SupervisorLog "cannot restart ${svcName} ($reason): dll not found ($dll)"
            return
        }
        $logDir = Join-Path $ProfileDir 'logs'
        if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
        # One log file in the configured format (Logging:ArcaneCore:Format): .jsonl for Json, .log for Text. The format is computed once and
        # set explicitly below, so the extension and the daemon's format cannot disagree; the deprecated Json:Path is removed, not set.
        $logFormat = Get-LiveLogFormat -LiveProfile $ProfileDir
        $logExt = if ($logFormat -eq 'Json') { 'jsonl' } else { 'log' }
        $logFile = Join-Path $logDir ("{0}-supervised.{1}" -f $svcName.ToLowerInvariant(), $logExt)
        $psi = [System.Diagnostics.ProcessStartInfo]::new('dotnet', "`"$dll`"")
        $psi.WorkingDirectory = $ProfileDir
        $psi.UseShellExecute = $true
        $psi.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Normal
        $envPrevFormat = $env:Logging__ArcaneCore__Format
        $envPrevEnabled = $env:Logging__ArcaneCore__File__Enabled
        $envPrevPath = $env:Logging__ArcaneCore__File__Path
        $envPrevJsonPath = $env:Logging__ArcaneCore__Json__Path
        $envPrevLive = $env:ARCANECORE_LIVE_SERVER
        try {
            $env:Logging__ArcaneCore__Format = $logFormat
            $env:Logging__ArcaneCore__File__Enabled = 'true'
            $env:Logging__ArcaneCore__File__Path = $logFile
            Remove-Item Env:Logging__ArcaneCore__Json__Path -ErrorAction SilentlyContinue
            # A supervised restart is a live-server launcher (deploy-live.py opens this console with -Supervise): the World refuses the
            # map feed's packet tools whenever this is 1, whatever the profile says (docs/areas/map-feed.md, "Never on the live server").
            # Set on every supervised restart, live or not: the only effect is that the packet tools stay off.
            $env:ARCANECORE_LIVE_SERVER = '1'
            $proc = [System.Diagnostics.Process]::Start($psi)
        } finally {
            $env:Logging__ArcaneCore__Format = $envPrevFormat
            $env:Logging__ArcaneCore__File__Enabled = $envPrevEnabled
            $env:Logging__ArcaneCore__File__Path = $envPrevPath
            $env:Logging__ArcaneCore__Json__Path = $envPrevJsonPath
            $env:ARCANECORE_LIVE_SERVER = $envPrevLive
        }
        if ($null -ne $proc) {
            try { $proc.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::AboveNormal } catch { }
            $watchedProcs[$svcName] = $proc
            $intentionalStop[$svcName] = $false
            $hist.Add($nowUtc)
            $svcMeta.pid = $proc.Id
            $meta | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $metaPath -Encoding utf8
            Write-SupervisorLog "restarted ${svcName} as pid $($proc.Id) (reason: $reason)"
        }
    } catch {
        Write-SupervisorLog "failed to restart ${svcName} ($reason): $($_.Exception.Message)"
    }
}

function AuthDbPath {
    $dbSection = Get-JsonProp $settings 'Database'
    $authSection = Get-JsonProp $dbSection 'Auth'
    $cs = if ($null -ne (Get-JsonProp $authSection 'ConnectionString')) { [string](Get-JsonProp $authSection 'ConnectionString') } else { [string]$authSection }
    if ($cs -match '(?i)Data Source=([^;}]+)') {
        $p = $Matches[1].Trim().Trim('"')
        if (-not [System.IO.Path]::IsPathRooted($p)) { $p = Join-Path $ProfileDir $p }
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

$logicalCores = [Math]::Max(1, [Environment]::ProcessorCount)
$prevCpu = @{}
$prevCpuAt = $null
$prev = $null
while ($true) {
    $now = Get-Date
    $cpuWindowSec = if ($null -ne $prevCpuAt) { ($now - $prevCpuAt).TotalSeconds } else { 0.0 }
    $nextCpu = @{}
    $out = [System.Collections.Generic.List[string]]::new()
    $out.Add("ArcaneCore status   $($now.ToString('yyyy-MM-dd HH:mm:ss'))   profile: $ProfileDir")
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
    $activeListeners = @{}
    foreach ($svc in $servicesList) {
        $p = ListenerProc $svc.P
        $activeListeners[$svc.N] = $p
        if ($p) {
            if ($svc.N -eq 'Realm' -or $svc.N -eq 'World') {
                $watchedProcs[$svc.N] = $p
                $intentionalStop[$svc.N] = $false
            }
            $up = (New-TimeSpan -Start $p.StartTime -End $now)
            $cpuSec = if ($null -ne $p.CPU) { [double]$p.CPU } else { 0.0 }
            $nextCpu[$p.Id] = $cpuSec
            if ($cpuWindowSec -gt 0.1 -and $prevCpu.ContainsKey($p.Id)) {
                $deltaCpu = [Math]::Max(0.0, $cpuSec - $prevCpu[$p.Id])
                $cpuPct = ($deltaCpu / ($cpuWindowSec * $logicalCores)) * 100.0
            } elseif ($up.TotalSeconds -gt 0.1) {
                $cpuPct = ($cpuSec / ($up.TotalSeconds * $logicalCores)) * 100.0
            } else {
                $cpuPct = 0.0
            }
            $out.Add(('  {0,-16} pid {1,-7} port {2,-5} up {3:d\.hh\:mm\:ss}   mem {4,7:N0} MB   cpu {5,5:N1}% ({6,5:N0}s)   [{7}]' -f $svc.N, $p.Id, $svc.P, $up, ($p.WorkingSet64 / 1MB), $cpuPct, $cpuSec, $svc.Role))
        } else {
            $out.Add(('  {0,-16} NOT LISTENING on port {1,-5} [{2}]' -f $svc.N, $svc.P, $svc.Role))
        }
    }
    $prevCpu = $nextCpu
    $prevCpuAt = $now
    $out.Add('')

    $hbInfo = Read-HeartbeatInfo
    $deployLocked = Test-Path -LiteralPath $deployLockPath
    if ($Supervise -and -not $deployLocked) {
        foreach ($svcName in @('Realm', 'World')) {
            $listener = $activeListeners[$svcName]
            if ($null -ne $listener) {
                if ($svcName -eq 'World' -and $null -ne $hbInfo -and $hbInfo.Pid -eq $listener.Id -and $hbInfo.State -ne 'stopping' -and -not ($hbInfo.State -eq 'dumping' -and $hbInfo.AgeSec -le 420) -and $hbInfo.AgeSec -gt $HangKillSeconds) {
                    Write-SupervisorLog "World pid $($listener.Id) heartbeat stale for $([int]$hbInfo.AgeSec)s (> ${HangKillSeconds}s); killing and restarting"
                    Stop-Process -Id $listener.Id -Force -ErrorAction SilentlyContinue
                    Start-Sleep -Seconds 1
                    Restart-SupervisedService 'World' "heartbeat stale ($([int]$hbInfo.AgeSec)s)"
                }
                continue
            }
            # Not listening on its port: check if a recently spawned process is still warming up before binding its socket.
            $existing = $watchedProcs[$svcName]
            if ($null -ne $existing -and -not $existing.HasExited) {
                $warmupLimit = if ($svcName -eq 'World') { 180.0 } else { 60.0 }
                if (($now - $existing.StartTime).TotalSeconds -lt $warmupLimit) {
                    continue
                }
            }
            if ($intentionalStop[$svcName]) {
                continue
            }
            $shouldRestart = $true
            $reason = 'listener down'
            if ($null -ne $existing -and $existing.HasExited) {
                $code = $existing.ExitCode
                if ($code -eq 0 -or $code -eq 78) {
                    $intentionalStop[$svcName] = $true
                    $shouldRestart = $false
                    Write-SupervisorLog "${svcName} pid $($existing.Id) exited with code $code (clean shutdown or config error); not restarting"
                } else {
                    $reason = "pid $($existing.Id) exited with code $code"
                }
            } elseif ($svcName -eq 'World' -and $null -ne $hbInfo -and $hbInfo.State -eq 'stopping') {
                $intentionalStop['World'] = $true
                $shouldRestart = $false
            }
            if ($shouldRestart) {
                Restart-SupervisedService $svcName $reason
            }
        }
    }

    $out.Add('WATCHDOG & SUPERVISOR')
    $supState = if ($deployLocked) { 'PAUSED (deploying.lock present)' } elseif ($Supervise) { "ACTIVE (auto-restart on crash/exit!=0,78; hang kill > ${HangKillSeconds}s)" } else { 'READ-ONLY (pass -Supervise to enable auto-recovery)' }
    $out.Add("  supervisor: $supState")
    if ($null -ne $hbInfo) {
        $out.Add(('  heartbeat:  pid={0}   state={1}   age={2:N1}s   ({3})' -f $hbInfo.Pid, $hbInfo.State, $hbInfo.AgeSec, $heartbeatPath))
    } else {
        $out.Add("  heartbeat:  not present ($heartbeatPath)")
    }
    $out.Add("  last event: $lastSupervisorNote")
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
