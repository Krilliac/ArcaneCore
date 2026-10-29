# Redeploys the local live server from a built worktree, by the cheapest safe path (docs/ops/redeploy.md). A planner (RedeployPlan.ps1)
# compares the worktree with what the live World runs (the deploy record in <LiveHome>\bin, the profile's configuration, the module
# directory) and picks one of:
#   None     nothing that reaches the daemons changed: nothing is touched
#   Hot      only live configuration keys, content tables .reload supports and allowlisted hot modules changed: applied through the World's
#            control pipe (World:Control:Pipe:Enabled) with .reload config / .reload <table> / .hotmodule load|reload, no restart
#   Restart  anything else: graceful Ctrl-C stop of World then Realm, a snapshot of <LiveHome>\bin, an optional world.db swap, a copy of
#            both Debug builds (and changed approved modules), then a start of each daemon in its own console at AboveNormal priority
#   Blocked  a changed hot module is not built, stale or not on the allowlist: nothing is touched (exit 2)
# A hot path that turns out to be unavailable (no pipe, command missing, busy, no answer) falls back to the restart; a change the World
# rejects (validation, a module refused) stops with exit 3 and the World keeps running what it ran.
#   pwsh -NoProfile -File tools/ops/live/redeploy.ps1 [-Src <worktree>] [-Plan|-WhatIf] [-Json] [-ForceRestart] [-ContentTables <names>]
#        [-WorldDbSwap <world.db> [-WorldDb <target>]] [-LiveHome <dir>] [-Profile <dir>] [-Diagnostics Standard|Max]
# **Touches the live server** (not with -Plan/-WhatIf). -Src defaults to the worktree this script is in; build it first (dotnet build
# ArcaneCore.slnx, Debug: live stays a Debug build). Uses ARCANECORE_LIVE_HOME and ARCANECORE_LIVE_PROFILE when the parameters are omitted.
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Src = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path,
    # A refreshed world database (for example a content-importer refresh run on a copy) put in place while the World is stopped.
    # The old one, with its -wal/-shm, is kept beside it as world.db.bak-<stamp>. Always a restart.
    [string]$WorldDbSwap = '',
    # The world database the swap replaces. Default: the Data Source of Database:World:ConnectionString in the profile's
    # appsettings.json (the file the World actually opens, which need not be in the profile directory).
    [string]$WorldDb = '',
    [string]$LiveHome,
    [Alias('Profile')][string]$LiveProfile,
    # Reloadable content names (.reload <name>: spell_template, npc_vendor, ...) whose rows were changed in the live world database.
    [string[]]$ContentTables = @(),
    # Print the plan and stop (the same as -WhatIf).
    [switch]$Plan,
    # Also print the plan as JSON.
    [switch]$Json,
    # Hard restart whatever the plan says (also past a Blocked plan).
    [switch]$ForceRestart,
    # The reason announced with each hot step.
    [string]$Reason = 'redeploy',
    [int]$WorldStopTimeoutSec = 180,
    [int]$RealmStopTimeoutSec = 60,
    # The diagnostics level of the restarted daemons (launch-daemon.ps1, docs/ops/live-diagnostics.md).
    [ValidateSet('Standard', 'Max')][string]$Diagnostics = 'Max'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'LiveEnv.ps1')
. (Join-Path $PSScriptRoot 'RedeployPlan.ps1')
$LiveHome = Resolve-LiveHome $LiveHome
$LiveProfile = Resolve-LiveProfile $LiveProfile
$launcher = Join-Path $PSScriptRoot 'launch-daemon.ps1'

function Get-ProfileWorldDb([string]$ProfileDir) {
    $settings = Get-Content -Raw -LiteralPath (Join-Path $ProfileDir 'appsettings.json') | ConvertFrom-Json
    $world = $settings.Database.World
    if (-not $world -or $world.Provider -ne 'Sqlite') { throw 'appsettings.json Database:World is not a Sqlite database: pass -WorldDb' }
    if ($world.ConnectionString -notmatch '(?i)(?:^|;)\s*Data Source\s*=\s*([^;]+)') { throw 'no Data Source in Database:World:ConnectionString: pass -WorldDb' }
    $path = $Matches[1].Trim()
    if (-not [IO.Path]::IsPathRooted($path)) { $path = Join-Path $ProfileDir $path }
    return $path
}

