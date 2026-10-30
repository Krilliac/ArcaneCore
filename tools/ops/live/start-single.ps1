<#
.SYNOPSIS
  Starts the local live server as two daemons (Realm, then World), each in its own console window, then opens a status window and a
  debugger window. **Starts real server processes** (not with -Plan / -WhatIf).
.DESCRIPTION
  1. Refuses when a live daemon is already running (Get-LiveDaemon, as the other live tools use it) or a needed port is in use.
  2. Starts ArcaneCore.Realm, then ArcaneCore.World, each through launch-daemon.ps1 in its own console (pwsh -NoExit), so the
     diagnostics sidecar, the per-role log paths, ARCANECORE_LIVE_SERVER=1 and the GC settings are exactly those of redeploy.ps1.
     After each one it waits until the process is alive and listening on its port (Auth:Port for the Realm, World:Port for the World, from
     the profile's appsettings.json unless -AuthPort/-WorldPort are given) and stays up for a few seconds; the Realm must be up before the
     World starts. A daemon that exits or is not up within -StartTimeoutSec stops the script with a message (nothing is stopped).
  3. Opens the status window (tools/ops/diag/server-status.ps1: read-only; -Supervise passes through and lets that window restart a
     crashed or hung daemon, single host only) and the debugger window (tools/ops/diag/attach-debugger.ps1 with both PIDs). -NoStatus and
     -NoDebugger skip them.
  Every window title carries the build found in <LiveHome>\bin: "ArcaneCore.World (Debug, live)" (Debug, Release, Mixed or unknown; read
  from the assemblies' DebuggableAttribute / AssemblyConfigurationAttribute without loading them). The debugger attaches for either build.

  WARNING (debugger): while a Win32 debugger is attached, every debug event stops the whole daemon until the debugger console continues it
  (docs/ops/tooling.md). The local tools/ops/diag/server-status.ps1 -Supervise kills a World whose heartbeat file is older than
  -HangKillSeconds (90 s): a debugger console that stalls (text selected in it, the machine paging) stalls the World past that and the
  status window then kills and restarts it. A burst of first-chance exceptions alone only slows the World; it takes a rate in the
  thousands a second sustained for the whole window to look hung. Use -NoDebugger when the World must not be touched by one.

  Locations come from ARCANECORE_LIVE_HOME and ARCANECORE_LIVE_PROFILE (or -LiveHome / -Profile); see docs/ops/tooling.md.
.PARAMETER Plan
  Print every process, window title, command line, environment variable (secrets masked) and wait that would be used, then exit 0
  without starting anything. -WhatIf does the same.
.EXAMPLE
  pwsh -NoProfile -File tools/ops/live/start-single.ps1 -Plan
.EXAMPLE
  pwsh -NoProfile -File tools/ops/live/start-single.ps1 -Supervise -NoDebugger
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$LiveHome,
    [Alias('Profile')][string]$LiveProfile,
    [ValidateSet('Standard', 'Max')][string]$Diagnostics = 'Max',
    [ValidateRange(50, 99)][int]$GCHighMemPercent = 97,
    # The logon port the Realm listens on and the world port of the World; default: the profile's Auth:Port / World:Port (3724 / 8085).
    # They are not passed to the daemons: they must match the profile, they say where to wait and what the status window watches.
    [ValidateRange(1, 65535)][int]$AuthPort,
    [ValidateRange(1, 65535)][int]$WorldPort,
    [ValidateRange(10, 3600)][int]$StartTimeoutSec = 600,
    [switch]$NoStatus,
    [switch]$NoDebugger,
    # Passed to server-status.ps1: that window restarts a crashed or hung daemon (single host only).
    [switch]$Supervise,
    [ValidateRange(30, 3600)][int]$HangKillSeconds = 90,
    [switch]$Plan
)
$ErrorActionPreference = 'Stop'
# -WhatIf is the same as -Plan; the preference itself must not reach the cmdlets the plan code calls (Remove-Item would skip its work).
$planOnly = $Plan -or $WhatIfPreference
$WhatIfPreference = $false
. (Join-Path $PSScriptRoot 'LiveLaunch.ps1')

# ---- validate everything before anything starts --------------------------------------------------------------------------------------
$LiveHome = Resolve-LiveHome $LiveHome
$LiveProfile = Resolve-LiveProfile $LiveProfile
$bin = Join-Path $LiveHome 'bin'
foreach ($role in 'Realm', 'World') {
    $exe = Join-Path $bin "ArcaneCore.$role.exe"
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "$exe not found: deploy a build to the live home first (tools/ops/live/redeploy.ps1)." }
}
if (-not (Test-Path -LiteralPath (Join-Path $LiveProfile 'appsettings.json') -PathType Leaf)) { throw "no appsettings.json in the profile directory $LiveProfile" }
if ($Supervise -and $NoStatus) { throw '-Supervise needs the status window: it is the status window that supervises. Drop -NoStatus or -Supervise.' }
$profilePorts = Get-LiveProfilePorts -LiveProfile $LiveProfile
if (-not $PSBoundParameters.ContainsKey('AuthPort')) { $AuthPort = $profilePorts.Auth }
if (-not $PSBoundParameters.ContainsKey('WorldPort')) { $WorldPort = $profilePorts.World }
if ($AuthPort -eq $WorldPort) { throw "-AuthPort and -WorldPort are both ${AuthPort}: the Realm and the World cannot share a port." }

# ---- the plan ------------------------------------------------------------------------------------------------------------------------
$build = Get-LiveBuildConfiguration -BinDir $bin
$configuration = $build.Configuration
$launcher = Join-Path $PSScriptRoot 'launch-daemon.ps1'
$statusScript = Join-Path (Join-Path (Split-Path -Parent $PSScriptRoot) 'diag') 'server-status.ps1'
$debuggerScript = Join-Path (Join-Path (Split-Path -Parent $PSScriptRoot) 'diag') 'attach-debugger.ps1'

