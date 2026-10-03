<#
.SYNOPSIS
  One command: a local, disposable ArcaneCore server you can connect a real WoW 1.12.1 client to, whose code
  changes live while it runs (docs/areas/code-hot-reload.md, "Quick start").

.DESCRIPTION
  Start (default):
    * refuses to run when the environment is Production (Development / Staging only);
    * builds Debug (hot reload cannot patch an optimized build);
    * creates a disposable run directory outside the repo ($env:TEMP\ArcaneCore-dev-<Name>) with FRESH SQLite
      databases, or reuses it with -Reuse (databases, characters and the account are kept);
    * creates an Administrator dev account with a generated password written to <run>\dev-account.txt (the password
      is never put on a command line: the account tool reads it from an environment variable);
    * starts the realm and the world daemon, each in its OWN visible console window under `dotnet watch` hot
      reload, with World:HotCode:Enabled=true and the module lane (.hotmodule) enabled for this process only;
    * prints the realm address, the account, the password file and how to see hot-reload status.
  An edit that cannot be applied live (a signature change, a rename ...) is NEVER restarted silently: the world
  window prints a "restart required" message and asks. Pass -RestartOnRudeEdit to have watch restart by itself
  (that restart does not run the graceful save path).

  -Stop   stops both daemons with Ctrl+Break (the world saves), world first. -Status shows what is running.
  A normal `dotnet run` of the server, without this script, is unchanged: hot code stays disabled and the launch
  gate (HotCodeGuard) still refuses a hot-reload-capable process that did not opt in.

.PARAMETER Name         Run name; the run directory is $env:TEMP\ArcaneCore-dev-<Name>. Default: dev.
.PARAMETER Reuse        Reuse an existing run directory (keeps databases, characters, account and password file).
.PARAMETER Stop         Stop the daemons of this run (graceful) and exit.
.PARAMETER Status       Show the state of this run and exit.
.PARAMETER ContentDir   Optional terrain/collision data directory (World:Maps:DataDirectory; vmaps/ and mmaps/ inside it
                        are picked up by default). Without it the world content directories are empty.
.PARAMETER Account      Dev account name. Default: DEVGM.
.PARAMETER RealmPort    Realm (logon) port. Default 3724. The client's realmlist must use this port.
.PARAMETER WorldPort    World port advertised in the realm list. Default 8085.
.PARAMETER NoBuild      Skip the Debug build (use when the build is already current).
.PARAMETER NoModules    Do not enable the module lane (.hotmodule).
.PARAMETER RestartOnRudeEdit  Let dotnet watch restart the daemon by itself on an edit that cannot be applied live.
.PARAMETER RunRoot      Parent of the run directory. Default: $env:TEMP.

.EXAMPLE
  powershell -File scripts/dev-runner.ps1
  powershell -File scripts/dev-runner.ps1 -Reuse
  powershell -File scripts/dev-runner.ps1 -Stop
#>
[CmdletBinding()]
param(
    [string]$Name = 'dev',
    [switch]$Reuse,
    [switch]$Stop,
    [switch]$Status,
    [string]$ContentDir = '',
    [string]$Account = 'DEVGM',
    [int]$RealmPort = 3724,
    [int]$WorldPort = 8085,
    [switch]$NoBuild,
    [switch]$NoModules,
    [switch]$RestartOnRudeEdit,
    [string]$RunRoot = ''
)

$ErrorActionPreference = 'Stop'

function Fail([string]$message, [int]$code = 1) {
    [Console]::Error.WriteLine("dev-runner: $message")
    exit $code
}

if ($Name -notmatch '^[A-Za-z0-9_-]{1,32}$') { Fail "-Name must be 1-32 letters, digits, '_' or '-'." 64 }
if ($Account -notmatch '^[A-Za-z0-9]{1,16}$') { Fail '-Account must be 1-16 letters or digits.' 64 }

$repo = Split-Path -Parent $PSScriptRoot
if (-not $RunRoot) { $RunRoot = $env:TEMP }
$run = Join-Path $RunRoot "ArcaneCore-dev-$Name"
$statePath = Join-Path $run 'runner.json'
$psExe = (Get-Process -Id $PID).Path

function Read-State {
    if (Test-Path -LiteralPath $statePath) { return Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json }
    return $null
}

function Test-Alive($processId) {
    if (-not $processId) { return $false }
    return [bool](Get-Process -Id $processId -ErrorAction SilentlyContinue)
}

