<#
.SYNOPSIS
  Runbook script for moving the live realm from SQLite to PostgreSQL 16 (auth, characters, world): plan, backup, copy, verify, schema check,
  settings switch and rollback (docs/ops/database-upgrade.md, "Cutover of the live realm").
.DESCRIPTION
  pwsh -NoProfile -File tools/ops/live/pg-cutover.ps1 -Plan
  pwsh -NoProfile -File tools/ops/live/pg-cutover.ps1 -Step Backup   -BackupDir <dir> -Confirm
  pwsh -NoProfile -File tools/ops/live/pg-cutover.ps1 -Step Copy     -BackupDir <dir> -Confirm [-Replace]
  pwsh -NoProfile -File tools/ops/live/pg-cutover.ps1 -Step Verify
  pwsh -NoProfile -File tools/ops/live/pg-cutover.ps1 -Step Check
  pwsh -NoProfile -File tools/ops/live/pg-cutover.ps1 -Step Switch
  pwsh -NoProfile -File tools/ops/live/pg-cutover.ps1 -Step Rollback -BackupDir <dir> [-Confirm]

  Every database access goes through arcane-db (this build: its backup, copy, verify, status and check commands), which runs on the repository's own
  runtime with its own drivers; the script itself loads no database driver. arcane-db is started with the live profile as its working directory
  and with the profile's appsettings.json as --config, so the SQLite files it reads are the ones the daemons open (a relative Data Source resolves
  against the profile). The script finds the same files for Rollback with the same precedence: bin\appsettings.json, then the profile's
  appsettings.json, then Database__* environment variables, the component's own ConnectionString before Database:ConnectionString.

  Steps (the order of the cutover; -Plan prints all of them and touches nothing, not even a database):
    Backup    verified copy of the SQLite files: arcane-db backup writes each file with VACUUM INTO (a consistent snapshot, the -wal is folded in) and
              checks it with PRAGMA integrity_check; arcane-db verify then compares each copy with its source table by table (row count and a digest
              of every value) and the SHA-256 of each copy goes into manifest.json. Needs -Confirm. Refuses while a daemon runs.
    Copy      arcane-db copy --component all into the PostgreSQL databases (the schema is created by the same bootstrap the daemons run). Needs
              -Confirm and the Backup manifest. Refuses while a daemon runs. -Replace re-runs a copy that stopped part-way.
    Verify    arcane-db verify of each component: the configured SQLite database against its PostgreSQL target, per table the row count and an
              order-independent digest of every value read through the model column types. Read-only. Exit 0 all tables match, 1 any table differs
              (a dropped row shows as a count and digest difference).
    Check     arcane-db status and check against the target, read-only: the schema is at the build's version and equals the model.
    Switch    prints the Database:* settings (environment variable form, secrets masked) for the realm, world, gateway and tools. Changes nothing.
    Rollback  checks the backup against its manifest and prints the revert; with -Confirm restores the SQLite files from the backup (refuses while a
              daemon runs; the files being replaced are kept as <name>.pre-rollback-<stamp>).

  Secrets: the PostgreSQL host, user and password are read only from the secret file of the user profile (default <profile>\.arcanecore\pg-cutover.json,
  or ARCANECORE_PG_SECRET_FILE, or -SecretFile: a path, never a value). JSON keys: Host, Port, Username, Password and optionally AuthDatabase,
  CharactersDatabase, WorldDatabase (defaults arcane_auth, arcane_characters, arcane_world), SslMode. Connection strings are built in memory, handed
  to arcane-db only through the child process environment (ARCANE_DB_TARGET_*_CONNECTION, Database__*__ConnectionString) and never printed, logged or
  taken from an argument. Output is scrubbed of the password. The secret file is read and checked before arcane-db is started.

  The script never starts a daemon. Run it only inside the downtime window.
.PARAMETER Plan
  Print every step with the resolved locations (secrets masked) and stop. Reads no database.
.PARAMETER Step
  Backup, Copy, Verify, Check, Switch or Rollback.
.PARAMETER LiveProfile
  The live profile directory (appsettings.json, auth.db, characters.db, world.db). Default ARCANECORE_LIVE_PROFILE.
.PARAMETER BackupDir
  Where Backup writes (must hold no .db yet) and Copy and Rollback read the verified backup.
