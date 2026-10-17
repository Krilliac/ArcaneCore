# Redeploys the local live server from a built worktree: graceful Ctrl-C stop of World then Realm, a snapshot of <LiveHome>\bin, an
# optional world.db swap, a copy of both Debug builds into <LiveHome>\bin, then a start of each daemon in its own console through
# launch-daemon.ps1 at AboveNormal priority.
#   pwsh -NoProfile -File tools/ops/live/redeploy.ps1 [-Src <worktree>] [-WorldDbSwap <world.db> [-WorldDb <target>]] [-LiveHome <dir>] [-Profile <dir>]
# **Touches the live server.** -Src defaults to the worktree this script is in; build it first (dotnet build ArcaneCore.slnx, Debug:
# live stays a Debug build). Uses ARCANECORE_LIVE_HOME and ARCANECORE_LIVE_PROFILE when the parameters are omitted.
param(
    [string]$Src = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path,
    # A refreshed world database (for example a content-importer refresh run on a copy) put in place while the World is stopped.
    # The old one, with its -wal/-shm, is kept beside it as world.db.bak-<stamp>.
    [string]$WorldDbSwap = '',
    # The world database the swap replaces. Default: the Data Source of Database:World:ConnectionString in the profile's
    # appsettings.json (the file the World actually opens, which need not be in the profile directory).
    [string]$WorldDb = '',
    [string]$LiveHome,
    [Alias('Profile')][string]$LiveProfile,
    [int]$WorldStopTimeoutSec = 180,
    [int]$RealmStopTimeoutSec = 60
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'LiveEnv.ps1')
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

foreach ($proj in 'ArcaneCore.Realm', 'ArcaneCore.World') {
    $built = Join-Path $Src "src\$proj\bin\Debug\net11.0\$proj.exe"
    if (-not (Test-Path -LiteralPath $built)) { throw "$built not found: build $Src (Debug) first" }
}
if ($WorldDbSwap -and -not (Test-Path -LiteralPath $WorldDbSwap)) { throw "world db swap source $WorldDbSwap not found" }

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
    $log = Get-LiveDaemonLogPath -Role $Role -LiveHome $LiveHome
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

$tail = Get-Content (Get-LiveDaemonLogPath -Role World -LiveHome $LiveHome) -Tail 3000 -ErrorAction SilentlyContinue | Select-String 'saved and stopped' | Select-Object -Last 1
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

$startedUtc = [DateTime]::UtcNow
foreach ($role in 'Realm', 'World') {
    Start-Process pwsh -ArgumentList '-NoExit', '-NoProfile', '-File', $launcher, '-Role', $role, '-LiveHome', $LiveHome, '-Profile', $LiveProfile | Out-Null
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
"logs: $(Get-LiveDaemonLogPath -Role World -LiveHome $LiveHome), $(Get-LiveDaemonLogPath -Role Realm -LiveHome $LiveHome)"
# robocopy's 1-7 are success codes; don't let the last one leak out as the script's exit code.
exit 0