function Get-ListenerOwner([int]$port) {
    $c = Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $c) { return $null }
    $p = Get-Process -Id $c.OwningProcess -ErrorAction SilentlyContinue
    return "pid $($c.OwningProcess) ($(if ($p) { $p.ProcessName } else { 'unknown' }))"
}

# ---------------------------------------------------------------- -Status
if ($Status) {
    $s = Read-State
    if (-not $s) { Write-Host "dev-runner: no run named '$Name' ($run)"; exit 0 }
    Write-Host "run directory : $($s.run)"
    Write-Host "realm window  : pid $($s.realmWindowPid) $(if (Test-Alive $s.realmWindowPid) { 'RUNNING' } else { 'not running' }); port $($s.realmPort) $(if (Get-ListenerOwner $s.realmPort) { 'listening' } else { 'not listening' })"
    Write-Host "world window  : pid $($s.worldWindowPid) $(if (Test-Alive $s.worldWindowPid) { 'RUNNING' } else { 'not running' }); port $($s.worldPort) $(if (Get-ListenerOwner $s.worldPort) { 'listening' } else { 'not listening' })"
    Write-Host "account       : $($s.account) (password in $($s.run)\dev-account.txt)"
    $audit = Join-Path $s.run 'logs\hotcode-audit.log'
    if (Test-Path -LiteralPath $audit) { Write-Host 'hot-code audit (last 5):'; Get-Content -LiteralPath $audit -Tail 5 | ForEach-Object { Write-Host "  $_" } }
    $worldLog = Join-Path $s.run 'logs\world.log'
    if (Test-Path -LiteralPath $worldLog) {
        Write-Host 'world window, last hot reload lines:'
        Select-String -LiteralPath $worldLog -Pattern 'Hot reload|hot reload|restart|Restart|Code hot reload' | Select-Object -Last 6 | ForEach-Object { Write-Host "  $($_.Line)" }
    }
    Write-Host 'In game (as the dev account):  .hotcode status    and    .hotmodule list'
    exit 0
}

