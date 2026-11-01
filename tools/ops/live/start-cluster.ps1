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
      With -Gateway the world child is given Cluster:Fencing:Enabled=true (an unfenced world skips the durable id allocator priming and
      collides on in-memory id counters once a second world exists). The single combined world is NOT fenced: a fenced world releases its
      character_ownership row only on a graceful stop, so the supervisor could not restart it after a crash or hang kill.
      Each child runs from <LiveHome>\bin in the profile directory with its own log (logs\<name>.debug.*), Console = -ChildConsole
      (Merged by default: the supervisor window shows the children's output prefixed [realm], [world], ...), and a hang timeout of
      -HangTimeoutSeconds. MinClusterProcesses defaults to the number of children.
    - With -Gateway: also ArcaneCore.Gateway (Role gateway, StartOrder 2; client port -GatewayPort), and the world child becomes a gateway
      worker (Role worker; Cluster:Worker:Enabled, BindAddress -WorkerAddress, Port -WorkerPort; docs/areas/gateway.md). The realm list row must name the gateway port (SQL; this script does not touch databases).
      -MapWorker additionally sets Cluster:Node:Role=MapWorker on that world (realm-wide singletons then run only while it holds their
      lease; docs/areas/gateway.md, "Node role and realm authority leases").
      The link secret (at least 16 characters) comes from -LinkSecret or the ARCANECORE_CLUSTER_LINK_SECRET environment variable, never a
      default. It goes only into the console's environment (Cluster__Worker__LinkSecret, Gateway__LinkSecret), never onto a command line, and
      is never printed. Every child of the supervisor inherits that environment, the Realm included. The same environment also reaches the
      hidden diagnostics sidecar and dotnet-counters (launch-daemon.ps1 starts them from the supervisor console) and is part of the
      memory of the supervisor and every child, so it appears in a full crash dump or hang dump: keep the dumps private.
  The world child is also given --Cluster:Supervisor:Enabled=false on its command line: it inherits the supervisor's Cluster__Supervisor__
  variables and a World that finds them with ARCANECORE_SUPERVISED_CHILD set refuses to start (exit 78).
  MinClusterProcesses equals the child count by default and the supervisor stops everything when fewer remain (StopWhenBelowMinimum is
  on): if it gives up on a crash-looping gateway or realm, the whole cluster stops, not just that child.
  In cluster mode the diagnostics sidecar (-Diagnostics Max) collects counters from the supervisor process, not from the world child.
  A child's own crash and fault dialogs (Diagnostics:CrashDialog / FaultDialog) do NOT appear in any -ChildConsole mode: the dialog
  needs standard input, output and error unredirected, and the supervisor starts every child with all three redirected
  (ClusterHostSupervisor.BuildStartInfo), also for Window. Window only gives the child its own console window (stdout still goes to the
  supervisor, so heartbeats and the hang watch work unchanged); it does not enable dialogs, which is why the default stays Merged.
  Instead, with Diagnostics:CrashDialog on for the SUPERVISOR (e.g. Diagnostics__CrashDialog=true in its console), the supervisor opens
  the crash dialog itself when a child exits unexpectedly with a nonzero code (SupervisorCrashDialog; docs/ops/invariants.md).
  Children inherit the console environment, so the same variable makes each child write its crash-report file (no box) for the supervisor to show.
  A second World in a Combined or Gateway topology (a standby worker, a second map worker) is not started: those worlds share one characters
  database and one realm-wide writer lock (WorldWriterGuard; on PostgreSQL a session advisory lock named after the characters database),
  and every non-standby world takes it at start, so a second one refuses to start. Concurrent map workers exist only in -Topology Split,
  where every world runs Cluster:Fencing:Scope=Worker: one durable ownership scope and one shard lock per world instead of the realm-wide lock.

  -Topology Split (implies the gateway; needs the link secret and the PostgreSQL secret file)
    A real start needs PostgreSQL (the secret file below; a real start refuses without it). Every world runs Cluster:Fencing:Scope=Worker, so
    the three worlds write the one characters database concurrently, each under its own shard lock and ownership scope. The world named by
    -LeaseHolder starts first and runs the schema DDL; the other worlds get Database:Upgrade:ValidateOnly=true. -Plan prints the layout.
    Starts the realm (order 0), three fenced worker worlds (orders 1 to 3) and the gateway (order 4, Gateway:Workers lists the three):
        worker-EK         node worker:EK         map 0 (Eastern Kingdoms)              no instance copies
        worker-Kalimdor   node worker:Kalimdor   map 1 (Kalimdor)                      no instance copies
        worker-instances  node worker:instances  the dungeon, raid and battleground maps, instance copies allowed
    The node ids and map lists are the three worker entries of ClusterNodeBridgeFeature.DefaultEndpointsSpec (a test pins the two tables
    together). Every world gets its own Cluster:Worker:NodeId, Cluster:Fencing:OwnerNode (the same text), link port (-WorkerPort + index),
    client port (-WorldPort + 100 * index) and heartbeat file; OwnedMaps lists are disjoint and are also given to the supervisor as
    Processes:<name>:Maps. Every world gets Cluster:Node:Role=MapWorker, so the realm authority lease picks the single runner of the realm-wide
    singletons (auction bot, AutoBroadcast, expiry timers); a Combined role would run them on every world. The world named by -LeaseHolder
    (default EK) is the gateway's login worker and the child with the supervisor RealmId (-RealmId; the supervisor
    needs unique ids, so the other worlds have none and are not addressed by a cross-world .server command).
    The characters, auth and world databases are PostgreSQL: host, port, user, password and database names come from the secret file of
    the user profile (<profile>\.arcanecore\pg-cutover.json, or ARCANECORE_PG_SECRET_FILE, or -PgSecretFile: a path, never a value; the
    same file pg-cutover.ps1 reads). They reach the children only as Database__<Component>__Provider / __ConnectionString variables of the
    supervisor console, never a command line, and -Plan prints neither them nor the file's contents. The link secret is read as for -Gateway.
    There is no separate peer secret: gateway and workers share the one link secret.

  What it waits for: the supervisor process stays alive, then each child (a child of the supervisor) listens on its port (Auth:Port, World:Port,
  gateway port) and stays up a few seconds. A child that exits while it starts is relaunched by the supervisor, so the wait follows the
  image's next process within -StartTimeoutSec instead of failing; the status and debugger windows still open. A refusal (below MinClusterProcesses, capacity) shows as the supervisor exiting: the script stops
  with a message and leaves the windows open.

  Hang evidence: the world child gets HangCapture = -HangCapture (default Dump): when the supervisor kills it for a missed heartbeat it first
  writes a full memory dump through the child's diagnostics endpoint (a crash dump by createdump never fires for a supervisor kill). Files
  are <name>-<pid>-<UTC stamp>.dmp in <LiveHome>\dumps, the directory LiveDiagnostics retention already caps (*.dmp); Stacks writes
  <name>-<pid>-<stamp>.stacks.txt with dotnet-stack to <LiveHome>\logs\hangstacks instead (that retention filter does not match the
  name, so those are not trimmed). A full dump is gigabytes and holds secrets (session keys, hashes in flight): the supervisor needs about a
  minute per 7 GB, so the default -HangCaptureTimeoutSeconds is 300 (the World accepts 1 to 3600; -Plan warns when Dump is chosen with a
  value under 120, which cuts a large dump off). The crash-dump variables (DOTNET_DbgEnableMiniDump, DOTNET_DbgMiniDumpName,
  ...) of the supervisor console are inherited by every child, so a crashing child dumps too, but all children share the World pattern
  world-%p-%t.dmp (and one createdump log): the pid in the name tells the processes apart.

  Status window: server-status.ps1 WITHOUT -Supervise (the cluster supervisor owns restarts) with the cluster's World/Auth ports and the world
  child's heartbeat file (-HeartbeatPath, default <LiveHome>\logs\world.heartbeat, passed to the world as Ops:Watchdog:Heartbeat:Mode=File).
  Debugger window: attach-debugger.ps1 -SupervisorPid: attaches to every ArcaneCore.* child of the supervisor but never to the supervisor
  itself, and keeps attaching to the children it relaunches (new PIDs), polling every 2 s; KillOnExit stays false, so closing it detaches. -NoDebugger skips it, -NoStatus skips
  the status window. It attaches for Debug and Release builds alike. Every window title carries the build found in <LiveHome>\bin:
  "ArcaneCore.World supervisor (Debug, live)" (Debug, Release, Mixed or unknown; read from assembly metadata without loading it).

  WARNING (debugger): a Win32 debugger stops the whole target on every debug event until it continues it (docs/ops/tooling.md). The
  debugger window continues each event at once and logs through bounded queues drained by separate writer threads, so a stalled debugger
  console (text selected in it, the machine paging) does not keep a child stopped: log lines are dropped and counted ("N lines dropped")
  instead of blocking. It is never attached to the supervisor, because a stopped supervisor stops reading its Merged children's stdout,
  which blocks every child and races the hang watch. What remains: (1) an exception-heavy child is slowed by one whole-process stop per
  exception (measured with a throwaway pwsh process: about 60000 exceptions a second alone, about 4500 attached, roughly 0.2 ms each), and
  a child would have to throw thousands a second for the whole -HangTimeoutSeconds (default 120 s; the watch is armed by the first beat) to
  starve its heartbeat; (2) with -HangCapture Dump, when a child that this debugger has stopped is judged hung, the supervisor's dump
  request goes through the child's diagnostics endpoint and can wait up to -HangCaptureTimeoutSeconds before the supervisor kills it
  anyway. Raise -HangTimeoutSeconds (0 turns hang detection off; allowed values 0 and 30 to 3600) or pass -NoDebugger when a debugging
  session must not cost a restart.

  -Standby and -Transfers (-Topology Split only; both off by default, and without them the launch is exactly as described above)
    -Standby    Whole-World handoff (docs/design/hot-reload-tiers.md 5.6, design _wf/handoff-control-DESIGN.md). Adds one standby child per world
                (key standby-EK, standby-Kalimdor, standby-instances; node id standby:<slug>; its own link port, client port and heartbeat file after the
                three worlds'), gives every world Cluster:Worker:Handoff:Enabled=true and the gateway Gateway:Handoff:Enabled=true with
                Gateway:Workers:<i>:StandbyNodeId/StandbyAddress/StandbyPort. A standby is started with Cluster:Worker:Standby=true and the shard of the
                world it stands behind (Cluster:Fencing:Shard=worker:<slug>, so its promotion takes over that world's durable scope; it has its own
                OwnerNode and NodeId), claims and locks nothing, and is validate-only for the schema. Standbys start in the gateway's start-order group and run
                without a hang watch. The supervisor children of the worlds are then named by node id (worker:EK, ...) instead of the key, because
                the gateway asks the supervisor to relaunch a retired world as the slot's next standby ("restart by child name"). The gateway needs the
                characters database (it already inherits the PostgreSQL environment; validate-only, a least-privilege role limited to cluster_world_handoff,
                cluster_transfer_* and character_ownership is enough).
    -Transfers  Per-player transfers between workers (a teleport to a map another worker owns): every world gets Cluster:Worker:Transfers:Enabled=true
                and the gateway Gateway:Transfers:Enabled=true. Combines with -Standby (the standbys get the key too, so a promoted one behaves like the world it replaced).

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
    # The World accepts 1 to 3600; a full dump of a large World takes minutes, so -Plan warns when Dump is chosen with less than 120.
    [ValidateRange(1, 3600)][int]$HangCaptureTimeoutSeconds = 300,
    [switch]$Gateway,
    [ValidateRange(1, 65535)][int]$GatewayPort = 8086,
    [ValidateRange(1, 65535)][int]$WorkerPort = 8095,
    [string]$WorkerAddress = '127.0.0.1',
    [switch]$MapWorker,
    # Combined: realm + one world (+ gateway with -Gateway). Split: realm + gateway + three fenced worker worlds on PostgreSQL (see the help).
    [ValidateSet('Combined', 'Split')][string]$Topology = 'Combined',
    # Split only: the world that takes the gateway's logins and carries the supervisor RealmId (every Split world is a MapWorker).
    [ValidateSet('EK', 'Kalimdor', 'instances')][string]$LeaseHolder = 'EK',
    # Split only: a path to the PostgreSQL secret file (default: ARCANECORE_PG_SECRET_FILE, else <profile>\.arcanecore\pg-cutover.json).
    [string]$PgSecretFile,
    # Split only: one standby child per world and the whole-World handoff keys (see the help). Off by default.
    [switch]$Standby,
    # Split only: the per-player transfer keys on the workers and the gateway (see the help). Off by default.
    [switch]$Transfers,
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
$split = $Topology -eq 'Split'
if ($split) {
    if ($MapWorker) { throw '-MapWorker does not combine with -Topology Split: every Split world is a map worker.' }
    # A real start is allowed: every Split world runs Cluster:Fencing:Scope=Worker (one durable ownership scope and one shard advisory lock per world), and the
    # databases are PostgreSQL by construction (the secret file below; a real start refuses without it).
    $Gateway = $true
} elseif ($PSBoundParameters.ContainsKey('LeaseHolder') -or $PSBoundParameters.ContainsKey('PgSecretFile')) {
    throw '-LeaseHolder and -PgSecretFile only mean something with -Topology Split.'
}
if (($Standby -or $Transfers) -and -not $split) {
    throw '-Standby and -Transfers only mean something with -Topology Split: they need several workers behind the gateway.'
}
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
if ($split) {
    # One row per split world: the table is the three Worker entries of ClusterNodeBridgeFeature.DefaultEndpointsSpec (node id and maps).
    # Instance copies are allowed only on the instances world.
    $splitWorlds = @(
        [pscustomobject]@{ Slug = 'EK'; NodeId = 'worker:EK'; Maps = @(0); AllowInstanceMaps = $false }
        [pscustomobject]@{ Slug = 'Kalimdor'; NodeId = 'worker:Kalimdor'; Maps = @(1); AllowInstanceMaps = $false }
        [pscustomobject]@{ Slug = 'instances'; NodeId = 'worker:instances'; AllowInstanceMaps = $true
            Maps = @(33, 34, 36, 43, 47, 48, 70, 90, 109, 129, 189, 209, 229, 230, 249, 289, 309, 329, 349, 389, 409, 429, 469, 489, 509, 529, 531, 533) }
    )
    $portMap.Remove('World')
    for ($i = 0; $i -lt $splitWorlds.Count; $i++) {
        $world = $splitWorlds[$i]
        $world | Add-Member -NotePropertyName WorkerPort -NotePropertyValue ($WorkerPort + $i)
        $world | Add-Member -NotePropertyName ClientPort -NotePropertyValue ($worldPortValue + 100 * $i)
        $world | Add-Member -NotePropertyName Key -NotePropertyValue "worker-$($world.Slug)"
        $portMap["World $($world.Slug)"] = $world.ClientPort
    }
    $portMap['Gateway'] = $GatewayPort
    foreach ($world in $splitWorlds) { $portMap["Worker link $($world.Slug)"] = $world.WorkerPort }
    if ($Standby) {
        # One standby per world, numbered after the worlds: its own link port (the gateway's control link), client port (used once it is promoted) and node id.
        for ($i = 0; $i -lt $splitWorlds.Count; $i++) {
            $world = $splitWorlds[$i]
            $index = $splitWorlds.Count + $i
            $world | Add-Member -NotePropertyName Standby -NotePropertyValue ([pscustomobject]@{
                Key = "standby-$($world.Slug)"; NodeId = "standby:$($world.Slug)"; WorkerPort = ($WorkerPort + $index); ClientPort = ($worldPortValue + 100 * $index)
            })
            $portMap["World standby $($world.Slug)"] = $world.Standby.ClientPort
            $portMap["Standby link $($world.Slug)"] = $world.Standby.WorkerPort
        }
    }
    if (@($portMap.Values | Where-Object { $_ -gt 65535 }).Count -gt 0) { throw '-WorldPort / -WorkerPort leave a split port above 65535.' }
} elseif ($Gateway) { $portMap['Gateway'] = $GatewayPort; $portMap['Worker link'] = $WorkerPort }
$seenPorts = @{}
foreach ($entry in $portMap.GetEnumerator()) {
    if ($seenPorts.ContainsKey([int]$entry.Value)) { throw "the $($entry.Key) port and the $($seenPorts[[int]$entry.Value]) port are both $($entry.Value): each listener needs its own port." }
    $seenPorts[[int]$entry.Value] = $entry.Key
}
$childCount = if ($split) { $(if ($Standby) { 8 } else { 5 }) } elseif ($Gateway) { 3 } else { 2 }
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

# Split: PostgreSQL settings from the secret file of the user profile (the file pg-cutover.ps1 reads). A plan reads it when it exists and only
# warns when it does not; a real start refuses. Values are never printed.
$pgEnvironment = [ordered]@{}
$pgSecrets = @()
$pgPlanWarnings = @()
$pgSecretPath = $null
if ($split) {
    $pgSecretPath = if ($PgSecretFile) { $PgSecretFile } elseif (Get-OpsEnv 'ARCANECORE_PG_SECRET_FILE') { Get-OpsEnv 'ARCANECORE_PG_SECRET_FILE' } else {
        $userHome = if ($env:USERPROFILE) { $env:USERPROFILE } else { $env:HOME }
        if (-not $userHome) { throw 'no user profile directory: pass -PgSecretFile or set ARCANECORE_PG_SECRET_FILE' }
        Join-Path (Join-Path $userHome '.arcanecore') 'pg-cutover.json'
    }
    $pgProblem = $null
    $pg = $null
    if (-not (Test-Path -LiteralPath $pgSecretPath -PathType Leaf)) {
        $pgProblem = "the PostgreSQL secret file '$pgSecretPath' does not exist (keys: Host, Port, Username, Password; see pg-cutover.ps1)"
    } else {
        try { $pgJson = Get-Content -Raw -LiteralPath $pgSecretPath | ConvertFrom-Json } catch { $pgJson = $null; $pgProblem = "the PostgreSQL secret file '$pgSecretPath' is not valid JSON" }
        if (-not $pgProblem) {
            $readPg = { param([string]$Key, [string]$Default) $p = $pgJson.PSObject.Properties[$Key]; if ($p -and -not [string]::IsNullOrWhiteSpace([string]$p.Value)) { [string]$p.Value } else { $Default } }
            $pg = [ordered]@{
                Host = & $readPg 'Host' ''; Port = & $readPg 'Port' '5432'; Username = & $readPg 'Username' ''; Password = & $readPg 'Password' ''
                SslMode = & $readPg 'SslMode' ''
                Auth = & $readPg 'AuthDatabase' 'arcane_auth'; Characters = & $readPg 'CharactersDatabase' 'arcane_characters'; World = & $readPg 'WorldDatabase' 'arcane_world'
            }
            foreach ($pgKey in 'Host', 'Username', 'Password') {
                if (-not $pg[$pgKey]) { $pgProblem = "the PostgreSQL secret file '$pgSecretPath' has no $pgKey"; break }
            }
        }
    }
    if ($pgProblem -and -not $planOnly) { throw $pgProblem }
    if ($pgProblem) { $pgPlanWarnings += "WARNING: $pgProblem; a real run would refuse." }
    if ($pg -and -not $pgProblem) { $pgSecrets += $pg.Password }
    foreach ($component in 'Auth', 'Characters', 'World') {
        $pgEnvironment["Database__${component}__Provider"] = 'PostgreSql'
        $pgEnvironment["Database__${component}__ConnectionString"] = if ($planOnly -or -not $pg) { "<PostgreSQL $component settings from the secret file>" } else {
            $cs = "Host=$($pg.Host);Port=$($pg.Port);Database=$($pg[$component]);Username=$($pg.Username);Password=$($pg.Password)"
            if ($pg.SslMode) { $cs += ";SSL Mode=$($pg.SslMode)" }
            $cs
        }
    }
}

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
function Add-Child([string]$Key, [string]$Role, [string]$Image, [int]$Order, [string[]]$ExtraArguments, [int]$ChildRealmId, [int]$Port = 0, [string]$Maps = '', [bool]$Capture = $false, [string]$DisplayName = '', [bool]$NoHangWatch = $false) {
    $log = Get-LiveLogSettings -LiveHome $LiveHome -LiveProfile $LiveProfile -LogStem $Key
    $children[$Key] = [pscustomobject]@{
        Key = $Key; Role = $Role; Executable = (Join-Path $bin $Image); Order = $Order; RealmId = $ChildRealmId
        Port = $Port; Maps = $Maps; Capture = ($Capture -or $ChildRealmId -gt 0); DisplayName = $DisplayName; NoHangWatch = $NoHangWatch
        Arguments = ConvertTo-LiveArgumentString (@(ConvertTo-LiveConfigArguments $log) + @($ExtraArguments))
        LogFile = Join-Path (Join-Path $LiveHome 'logs') "$Key.console.log"
    }
}
$realmArgs = @()
if ($PSBoundParameters.ContainsKey('AuthPort')) { $realmArgs += "--Auth:Port=$AuthPort" }
Add-Child 'realm' 'realm' 'ArcaneCore.Realm.exe' 0 $realmArgs 0 $authPortValue

$worldArgs = @('--Cluster:Supervisor:Enabled=false', '--Ops:Watchdog:Heartbeat:Mode=File', "--Ops:Watchdog:Heartbeat:FilePath=$HeartbeatPath")
if ($PSBoundParameters.ContainsKey('WorldPort')) { $worldArgs += "--World:Port=$WorldPort" }
if ($Gateway) {
    # Fenced only with a gateway (a second world writer is then possible). The single combined world stays unfenced: a fenced world frees its
    # character_ownership row only on a graceful stop, so after a crash or a hang kill the supervisor's restart would be refused as already owned.
    $worldArgs += '--Cluster:Fencing:Enabled=true'
    $worldArgs += @('--Cluster:Worker:Enabled=true', "--Cluster:Worker:BindAddress=$WorkerAddress", "--Cluster:Worker:Port=$WorkerPort",
        "--Cluster:Worker:RealmId=$RealmId")
    if ($MapWorker) { $worldArgs += '--Cluster:Node:Role=MapWorker' }
}
if ($split) {
    $heartbeatDirectory = Split-Path -Parent $HeartbeatPath
    $order = 1
    # The lease holder starts first, and the supervisor (Cluster:Supervisor:StartGroupReadySeconds, set below) waits for its first heartbeat before it launches the next start-order group,
    # so its schema DDL is done before the validate-only worlds check the schema.
    foreach ($world in @($splitWorlds | Where-Object { $_.Slug -eq $LeaseHolder }) + @($splitWorlds | Where-Object { $_.Slug -ne $LeaseHolder })) {
        # Fenced with its own OwnerNode and NodeId (the supervisor check needs both distinct); the same text for both.
        $heartbeatFile = if ($world.Slug -eq 'EK') { $HeartbeatPath } else { Join-Path $heartbeatDirectory "world-$($world.Slug).heartbeat" }
        $splitArgs = @('--Cluster:Supervisor:Enabled=false', '--Ops:Watchdog:Heartbeat:Mode=File', "--Ops:Watchdog:Heartbeat:FilePath=$heartbeatFile",
            "--World:Port=$($world.ClientPort)", '--Cluster:Fencing:Enabled=true', '--Cluster:Fencing:Scope=Worker', "--Cluster:Fencing:OwnerNode=$($world.NodeId)",
            '--Cluster:Worker:Enabled=true', "--Cluster:Worker:BindAddress=$WorkerAddress", "--Cluster:Worker:Port=$($world.WorkerPort)",
            "--Cluster:Worker:RealmId=$RealmId", "--Cluster:Worker:NodeId=$($world.NodeId)",
            "--Cluster:Worker:OwnedMaps=$($world.Maps -join ',')", "--Cluster:Worker:AllowInstanceMaps=$(([string]$world.AllowInstanceMaps).ToLowerInvariant())")
        $isLeaseHolder = $world.Slug -eq $LeaseHolder
        # One designated world runs the schema DDL (the lease holder, started first below); every other world only validates the schema.
        if (-not $isLeaseHolder) { $splitArgs += '--Database:Upgrade:ValidateOnly=true' }
        # Every world is a MapWorker so ClusterRoleFeatureFilter lets the realm authority lease choose the one singleton runner.
        $splitArgs += '--Cluster:Node:Role=MapWorker'
        if ($Standby) {
            $splitArgs += '--Cluster:Worker:Handoff:Enabled=true'
            # A retired world is relaunched by the supervisor as the slot's next standby (it only appends Cluster:Worker:Standby=true);
            # CharacterFencingOptions.ValidateWorkerScope rejects a standby without an explicit shard, so every world names its own.
            $splitArgs += "--Cluster:Fencing:Shard=$($world.NodeId)"
        }
        if ($Transfers) { $splitArgs += '--Cluster:Worker:Transfers:Enabled=true' }
        # With -Standby the supervisor child is named by node id: the gateway asks the supervisor to relaunch a retired world by that name.
        Add-Child $world.Key 'worker' 'ArcaneCore.World.exe' $order $splitArgs $(if ($isLeaseHolder) { $RealmId } else { 0 }) $world.ClientPort ($world.Maps -join ',') $true $(if ($Standby) { $world.NodeId } else { '' })
        $order++
    }
    if ($Standby) {
        # The standbys share the gateway's start-order group (no heartbeat of theirs is waited for), after the last world.
        foreach ($world in $splitWorlds) {
            $standbyWorld = $world.Standby
            $standbyHeartbeat = Join-Path $heartbeatDirectory "world-standby-$($world.Slug).heartbeat"
            $standbyArgs = @('--Cluster:Supervisor:Enabled=false', '--Ops:Watchdog:Heartbeat:Mode=File', "--Ops:Watchdog:Heartbeat:FilePath=$standbyHeartbeat",
                "--World:Port=$($standbyWorld.ClientPort)", '--Cluster:Fencing:Enabled=true', '--Cluster:Fencing:Scope=Worker', "--Cluster:Fencing:OwnerNode=$($standbyWorld.NodeId)",
                "--Cluster:Fencing:Shard=$($world.NodeId)",
                '--Cluster:Worker:Enabled=true', "--Cluster:Worker:BindAddress=$WorkerAddress", "--Cluster:Worker:Port=$($standbyWorld.WorkerPort)",
                "--Cluster:Worker:RealmId=$RealmId", "--Cluster:Worker:NodeId=$($standbyWorld.NodeId)",
                "--Cluster:Worker:OwnedMaps=$($world.Maps -join ',')", "--Cluster:Worker:AllowInstanceMaps=$(([string]$world.AllowInstanceMaps).ToLowerInvariant())",
                '--Cluster:Worker:Standby=true', '--Cluster:Worker:Handoff:Enabled=true', '--Database:Upgrade:ValidateOnly=true', '--Cluster:Node:Role=MapWorker')
            if ($Transfers) { $standbyArgs += '--Cluster:Worker:Transfers:Enabled=true' }
            Add-Child $standbyWorld.Key 'worker' 'ArcaneCore.World.exe' $order $standbyArgs 0 0 '' $false $standbyWorld.NodeId $true
        }
    }
    $loginNode = ($splitWorlds | Where-Object { $_.Slug -eq $LeaseHolder }).NodeId
    $gatewayArgs = @("--Gateway:Port=$GatewayPort", "--Gateway:RealmId=$RealmId", "--Gateway:LoginWorker=$loginNode")
    for ($i = 0; $i -lt $splitWorlds.Count; $i++) {
        $world = $splitWorlds[$i]
        $gatewayArgs += @("--Gateway:Workers:${i}:NodeId=$($world.NodeId)", "--Gateway:Workers:${i}:Address=$WorkerAddress", "--Gateway:Workers:${i}:Port=$($world.WorkerPort)")
        for ($m = 0; $m -lt $world.Maps.Count; $m++) { $gatewayArgs += "--Gateway:Workers:${i}:Maps:${m}=$($world.Maps[$m])" }
        if ($Standby) {
            $gatewayArgs += @("--Gateway:Workers:${i}:StandbyNodeId=$($world.Standby.NodeId)", "--Gateway:Workers:${i}:StandbyAddress=$WorkerAddress", "--Gateway:Workers:${i}:StandbyPort=$($world.Standby.WorkerPort)")
        }
    }
    if ($Standby) { $gatewayArgs += '--Gateway:Handoff:Enabled=true' }
    if ($Transfers) { $gatewayArgs += '--Gateway:Transfers:Enabled=true' }
    Add-Child 'gateway' 'gateway' 'ArcaneCore.Gateway.exe' $order $gatewayArgs 0 $GatewayPort
} else {
    Add-Child 'world' $(if ($Gateway) { 'worker' } else { 'combined' }) 'ArcaneCore.World.exe' 1 $worldArgs $RealmId $worldPortValue

    if ($Gateway) {
        Add-Child 'gateway' 'gateway' 'ArcaneCore.Gateway.exe' 2 @("--Gateway:Port=$GatewayPort", "--Gateway:WorkerAddress=$WorkerAddress",
            "--Gateway:WorkerPort=$WorkerPort", "--Gateway:RealmId=$RealmId") 0 $GatewayPort
    }
}

# The supervisor console's environment: launch-daemon.ps1 sets its own (diagnostics, logging, GC) inside the console; these are what start
# this script adds before the console starts.
$clusterEnvironment = [ordered]@{ Cluster__Supervisor__Enabled = 'true'; Cluster__Supervisor__MinClusterProcesses = [string]$MinClusterProcesses }
foreach ($child in $children.Values) {
    $prefix = "Cluster__Supervisor__Processes__$($child.Key)__"
    $clusterEnvironment[$prefix + 'Name'] = if ($child.DisplayName) { $child.DisplayName } else { $child.Key }
    $clusterEnvironment[$prefix + 'Role'] = $child.Role
    $clusterEnvironment[$prefix + 'ExecutablePath'] = $child.Executable
    $clusterEnvironment[$prefix + 'Arguments'] = $child.Arguments
    $clusterEnvironment[$prefix + 'WorkingDirectory'] = $LiveProfile
    $clusterEnvironment[$prefix + 'LogFilePath'] = $child.LogFile
    $clusterEnvironment[$prefix + 'Console'] = $ChildConsole
    $clusterEnvironment[$prefix + 'StartOrder'] = [string]$child.Order
    $clusterEnvironment[$prefix + 'HangTimeoutSeconds'] = if ($child.NoHangWatch) { '0' } else { [string]$HangTimeoutSeconds }
    if ($child.RealmId -gt 0) { $clusterEnvironment[$prefix + 'RealmId'] = [string]$child.RealmId }
    if ($child.Capture) { $clusterEnvironment[$prefix + 'HangCapture'] = $HangCapture }
    if ($child.Maps) { $clusterEnvironment[$prefix + 'Maps'] = $child.Maps }
}
# Split: each start-order group waits (up to 10 minutes) for the previous group's first heartbeat; a silent child only delays the start by that long.
if ($split) { $clusterEnvironment['Cluster__Supervisor__StartGroupReadySeconds'] = '600' }
if ($HangCapture -ne 'None') {
    $clusterEnvironment['Cluster__Supervisor__HangCaptureDirectory'] = $captureDirectory
    $clusterEnvironment['Cluster__Supervisor__HangCaptureTimeoutSeconds'] = [string]$HangCaptureTimeoutSeconds
    if ($stackTool) { $clusterEnvironment['Cluster__Supervisor__StackCaptureTool'] = $stackTool }
}
if ($Gateway) {
    $clusterEnvironment['Cluster__Worker__LinkSecret'] = $secret
    $clusterEnvironment['Gateway__LinkSecret'] = $secret
}
foreach ($entry in $pgEnvironment.GetEnumerator()) { $clusterEnvironment[$entry.Key] = $entry.Value }

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
        Wait        = $(if ($Port -gt 0) { "$name alive as a child of the supervisor and listening on port $Port (timeout $StartTimeoutSec s), then AboveNormal priority" } else { "$name alive as a child of the supervisor for 3 s (a standby opens no client listener until it is promoted; timeout $StartTimeoutSec s), then AboveNormal priority" })
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
        Name        = 'debugger: attach-debugger.ps1 -SupervisorPid (attaches to each child, never the supervisor; new PIDs after a restart included; KillOnExit off)'
        Title       = $title
        Command     = Get-LiveWindowCommand -Script $debuggerScript -Arguments @('-SupervisorPid', $SupervisorPid, '-LiveHome', $LiveHome, '-WindowTitle', $title)
        Environment = $null; Wait = $null
        Note        = "a debug event stops the target until this window continues it (at once; console output goes through a bounded queue, so a stalled console drops lines instead of stopping a child); an exception-heavy child is slowed per exception$(if ($HangCapture -eq 'Dump') { "; a dump request for a debugger-stopped child can wait up to $HangCaptureTimeoutSeconds s before the kill" }): see the help"
    }
}

