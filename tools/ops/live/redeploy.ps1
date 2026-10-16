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

function Stop-Graceful([int]$ProcId, [int]$TimeoutSec) {
    # A console daemon saves and stops on Ctrl-C. A helper process attaches to the daemon's console to send it, so this shell's own
    # console is never the one that gets the signal.
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
[W.K]::GenerateConsoleCtrlEvent(0, 0) | Out-Null
Start-Sleep -Milliseconds 500
exit 0
"@
    $enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($helper))
    $h = Start-Process pwsh -ArgumentList '-NoProfile', '-EncodedCommand', $enc -PassThru -WindowStyle Hidden
    $h.WaitForExit(30000) | Out-Null
    "ctrl-c helper for $ProcId exit=$($h.ExitCode)"
    $p = Get-Process -Id $ProcId -ErrorAction SilentlyContinue
    if ($p -and -not $p.WaitForExit($TimeoutSec * 1000)) { throw "PID $ProcId did not exit within $TimeoutSec s" }
    "PID $ProcId stopped"
}

$world = Get-LiveDaemon World
$realm = Get-LiveDaemon Realm
if ($world) { Stop-Graceful $world.Id $WorldStopTimeoutSec }
if ($realm) { Stop-Graceful $realm.Id $RealmStopTimeoutSec }

$tail = Get-Content (Join-Path $LiveHome 'logs\world.debug.log') -Tail 3000 -ErrorAction SilentlyContinue | Select-String 'saved and stopped' | Select-Object -Last 1
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
# robocopy's 1-7 are success codes; don't let the last one leak out as the script's exit code.
exit 0