.PARAMETER BinDir
  Directory with arcane-db (this build) and its appsettings.json. Default <ARCANECORE_LIVE_HOME>\bin.
.PARAMETER TargetSqliteDir
  Verify against SQLite files <dir>\<component>.db instead of PostgreSQL (fixtures; no secrets involved).
.PARAMETER Component
  auth, characters, world (default all three).
.PARAMETER Confirm
  Required by Backup, Copy and Rollback restore.
.PARAMETER Replace
  Copy: pass --replace to arcane-db.
.PARAMETER DaemonProcessName
  Test seam: the process names that block Backup, Copy and a Rollback restore, instead of ArcaneCore.Realm, ArcaneCore.World, ArcaneCore.Gateway,
  arcane-db, arcane-account and arcane-content-importer. Leave it unset in a cutover.
#>
[CmdletBinding()]
param(
    [switch]$Plan,
    [ValidateSet('Backup', 'Copy', 'Verify', 'Check', 'Switch', 'Rollback')][string]$Step,
    [string]$LiveProfile,
    [string]$BackupDir,
    [string]$SecretFile,
    [string]$BinDir,
    [string]$TargetSqliteDir,
    [ValidateSet('auth', 'characters', 'world')][string[]]$Component = @('auth', 'characters', 'world'),
    [switch]$Confirm,
    [switch]$Replace,
    [string[]]$DaemonProcessName
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'LiveEnv.ps1')

$script:Password = $null

function Stop-Cutover {
    param([Parameter(Mandatory)][string]$Message, [int]$Code = 2)
    [Console]::Error.WriteLine("pg-cutover: refused: $(Protect-Secret $Message)")
    exit $Code
}

function Protect-Secret {
    # The text with the password (when one has been read) replaced by ***.
    param([string]$Text)
    if ($script:Password -and $Text) { return $Text.Replace($script:Password, '***') }
    return $Text
}

function Resolve-FullPath {
    # A path given relative to the operator's directory, made absolute before any child process starts in another directory.
    param([string]$Path)
    if (-not $Path) { return $Path }
    return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
}

function Get-SecretFilePath {
    if ($SecretFile) { return $SecretFile }
    $fromEnv = Get-OpsEnv 'ARCANECORE_PG_SECRET_FILE'
    if ($fromEnv) { return $fromEnv }
    $userHome = if ($env:USERPROFILE) { $env:USERPROFILE } elseif ($env:HOME) { $env:HOME } else { Stop-Cutover 'no user profile directory: pass -SecretFile or set ARCANECORE_PG_SECRET_FILE' }
    return (Join-Path (Join-Path $userHome '.arcanecore') 'pg-cutover.json')
}

function Read-PgSecret {
    # Host, port, user, password and database names from the secret file. A problem names the file and key, never a value: it stops the script,
    # or with -Quiet throws (the plan prints without a secret).
    param([switch]$Quiet)
    $fail = { param([string]$Message) if ($Quiet) { throw $Message } else { Stop-Cutover $Message } }
    $path = Get-SecretFilePath
    if (-not (Test-Path -LiteralPath $path)) { & $fail "the secret file '$path' does not exist (keys: Host, Port, Username, Password; see the script help)" }
    try { $json = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json } catch { & $fail "the secret file '$path' is not valid JSON" }
    $read = {
        param([string]$Key, [string]$Default)
        $p = $json.PSObject.Properties[$Key]
        if ($p -and -not [string]::IsNullOrWhiteSpace([string]$p.Value)) { return [string]$p.Value }
        return $Default
    }
    $secret = [ordered]@{
        Host = & $read 'Host' ''
        Port = & $read 'Port' '5432'
        Username = & $read 'Username' ''
        Password = & $read 'Password' ''
        SslMode = & $read 'SslMode' ''
        auth = & $read 'AuthDatabase' 'arcane_auth'
        characters = & $read 'CharactersDatabase' 'arcane_characters'
        world = & $read 'WorldDatabase' 'arcane_world'
    }
    foreach ($key in 'Host', 'Username', 'Password') {
        if (-not $secret[$key]) { & $fail "the secret file '$path' has no $key" }
    }
    $script:Password = $secret.Password
    return $secret
}