$steps = @(New-SupervisorStep)
foreach ($child in $children.Values) {
    $steps += New-ChildStep $child $child.Port
}
if (-not $NoStatus) { $steps += New-StatusStep }
if (-not $NoDebugger) { $steps += New-DebuggerStep '<supervisor pid>' }

$launchWarnings = @($pgPlanWarnings)
if ($HangCapture -eq 'Dump' -and $HangCaptureTimeoutSeconds -lt 120) {
    $launchWarnings += "WARNING: -HangCapture Dump with -HangCaptureTimeoutSeconds $HangCaptureTimeoutSeconds (under 120): a full dump of the World usually takes longer, so the supervisor kills the child before the dump is complete."
}
$crashDumpPattern = (New-SupervisorStep).Environment['DOTNET_DbgMiniDumpName']
$secrets = @(@($secret) + $pgSecrets | Where-Object { $_ })
if ($planOnly) {
    $problems = @(Get-LiveLaunchProblems -Ports $portMap -ExtraImages $(if ($Gateway) { @('ArcaneCore.Gateway') } else { @() }))
    $header = @(
        "live home:  $LiveHome",
        "profile:    $LiveProfile",
        "build:      $configuration ($($build.Detail))",
        "ports:      $(($portMap.GetEnumerator() | ForEach-Object { "$($_.Key) $($_.Value)" }) -join ', ')",
        "topology:   $(if ($split) { "split: realm + gateway + 3 workers$(if ($Standby) { ' + 3 standbys (whole-World handoff on)' })$(if ($Transfers) { ', player transfers on' }) ($(($splitWorlds | ForEach-Object { "$($_.NodeId) maps $($_.Maps -join ',')" }) -join '; ')), MapWorker on worker:$LeaseHolder, PostgreSQL from $pgSecretPath" } elseif ($Gateway) { "realm + gateway + worker$(if ($MapWorker) { ' (MapWorker)' })" } else { 'realm + combined world' }), MinClusterProcesses $MinClusterProcesses, RealmId $RealmId, hang timeout $HangTimeoutSeconds s, child console $ChildConsole",
        "heartbeat:  $HeartbeatPath",
        $(if ($HangCapture -eq 'None') { 'hang capture: None (a supervisor kill leaves no evidence)' } else { "hang capture: $HangCapture of the world child into $captureDirectory (timeout $HangCaptureTimeoutSeconds s)" }),
        $(if ($crashDumpPattern) { "crash dumps: inherited by every child from the supervisor console (DOTNET_DbgEnableMiniDump=1), file pattern $crashDumpPattern shared by all children; the pid tells them apart" } else { 'crash dumps: not set by this launch (-Diagnostics Standard, or kept from the environment)' }),
        "diagnostics $Diagnostics, GC high-memory threshold $GCHighMemPercent%",
        $(if ($problems.Count -eq 0) { 'preflight:  ok (no live daemon running, ports free)' } else { 'preflight:  a real run would REFUSE: ' + ($problems -join ' ') })
    ) + $launchWarnings
    Write-LivePlan -Heading 'start-cluster' -Header $header -Steps $steps -Secrets $secrets
    exit 0
}

