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

  Steps (the order of the cutover; -Plan prints all of them and touches nothing, not even a database):
    Backup    verified copy of the SQLite files: each file is written with VACUUM INTO (a consistent snapshot, the -wal is folded in), checked with
              PRAGMA integrity_check, compared with its source table by table (row counts and key digests, the Verify engine) and recorded with its
              SHA-256 in manifest.json. Needs -Confirm. Refuses while a daemon runs.
    Copy      arcane-db copy --component all into the PostgreSQL databases (the schema is created by the same bootstrap the daemons run). Needs
              -Confirm and the Backup manifest. Refuses while a daemon runs. -Replace re-runs a copy that stopped part-way.
    Verify    independent per-table comparison of each SQLite component with its target: row count and an order-independent digest of every
              primary key. Read-only. Exit 0 all tables match, 1 any table differs (a dropped row shows as a count and digest difference).
    Check     arcane-db status and check against the target, read-only: the schema is at the build's version and equals the model.
    Switch    prints the Database:* settings (environment variable form, secrets masked) for the realm, world, gateway and tools. Changes nothing.
    Rollback  checks the backup against its manifest and prints the revert; with -Confirm restores the SQLite files from the backup (refuses while a
              daemon runs; the files being replaced are kept as <name>.pre-rollback-<stamp>).

  Secrets: the PostgreSQL host, user and password are read only from the secret file of the user profile (default <profile>\.arcanecore\pg-cutover.json,
  or ARCANECORE_PG_SECRET_FILE, or -SecretFile: a path, never a value). JSON keys: Host, Port, Username, Password and optionally AuthDatabase,
  CharactersDatabase, WorldDatabase (defaults arcane_auth, arcane_characters, arcane_world), SslMode. Connection strings are built in memory, handed
  to arcane-db only through the child process environment (ARCANE_DB_TARGET_*_CONNECTION, Database__*__ConnectionString) and never printed, logged or
  taken from an argument. Output is scrubbed of the password.

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
  Directory with arcane-db and the Npgsql and Microsoft.Data.Sqlite assemblies (Verify, Backup). Default <ARCANECORE_LIVE_HOME>\bin.
.PARAMETER TargetSqliteDir
  Verify against SQLite files <dir>\<component>.db instead of PostgreSQL (fixtures; no secrets involved).
.PARAMETER Component
  auth, characters, world (default all three).
.PARAMETER Confirm
  Required by Backup, Copy and Rollback restore.
.PARAMETER Replace
  Copy: pass --replace to arcane-db.
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
    [switch]$Replace
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

function Get-SecretFilePath {
    if ($SecretFile) { return $SecretFile }
    $fromEnv = Get-OpsEnv 'ARCANECORE_PG_SECRET_FILE'
    if ($fromEnv) { return $fromEnv }
    $userHome = if ($env:USERPROFILE) { $env:USERPROFILE } elseif ($env:HOME) { $env:HOME } else { Stop-Cutover 'no user profile directory: pass -SecretFile or set ARCANECORE_PG_SECRET_FILE' }
    return (Join-Path (Join-Path $userHome '.arcanecore') 'pg-cutover.json')
}

function Read-PgSecret {
    # Host, port, user, password and database names from the secret file. Throws a message that names the file and key, never a value.
    $path = Get-SecretFilePath
    if (-not (Test-Path -LiteralPath $path)) { Stop-Cutover "the secret file '$path' does not exist (keys: Host, Port, Username, Password; see the script help)" }
    try { $json = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json } catch { Stop-Cutover "the secret file '$path' is not valid JSON" }
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
        if (-not $secret[$key]) { Stop-Cutover "the secret file '$path' has no $key" }
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

function Get-ProfileSettings {
    $settings = Join-Path $LiveProfile 'appsettings.json'
    if (-not (Test-Path -LiteralPath $settings)) { return $null }
    try { return Get-Content -Raw -LiteralPath $settings | ConvertFrom-Json } catch { Stop-Cutover "'$settings' is not valid JSON" }
}

function Get-SqlitePath {
    # The file the profile's Database:<Component> names (Provider Sqlite, Data Source), else <profile>\<component>.db.
    param([Parameter(Mandatory)][string]$ComponentName)
    $settings = Get-ProfileSettings
    if ($null -ne $settings -and $settings.PSObject.Properties['Database']) {
        $db = $settings.Database
        $section = $null
        foreach ($p in $db.PSObject.Properties) { if ($p.Name -ieq $ComponentName) { $section = $p.Value } }
        if ($null -ne $section -and $section.PSObject.Properties['ConnectionString'] -and ([string]$section.ConnectionString) -match '(?i)(?:^|;)\s*Data Source\s*=\s*([^;]+)') {
            $path = $Matches[1].Trim()
            if (-not [IO.Path]::IsPathRooted($path)) { $path = Join-Path $LiveProfile $path }
            return $path
        }
    }
    return (Join-Path $LiveProfile "$ComponentName.db")
}

function Test-DaemonsRunning {
    $names = 'ArcaneCore.Realm', 'ArcaneCore.World', 'ArcaneCore.Gateway', 'arcane-db', 'arcane-account', 'arcane-content-importer'
    return @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $names -contains $_.ProcessName })
}