function Get-PgConnectionString {
    param([Parameter(Mandatory)]$Secret, [Parameter(Mandatory)][string]$ComponentName, [switch]$Masked)
    $password = if ($Masked) { '***' } else { $Secret.Password }
    $text = "Host=$($Secret.Host);Port=$($Secret.Port);Database=$($Secret[$ComponentName]);Username=$($Secret.Username);Password=$password"
    if ($Secret.SslMode) { $text += ";SSL Mode=$($Secret.SslMode)" }
    return $text
}

function ConvertTo-FlatSettings {
    # Flattens a parsed JSON object into 'A:B:C' keys the way the .NET configuration does (a PowerShell hashtable is case-insensitive).
    param($Node, [string]$Prefix, [hashtable]$Into)
    if ($Node -is [System.Management.Automation.PSCustomObject]) {
        foreach ($p in $Node.PSObject.Properties) {
            $key = if ($Prefix) { "${Prefix}:$($p.Name)" } else { $p.Name }
            ConvertTo-FlatSettings $p.Value $key $Into
        }
    } elseif ($Node -is [array]) {
        for ($i = 0; $i -lt $Node.Count; $i++) { ConvertTo-FlatSettings $Node[$i] "${Prefix}:$i" $Into }
    } elseif ($null -ne $Node) {
        $Into[$Prefix] = [string]$Node
    }
}

function Get-EffectiveDatabaseSettings {
    # The Database:* keys arcane-db and the daemons see, in their precedence: the appsettings.json beside arcane-db (bin), then the profile's
    # appsettings.json (passed to arcane-db as --config; the daemons start in the profile), then Database__* environment variables.
    $flat = @{}
    $files = New-Object System.Collections.Generic.List[string]
    $bin = $null
    try { $bin = Get-BinDirectory } catch { $bin = $null }
    if ($bin) { $files.Add((Join-Path $bin 'appsettings.json')) }
    $files.Add((Join-Path $LiveProfile 'appsettings.json'))
    foreach ($file in $files) {
        if (-not (Test-Path -LiteralPath $file)) { continue }
        try { $root = Get-Content -Raw -LiteralPath $file | ConvertFrom-Json } catch { Stop-Cutover "'$file' is not valid JSON" }
        ConvertTo-FlatSettings $root '' $flat
    }
    foreach ($entry in [Environment]::GetEnvironmentVariables().GetEnumerator()) {
        $name = [string]$entry.Key
        if ($name.StartsWith('Database__', [StringComparison]::OrdinalIgnoreCase) -or $name.StartsWith('Database:', [StringComparison]::OrdinalIgnoreCase)) {
            $flat[$name.Replace('__', ':')] = [string]$entry.Value
        }
    }
    return $flat
}

function Get-SqlitePath {
    # The file the effective Database section names for the component (the resolution of DatabaseOptions.Resolve: the component's own
    # ConnectionString, else Database:ConnectionString), made absolute against the profile directory, where the daemons and arcane-db run.
    param([Parameter(Mandatory)][string]$ComponentName)
    $flat = Get-EffectiveDatabaseSettings
    $section = "Database:$($ComponentName.Substring(0, 1).ToUpperInvariant())$($ComponentName.Substring(1))"
    $connection = $flat["${section}:ConnectionString"]
    $provider = $flat["${section}:Provider"]
    if ([string]::IsNullOrWhiteSpace($connection)) {
        $connection = $flat['Database:ConnectionString']
        $provider = $flat['Database:Provider']
    }
    if ([string]::IsNullOrWhiteSpace($connection)) { Stop-Cutover "no connection string is configured for $ComponentName (set ${section}:ConnectionString or Database:ConnectionString)" }
    if ([string]::IsNullOrWhiteSpace($provider)) { $provider = 'MariaDb' }
    if ($provider.Trim() -ine 'Sqlite' -and $provider.Trim() -ne '3') {
        Stop-Cutover "$ComponentName is configured for $($provider.Trim()), not SQLite: this step works on the SQLite files. Remove the Database__* overrides (or the PostgreSQL settings) first"
    }
    if ($connection -notmatch '(?i)(?:^|;)\s*(?:Data Source|DataSource|Filename)\s*=\s*([^;]+)') { Stop-Cutover "the $ComponentName SQLite connection string has no Data Source" }
    $path = $Matches[1].Trim().Trim('"', "'")
    if (-not [IO.Path]::IsPathRooted($path)) { $path = Join-Path $LiveProfile $path }
    return $path
}