# ---- start ---------------------------------------------------------------------------------------------------------------------------
if (-not $IsWindows) { throw 'start-cluster.ps1 starts console windows and a Win32 debugger: Windows only. Use -Plan elsewhere.' }
$problems = @(Get-LiveLaunchProblems -Ports $portMap -ExtraImages $(if ($Gateway) { @('ArcaneCore.Gateway') } else { @() }))
if ($problems.Count -gt 0) { throw "refusing to start: $($problems -join ' ')" }
Write-Host "build $configuration ($($build.Detail))"
foreach ($launchWarning in $launchWarnings) { Write-Warning $launchWarning }
Write-Host 'starting the cluster supervisor ...'
$supervisorStep = New-SupervisorStep
# The console inherits these; the secret is in this process's environment only for the duration of the call.
$console = Invoke-WithLiveEnvironment -Variables $clusterEnvironment -ScriptBlock { Start-LiveWindow -CommandLine $supervisorStep.Command }
$supervisor = Wait-LiveDaemonUp -Label 'the cluster supervisor (ArcaneCore.World)' -ParentPid $console.Id -ImageName 'ArcaneCore.World.exe' -TimeoutSec $StartTimeoutSec
Write-Host "supervisor is up: pid $($supervisor.Id)"
$running = @($supervisor)
foreach ($child in $children.Values) {
    $port = $child.Port
    $process = Wait-LiveDaemonUp -Label "child '$($child.Key)'" -ParentPid $supervisor.Id -ImageName ([IO.Path]::GetFileName($child.Executable)) -Port $port -TimeoutSec $StartTimeoutSec -WatchPid $supervisor.Id -FollowRelaunch
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