# ---------------------------------------------------------------- -Stop
if ($Stop) {
    $s = Read-State
    if (-not $s) { Fail "no run named '$Name' ($run) to stop." }
    $signal = Join-Path $PSScriptRoot 'dev-runner-signal.ps1'
    $failed = $false
    # Tells the window scripts this exit is intended, so they close instead of waiting for Enter.
    Set-Content -LiteralPath (Join-Path $s.run 'stop.flag') -Value (Get-Date).ToString('o')
    foreach ($entry in @(@{ Label = 'world'; ProcessId = $s.worldWindowPid }, @{ Label = 'realm'; ProcessId = $s.realmWindowPid })) {
        if (-not (Test-Alive $entry.ProcessId)) { Write-Host "$($entry.Label): not running (pid $($entry.ProcessId))"; continue }
        Write-Host "$($entry.Label): sending Ctrl+Break to the window (pid $($entry.ProcessId)) ..."
        & $psExe -NoProfile -File $signal -ProcessId $entry.ProcessId -Signal Break | Out-Null
        $deadline = (Get-Date).AddSeconds(45)
        while ((Test-Alive $entry.ProcessId) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
        if (Test-Alive $entry.ProcessId) {
            Write-Host "$($entry.Label): STILL RUNNING after 45 s; it did not stop gracefully. Close its window by hand (the world may not have saved)."
            $failed = $true
        } else {
            Write-Host "$($entry.Label): stopped"
        }
    }

    $worldLog = Join-Path $s.run 'logs\world.log'
    if (Test-Path -LiteralPath $worldLog) {
        $saved = Select-String -LiteralPath $worldLog -Pattern 'World saved and stopped' -Quiet
        Write-Host "world log says 'World saved and stopped': $(if ($saved) { 'yes' } else { 'NO (check ' + $worldLog + ')' })"
        if (-not $saved) { $failed = $true }
    }

    if ($failed) { exit 1 }
    exit 0
}

# ---------------------------------------------------------------- start
$environmentName = if ($env:DOTNET_ENVIRONMENT) { $env:DOTNET_ENVIRONMENT } elseif ($env:ASPNETCORE_ENVIRONMENT) { $env:ASPNETCORE_ENVIRONMENT } else { 'Development' }
if ($environmentName -notin @('Development', 'Staging')) {
    Fail "refusing to start: the environment is '$environmentName'. The dev runner (code hot reload) runs only in Development or Staging." 2
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail 'needs the .NET SDK (dotnet watch); it is not on PATH.' 3 }
if ($ContentDir -and -not (Test-Path -LiteralPath $ContentDir -PathType Container)) { Fail "-ContentDir '$ContentDir' is not a directory." 64 }

$existing = Read-State
if ($existing -and ((Test-Alive $existing.realmWindowPid) -or (Test-Alive $existing.worldWindowPid))) {
    Fail "run '$Name' is already running (realm pid $($existing.realmWindowPid), world pid $($existing.worldWindowPid)). Use -Stop first." 4
}

if ((Test-Path -LiteralPath $run) -and -not $Reuse) {
    Fail "$run already exists. Use -Reuse to keep its databases and account, pick another -Name, or delete the directory." 5
}

foreach ($port in @($RealmPort, $WorldPort)) {
    $owner = Get-ListenerOwner $port
    if ($owner) { Fail "port $port is already in use by $owner. Pick another with -RealmPort / -WorldPort (the client's realmlist needs the realm port), or stop that process yourself." 6 }
}

$projects = @{
    World   = Join-Path $repo 'src\ArcaneCore.World\ArcaneCore.World.csproj'
    Realm   = Join-Path $repo 'src\ArcaneCore.Realm\ArcaneCore.Realm.csproj'
    Account = Join-Path $repo 'tools\ArcaneCore.AccountTool\ArcaneCore.AccountTool.csproj'
}

if (-not $NoBuild) {
    foreach ($key in @('Account', 'Realm', 'World')) {
        Write-Host "dev-runner: building $key (Debug) ..."
        & dotnet build $projects[$key] -c Debug -m:1 --nologo -v:q
        if ($LASTEXITCODE -ne 0) { Fail "the Debug build of $key failed (exit $LASTEXITCODE); nothing was started." 7 }
    }
}

$accountDll = Join-Path $repo 'tools\ArcaneCore.AccountTool\bin\Debug\net10.0\arcane-account.dll'
if (-not (Test-Path -LiteralPath $accountDll)) { Fail "the account tool is not built: $accountDll (run without -NoBuild)." 7 }

New-Item -ItemType Directory -Force -Path $run, (Join-Path $run 'logs'), (Join-Path $run 'modules') | Out-Null

# Configuration: SQLite databases inside the run directory, loopback binds, content directories empty.
$db = @{}
foreach ($component in 'Auth', 'Characters', 'World') {
    $file = Join-Path $run ($component.ToLowerInvariant() + '.db')
    $db[$component] = @{ Provider = 'Sqlite'; ConnectionString = "Data Source=$file;Mode=ReadWriteCreate;Pooling=False;Default Timeout=5" }
}
$maps = @{ DataDirectory = '' }
$collision = @{ VMapDirectory = ''; MMapDirectory = '' }
if ($ContentDir) {
    $maps.DataDirectory = (Resolve-Path -LiteralPath $ContentDir).Path
    $vmaps = Join-Path $maps.DataDirectory 'vmaps'
    $mmaps = Join-Path $maps.DataDirectory 'mmaps'
    if (Test-Path -LiteralPath $vmaps) { $collision.VMapDirectory = $vmaps }
    if (Test-Path -LiteralPath $mmaps) { $collision.MMapDirectory = $mmaps }
}
$config = @{
    Logging = @{ LogLevel = @{ Default = 'Information'; 'Microsoft.EntityFrameworkCore' = 'Warning'; 'Microsoft.Hosting.Lifetime' = 'Information' } }
    Database = $db
    Auth = @{ BindAddress = '127.0.0.1'; Port = $RealmPort; AutocreateAccounts = $false }
    Realms = @{ Seed = @(@{ Name = 'ArcaneCore Dev'; Address = "127.0.0.1:$WorldPort"; Type = 'Normal'; Flags = 'None'; Population = 0.0; Category = 0 }) }
    World = @{
        BindAddress = '127.0.0.1'; Port = $WorldPort; TickIntervalMs = 50; AutosaveIntervalMs = 10000; LogoutDelayMs = 20000
        PlayerCommands = $true; Motd = 'ArcaneCore dev runner: this server reloads code live.'
        Maps = $maps; Collision = $collision
    }
}
$config | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $run 'appsettings.json') -Encoding utf8