function Test-DaemonsRunning {
    $names = if ($DaemonProcessName) { $DaemonProcessName } else { @('ArcaneCore.Realm', 'ArcaneCore.World', 'ArcaneCore.Gateway', 'arcane-db', 'arcane-account', 'arcane-content-importer') }
    return @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $names -contains $_.ProcessName })
}

function Assert-NoDaemons {
    $running = @(Test-DaemonsRunning)
    if ($running.Count -gt 0) { Stop-Cutover "$(($running | ForEach-Object { "$($_.ProcessName) ($($_.Id))" }) -join ', ') is running; stop every daemon and tool first" }
}

function Get-BinDirectory {
    if ($BinDir) { return $BinDir }
    return (Join-Path (Resolve-LiveHome $null) 'bin')
}

function Get-ArcaneDbPath {
    $bin = Get-BinDirectory
    foreach ($name in 'arcane-db.exe', 'arcane-db') {
        $candidate = Join-Path $bin $name
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    Stop-Cutover "arcane-db not found in '$bin' (pass -BinDir)"
}

# ---- arcane-db: every database access goes through the tool, which runs on the repository's own runtime with its own drivers ----------------------

function Invoke-ArcaneDb {
    # Runs arcane-db with the profile as its working directory (a relative Data Source then means the same file it means for the daemons) and the
    # profile's appsettings.json as --config, scrubs the password from what it prints, returns its exit code.
    param([string[]]$Arguments)
    $exe = Get-ArcaneDbPath
    if (-not (Test-Path -LiteralPath $LiveProfile -PathType Container)) { Stop-Cutover "the live profile directory '$LiveProfile' does not exist" }
    $all = @($Arguments)
    $settings = Join-Path $LiveProfile 'appsettings.json'
    if (Test-Path -LiteralPath $settings) { $all += @('--config', $settings) }
    $start = New-Object System.Diagnostics.ProcessStartInfo($exe)
    $start.WorkingDirectory = $LiveProfile
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $all) { $start.ArgumentList.Add($argument) }
    $process = [System.Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        foreach ($text in @($stdout.Result, $stderr.Result)) {
            foreach ($line in ($text -split "\r?\n")) {
                if ($line) { Write-Host (Protect-Secret $line) }
            }
        }
        return $process.ExitCode
    } finally { $process.Dispose() }
}

function Set-TargetEnvironment {
    # Puts the target settings into this process's environment (inherited by arcane-db only). Returns the names set.
    param($Secret, [switch]$DatabaseSection)
    $names = New-Object System.Collections.Generic.List[string]
    $set = { param($k, $v) [Environment]::SetEnvironmentVariable($k, $v); $names.Add($k) }
    if (-not $DatabaseSection) { & $set 'ARCANE_DB_TARGET_PROVIDER' 'postgresql' }
    foreach ($name in $Component) {
        $cs = Get-PgConnectionString $Secret $name
        if ($DatabaseSection) {
            $key = (Get-Culture).TextInfo.ToTitleCase($name)
            & $set "Database__${key}__Provider" 'PostgreSql'
            & $set "Database__${key}__ConnectionString" $cs
        } else {
            & $set "ARCANE_DB_TARGET_$($name.ToUpperInvariant())_CONNECTION" $cs
        }
    }
    return $names
}

function Set-SqliteTargetEnvironment {
    # Puts SQLite files <dir>\<component>.db in as the target of arcane-db verify. Returns the names set.
    param([Parameter(Mandatory)][string]$Directory)
    $names = New-Object System.Collections.Generic.List[string]
    [Environment]::SetEnvironmentVariable('ARCANE_DB_TARGET_PROVIDER', 'sqlite')
    $names.Add('ARCANE_DB_TARGET_PROVIDER')
    foreach ($name in $Component) {
        $file = Join-Path $Directory "$name.db"
        if (-not (Test-Path -LiteralPath $file)) { Stop-Cutover "the target file for $name does not exist" }
        $key = "ARCANE_DB_TARGET_$($name.ToUpperInvariant())_CONNECTION"
        [Environment]::SetEnvironmentVariable($key, "Data Source=$file")
        $names.Add($key)
    }
    return $names
}