function Assert-NoDaemons {
    $running = Test-DaemonsRunning
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

# ---- the verify engine: row counts and an order-independent key digest, per table ----------------------------------------------------------

function Initialize-DriverTypes {
    if ('PgCutoverDigest' -as [type]) { return }
    $bin = Get-BinDirectory
    foreach ($name in 'Microsoft.Extensions.Logging.Abstractions', 'Microsoft.Extensions.DependencyInjection.Abstractions', 'SQLitePCLRaw.core',
        'SQLitePCLRaw.provider.e_sqlite3', 'SQLitePCLRaw.batteries_v2', 'Microsoft.Data.Sqlite', 'Npgsql') {
        $path = Join-Path $bin "$name.dll"
        if (Test-Path -LiteralPath $path) {
            # An assembly of that name already loaded (the host's own) throws; the loaded one is then used.
            try { [void][System.Runtime.Loader.AssemblyLoadContext]::Default.LoadFromAssemblyPath((Resolve-Path -LiteralPath $path).Path) } catch { $null = $_ }
        }
    }
    if (-not ('Microsoft.Data.Sqlite.SqliteConnection' -as [type])) { Stop-Cutover "Microsoft.Data.Sqlite.dll was not found in '$bin' (pass -BinDir)" }
    [SQLitePCL.Batteries_V2]::Init()
    Add-Type -TypeDefinition @'
using System;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

public static class PgCutoverDigest
{
    // Reads every row of the reader and returns "rows:sum:xor", the 64-bit sum and xor of each row's SHA-256 prefix: independent of row order.
    public static string Compute(DbDataReader reader)
    {
        ulong sum = 0;
        ulong xor = 0;
        long rows = 0;
        object[] values = new object[reader.FieldCount];
        StringBuilder text = new StringBuilder();
        SHA256 sha = SHA256.Create();
        try
        {
            while (reader.Read())
            {
                reader.GetValues(values);
                text.Clear();
                for (int i = 0; i < values.Length; i++)
                {
                    if (i > 0) { text.Append('\u001f'); }
                    text.Append(Normalize(values[i]));
                }

                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()));
                ulong h = BitConverter.ToUInt64(hash, 0);
                unchecked { sum += h; }
                xor ^= h;
                rows++;
            }
        }
        finally
        {
            sha.Dispose();
        }

        return rows.ToString(CultureInfo.InvariantCulture) + ":" + sum.ToString("x16", CultureInfo.InvariantCulture) + ":" + xor.ToString("x16", CultureInfo.InvariantCulture);
    }

    // One text form per value, equal for SQLite's and PostgreSQL's reading of the same key.
    private static string Normalize(object value)
    {
        if (value == null || value is DBNull) { return "\u0001NULL"; }
        if (value is bool) { return ((bool)value) ? "1" : "0"; }
        if (value is byte[]) { return BitConverter.ToString((byte[])value); }
        if (value is decimal) { return ((decimal)value).ToString("0.############################", CultureInfo.InvariantCulture); }
        if (value is DateTime) { return ((DateTime)value).ToString("yyyy-MM-ddTHH:mm:ss.ffffff", CultureInfo.InvariantCulture); }
        if (value is Guid) { return ((Guid)value).ToString("D"); }
        IFormattable formattable = value as IFormattable;
        if (formattable != null) { return formattable.ToString(null, CultureInfo.InvariantCulture); }
        return value.ToString();
    }
}
'@
}

