<#
.SYNOPSIS
  Starts the local live server as a cluster: ArcaneCore.World as the host supervisor (Cluster:Supervisor:Enabled) in its own console, which
  launches the Realm and the World as children, then opens a status window and a debugger that follows the children across restarts.
  **Starts real server processes** (not with -Plan / -WhatIf).
.DESCRIPTION
  What it starts
    - The supervisor: launch-daemon.ps1 -Role World -ClusterSupervisor in its own console (so the diagnostics environment, ARCANECORE_LIVE_SERVER=1
      and the GC settings are those of the other live launchers, and the children inherit them; the supervisor logs to logs\supervisor.debug.*).
    - Its children, by supervisor configuration given as Cluster__Supervisor__... environment variables of that console (built from the
      parameters; no configuration file is edited):
        realm    Role realm,    StartOrder 0   ArcaneCore.Realm
        world    Role combined, StartOrder 1   ArcaneCore.World, RealmId -RealmId
      Each child runs from <LiveHome>\bin in the profile directory with its own log (logs\<name>.debug.*), Console = -ChildConsole
      (Merged by default: the supervisor window shows the children's output prefixed [realm], [world], ...), and a hang timeout of
      -HangTimeoutSeconds. MinClusterProcesses defaults to the number of children.
    - With -Gateway: also ArcaneCore.Gateway (Role gateway, StartOrder 2; client port -GatewayPort), and the world child becomes a gateway
      worker (Role worker; Cluster:Worker:Enabled, BindAddress -WorkerAddress, Port -WorkerPort, Cluster:Fencing:Enabled, which worker mode
      requires; docs/areas/gateway.md). The realm list row must name the gateway port (SQL; this script does not touch databases).
      -MapWorker additionally sets Cluster:Node:Role=MapWorker on that world (realm-wide singletons then run only while it holds their
      lease; docs/areas/gateway.md, "Node role and realm authority leases").
      The link secret (at least 16 characters) comes from -LinkSecret or the ARCANECORE_CLUSTER_LINK_SECRET environment variable, never a
      default. It goes only into the console's environment (Cluster__Worker__LinkSecret, Gateway__LinkSecret), never onto a command line, and
      is never printed. Every child of the supervisor inherits that environment, the Realm included.
  The world child is also given --Cluster:Supervisor:Enabled=false on its command line: it inherits the supervisor's Cluster__Supervisor__
  variables and a World that finds them with ARCANECORE_SUPERVISED_CHILD set refuses to start (exit 78).
  A second World (a standby worker, several map workers) is not started: all worlds of a realm share one characters database and one
  writer lock, so only a worker, the standby handoff target, can coexist, and that is not hosted by a launcher yet.

  What it waits for: the supervisor process stays alive, then each child (a child of the supervisor) listens on its port (Auth:Port, World:Port,
  gateway port) and stays up a few seconds. A refusal (below MinClusterProcesses, capacity) shows as the supervisor exiting: the script stops
  with a message and leaves the windows open.

  Hang evidence: the world child gets HangCapture = -HangCapture (default Dump): when the supervisor kills it for a missed heartbeat it first
  writes a full memory dump through the child's diagnostics endpoint (a crash dump by createdump never fires for a supervisor kill). Files
  are <name>-<pid>-<UTC stamp>.dmp in <LiveHome>\dumps, the directory LiveDiagnostics retention already caps (*.dmp); Stacks writes
  <name>-<pid>-<stamp>.stacks.txt with dotnet-stack to <LiveHome>\logs\hangstacks instead (that retention filter does not match the
  name, so those are not trimmed). A full dump is gigabytes and holds secrets (session keys, hashes in flight): the supervisor needs about a
  minute per 7 GB, so -HangCaptureTimeoutSeconds is 300 or more. The crash-dump variables (DOTNET_DbgEnableMiniDump, DOTNET_DbgMiniDumpName,
  ...) of the supervisor console are inherited by every child, so a crashing child dumps too, but all children share the World pattern
  world-%p-%t.dmp (and one createdump log): the pid in the name tells the processes apart.

  Status window: server-status.ps1 WITHOUT -Supervise (the cluster supervisor owns restarts) with the cluster's World/Auth ports and the world
  child's heartbeat file (-HeartbeatPath, default <LiveHome>\logs\world.heartbeat, passed to the world as Ops:Watchdog:Heartbeat:Mode=File).
  Debugger window: attach-debugger.ps1 -SupervisorPid: attaches to the supervisor and to every ArcaneCore.* child, and keeps attaching to the
  children it relaunches (new PIDs), polling every 2 s; KillOnExit stays false, so closing it detaches. -NoDebugger skips it, -NoStatus skips
  the status window. It attaches for Debug and Release builds alike. Every window title carries the build found in <LiveHome>\bin:
  "ArcaneCore.World supervisor (Debug, live)" (Debug, Release, Mixed or unknown; read from assembly metadata without loading it).

  WARNING (debugger and the supervisor's hang kill): a Win32 debugger stops the whole target on every debug event until the debugger console
  continues it (docs/ops/tooling.md). The supervisor drains and kills (and relaunches) a child that sends no `alive` heartbeat for
  -HangTimeoutSeconds (default 120 s; the watch is armed by the first beat). A burst of CLR first-chance exceptions alone does not do that:
  each event is a round trip that this debugger continues at once (it logs the first 10 and every 500th), so the child slows down: measured
  with a throwaway pwsh process throwing and catching exceptions in a tight loop, about 60000 a second alone and about 4500 a second attached
  (roughly 0.2 ms of whole-process stop per exception). A World would have to throw several thousand exceptions a second, sustained for the
  whole timeout, to starve the heartbeat; ordinary rates (tens to hundreds a second) cost a few percent. What does cause a hang kill is the
  debugger console itself stopping (text selected in it, the machine paging or out of memory): the target then stays stopped at its next
  event and is killed 120 s later and relaunched. A relaunched child is attached again, but the kill is real. Raise -HangTimeoutSeconds
  (or use 0 for no hang detection; allowed values 0 and 30 to 3600) or pass -NoDebugger when a debugging session must not cost a restart.

  Locations come from ARCANECORE_LIVE_HOME and ARCANECORE_LIVE_PROFILE (or -LiveHome / -Profile); see docs/ops/tooling.md.
.PARAMETER Plan
  Print every process, window title, command line, environment variable (secrets masked) and wait that would be used, then exit 0
  without starting anything. -WhatIf does the same.
.EXAMPLE
  pwsh -NoProfile -File tools/ops/live/start-cluster.ps1 -Plan
.EXAMPLE
  pwsh -NoProfile -File tools/ops/live/start-cluster.ps1 -Gateway -MapWorker -NoDebugger
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$LiveHome,
    [Alias('Profile')][string]$LiveProfile,
    [ValidateSet('Standard', 'Max')][string]$Diagnostics = 'Max',
    [ValidateRange(50, 99)][int]$GCHighMemPercent = 97,
    # Passed to the Realm (--Auth:Port) and the World (--World:Port) children only when given; otherwise the profile's values (3724 / 8085) apply.
    [ValidateRange(1, 65535)][int]$AuthPort,
    [ValidateRange(1, 65535)][int]$WorldPort,
    # The realm id of the world child (Cluster:Supervisor:Processes:world:RealmId and, with -Gateway, Cluster:Worker:RealmId / Gateway:RealmId).
    [ValidateRange(1, 65535)][int]$RealmId = 1,
    # Default: the number of children. The supervisor refuses to start when it can admit fewer.
    [ValidateRange(1, 9)][int]$MinClusterProcesses,
    # Seconds without a heartbeat before the supervisor drains, kills and relaunches a child: 0 (off) or 30 to 3600. See the debugger warning.
    [ValidateScript({ $_ -eq 0 -or ($_ -ge 30 -and $_ -le 3600) })][int]$HangTimeoutSeconds = 120,
    [ValidateSet('Merged', 'Window', 'None')][string]$ChildConsole = 'Merged',
    # What the supervisor captures from the world child before it kills it as hung (see the help). Stacks needs dotnet-stack.
    [ValidateSet('None', 'Stacks', 'Dump')][string]$HangCapture = 'Dump',
    [ValidateRange(300, 3600)][int]$HangCaptureTimeoutSeconds = 300,
    [switch]$Gateway,
    [ValidateRange(1, 65535)][int]$GatewayPort = 8086,
    [ValidateRange(1, 65535)][int]$WorkerPort = 8095,
    [string]$WorkerAddress = '127.0.0.1',
    [switch]$MapWorker,
    # Prefer the environment variable: a parameter value shows in this shell's history.
    [string]$LinkSecret,
    [string]$HeartbeatPath,
    [ValidateRange(10, 3600)][int]$StartTimeoutSec = 600,
    [switch]$NoStatus,
    [switch]$NoDebugger,
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
$images = @('Realm', 'World')
if ($Gateway) { $images += 'Gateway' }
foreach ($name in $images) {
    $exe = Join-Path $bin "ArcaneCore.$name.exe"
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "$exe not found: deploy a build to the live home first (tools/ops/live/redeploy.ps1)." }
}
if (-not (Test-Path -LiteralPath (Join-Path $LiveProfile 'appsettings.json') -PathType Leaf)) { throw "no appsettings.json in the profile directory $LiveProfile" }
if ($MapWorker -and -not $Gateway) { throw '-MapWorker needs -Gateway: a map worker is the world a gateway links to (Cluster:Node:Role MapWorker needs Cluster:Worker:Enabled).' }
$address = $null
if (-not [System.Net.IPAddress]::TryParse($WorkerAddress, [ref]$address)) { throw "-WorkerAddress '$WorkerAddress' is not an IP address." }
$secret = $null
if ($Gateway) {
    $secret = if ($LinkSecret) { $LinkSecret } else { Get-OpsEnv 'ARCANECORE_CLUSTER_LINK_SECRET' }
    if (-not $secret) { throw '-Gateway needs the link secret: set ARCANECORE_CLUSTER_LINK_SECRET (or pass -LinkSecret). It has no default.' }
    if ($secret.Length -lt 16) { throw 'the link secret must be at least 16 characters (the worker and the gateway refuse to start otherwise).' }
} elseif ($LinkSecret) {
    throw '-LinkSecret is the gateway link secret: it only means something with -Gateway.'
}
$profilePorts = Get-LiveProfilePorts -LiveProfile $LiveProfile
$authPortValue = if ($PSBoundParameters.ContainsKey('AuthPort')) { $AuthPort } else { $profilePorts.Auth }
$worldPortValue = if ($PSBoundParameters.ContainsKey('WorldPort')) { $WorldPort } else { $profilePorts.World }
$portMap = [ordered]@{ Auth = $authPortValue; World = $worldPortValue }
if ($Gateway) { $portMap['Gateway'] = $GatewayPort; $portMap['Worker link'] = $WorkerPort }
$seenPorts = @{}
foreach ($entry in $portMap.GetEnumerator()) {
    if ($seenPorts.ContainsKey([int]$entry.Value)) { throw "the $($entry.Key) port and the $($seenPorts[[int]$entry.Value]) port are both $($entry.Value): each listener needs its own port." }
    $seenPorts[[int]$entry.Value] = $entry.Key
}
$childCount = if ($Gateway) { 3 } else { 2 }
if (-not $PSBoundParameters.ContainsKey('MinClusterProcesses')) { $MinClusterProcesses = $childCount }
if ($MinClusterProcesses -gt $childCount) { throw "-MinClusterProcesses $MinClusterProcesses is more than the $childCount process(es) this launch configures: the supervisor would refuse to start." }
$captureDirs = Get-LiveDiagnosticsDirectories -LiveHome $LiveHome
$captureDirectory = if ($HangCapture -eq 'Stacks') { $captureDirs.HangStacks } else { $captureDirs.Dumps }
$stackTool = $null
if ($HangCapture -eq 'Stacks') {
    $stackTool = Find-DotnetTool -Name 'dotnet-stack' -DotnetRoot (Get-LiveDotnetRoot) -UserHome $(if ($env:USERPROFILE) { $env:USERPROFILE } else { $env:HOME })
    if (-not $stackTool) { throw '-HangCapture Stacks needs dotnet-stack (dotnet tool install -g dotnet-stack); it was not found under DOTNET_ROOT\tools or the user profile .dotnet\tools.' }
}
if (-not $HeartbeatPath) { $HeartbeatPath = Join-Path (Join-Path $LiveHome 'logs') 'world.heartbeat' }

# ---- the plan ------------------------------------------------------------------------------------------------------------------------
$build = Get-LiveBuildConfiguration -BinDir $bin
$configuration = $build.Configuration
$launcher = Join-Path $PSScriptRoot 'launch-daemon.ps1'
$diagDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'diag'
$statusScript = Join-Path $diagDir 'server-status.ps1'
$debuggerScript = Join-Path $diagDir 'attach-debugger.ps1'

# One entry per supervised child: key -> settings. Arguments are command-line tokens of that child (a daemon's command line beats its
# environment, so each child gets its own log file and the World children switch the inherited Cluster:Supervisor:Enabled off).
$children = [ordered]@{}
function Add-Child([string]$Key, [string]$Role, [string]$Image, [int]$Order, [string[]]$ExtraArguments, [int]$ChildRealmId) {
    $log = Get-LiveLogSettings -LiveHome $LiveHome -LiveProfile $LiveProfile -LogStem $Key
    $children[$Key] = [pscustomobject]@{
        Key = $Key; Role = $Role; Executable = (Join-Path $bin $Image); Order = $Order; RealmId = $ChildRealmId
        Arguments = ConvertTo-LiveArgumentString (@(ConvertTo-LiveConfigArguments $log) + @($ExtraArguments))
        LogFile = Join-Path (Join-Path $LiveHome 'logs') "$Key.console.log"
    }
}
$realmArgs = @()
if ($PSBoundParameters.ContainsKey('AuthPort')) { $realmArgs += "--Auth:Port=$AuthPort" }
Add-Child 'realm' 'realm' 'ArcaneCore.Realm.exe' 0 $realmArgs 0

$worldArgs = @('--Cluster:Supervisor:Enabled=false', '--Ops:Watchdog:Heartbeat:Mode=File', "--Ops:Watchdog:Heartbeat:FilePath=$HeartbeatPath")
if ($PSBoundParameters.ContainsKey('WorldPort')) { $worldArgs += "--World:Port=$WorldPort" }
if ($Gateway) {
    $worldArgs += @('--Cluster:Worker:Enabled=true', "--Cluster:Worker:BindAddress=$WorkerAddress", "--Cluster:Worker:Port=$WorkerPort",
        "--Cluster:Worker:RealmId=$RealmId", '--Cluster:Fencing:Enabled=true')
    if ($MapWorker) { $worldArgs += '--Cluster:Node:Role=MapWorker' }
}
Add-Child 'world' $(if ($Gateway) { 'worker' } else { 'combined' }) 'ArcaneCore.World.exe' 1 $worldArgs $RealmId

if ($Gateway) {
    Add-Child 'gateway' 'gateway' 'ArcaneCore.Gateway.exe' 2 @("--Gateway:Port=$GatewayPort", "--Gateway:WorkerAddress=$WorkerAddress",
        "--Gateway:WorkerPort=$WorkerPort", "--Gateway:RealmId=$RealmId") 0
}

# The supervisor console's environment: launch-daemon.ps1 sets its own (diagnostics, logging, GC) inside the console; these are what start
# this script adds before the console starts.
$clusterEnvironment = [ordered]@{ Cluster__Supervisor__Enabled = 'true'; Cluster__Supervisor__MinClusterProcesses = [string]$MinClusterProcesses }
foreach ($child in $children.Values) {
    $prefix = "Cluster__Supervisor__Processes__$($child.Key)__"
    $clusterEnvironment[$prefix + 'Name'] = $child.Key
    $clusterEnvironment[$prefix + 'Role'] = $child.Role
    $clusterEnvironment[$prefix + 'ExecutablePath'] = $child.Executable
    $clusterEnvironment[$prefix + 'Arguments'] = $child.Arguments
    $clusterEnvironment[$prefix + 'WorkingDirectory'] = $LiveProfile
    $clusterEnvironment[$prefix + 'LogFilePath'] = $child.LogFile
    $clusterEnvironment[$prefix + 'Console'] = $ChildConsole
    $clusterEnvironment[$prefix + 'StartOrder'] = [string]$child.Order
    $clusterEnvironment[$prefix + 'HangTimeoutSeconds'] = [string]$HangTimeoutSeconds
    if ($child.RealmId -gt 0) {
        $clusterEnvironment[$prefix + 'RealmId'] = [string]$child.RealmId
        $clusterEnvironment[$prefix + 'HangCapture'] = $HangCapture
    }
}
if ($HangCapture -ne 'None') {
    $clusterEnvironment['Cluster__Supervisor__HangCaptureDirectory'] = $captureDirectory
    $clusterEnvironment['Cluster__Supervisor__HangCaptureTimeoutSeconds'] = [string]$HangCaptureTimeoutSeconds
    if ($stackTool) { $clusterEnvironment['Cluster__Supervisor__StackCaptureTool'] = $stackTool }
}
if ($Gateway) {
    $clusterEnvironment['Cluster__Worker__LinkSecret'] = $secret
    $clusterEnvironment['Gateway__LinkSecret'] = $secret
}

function New-SupervisorStep {
    $environment = Get-LiveDaemonEnvironmentPlan -Role World -LiveHome $LiveHome -LiveProfile $LiveProfile -Diagnostics $Diagnostics -GCHighMemPercent $GCHighMemPercent -LogStem 'supervisor'
    foreach ($entry in $clusterEnvironment.GetEnumerator()) { $environment[$entry.Key] = $entry.Value }
    [pscustomobject]@{
        Name        = 'supervisor: ArcaneCore.World as the cluster host supervisor in its own console (launch-daemon.ps1 -ClusterSupervisor)'
        Title       = Get-LiveWindowTitle -Name 'ArcaneCore.World supervisor' -Configuration $configuration
        Command     = Get-LiveWindowCommand -Script $launcher -Arguments @('-Role', 'World', '-ClusterSupervisor', '-LiveHome', $LiveHome, '-Profile', $LiveProfile, '-Diagnostics', $Diagnostics, '-GCHighMemPercent', [string]$GCHighMemPercent)
        Environment = $environment
        Wait        = "ArcaneCore.World.exe alive as a child of that console for 3 s (it has no listener); the Cluster__ variables below configure it and its children"
        Note        = $null
    }
}

function New-ChildStep($Child, [int]$Port) {
    $name = [IO.Path]::GetFileName($Child.Executable)
    [pscustomobject]@{
        Name        = "child '$($Child.Key)' (role $($Child.Role), start order $($Child.Order)): started by the supervisor, not by this script"
        Title       = $(if ($ChildConsole -eq 'Window') { '(its own console window, title set by the OS)' } else { "(no window: console $ChildConsole, output prefixed [$($Child.Key)] in the supervisor window)" })
        Program     = $Child.Executable
        Command     = $Child.Arguments
        Environment = $null
        Wait        = "$name alive as a child of the supervisor and listening on port $Port (timeout $StartTimeoutSec s), then AboveNormal priority"
        Note        = "working directory is the profile; the supervisor adds ARCANECORE_SUPERVISED_CHILD=$($Child.Key), ARCANECORE_SUPERVISOR_PROTOCOL=1, a per-run ARCANECORE_SUPERVISOR_TOKEN$(if ($Child.RealmId -gt 0) { ", ARCANECORE_SUPERVISED_REALM_ID=$($Child.RealmId)" }), and the child inherits the supervisor console's environment"
    }
}

function New-StatusStep {
    $title = Get-LiveWindowTitle -Name 'ArcaneCore status' -Configuration $configuration
    [pscustomobject]@{
        Name        = 'status: server-status.ps1 (read-only; the cluster supervisor owns restarts)'
        Title       = $title
        Command     = Get-LiveWindowCommand -Script $statusScript -Arguments @('-Profile', $LiveProfile, '-WorldPort', [string]$worldPortValue, '-AuthPort', [string]$authPortValue, '-HeartbeatPath', $HeartbeatPath, '-WindowTitle', $title)
        Environment = $null; Wait = $null; Note = $null
    }
}

function New-DebuggerStep([string]$SupervisorPid) {
    $title = Get-LiveWindowTitle -Name 'ArcaneCore Win32 debugger' -Configuration $configuration
    [pscustomobject]@{
        Name        = 'debugger: attach-debugger.ps1 -SupervisorPid (attaches to the supervisor and to each child, new PIDs after a restart included; KillOnExit off)'
        Title       = $title
        Command     = Get-LiveWindowCommand -Script $debuggerScript -Arguments @('-SupervisorPid', $SupervisorPid, '-LiveHome', $LiveHome, '-WindowTitle', $title)
        Environment = $null; Wait = $null
        Note        = "a debug event stops the target until this window continues it; a child silent for $(if ($HangTimeoutSeconds -eq 0) { '(hang detection is off)' } else { "$HangTimeoutSeconds s" }) is killed as hung: see the help"
    }
}

$steps = @(New-SupervisorStep)
foreach ($child in $children.Values) {
    $port = switch ($child.Key) { 'realm' { $authPortValue } 'world' { $worldPortValue } default { $GatewayPort } }
    $steps += New-ChildStep $child $port
}
if (-not $NoStatus) { $steps += New-StatusStep }
if (-not $NoDebugger) { $steps += New-DebuggerStep '<supervisor pid>' }

$crashDumpPattern = (New-SupervisorStep).Environment['DOTNET_DbgMiniDumpName']
$secrets = if ($secret) { @($secret) } else { @() }
if ($planOnly) {
    $problems = @(Get-LiveLaunchProblems -Ports $portMap -ExtraImages $(if ($Gateway) { @('ArcaneCore.Gateway') } else { @() }))
    $header = @(
        "live home:  $LiveHome",
        "profile:    $LiveProfile",
        "build:      $configuration ($($build.Detail))",
        "ports:      $(($portMap.GetEnumerator() | ForEach-Object { "$($_.Key) $($_.Value)" }) -join ', ')",
        "topology:   $(if ($Gateway) { "realm + gateway + worker$(if ($MapWorker) { ' (MapWorker)' })" } else { 'realm + combined world' }), MinClusterProcesses $MinClusterProcesses, RealmId $RealmId, hang timeout $HangTimeoutSeconds s, child console $ChildConsole",
        "heartbeat:  $HeartbeatPath",
        $(if ($HangCapture -eq 'None') { 'hang capture: None (a supervisor kill leaves no evidence)' } else { "hang capture: $HangCapture of the world child into $captureDirectory (timeout $HangCaptureTimeoutSeconds s)" }),
        $(if ($crashDumpPattern) { "crash dumps: inherited by every child from the supervisor console (DOTNET_DbgEnableMiniDump=1), file pattern $crashDumpPattern shared by all children; the pid tells them apart" } else { 'crash dumps: not set by this launch (-Diagnostics Standard, or kept from the environment)' }),
        "diagnostics $Diagnostics, GC high-memory threshold $GCHighMemPercent%",
        $(if ($problems.Count -eq 0) { 'preflight:  ok (no live daemon running, ports free)' } else { 'preflight:  a real run would REFUSE: ' + ($problems -join ' ') })
    )
    Write-LivePlan -Heading 'start-cluster' -Header $header -Steps $steps -Secrets $secrets
    exit 0
}

# ---- start ---------------------------------------------------------------------------------------------------------------------------
if (-not $IsWindows) { throw 'start-cluster.ps1 starts console windows and a Win32 debugger: Windows only. Use -Plan elsewhere.' }
$problems = @(Get-LiveLaunchProblems -Ports $portMap -ExtraImages $(if ($Gateway) { @('ArcaneCore.Gateway') } else { @() }))
if ($problems.Count -gt 0) { throw "refusing to start: $($problems -join ' ')" }
Write-Host "build $configuration ($($build.Detail))"
Write-Host 'starting the cluster supervisor ...'
$supervisorStep = New-SupervisorStep
# The console inherits these; the secret is in this process's environment only for the duration of the call.
$console = Invoke-WithLiveEnvironment -Variables $clusterEnvironment -ScriptBlock { Start-LiveWindow -CommandLine $supervisorStep.Command }
$supervisor = Wait-LiveDaemonUp -Label 'the cluster supervisor (ArcaneCore.World)' -ParentPid $console.Id -ImageName 'ArcaneCore.World.exe' -TimeoutSec $StartTimeoutSec
Write-Host "supervisor is up: pid $($supervisor.Id)"
$running = @($supervisor)
foreach ($child in $children.Values) {
    $port = switch ($child.Key) { 'realm' { $authPortValue } 'world' { $worldPortValue } default { $GatewayPort } }
    $process = Wait-LiveDaemonUp -Label "child '$($child.Key)'" -ParentPid $supervisor.Id -ImageName ([IO.Path]::GetFileName($child.Executable)) -Port $port -TimeoutSec $StartTimeoutSec -WatchPid $supervisor.Id
    Write-Host "child '$($child.Key)' is up: pid $($process.Id), port $port"
    $running += $process
}
foreach ($process in $running) {
    try { $process.PriorityClass = 'AboveNormal' } catch { Write-Warning "could not raise the priority of pid $($process.Id): $($_.Exception.Message)" }
}
if (-not $NoStatus) {
    $status = Start-LiveWindow -CommandLine (New-StatusStep).Command
    Write-Host "status window: pid $($status.Id)"
}
if (-not $NoDebugger) {
    $debugger = Start-LiveWindow -CommandLine (New-DebuggerStep ([string]$supervisor.Id)).Command
    Write-Host "debugger window: pid $($debugger.Id) (follows supervisor $($supervisor.Id) and its children, restarts included)"
}
exit 0