function New-DaemonStep([string]$Role, [int]$Port) {
    [pscustomobject]@{
        Name        = "daemon: ArcaneCore.$Role in its own console (launch-daemon.ps1)"
        Title       = Get-LiveWindowTitle -Name "ArcaneCore.$Role" -Configuration $configuration
        Command     = Get-LiveWindowCommand -Script $launcher -Arguments @('-Role', $Role, '-LiveHome', $LiveHome, '-Profile', $LiveProfile, '-Diagnostics', $Diagnostics, '-GCHighMemPercent', [string]$GCHighMemPercent)
        Environment = Get-LiveDaemonEnvironmentPlan -Role $Role -LiveHome $LiveHome -LiveProfile $LiveProfile -Diagnostics $Diagnostics -GCHighMemPercent $GCHighMemPercent
        Wait        = "ArcaneCore.$Role.exe alive as a child of that console and listening on port $Port (timeout $StartTimeoutSec s), then AboveNormal priority"
        Note        = $null
    }
}

function New-StatusStep {
    $statusArgs = @('-Profile', $LiveProfile, '-WorldPort', [string]$WorldPort, '-AuthPort', [string]$AuthPort,
        '-WindowTitle', (Get-LiveWindowTitle -Name 'ArcaneCore status' -Configuration $configuration))
    if ($Supervise) { $statusArgs += @('-Supervise', '-HangKillSeconds', [string]$HangKillSeconds) }
    [pscustomobject]@{
        Name        = 'status: server-status.ps1 ' + $(if ($Supervise) { '-Supervise (restarts a crashed or hung daemon, single host)' } else { '(read-only)' })
        Title       = Get-LiveWindowTitle -Name 'ArcaneCore status' -Configuration $configuration
        Command     = Get-LiveWindowCommand -Script $statusScript -Arguments $statusArgs
        Environment = $null; Wait = $null; Note = $null
    }
}

function New-DebuggerStep([string]$RealmPid, [string]$WorldPid) {
    $title = Get-LiveWindowTitle -Name 'ArcaneCore Win32 debugger' -Configuration $configuration
    [pscustomobject]@{
        Name        = 'debugger: attach-debugger.ps1 (Win32; never breaks in, KillOnExit off)'
        Title       = $title
        Command     = Get-LiveWindowCommand -Script $debuggerScript -Arguments @('-RealmPid', $RealmPid, '-WorldPid', $WorldPid, '-LiveHome', $LiveHome, '-WindowTitle', $title)
        Environment = $null; Wait = $null
        Note        = 'a debug event stops the target until this window continues it: see the help (hang kill)'
    }
}

$steps = @(New-DaemonStep 'Realm' $AuthPort; New-DaemonStep 'World' $WorldPort)
if (-not $NoStatus) { $steps += New-StatusStep }
if (-not $NoDebugger) { $steps += New-DebuggerStep '<realm pid>' '<world pid>' }

if ($planOnly) {
    $problems = @(Get-LiveLaunchProblems -Ports ([ordered]@{ Auth = $AuthPort; World = $WorldPort }))
    $header = @(
        "live home:  $LiveHome",
        "profile:    $LiveProfile",
        "build:      $configuration ($($build.Detail))",
        "ports:      auth $AuthPort, world $WorldPort",
        "diagnostics $Diagnostics, GC high-memory threshold $GCHighMemPercent%",
        $(if ($problems.Count -eq 0) { 'preflight:  ok (no live daemon running, ports free)' } else { 'preflight:  a real run would REFUSE: ' + ($problems -join ' ') })
    )
    Write-LivePlan -Heading 'start-single' -Header $header -Steps $steps
    exit 0
}

# ---- start ---------------------------------------------------------------------------------------------------------------------------
if (-not $IsWindows) { throw 'start-single.ps1 starts console windows and a Win32 debugger: Windows only. Use -Plan elsewhere.' }
$problems = @(Get-LiveLaunchProblems -Ports ([ordered]@{ Auth = $AuthPort; World = $WorldPort }))
if ($problems.Count -gt 0) { throw "refusing to start: $($problems -join ' ')" }
Write-Host "build $configuration ($($build.Detail))"

$daemons = [ordered]@{}
foreach ($step in $steps | Where-Object { $_.Name -like 'daemon:*' }) {
    $role = if ($step.Name -like '*Realm*') { 'Realm' } else { 'World' }
    $port = if ($role -eq 'Realm') { $AuthPort } else { $WorldPort }
    Write-Host "starting ArcaneCore.$role ..."
    $console = Start-LiveWindow -CommandLine $step.Command
    $daemons[$role] = Wait-LiveDaemonUp -Label "ArcaneCore.$role" -ParentPid $console.Id -ImageName "ArcaneCore.$role.exe" -Port $port -TimeoutSec $StartTimeoutSec
    try { $daemons[$role].PriorityClass = 'AboveNormal' } catch { Write-Warning "could not raise the priority of ArcaneCore.$role : $($_.Exception.Message)" }
    Write-Host "ArcaneCore.$role is up: pid $($daemons[$role].Id), port $port"
}
if (-not $NoStatus) {
    $status = Start-LiveWindow -CommandLine (New-StatusStep).Command
    Write-Host "status window: pid $($status.Id)"
}
if (-not $NoDebugger) {
    $debugger = Start-LiveWindow -CommandLine (New-DebuggerStep ([string]$daemons['Realm'].Id) ([string]$daemons['World'].Id)).Command
    Write-Host "debugger window: pid $($debugger.Id) (attached to realm $($daemons['Realm'].Id), world $($daemons['World'].Id))"
}
exit 0