function Open-Connection {
    param([Parameter(Mandatory)][ValidateSet('sqlite', 'postgresql')][string]$Provider, [Parameter(Mandatory)][string]$ConnectionString)
    if ($Provider -eq 'sqlite') {
        $connection = New-Object Microsoft.Data.Sqlite.SqliteConnection($ConnectionString)
    } else {
        if (-not ('Npgsql.NpgsqlConnection' -as [type])) { Stop-Cutover "Npgsql.dll was not found in '$(Get-BinDirectory)' (pass -BinDir)" }
        $connection = New-Object Npgsql.NpgsqlConnection($ConnectionString)
    }
    $connection.Open()
    return $connection
}

function Invoke-Rows {
    param([Parameter(Mandatory)]$Connection, [Parameter(Mandatory)][string]$Sql)
    $command = $Connection.CreateCommand()
    $command.CommandText = $Sql
    $command.CommandTimeout = 0
    $reader = $command.ExecuteReader()
    try {
        while ($reader.Read()) {
            $row = New-Object object[] $reader.FieldCount
            [void]$reader.GetValues($row)
            , $row
        }
    } finally { $reader.Dispose(); $command.Dispose() }
}

function ConvertTo-QuotedIdentifier { param([string]$Name) return '"' + $Name.Replace('"', '""') + '"' }

function Get-TableNames {
    param($Connection, [string]$Provider, [string]$ComponentName)
    $sql = if ($Provider -eq 'sqlite') {
        "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite\_%' ESCAPE '\'"
    } else {
        "SELECT table_name FROM information_schema.tables WHERE table_schema = current_schema() AND table_type = 'BASE TABLE'"
    }
    return @(Invoke-Rows $Connection $sql | ForEach-Object { [string]$_[0] } | Where-Object { $_ -ine "${ComponentName}_schema" } | Sort-Object)
}

function Get-KeyColumns {
    param($Connection, [string]$Provider, [string]$Table)
    $quoted = $Table.Replace("'", "''")
    $sql = if ($Provider -eq 'sqlite') {
        "SELECT name FROM pragma_table_info('$quoted') WHERE pk > 0 ORDER BY pk"
    } else {
        "SELECT kcu.column_name FROM information_schema.table_constraints tc JOIN information_schema.key_column_usage kcu " +
        "ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema AND tc.table_name = kcu.table_name " +
        "WHERE tc.constraint_type = 'PRIMARY KEY' AND tc.table_schema = current_schema() AND tc.table_name = '$quoted' ORDER BY kcu.ordinal_position"
    }
    return @(Invoke-Rows $Connection $sql | ForEach-Object { [string]$_[0] })
}

function Get-TableFingerprint {
    # @{ Rows = <count>; Digest = "rows:sum:xor" of the primary key columns, or 'count-only' for a table without a primary key }
    param($Connection, [string]$Provider, [string]$Table, [string[]]$KeyColumns)
    $quotedTable = ConvertTo-QuotedIdentifier $Table
    if ($KeyColumns.Count -eq 0) {
        $rows = [long](@(Invoke-Rows $Connection "SELECT COUNT(*) FROM $quotedTable")[0][0])
        return @{ Rows = $rows; Digest = 'count-only' }
    }
    $columns = ($KeyColumns | ForEach-Object { ConvertTo-QuotedIdentifier $_ }) -join ', '
    $command = $Connection.CreateCommand()
    $command.CommandText = "SELECT $columns FROM $quotedTable"
    $command.CommandTimeout = 0
    $reader = $command.ExecuteReader()
    try { $digest = [PgCutoverDigest]::Compute($reader) } finally { $reader.Dispose(); $command.Dispose() }
    return @{ Rows = [long]($digest.Split(':')[0]); Digest = $digest }
}