function Clear-Environment {
    param($Names)
    foreach ($n in $Names) { [Environment]::SetEnvironmentVariable($n, $null) }
}

function Invoke-VerifyRun {
    # arcane-db verify for each component against the target already placed in the environment. Returns the number of components that differ;
    # any other failure of the tool stops the script with its exit code.
    $differing = 0
    foreach ($name in $Component) {
        $code = Invoke-ArcaneDb @('verify', '--component', $name)
        if ($code -eq 9) { $differing++ }
        elseif ($code -ne 0) { Stop-Cutover "arcane-db verify of $name exited $code (see its output above)" -Code $code }
    }
    return $differing
}

function Invoke-Verify {
    # Each component of the configured SQLite databases against the PostgreSQL target (or, with -TargetSqliteDir, against fixture files).
    # The secret is read and validated first, before arcane-db is looked for or started.
    $secret = $null
    if (-not $TargetSqliteDir) { $secret = Read-PgSecret }
    $names = if ($TargetSqliteDir) { Set-SqliteTargetEnvironment $TargetSqliteDir } else { Set-TargetEnvironment $secret }
    try { $differing = Invoke-VerifyRun } finally { Clear-Environment $names }
    return $differing
}

# ---- the steps ------------------------------------------------------------------------------------------------------------------------------

function Get-SwitchLines {
    param($Secret)
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('Set these for the realm (ArcaneCore.Realm), the world (ArcaneCore.World), the gateway (ArcaneCore.Gateway, when it opens a database) and the tools')
    $lines.Add('(arcane-db, arcane-account, arcane-content-importer), as environment variables of each process or the same keys in its configuration:')
    foreach ($name in 'Auth', 'Characters', 'World') {
        $cs = if ($Secret) { Get-PgConnectionString $Secret $name.ToLowerInvariant() -Masked } else { "Host=<host>;Port=5432;Database=arcane_$($name.ToLowerInvariant());Username=<user>;Password=***" }
        $lines.Add("  Database__${name}__Provider=PostgreSql")
        $lines.Add("  Database__${name}__ConnectionString=$cs")
    }
    $lines.Add('  Database__Upgrade__Policy=Never   (a daemon then only validates the schema: it refuses a missing or different schema and writes none)')
    $lines.Add('Put the real connection strings into the environment from the secret file, never into a file of the repository or a command line.')
    return $lines
}

function Write-PlanOutput {
    $secretPath = Get-SecretFilePath
    $secret = $null
    if (Test-Path -LiteralPath $secretPath) {
        try { $secret = Read-PgSecret -Quiet } catch { $secret = $null }
    }
    $profileText = if ($LiveProfile) { $LiveProfile } else { '<ARCANECORE_LIVE_PROFILE>' }
    $backupText = if ($BackupDir) { $BackupDir } else { '<backup directory>' }
    Write-Output 'pg-cutover plan (nothing is touched: no database is opened, no process started, no file written)'
    Write-Output "  live profile : $profileText"
    Write-Output "  backup dir   : $backupText"
    Write-Output "  secret file  : $secretPath ($(if ($secret) { 'found; password masked' } else { 'not read in -Plan' }))"
    Write-Output "  components   : $($Component -join ', ')"
    Write-Output ''
    Write-Output 'STEP 1  Preflight: stop the realm, world, gateway and every tool that holds auth.db, characters.db or world.db. Needs arcane-db of this build in the bin directory (it has the copy, backup and verify commands), a reachable PostgreSQL 16 server and empty UTF8 databases arcane_auth, arcane_characters, arcane_world owned by a role that may create tables.'
    Write-Output "STEP 2  Backup (-Step Backup -BackupDir <dir> -Confirm): arcane-db backup writes a VACUUM INTO copy of $($Component | ForEach-Object { "$_.db" }) into the backup directory with integrity_check, arcane-db verify compares each copy with its source table by table, manifest.json holds the SHA-256."
    Write-Output 'STEP 3  Copy (-Step Copy -BackupDir <dir> -Confirm): arcane-db copy --component all --config <profile>\appsettings.json, run in the profile directory; target from ARCANE_DB_TARGET_PROVIDER=postgresql and ARCANE_DB_TARGET_<AUTH|CHARACTERS|WORLD>_CONNECTION (set only in the child process from the secret file).'
    Write-Output '        World content: copied with the world component. If world.creature_template is empty in the source, run arcane-content-importer --provider postgresql --connection-string <target> (the importers accept PostgreSQL) before opening to players.'
    Write-Output 'STEP 4  Verify (-Step Verify): arcane-db verify per component, per table the row count and an order-independent digest of every value, SQLite source against PostgreSQL target; exit 1 on any difference.'
    Write-Output 'STEP 5  Schema check (-Step Check): arcane-db status and arcane-db check against the target; then start with Database:Upgrade:Policy=Never so the schema initializers only validate (ValidateOnly) and must pass.'
    Write-Output 'STEP 6  Switch settings (-Step Switch prints them):'
    foreach ($line in Get-SwitchLines $secret) { Write-Output "        $line" }
    Write-Output 'STEP 7  Start the realm, then the world (and gateway); log in, load a character.'
    Write-Output 'STEP 8  Rollback (-Step Rollback -BackupDir <dir> [-Confirm]): stop the daemons, remove the Database__* overrides (the SQLite settings apply again), restore the SQLite files from the backup if they were touched. Changes made on PostgreSQL after the switch are not in the SQLite files; bring them back first with arcane-db copy --source-provider postgresql ... --replace if they matter.'
}

