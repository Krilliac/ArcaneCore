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
    # A collector that ends within a minute of its start is restarted only after this many seconds (tests shorten it).
    [ValidateRange(0, 3600)][int]$RestartBackoffSeconds = 60,
    [ValidateRange(1, 10000)][int]$MaxDumpFiles = 3, [ValidateRange(1, 10000)][int]$MaxDumpGB = 30,
    [ValidateRange(1, 100000)][int]$MaxHangStackFiles = 500, [ValidateRange(1, 1048576)][int]$MaxHangStackMB = 200,
    [ValidateRange(1, 10000)][int]$MaxCountersFiles = 48, [ValidateRange(1, 1048576)][int]$MaxCountersMB = 2048
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

function Get-HeldProcess([int]$ProcId) {
    # The Process object with its OS handle opened now: from then on HasExited and WaitForExit follow this very process, so a later
    # process that is given the same id is never mistaken for it (a fresh Get-Process -Id each poll would find the newcomer).
    # A process that exited but is still listed because another process holds a handle reports HasExited, so it counts as gone.
    $p = Get-Process -Id $ProcId -ErrorAction SilentlyContinue
    if ($null -eq $p) { return $null }
    try {
        [void]$p.Handle
        if ($p.HasExited) { return $null }
    } catch { return $null }
    return $p
}

function Find-Daemon {
    # Returns the daemon's held Process object, or $null when it is not (or no longer) there.
    if ($DaemonPid -gt 0) { return Get-HeldProcess $DaemonPid }
    $launcher = Get-HeldProcess $ParentPid
    if ($null -eq $launcher) { return $null }
    $deadline = [DateTime]::UtcNow.AddSeconds($FindTimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($launcher.HasExited) { return $null }
        $child = Get-CimInstance Win32_Process -Filter "ParentProcessId=$ParentPid AND Name='ArcaneCore.$Role.exe'" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($child) { return Get-HeldProcess ([int]$child.ProcessId) }
        [void]$launcher.WaitForExit(1000)
    }
    return $null
}

# No console window for the collector on Windows (a visible one would flash up and, if killed, leave an error tab in Windows Terminal).
$hidden = if ($IsWindows) { @{ WindowStyle = 'Hidden' } } else { @{} }

$daemonProcess = Find-Daemon
if ($null -eq $daemonProcess) { Write-SidecarLog "no ArcaneCore.$Role found under launcher $ParentPid; sidecar exits"; exit 0 }
$daemon = $daemonProcess.Id
Write-SidecarLog "sidecar for ArcaneCore.$Role pid $daemon (counters tool: $(if ($CountersTool) { $CountersTool } else { 'none' }))"

while (-not $daemonProcess.HasExited) {
    foreach ($removed in Invoke-DiagnosticsRetentionRules -Rules $rules) { Write-SidecarLog "retention removed $removed" }
    $collector = $null
    if ($CountersTool) {
        $stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
        $out = Join-Path $dirs.Counters "$($Role.ToLowerInvariant())-$daemon-$stamp.csv"
        $arguments = Get-CountersCollectArguments -ProcessId $daemon -OutputPath $out -RefreshSeconds $RefreshSeconds -SegmentMinutes $SegmentMinutes
        try {
            $collector = Start-Process -FilePath $CountersTool -ArgumentList (ConvertTo-LiveArgumentString $arguments) -PassThru @hidden `
                -RedirectStandardOutput (Join-Path $dirs.Counters "$($Role.ToLowerInvariant())-collector.out") `
                -RedirectStandardError (Join-Path $dirs.Counters "$($Role.ToLowerInvariant())-collector.err")
            [void]$collector.Handle # cache the handle now: ExitCode is $null for a process whose handle was never opened
        } catch { Write-SidecarLog "counters collector did not start: $($_.Exception.Message)" }
    }
    $segmentStart = [DateTime]::UtcNow
    $segmentEnd = $segmentStart.AddMinutes($SegmentMinutes)
    # One segment: until the collector ends by itself (--duration), the daemon ends, or (no collector) the retention interval passes.
    # The daemon is held as a Process object, so these checks cannot be fooled by a reused process id.
    while (-not $daemonProcess.HasExited -and [DateTime]::UtcNow -lt $segmentEnd.AddSeconds(60) -and ($null -eq $collector -or -not $collector.HasExited)) {
        if ($null -eq $collector -and [DateTime]::UtcNow -ge $segmentEnd) { break }
        [void]$daemonProcess.WaitForExit($PollSeconds * 1000)
    }
    if ($collector -and -not $collector.HasExited) {
        # The daemon is gone (or the segment overran): the collector ends itself when its target exits; give it a moment, then stop this one process by id.
        if (-not $collector.WaitForExit(10000)) {
            try { $collector.Kill($true); Write-SidecarLog "stopped collector pid $($collector.Id)" } catch { }
        }
    }
    # A collector that ends at once (bad tool, the runtime refusing a second session) must not be restarted in a tight loop.
    if ($collector -and -not $daemonProcess.HasExited -and ([DateTime]::UtcNow - $segmentStart).TotalSeconds -lt 60) {
        Write-SidecarLog "counters collector ended after $([int]([DateTime]::UtcNow - $segmentStart).TotalSeconds) s (exit $($collector.ExitCode)); retrying in $RestartBackoffSeconds s"
        [void]$daemonProcess.WaitForExit($RestartBackoffSeconds * 1000) # returns early when the daemon ends
    }
}
foreach ($removed in Invoke-DiagnosticsRetentionRules -Rules $rules) { Write-SidecarLog "retention removed $removed" }
Write-SidecarLog "ArcaneCore.$Role pid $daemon ended; sidecar exits"
exit 0