function Compare-Component {
    # Compares one component's tables on two open connections; writes one line per table; returns the number of differences.
    param($Source, [string]$SourceProvider, $Target, [string]$TargetProvider, [string]$ComponentName)
    $sourceTables = Get-TableNames $Source $SourceProvider $ComponentName
    $targetTables = Get-TableNames $Target $TargetProvider $ComponentName
    $bad = 0
    foreach ($missing in $sourceTables | Where-Object { $targetTables -notcontains $_ }) {
        Write-Output "FAIL $ComponentName.$missing  missing in the target"
        $bad++
    }
    foreach ($extra in $targetTables | Where-Object { $sourceTables -notcontains $_ }) {
        Write-Output "FAIL $ComponentName.$extra  not in the source"
        $bad++
    }
    foreach ($table in $sourceTables | Where-Object { $targetTables -contains $_ }) {
        $keys = Get-KeyColumns $Source $SourceProvider $table
        $a = Get-TableFingerprint $Source $SourceProvider $table $keys
        $b = Get-TableFingerprint $Target $TargetProvider $table $keys
        if ($a.Rows -eq $b.Rows -and $a.Digest -ceq $b.Digest) {
            Write-Output "OK   $ComponentName.$table  rows=$($a.Rows) digest=$($a.Digest)"
        } else {
            Write-Output "FAIL $ComponentName.$table  source rows=$($a.Rows) digest=$($a.Digest)  target rows=$($b.Rows) digest=$($b.Digest)"
            $bad++
        }
        if ($ComponentName -eq 'world' -and $table -eq 'creature_template' -and $a.Rows -eq 0) {
            Write-Output "WARN world.creature_template is empty in the source: the copy carries no world content; populate it with arcane-content-importer --provider postgresql (docs/areas/content-import.md)"
        }
    }
    return $bad
}