function Invoke-BackupStep {
    if (-not $Confirm) { Stop-Cutover 'Backup writes files; pass -Confirm' }
    if (-not $BackupDir) { Stop-Cutover 'Backup needs -BackupDir' }
    Assert-NoDaemons
    New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
    if (@(Get-ChildItem -LiteralPath $BackupDir -Filter '*.db' -File).Count -gt 0) { Stop-Cutover "'$BackupDir' already holds .db files; use an empty directory" }
    $manifest = [ordered]@{}
    foreach ($name in $Component) {
        $code = Invoke-ArcaneDb @('backup', '--component', $name, '--backup-dir', $BackupDir)
        if ($code -ne 0) { Stop-Cutover "arcane-db backup of $name exited $code (see its output above)" -Code $code }
        $backupPath = Join-Path $BackupDir "$name.db"
        if (-not (Test-Path -LiteralPath $backupPath)) { Stop-Cutover "arcane-db backup of $name reported success but wrote no $name.db" }
        $manifest[$name] = (Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash
    }
    # Compare each backup file with its source before the manifest is written: a backup that differs is not a backup.
    $names = Set-SqliteTargetEnvironment $BackupDir
    try { $differing = Invoke-VerifyRun } finally { Clear-Environment $names }
    if ($differing -gt 0) { Stop-Cutover "the backup differs from its source in $differing component(s); not recorded" -Code 1 }
    ($manifest | ConvertTo-Json) | Set-Content -LiteralPath (Join-Path $BackupDir 'manifest.json') -Encoding UTF8
    Write-Output "backup verified; manifest written to $(Join-Path $BackupDir 'manifest.json')"
}

function Assert-BackupMatchesManifest {
    if (-not $BackupDir) { Stop-Cutover 'this step needs -BackupDir (the verified backup)' }
    $manifestPath = Join-Path $BackupDir 'manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath)) { Stop-Cutover "no manifest.json in '$BackupDir': run -Step Backup first" }
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    foreach ($name in $Component) {
        $p = $manifest.PSObject.Properties[$name]
        if (-not $p) { Stop-Cutover "the manifest has no entry for $name" }
        $file = Join-Path $BackupDir "$name.db"
        if (-not (Test-Path -LiteralPath $file)) { Stop-Cutover "the backup file $name.db is missing" }
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne [string]$p.Value) { Stop-Cutover "the backup file $name.db no longer matches its manifest hash" }
        Write-Host "backup $name matches the manifest"
    }
}