if ($WorldDbSwap) {
    if (-not $WorldDb) { $WorldDb = Get-ProfileWorldDb $LiveProfile }
    if (-not (Test-Path -LiteralPath $WorldDb)) { throw "world database $WorldDb not found" }
    if ((Get-Item -LiteralPath $WorldDb).Length -eq 0) { throw "world database $WorldDb is empty: not the database the World uses? pass -WorldDb" }
    "world.db to swap: $WorldDb"
}

if ($WorldDbSwap -and -not (Test-Path -LiteralPath $WorldDbSwap)) { throw "world db swap source $WorldDbSwap not found" }

$world = Get-LiveDaemon World
$live = Get-LiveRedeployPlan -Src $Src -LiveHome $LiveHome -LiveProfile $LiveProfile -ContentTables $ContentTables -WorldDbSwap ([bool]$WorldDbSwap) `
    -WorldRunning ($null -ne $world) -ForceRestart ([bool]$ForceRestart)
$thePlan = $live.Plan
Format-RedeployPlan -Plan $thePlan
if ($Json) { $thePlan | ConvertTo-Json -Depth 6 }
if ($Plan -or $WhatIfPreference) { exit 0 }

switch ($thePlan.Action) {
    'Blocked' {
        [Console]::Error.WriteLine('redeploy: blocked; nothing was touched. Fix the module(s) above or pass -ForceRestart.')
        exit 2
    }
    'None' {
        'nothing to deploy: no change reaches the daemons'
        exit 0
    }
    'Hot' {
        $hot = Invoke-HotRedeploy -Plan $thePlan -WorldPid $world.Id -Configuration $live.Configuration -ModuleDirectory $live.ModuleDirectory -Reason $Reason
        foreach ($line in $hot.Applied) { "applied $line" }
        if ($hot.Outcome -eq 'Applied') {
            Write-DeployRecord -LiveHome $LiveHome -Record (New-DeployRecordData -Live $live -Src $Src -Method hot -Modules $hot.Modules)
            "hot redeploy done ($($hot.Applied.Count) step(s)); deployed $($live.Worktree.Sha) recorded in $(Get-DeployRecordPath -LiveHome $LiveHome)"
            exit 0
        }
        if ($hot.Outcome -eq 'Rejected') {
            [Console]::Error.WriteLine("redeploy: the World rejected a hot step and keeps running what it ran: $($hot.Detail)")
            if ($hot.Applied.Count -gt 0) { [Console]::Error.WriteLine("redeploy: applied before the rejection (live now has these): $($hot.Applied -join '; ')") }
            [Console]::Error.WriteLine('redeploy: the deploy record is unchanged; fix the change, or pass -ForceRestart for the hard restart.')
            exit 3
        }
        "hot path unavailable ($($hot.Detail)); falling back to the hard restart"
    }
}

foreach ($proj in 'ArcaneCore.Realm', 'ArcaneCore.World') {
    $built = Join-Path $Src "src\$proj\bin\Debug\net11.0\$proj.exe"
    if (-not (Test-Path -LiteralPath $built)) { throw "$built not found: build $Src (Debug) first" }
}

# The Playerbots pack is not a project reference of the World (it references the World), so building only ArcaneCore.World leaves the
# copy beside the world executable as it was (CopyPackBesideWorldHost runs only when ArcaneCore.Playerbots builds). Deploying that copy
# silently ships old bot code: the 2026-10-17 live sweep ran an 11:46 pack under an 18:50 World. Refuse before anything is stopped.
$packBeside = Join-Path $Src 'src\ArcaneCore.World\bin\Debug\net11.0\ArcaneCore.Playerbots.dll'
$packBuilt = Join-Path $Src 'src\ArcaneCore.Playerbots\bin\Debug\net11.0\ArcaneCore.Playerbots.dll'
$packSources = @(Get-ChildItem -Path (Join-Path $Src 'src\ArcaneCore.Playerbots') -Recurse -File -Include *.cs, *.csproj, *.json -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1)
if (-not (Test-Path -LiteralPath $packBuilt) -or -not (Test-Path -LiteralPath $packBeside)) {
    throw "the Playerbots pack is not built beside the World: build ArcaneCore.slnx (or src\ArcaneCore.Playerbots) Debug first"
}
if ($packSources.Count -gt 0 -and (Get-Item -LiteralPath $packBuilt).LastWriteTimeUtc -lt $packSources[0].LastWriteTimeUtc) {
    throw "the Playerbots pack is older than its source ($($packSources[0].Name)): build ArcaneCore.slnx (or src\ArcaneCore.Playerbots) Debug first"
}
if ((Get-FileHash -LiteralPath $packBuilt).Hash -ne (Get-FileHash -LiteralPath $packBeside).Hash) {
    throw "the Playerbots pack beside the World differs from its build output: build src\ArcaneCore.Playerbots Debug (it copies itself beside the World)"
}

function Send-ConsoleCtrl([int]$ProcId, [int]$CtrlEvent) {
    # A helper process attaches to the daemon's console to send the event, so this shell's own console is never the one that gets it.
    # Exit codes: 0 sent, 2 attach failed, 3 GenerateConsoleCtrlEvent failed.
    $helper = @"
Add-Type -Name K -Namespace W -MemberDefinition @'
[DllImport("kernel32.dll")] public static extern bool FreeConsole();
[DllImport("kernel32.dll")] public static extern bool AttachConsole(uint p);
[DllImport("kernel32.dll")] public static extern bool SetConsoleCtrlHandler(System.IntPtr h, bool add);
[DllImport("kernel32.dll")] public static extern bool GenerateConsoleCtrlEvent(uint e, uint g);
'@
[W.K]::FreeConsole() | Out-Null
if (-not [W.K]::AttachConsole($ProcId)) { exit 2 }
[W.K]::SetConsoleCtrlHandler([System.IntPtr]::Zero, `$true) | Out-Null
if (-not [W.K]::GenerateConsoleCtrlEvent($CtrlEvent, 0)) { exit 3 }
Start-Sleep -Milliseconds 500
exit 0
"@
    $enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($helper))
    $h = Start-Process pwsh -ArgumentList '-NoProfile', '-EncodedCommand', $enc -PassThru -WindowStyle Hidden
    $h.WaitForExit(30000) | Out-Null
    # Ctrl-Break also reaches the helper (its ignore flag covers Ctrl-C only) and can end it before it exits by itself.
    "console event $CtrlEvent for $ProcId helper exit=$($h.ExitCode)"
}