# The dev account. The password goes through an environment variable of the account tool process only.
$passwordFile = Join-Path $run 'dev-account.txt'
$accountUpper = $Account.ToUpperInvariant()
function New-DevPassword {
    $alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789'
    $bytes = New-Object byte[] 12
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    return -join ($bytes | ForEach-Object { $alphabet[$_ % $alphabet.Length] })
}

function Invoke-AccountTool([string[]]$toolArgs, [string]$password) {
    Push-Location $run
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue' # Windows PowerShell 5.1 turns native stderr into a terminating error otherwise
    try {
        $env:ARCANE_ACCOUNT_PASSWORD = $password
        $output = & dotnet $accountDll @toolArgs 2>&1
        $code = $LASTEXITCODE
    } finally {
        Remove-Item Env:\ARCANE_ACCOUNT_PASSWORD -ErrorAction SilentlyContinue
        $ErrorActionPreference = $previousPreference
        Pop-Location
    }
    return @{ Code = $code; Output = ($output | Out-String).Trim() }
}

$haveFile = Test-Path -LiteralPath $passwordFile
$password = $null
if ($haveFile) {
    $line = Get-Content -LiteralPath $passwordFile | Where-Object { $_ -like 'password=*' } | Select-Object -First 1
    if ($line) { $password = $line.Substring('password='.Length).Trim() }
}
if (-not $password) { $password = New-DevPassword }

$created = Invoke-AccountTool @('create', $accountUpper) $password
if ($created.Code -ne 0) {
    if ($created.Output -match 'already exists') {
        if (-not $haveFile) {
            # The account survived from an earlier run but its password file is gone: set a fresh one.
            $reset = Invoke-AccountTool @('set-password', $accountUpper) $password
            if ($reset.Code -ne 0) { Fail "could not reset the password of existing account $accountUpper : $($reset.Output)" 8 }
        }
    } else {
        Fail "could not create the dev account: $($created.Output)" 8
    }
}
$level = Invoke-AccountTool @('set-gmlevel', $accountUpper, 'administrator') $null
if ($level.Code -ne 0) { Fail "could not make $accountUpper an Administrator: $($level.Output)" 8 }

Set-Content -LiteralPath $passwordFile -Encoding ascii -Value @(
    '# ArcaneCore dev runner account (disposable; this server is local only)',
    "account=$accountUpper",
    "password=$password",
    "realm=127.0.0.1:$RealmPort"
)
& icacls.exe $passwordFile /inheritance:r /grant:r "$($env:USERNAME):(R,W)" | Out-Null

# The module allowlist starts with only its header: a module has to be approved (hash added) before it loads.
$allowlist = Join-Path $run 'modules-allowlist.txt'
if (-not (Test-Path -LiteralPath $allowlist)) {
    Set-Content -LiteralPath $allowlist -Encoding ascii -Value @(
        '# SHA-256 of each module dll that may be loaded with .hotmodule (one per line, # starts a comment).',
        '# scripts/dev-module.ps1 builds a module and appends its hash here.'
    )
}

# The two window scripts. Each runs `dotnet watch` (hot reload) and tees its console to <run>\logs\*.log.
function New-WindowScript([string]$label, [string]$project, [string[]]$extraEnv, [string]$logName) {
    $watchArgs = "watch --project `"$project`" -c Debug --no-launch-profile"
    if ($RestartOnRudeEdit) { $watchArgs += ' --non-interactive' }
    $tee = Join-Path $PSScriptRoot 'dev-runner-tee.ps1'
    $lines = @(
        "`$Host.UI.RawUI.WindowTitle = 'ArcaneCore dev: $label (hot reload) - $Name'",
        "Set-Location -LiteralPath '$run'",
        "`$env:DOTNET_ENVIRONMENT = '$environmentName'",
        "`$env:DOTNET_CONTENTROOT = '$run'",
        "`$env:DOTNET_WATCH_SUPPRESS_EMOJIS = '1'"
    ) + $extraEnv + @(
        "Write-Host 'ArcaneCore dev runner: $label under dotnet watch (hot reload). Edit and save a .cs file in the repo; this window says whether the change was applied.'",
        "Write-Host 'An edit that needs a restart is NOT restarted silently: watch asks here. Stop everything with: scripts\dev-runner.ps1 -Stop -Name $Name'",
        # The output is shown live and appended to the log as UTF-8 by a wrapper that survives Ctrl+C / Ctrl+Break, so a
        # graceful shutdown (the world saving) is still logged and never cut off by the window script itself.
        ". '$tee'",
        "`$code = Invoke-DevTee (Get-Command dotnet).Source '$watchArgs' '$run\logs\$logName'",
        "Write-Host `"$label exited (code `$code).`"",
        "if (`$code -ne 0 -and -not (Test-Path -LiteralPath '$run\stop.flag')) { Write-Host 'Press Enter to close this window.'; [void][Console]::ReadLine() }"
    )
    $path = Join-Path $run "start-$label.ps1"
    Set-Content -LiteralPath $path -Encoding ascii -Value $lines
    return $path
}