function Invoke-CopyStep {
    if (-not $Confirm) { Stop-Cutover 'Copy writes to the target databases; pass -Confirm' }
    if ($Component.Count -ne 3 -and $Component.Count -ne 1) { Stop-Cutover 'Copy takes all three components or exactly one' }
    Assert-NoDaemons
    [void](Assert-BackupMatchesManifest)
    $secret = Read-PgSecret
    $names = Set-TargetEnvironment $secret
    try {
        $arguments = @('copy', '--component', $(if ($Component.Count -eq 3) { 'all' } else { $Component[0] }))
        if ($Replace) { $arguments += '--replace' }
        $code = Invoke-ArcaneDb $arguments
    } finally { Clear-Environment $names }
    if ($code -ne 0) { Stop-Cutover "arcane-db copy exited $code (see its output above; 9 means a table differed and the component was rolled back)" -Code $code }
    Write-Output 'copy finished; run -Step Verify next'
}

function Invoke-CheckStep {
    $secret = Read-PgSecret
    $names = Set-TargetEnvironment $secret -DatabaseSection
    $worst = 0
    try {
        foreach ($command in 'status', 'check') {
            $code = Invoke-ArcaneDb @($command, '--component', 'all')
            if ($code -ne 0) { $worst = $code }
        }
    } finally { Clear-Environment $names }
    if ($worst -ne 0) { Stop-Cutover "arcane-db reported exit $worst against the target" -Code $worst }
    Write-Output 'schema check: every component Current and equal to the model'
}

function Invoke-RollbackStep {
    [void](Assert-BackupMatchesManifest)
    Write-Output 'rollback: (1) stop every daemon; (2) remove Database__*__Provider and Database__*__ConnectionString (and Database__Upgrade__Policy if you set it) from the realm, world, gateway and tool environments so the SQLite settings apply again; (3) start the realm, then the world.'
    Write-Output 'Changes made on PostgreSQL after the switch are not in the SQLite files.'
    if (-not $Confirm) { Write-Output 'no files restored (pass -Confirm to restore the SQLite files from the backup)'; return }
    Assert-NoDaemons
    $stamp = Get-Date -Format 'yyyyMMddHHmmss'
    foreach ($name in $Component) {
        $target = Get-SqlitePath $name
        if (Test-Path -LiteralPath $target) {
            Move-Item -LiteralPath $target -Destination "$target.pre-rollback-$stamp"
            foreach ($suffix in '-wal', '-shm') {
                if (Test-Path -LiteralPath "$target$suffix") { Move-Item -LiteralPath "$target$suffix" -Destination "$target$suffix.pre-rollback-$stamp" }
            }
        }
        Copy-Item -LiteralPath (Join-Path $BackupDir "$name.db") -Destination $target
        Write-Output "restored $name.db"
    }
}

# ---- main -----------------------------------------------------------------------------------------------------------------------------------

if (-not $Plan -and -not $Step) { Stop-Cutover 'pass -Plan or -Step Backup|Copy|Verify|Check|Switch|Rollback' }
if ($Step -and $Step -ne 'Switch' -and -not $LiveProfile) { $LiveProfile = Resolve-LiveProfile $null }
if ($Plan -and -not $LiveProfile) { $LiveProfile = Get-OpsEnv 'ARCANECORE_LIVE_PROFILE' }
# Paths given relative to the operator's directory are fixed now: arcane-db runs in the profile directory.
$LiveProfile = Resolve-FullPath $LiveProfile
$BackupDir = Resolve-FullPath $BackupDir
$BinDir = Resolve-FullPath $BinDir
$TargetSqliteDir = Resolve-FullPath $TargetSqliteDir
$SecretFile = Resolve-FullPath $SecretFile

try {
    if ($Plan) {
        Write-PlanOutput
        exit 0
    }
    switch ($Step) {
        'Backup' { Invoke-BackupStep }
        'Copy' { Invoke-CopyStep }
        'Verify' {
            $differing = Invoke-Verify
            if ($differing -gt 0) { exit 1 }
        }
        'Check' { Invoke-CheckStep }
        'Switch' {
            $secret = $null
            if (Test-Path -LiteralPath (Get-SecretFilePath)) { $secret = Read-PgSecret }
            Get-SwitchLines $secret | ForEach-Object { Write-Output $_ }
        }
        'Rollback' { Invoke-RollbackStep }
    }
} catch {
    [Console]::Error.WriteLine("pg-cutover: failed: $(Protect-Secret $_.Exception.Message)")
    exit 1
}