function Stop-Graceful([string]$Role, [int]$ProcId, [int]$TimeoutSec) {
    # A console daemon saves and stops on Ctrl-C. A daemon started from a process tree that ignores Ctrl-C (a background shell)
    # inherits that flag and never sees it (2026-10-17). .NET also stops on Ctrl-Break (SIGQUIT), but only before its host has begun
    # stopping, so Ctrl-Break goes out only when the log shows Ctrl-C did not start the shutdown.
    $p = Get-Process -Id $ProcId -ErrorAction SilentlyContinue
    if (-not $p) { "PID $ProcId already gone"; return }
    $log = Get-LiveDaemonLogPath -Role $Role -LiveHome $LiveHome -LiveProfile $LiveProfile
    $from = if (Test-Path -LiteralPath $log) { (Get-Item -LiteralPath $log).Length } else { 0 }
    Send-ConsoleCtrl $ProcId 0
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    $started = $false
    for ($i = 0; $i -lt 15 -and -not $started -and -not $p.HasExited; $i++) {
        Start-Sleep -Seconds 1
        $started = Test-LogSince $log $from 'Application is shutting down'
    }
    if (-not $started -and -not $p.HasExited) {
        "Ctrl-C did not start the $Role shutdown within 15 s; sending Ctrl-Break"
        Send-ConsoleCtrl $ProcId 1
    }
    $left = [Math]::Max(0, ($deadline - [DateTime]::UtcNow).TotalMilliseconds)
    if (-not $p.WaitForExit([int]$left)) { throw "PID $ProcId did not exit within $TimeoutSec s" }
    "PID $ProcId stopped"
}

