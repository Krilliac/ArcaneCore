# Companion of one live daemon, started hidden by launch-daemon.ps1 (-Diagnostics Max). It never outlives the daemon:
#   - finds the daemon (the child ArcaneCore.<Role>.exe of -ParentPid, or -DaemonPid when given) and exits when it exits;
#   - keeps the capture directories under their caps (dumps, hang stacks, counters; Invoke-DiagnosticsRetention) every segment;
#   - with dotnet-counters: collects System.Runtime and the repo's meters to <LiveHome>\logs\counters\<role>-<stamp>.csv, one
#     file per segment, and stops its own collector by process id (never by image name) once the daemon is gone.
#   pwsh -NoProfile -File tools/ops/live/diagnostics-sidecar.ps1 -Role World -LiveHome <dir> -ParentPid <launcher pid> [-CountersTool <path>]
param(
    [Parameter(Mandatory)][ValidateSet('Realm', 'World')][string]$Role,
    [Parameter(Mandatory)][string]$LiveHome,
    [int]$ParentPid = 0,
    [int]$DaemonPid = 0,
    [string]$CountersTool = '',
    [ValidateRange(1, 1440)][int]$SegmentMinutes = 60,
    [ValidateRange(1, 3600)][int]$RefreshSeconds = 10,
    [ValidateRange(1, 3600)][int]$PollSeconds = 5,
    [ValidateRange(1, 3600)][int]$FindTimeoutSeconds = 180,
    [int]$MaxDumpFiles = 3, [int]$MaxDumpGB = 30,
    [int]$MaxHangStackFiles = 500, [int]$MaxHangStackMB = 200,
    [int]$MaxCountersFiles = 48, [int]$MaxCountersMB = 2048
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'LiveDiagnostics.ps1')
if ($ParentPid -le 0 -and $DaemonPid -le 0) { throw 'pass -ParentPid (the launcher) or -DaemonPid' }
$dirs = Get-LiveDiagnosticsDirectories -LiveHome $LiveHome
$rules = Get-DiagnosticsRetentionRules -LiveHome $LiveHome -MaxDumpFiles $MaxDumpFiles -MaxDumpGB $MaxDumpGB `
    -MaxHangStackFiles $MaxHangStackFiles -MaxHangStackMB $MaxHangStackMB -MaxCountersFiles $MaxCountersFiles -MaxCountersMB $MaxCountersMB
New-Item -ItemType Directory -Force -Path $dirs.Counters | Out-Null
$log = Join-Path $dirs.Counters "$($Role.ToLowerInvariant())-sidecar.log"
function Write-SidecarLog([string]$Text) {
    try { Add-Content -LiteralPath $log -Value ("{0:u} {1}" -f [DateTime]::UtcNow, $Text) } catch { }
}

function Test-Alive([int]$ProcId) {
    # An exited process whose handle another process still holds is listed until that handle closes; HasExited tells them apart.
    $p = Get-Process -Id $ProcId -ErrorAction SilentlyContinue
    return $null -ne $p -and -not $p.HasExited
}

function Find-Daemon {
    if ($DaemonPid -gt 0) { return $(if (Test-Alive $DaemonPid) { $DaemonPid } else { 0 }) }
    $deadline = [DateTime]::UtcNow.AddSeconds($FindTimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (-not (Test-Alive $ParentPid)) { return 0 }
        $child = Get-CimInstance Win32_Process -Filter "ParentProcessId=$ParentPid AND Name='ArcaneCore.$Role.exe'" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($child) { return [int]$child.ProcessId }
        Start-Sleep -Seconds 1
    }
    return 0
}

$daemon = Find-Daemon
if ($daemon -le 0) { Write-SidecarLog "no ArcaneCore.$Role found under launcher $ParentPid; sidecar exits"; exit 0 }
Write-SidecarLog "sidecar for ArcaneCore.$Role pid $daemon (counters tool: $(if ($CountersTool) { $CountersTool } else { 'none' }))"

while (Test-Alive $daemon) {
    foreach ($removed in Invoke-DiagnosticsRetentionRules -Rules $rules) { Write-SidecarLog "retention removed $removed" }
    $collector = $null
    if ($CountersTool) {
        $stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
        $out = Join-Path $dirs.Counters "$($Role.ToLowerInvariant())-$daemon-$stamp.csv"
        $arguments = Get-CountersCollectArguments -ProcessId $daemon -OutputPath $out -RefreshSeconds $RefreshSeconds -SegmentMinutes $SegmentMinutes
        try {
            $collector = Start-Process -FilePath $CountersTool -ArgumentList $arguments -PassThru -WindowStyle Hidden `
                -RedirectStandardOutput (Join-Path $dirs.Counters "$($Role.ToLowerInvariant())-collector.out") `
                -RedirectStandardError (Join-Path $dirs.Counters "$($Role.ToLowerInvariant())-collector.err")
        } catch { Write-SidecarLog "counters collector did not start: $($_.Exception.Message)" }
    }
    $segmentStart = [DateTime]::UtcNow
    $segmentEnd = $segmentStart.AddMinutes($SegmentMinutes)
    # One segment: until the collector ends by itself (--duration), the daemon ends, or (no collector) the retention interval passes.
    while ((Test-Alive $daemon) -and [DateTime]::UtcNow -lt $segmentEnd.AddSeconds(60) -and ($null -eq $collector -or -not $collector.HasExited)) {
        if ($null -eq $collector -and [DateTime]::UtcNow -ge $segmentEnd) { break }
        Start-Sleep -Seconds $PollSeconds
    }
    if ($collector -and -not $collector.HasExited) {
        # The daemon is gone (or the segment overran): the collector ends itself when its target exits; give it a moment, then stop this one process by id.
        if (-not $collector.WaitForExit(10000)) {
            try { Stop-Process -Id $collector.Id -Force -ErrorAction Stop; Write-SidecarLog "stopped collector pid $($collector.Id)" } catch { }
        }
    }
    # A collector that ends at once (bad tool, the runtime refusing a second session) must not be restarted in a tight loop.
    if ($collector -and (Test-Alive $daemon) -and ([DateTime]::UtcNow - $segmentStart).TotalSeconds -lt 60) {
        Write-SidecarLog "counters collector ended after $([int]([DateTime]::UtcNow - $segmentStart).TotalSeconds) s (exit $($collector.ExitCode)); retrying in 60 s"
        Start-Sleep -Seconds 60
    }
}
foreach ($removed in Invoke-DiagnosticsRetentionRules -Rules $rules) { Write-SidecarLog "retention removed $removed" }
Write-SidecarLog "ArcaneCore.$Role pid $daemon ended; sidecar exits"
exit 0