$worldEnv = @(
    "`$env:World__HotCode__Enabled = 'true'",
    "`$env:World__HotCode__AuditLogPath = '$run\logs\hotcode-audit.log'"
)
if (-not $NoModules) {
    $worldEnv += @(
        "`$env:World__HotCode__Modules__Enabled = 'true'",
        "`$env:World__HotCode__Modules__Directory = '$run\modules'",
        "`$env:World__HotCode__Modules__Allowlist = '$allowlist'"
    )
}

$worldScript = New-WindowScript 'world' $projects.World $worldEnv 'world.log'
$realmScript = New-WindowScript 'realm' $projects.Realm @() 'realm.log'

foreach ($log in 'world.log', 'realm.log') { Remove-Item -LiteralPath (Join-Path $run "logs\$log") -ErrorAction SilentlyContinue }
Remove-Item -LiteralPath (Join-Path $run 'stop.flag') -ErrorAction SilentlyContinue

# World first, so its listener is up by the time the realm advertises it. Each gets its own console window.
$worldWindow = Start-Process -FilePath $psExe -ArgumentList @('-NoProfile', '-File', $worldScript) -WorkingDirectory $run -WindowStyle Normal -PassThru
$realmWindow = Start-Process -FilePath $psExe -ArgumentList @('-NoProfile', '-File', $realmScript) -WorkingDirectory $run -WindowStyle Normal -PassThru

@{
    name = $Name; run = $run; repo = $repo; environment = $environmentName; account = $accountUpper
    realmPort = $RealmPort; worldPort = $WorldPort; realmWindowPid = $realmWindow.Id; worldWindowPid = $worldWindow.Id
    startedUtc = (Get-Date).ToUniversalTime().ToString('o')
} | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding utf8

function Wait-Listening([int]$port, $window, [string]$label) {
    $deadline = (Get-Date).AddSeconds(180)
    while ((Get-Date) -lt $deadline) {
        if (-not (Test-Alive $window.Id)) { Fail "the $label window exited during startup; see $run\logs\$label.log" 9 }
        if (Get-ListenerOwner $port) { return }
        Start-Sleep -Milliseconds 500
    }
    Fail "the $label did not start listening on port $port within 180 s (a first start builds under watch and a busy machine is slower); see $run\logs\$label.log. The windows are still up: wait, or run -Stop -Name $Name." 9
}

Write-Host 'dev-runner: waiting for the daemons to listen (first start compiles under watch) ...'
Wait-Listening $WorldPort $worldWindow 'world'
Wait-Listening $RealmPort $realmWindow 'realm'

Write-Host ''
Write-Host '=== ArcaneCore dev runner is up ==='
Write-Host "environment   : $environmentName (Debug build, hot reload ON, modules $(if ($NoModules) { 'off' } else { 'on' }))"
Write-Host "realm address : 127.0.0.1:$RealmPort   (world 127.0.0.1:$WorldPort)"
Write-Host "account       : $accountUpper   (Administrator)"
Write-Host "password file : $passwordFile"
Write-Host "client        : realmlist.wtf needs 'set realmlist 127.0.0.1' (port 3724 is the client default$(if ($RealmPort -ne 3724) { "; for port $RealmPort use 'set realmlist 127.0.0.1:$RealmPort'" }))"
Write-Host "windows       : realm pid $($realmWindow.Id), world pid $($worldWindow.Id); logs in $run\logs"
Write-Host 'hot reload    : in game (as this account) type  .hotcode status  and  .hotmodule list ;'
Write-Host "                or run  powershell -File scripts\dev-runner.ps1 -Status -Name $Name"
Write-Host "edit and see  : edit a .cs file under src\ and save; the world window prints 'C# and Razor changes applied'."
Write-Host "modules       : powershell -File scripts\dev-module.ps1 -Project <module csproj> -Name <name> , then .hotmodule load <name>"
Write-Host "stop          : powershell -File scripts\dev-runner.ps1 -Stop -Name $Name"
exit 0