function Test-LogSince([string]$Path, [long]$From, [string]$Text) {
    # The daemon holds its log open for writing; open it shared and read only what was appended after $From.
    try {
        $fs = [IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite')
        try {
            if ($fs.Length -le $From) { return $false }
            $fs.Position = $From
            return (New-Object IO.StreamReader($fs)).ReadToEnd().Contains($Text)
        } finally { $fs.Dispose() }
    } catch { return $false }
}

$world = Get-LiveDaemon World
$realm = Get-LiveDaemon Realm
if ($world) { Stop-Graceful World $world.Id $WorldStopTimeoutSec }
if ($realm) { Stop-Graceful Realm $realm.Id $RealmStopTimeoutSec }

$tail = Get-Content (Get-LiveDaemonLogPath -Role World -LiveHome $LiveHome -LiveProfile $LiveProfile) -Tail 3000 -ErrorAction SilentlyContinue | Select-String 'saved and stopped' | Select-Object -Last 1
"last stop line: $tail"

$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
robocopy (Join-Path $LiveHome 'bin') (Join-Path $LiveHome "snapshots\bin-$stamp") /E /R:2 /W:2 /NFL /NDL /NJH /NJS | Out-Null
"snapshot: bin-$stamp (robocopy rc $LASTEXITCODE)"

if ($WorldDbSwap) {
    if (Get-Process ArcaneCore.World -ErrorAction SilentlyContinue) { throw 'World still running: refusing to swap world.db' }
    $bak = "$worldDb.bak-$stamp"
    Copy-Item -LiteralPath $worldDb -Destination $bak
    foreach ($side in '-wal', '-shm') { if (Test-Path -LiteralPath "$worldDb$side") { Move-Item -LiteralPath "$worldDb$side" -Destination "$bak$side" } }
    Copy-Item -LiteralPath $WorldDbSwap -Destination $worldDb -Force
    if ((Get-FileHash -LiteralPath $WorldDbSwap).Hash -ne (Get-FileHash -LiteralPath $worldDb).Hash) { throw 'world db swap: copied file hash mismatch' }
    "world.db swapped from $WorldDbSwap (backup $bak)"
}

foreach ($proj in 'ArcaneCore.Realm', 'ArcaneCore.World') {
    robocopy (Join-Path $Src "src\$proj\bin\Debug\net11.0") (Join-Path $LiveHome 'bin') /E /R:2 /W:2 /NFL /NDL /NJH /NJS | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy $proj failed rc $LASTEXITCODE" }
    "copied $proj (rc $LASTEXITCODE)"
}

# Changed hot modules that are built and approved go into the module directory while the World is stopped (LoadOnStart or a later
# .hotmodule load picks them up); an unapproved build is left out, the World would refuse it anyway.
$restartModules = @{}
foreach ($name in $thePlan.Modules) {
    $build = $live.ModuleBuilds[$name]
    if ($null -eq $build -or -not $build.Dll -or -not $build.Allowlisted) { "module ${name}: not copied (not built or not on the allowlist)"; continue }
    $folder = Join-Path $live.ModuleDirectory $name
    Copy-Item -LiteralPath $build.Dll -Destination (Join-Path $folder "$name.dll") -Force
    if ((Get-FileHash -LiteralPath (Join-Path $folder "$name.dll") -Algorithm SHA256).Hash -ne $build.Sha256) { throw "module ${name}: copied dll hash mismatch" }
    $restartModules[$name] = $build.Sha256
    "module ${name}: copied sha256 $($build.Sha256)"
}

$startedUtc = [DateTime]::UtcNow
foreach ($role in 'Realm', 'World') {
    Start-Process pwsh -ArgumentList '-NoExit', '-NoProfile', '-File', $launcher, '-Role', $role, '-LiveHome', $LiveHome, '-Profile', $LiveProfile, '-Diagnostics', $Diagnostics | Out-Null
    Start-Sleep -Seconds 5
}
Start-Sleep -Seconds 10
foreach ($role in 'Realm', 'World') {
    $p = Get-LiveDaemon $role
    if (-not $p) { throw "ArcaneCore.$role not running after start" }
    $p.PriorityClass = 'AboveNormal'
    "ArcaneCore.$role PID $($p.Id) $($p.PriorityClass)"
}
# Each daemon must own its own log (launch-daemon.ps1 sets <role>.debug.*). A fallback file written since the start means another process
# holds that name, and livecheck.ps1 and the stop-line check above would read someone else's lines as the World's.
$fallbacks = @(foreach ($role in 'Realm', 'World') { Get-LiveLogFallback -Role $role -LiveHome $LiveHome -SinceUtc $startedUtc })
if ($fallbacks.Count -gt 0) {
    throw "the daemons are running, but a log name is owned by another writer; fallback files written since the start: $($fallbacks.FullName -join ', ')"
}
"logs: $(Get-LiveDaemonLogPath -Role World -LiveHome $LiveHome -LiveProfile $LiveProfile), $(Get-LiveDaemonLogPath -Role Realm -LiveHome $LiveHome -LiveProfile $LiveProfile)"
Write-DeployRecord -LiveHome $LiveHome -Record (New-DeployRecordData -Live $live -Src $Src -Method restart -Modules $restartModules)
"deployed $($live.Worktree.Sha) recorded in $(Get-DeployRecordPath -LiveHome $LiveHome)"
# robocopy's 1-7 are success codes; don't let the last one leak out as the script's exit code.
exit 0