function Invoke-Verify {
    # Compares each component's SQLite file (-SourceDir overrides the profile) with the target; returns the number of differences.
    param([string]$SourceDir)
    Initialize-DriverTypes
    $secret = $null
    if (-not $TargetSqliteDir) { $secret = Read-PgSecret }
    $total = 0
    $tables = 0
    foreach ($name in $Component) {
        $sourcePath = if ($SourceDir) { Join-Path $SourceDir "$name.db" } else { Get-SqlitePath $name }
        if (-not (Test-Path -LiteralPath $sourcePath)) { Stop-Cutover "the source file for $name does not exist" }
        $source = Open-Connection 'sqlite' "Data Source=$sourcePath;Mode=ReadOnly;Pooling=False"
        try {
            if ($TargetSqliteDir) {
                $targetPath = Join-Path $TargetSqliteDir "$name.db"
                if (-not (Test-Path -LiteralPath $targetPath)) { Stop-Cutover "the target file for $name does not exist" }
                $target = Open-Connection 'sqlite' "Data Source=$targetPath;Mode=ReadOnly;Pooling=False"
                $targetProvider = 'sqlite'
            } else {
                $target = Open-Connection 'postgresql' (Get-PgConnectionString $secret $name)
                $targetProvider = 'postgresql'
            }
            try {
                $lines = @(Compare-Component $source 'sqlite' $target $targetProvider $name)
                $lines | ForEach-Object { Write-Host $_ }
                $total += @($lines | Where-Object { $_ -like 'FAIL *' }).Count
                $tables += @($lines | Where-Object { $_ -like 'OK *' -or $_ -like 'FAIL *' }).Count
            } finally { $target.Dispose() }
        } finally { $source.Dispose() }
    }
    Write-Host "verify: $tables table lines, $total difference(s)"
    return $total
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
        try { $secret = Read-PgSecret } catch { $secret = $null }
    }
    $profileText = if ($LiveProfile) { $LiveProfile } else { '<ARCANECORE_LIVE_PROFILE>' }
    $backupText = if ($BackupDir) { $BackupDir } else { '<backup directory>' }
    Write-Output 'pg-cutover plan (nothing is touched: no database is opened, no process started, no file written)'
    Write-Output "  live profile : $profileText"
    Write-Output "  backup dir   : $backupText"
    Write-Output "  secret file  : $secretPath ($(if ($secret) { 'found; password masked' } else { 'not read in -Plan' }))"
    Write-Output "  components   : $($Component -join ', ')"
    Write-Output ''
    Write-Output 'STEP 1  Preflight: stop the realm, world, gateway and every tool that holds auth.db, characters.db or world.db; merge PR arcane-db copy (PostgreSQL 16 server reachable, empty UTF8 databases arcane_auth, arcane_characters, arcane_world owned by a role that may create tables).'
    Write-Output "STEP 2  Backup (-Step Backup -BackupDir <dir> -Confirm): VACUUM INTO a verified copy of $($Component | ForEach-Object { "$_.db" }) into the backup directory, integrity_check, table-by-table comparison with the source, manifest.json with SHA-256."
    Write-Output 'STEP 3  Copy (-Step Copy -BackupDir <dir> -Confirm): arcane-db copy --component all --config <profile>\appsettings.json; target from ARCANE_DB_TARGET_PROVIDER=postgresql and ARCANE_DB_TARGET_<AUTH|CHARACTERS|WORLD>_CONNECTION (set only in the child process from the secret file).'
    Write-Output '        World content: copied with the world component. If world.creature_template is empty in the source, run arcane-content-importer --provider postgresql --connection-string <target> (the importers accept PostgreSQL) before opening to players.'
    Write-Output 'STEP 4  Verify (-Step Verify): per table row count and an order-independent digest of every primary key, SQLite source against PostgreSQL target; exit 1 on any difference.'
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
    Initialize-DriverTypes
    $manifest = [ordered]@{}
    foreach ($name in $Component) {
        $sourcePath = Get-SqlitePath $name
        if (-not (Test-Path -LiteralPath $sourcePath)) { Stop-Cutover "the source file for $name does not exist" }
        $backupPath = Join-Path $BackupDir "$name.db"
        $source = Open-Connection 'sqlite' "Data Source=$sourcePath;Mode=ReadOnly;Pooling=False"
        try {
            $command = $source.CreateCommand()
            $command.CommandText = 'VACUUM INTO $path'
            [void]$command.Parameters.AddWithValue('$path', $backupPath)
            [void]$command.ExecuteNonQuery()
            $command.Dispose()
        } finally { $source.Dispose() }
        $check = Open-Connection 'sqlite' "Data Source=$backupPath;Mode=ReadOnly;Pooling=False"
        try {
            $result = [string](@(Invoke-Rows $check 'PRAGMA integrity_check')[0][0])
            if ($result -ne 'ok') { Stop-Cutover "integrity_check of the $name backup failed: $result" }
        } finally { $check.Dispose() }
        $manifest[$name] = (Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash
        Write-Output "backup $name -> $backupPath integrity ok"
    }
    # Compare each backup file with its source before the manifest is written: a backup that differs is not a backup.
    $saved = $script:TargetSqliteDir
    $script:TargetSqliteDir = $BackupDir
    try { $differences = Invoke-Verify -SourceDir $null } finally { $script:TargetSqliteDir = $saved }
    if ($differences -gt 0) { Stop-Cutover "the backup differs from its source in $differences place(s); not recorded" -Code 1 }
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

function Invoke-ArcaneDb {
    param([string[]]$Arguments)
    $exe = Get-ArcaneDbPath
    $output = & $exe @Arguments 2>&1 | ForEach-Object { Protect-Secret ([string]$_) }
    $code = $LASTEXITCODE
    $output | ForEach-Object { Write-Host $_ }
    return $code
}

function Invoke-CopyStep {
    if (-not $Confirm) { Stop-Cutover 'Copy writes to the target databases; pass -Confirm' }
    Assert-NoDaemons
    [void](Assert-BackupMatchesManifest)
    $secret = Read-PgSecret
    $names = Set-TargetEnvironment $secret
    try {
        $arguments = @('copy', '--component', $(if ($Component.Count -eq 3) { 'all' } else { $Component[0] }))
        if ($Component.Count -ne 3 -and $Component.Count -ne 1) { Stop-Cutover 'Copy takes all three components or exactly one' }
        $settings = Join-Path $LiveProfile 'appsettings.json'
        if (Test-Path -LiteralPath $settings) { $arguments += @('--config', $settings) }
        if ($Replace) { $arguments += '--replace' }
        $code = Invoke-ArcaneDb $arguments
    } finally { foreach ($n in $names) { [Environment]::SetEnvironmentVariable($n, $null) } }
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
    } finally { foreach ($n in $names) { [Environment]::SetEnvironmentVariable($n, $null) } }
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

try {
    if ($Plan) {
        Write-PlanOutput
        exit 0
    }
    switch ($Step) {
        'Backup' { Invoke-BackupStep }
        'Copy' { Invoke-CopyStep }
        'Verify' {
            $differences = Invoke-Verify
            if ($differences -gt 0) { exit 1 }
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
