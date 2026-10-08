<#
.SYNOPSIS
  Brings an existing world database up to the content tables the current world reads, without touching its creatures, objects,
  items, quests, loot or NPC services. Safe to run again: the second run leaves the same rows.

.DESCRIPTION
  Runs `arcane-content-importer refresh` (docs/areas/content-import.md, "refresh") against -WorldDatabase with a classic-db or
  vmangos dump and the client DBCs it needs:

    world_safe_locs, game_graveyard_zone          dump (+ WorldSafeLocs.dbc for ids the dump lacks)
    battleground_template, battlemaster_entry,
    creature_battleground, gameobject_battleground  dump
    exploration_basexp, game_weather               dump
    areatrigger_tavern, transports                 dump
    gameobject_template, type 15 rows only         dump (the ships and zeppelins; every other object is left alone)
    spell_proc_event                               dump (cooldown unit from the classic-db core revision)
    dbscripts_on_relay, dbscript_relay_template    dump
    dbscripts_on_quest_start, dbscripts_on_quest_end,
    dbscripts_on_gossip, dbscripts_on_event,
    script_waypoint                                dump (world schema 42)
    quest_template.StartScript/CompleteScript,
    gossip_menu.script_id                          dump; set on the rows the world already has (no quest or menu is added)
    gossip_menu_option rows with action_script_id  dump; added when missing (importers before world 42 skipped them),
                                                   otherwise only their action_script_id is set
    areatrigger_template                           AreaTrigger.dbc
    map_template                                   Map.dbc (every map) + the dump's instance_template (player limit, reset
                                                   delay, ghost entrance, script of the dungeons and raids)
    area_template                                  AreaTable.dbc (every area, instance areas included)
    taxi_nodes, taxi_path                          TaxiNodes.dbc, TaxiPath.dbc (the flight masters' nodes and routes)

  TaxiPathNode.dbc is read to check every ship's route (data0 of its type 15 row): a route the world server would refuse at
  start (fewer than three path nodes, a map without a map_template row, no speed) is reported as a "check:" line.

  Each of these tables is emptied and refilled inside one transaction, and only when the inputs carry it; any failure leaves the
  database as it was. Before writing, the script

    1. refuses a database another process holds open (stop the world server first),
    2. copies the database to -BackupDirectory and checks the copy's SHA-256, and the same for a leftover -wal file (committed
       pages not yet checkpointed, which the refresh's own connection would fold into the database),
    3. takes AreaTrigger.dbc, WorldSafeLocs.dbc, Map.dbc, AreaTable.dbc, TaxiNodes.dbc, TaxiPath.dbc and TaxiPathNode.dbc from
       -DbcDirectory (checked against its SHA256SUMS file when it has one), or extracts them from the client's MPQs with -MpqTool
       (mpqcli; patch-2.MPQ over patch.MPQ over dbc.MPQ, the client's own precedence). Nothing is downloaded.

  -DryRun reads everything and writes nothing (no backup either).

  With -AppSettings the script then runs set-optional-data.ps1 on that appsettings.json with the same -DbcDirectory and -Dump, so the
  world also gets its optional client data (item sets, random suffixes, enchantments, readable pages, the character appearance check):
  seven keys, each file checked first, the old file kept as a .bak. That needs the DBCs in a permanent -DbcDirectory (also holding
  ItemSet.dbc, ItemRandomProperties.dbc, SpellItemEnchantment.dbc, CharSections.dbc and CharacterFacialHairStyles.dbc), not -MpqTool.

  The world's schema must already be the importer's: a database behind it is refused (nothing written) unless -Migrate is given.
  Run the refresh with the importer of the deploy that will serve the world afterwards; if that deploy raised the world schema,
  either start its world server once first (it migrates at start) or pass -Migrate (the backup above is taken first).

.PARAMETER WorldDatabase
  The SQLite world database to refresh (for example the path in Database:World:ConnectionString of the server's appsettings.json).

.PARAMETER Dump
  The classic-db or vmangos world dump (.sql or .sql.gz). Default: D:\refs\classic-db\Full_DB\ClassicDB_1_12_1_z2815.sql.gz.

.PARAMETER DbcDirectory
  A directory holding the build-5875 AreaTrigger.dbc, WorldSafeLocs.dbc, Map.dbc, AreaTable.dbc, TaxiNodes.dbc, TaxiPath.dbc and
  TaxiPathNode.dbc (the effective copies, patch-2 first), for example D:\refs\client-dbc-5875-effective. A SHA256SUMS file there
  (sha256sum format) must list all seven and is checked before anything is written.

.PARAMETER MpqTool
  mpqcli.exe, used to extract the DBCs from -ClientData when -DbcDirectory is not given.

.PARAMETER ClientData
  The 1.12.1 client's Data directory (read only). Default: D:\World of Warcraft Classic 1.12.1\Data.

.PARAMETER BackupDirectory
  Where the pre-refresh copy goes. Default: a 'content-refresh-backups' directory next to the database.

.PARAMETER CooldownUnit
  spell_proc_event cooldown unit: auto (default; seconds for classic-db before core z2829), ms or seconds.

.PARAMETER Importer
  arcane-content-importer.dll or .exe. Default: the Release build under tools/ArcaneCore.ContentImporter.

.PARAMETER Report
  Optional JSON report path (outside any git work tree).

.PARAMETER Migrate
  Let the refresh upgrade a world database whose schema is behind the importer's (arcane-content-importer refresh --migrate).

.PARAMETER AppSettings
  Optional: the world server's appsettings.json to point at the optional client data after the refresh (set-optional-data.ps1; with
  -DryRun its checks run and nothing is written). Restart the world server afterwards.

.EXAMPLE
  powershell -NoProfile -File tools\content\refresh-world-content.ps1 -WorldDatabase C:\srv\world.db -DbcDirectory C:\srv\dbc-5875

.EXAMPLE
  powershell -NoProfile -File tools\content\refresh-world-content.ps1 -WorldDatabase C:\srv\world.db -DbcDirectory D:\refs\client-dbc-5875-effective -AppSettings C:\srv\appsettings.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WorldDatabase,
    [string]$Dump = 'D:\refs\classic-db\Full_DB\ClassicDB_1_12_1_z2815.sql.gz',
    [string]$DbcDirectory,
    [string]$MpqTool,
    [string]$ClientData = 'D:\World of Warcraft Classic 1.12.1\Data',
    [string]$BackupDirectory,
    [ValidateSet('auto', 'ms', 'seconds')][string]$CooldownUnit = 'auto',
    [string]$Importer,
    [string]$Report,
    [switch]$DryRun,
    [switch]$Migrate,
    [string]$AppSettings
)

$ErrorActionPreference = 'Stop'

# The client DBCs the refresh reads (all required: a missing one stops the script before anything is written).
$dbcNames = @('AreaTrigger.dbc', 'WorldSafeLocs.dbc', 'Map.dbc', 'AreaTable.dbc', 'TaxiNodes.dbc', 'TaxiPath.dbc', 'TaxiPathNode.dbc')

function Get-Sha256([string]$Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try { return ([System.BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') }
        finally { $sha.Dispose() }
    }
    finally { $stream.Dispose() }
}

$WorldDatabase = [System.IO.Path]::GetFullPath($WorldDatabase)
if (-not (Test-Path -LiteralPath $WorldDatabase -PathType Leaf)) { throw "world database not found: $WorldDatabase" }
if (-not (Test-Path -LiteralPath $Dump -PathType Leaf)) { throw "dump not found: $Dump" }

if (-not $Importer) {
    $repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $Importer = Join-Path $repo 'tools\ArcaneCore.ContentImporter\bin\Release\net10.0\arcane-content-importer.dll'
}
if (-not (Test-Path -LiteralPath $Importer -PathType Leaf)) { throw "importer not found: $Importer (build the solution in Release first)" }
$optionalData = Join-Path $PSScriptRoot 'set-optional-data.ps1'
if ($AppSettings) {
    # The world reads these DBCs at every start: they must stay where the keys point, so not the temporary MPQ extraction.
    if (-not $DbcDirectory) { throw '-AppSettings needs -DbcDirectory (a permanent directory with the optional DBCs), not -MpqTool' }
    if (-not (Test-Path -LiteralPath $AppSettings -PathType Leaf)) { throw "appsettings not found: $AppSettings" }
    if (-not (Test-Path -LiteralPath $optionalData -PathType Leaf)) { throw "set-optional-data.ps1 not found next to this script: $optionalData" }
}

# 1. Nobody else may hold the database: a running world keeps it open.
try {
    $probe = [System.IO.File]::Open($WorldDatabase, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    $probe.Dispose()
}
catch {
    throw "the world database is in use by another process (stop the world server first): $WorldDatabase"
}

# 2. The DBCs: a given directory, or the client's MPQs through mpqcli (patch-2 over patch over dbc).
$temporaryDbc = $null
if (-not $DbcDirectory) {
    if (-not $MpqTool) { throw "give -DbcDirectory ($($dbcNames -join ', ')) or -MpqTool (mpqcli.exe) to extract them from -ClientData" }
    if (-not (Test-Path -LiteralPath $MpqTool -PathType Leaf)) { throw "mpq tool not found: $MpqTool" }
    $temporaryDbc = Join-Path ([System.IO.Path]::GetTempPath()) ('arcanecore-dbc-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temporaryDbc | Out-Null
    foreach ($name in $dbcNames) {
        foreach ($archive in 'patch-2.MPQ', 'patch.MPQ', 'dbc.MPQ') {
            $mpq = Join-Path $ClientData $archive
            if (-not (Test-Path -LiteralPath $mpq -PathType Leaf)) { continue }
            $out = Join-Path $temporaryDbc $archive
            & $MpqTool extract $mpq -f "DBFilesClient\$name" -o $out | Out-Null
            $file = Join-Path $out $name
            if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $file -PathType Leaf)) {
                Copy-Item -LiteralPath $file -Destination (Join-Path $temporaryDbc $name)
                Write-Host "$name from $archive (sha256 $(Get-Sha256 $file))"
                break
            }
        }
        if (-not (Test-Path -LiteralPath (Join-Path $temporaryDbc $name))) { throw "$name was not found in $ClientData" }
    }
    $DbcDirectory = $temporaryDbc
}
$manifest = Join-Path $DbcDirectory 'SHA256SUMS'
$expected = @{}
if (Test-Path -LiteralPath $manifest -PathType Leaf) {
    foreach ($line in Get-Content -LiteralPath $manifest) {
        if ($line -match '^([0-9A-Fa-f]{64}) [ *]?(.+)$') { $expected[$Matches[2].Trim()] = $Matches[1].ToUpperInvariant() }
    }
}
foreach ($name in $dbcNames) {
    $file = Join-Path $DbcDirectory $name
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "$name is missing from $DbcDirectory" }
    $actual = Get-Sha256 $file
    if ($expected.Count -gt 0) {
        if (-not $expected.ContainsKey($name)) { throw "$name is not listed in $manifest" }
        if ($expected[$name] -ne $actual) { throw "$name does not match $manifest (sha256 $actual)" }
        Write-Host "$name sha256 $actual (matches SHA256SUMS)"
    }
    else {
        Write-Host "$name sha256 $actual"
    }
}

try {
    # 3. The backup, checked byte for byte.
    if (-not $DryRun) {
        if (-not $BackupDirectory) { $BackupDirectory = Join-Path (Split-Path -Parent $WorldDatabase) 'content-refresh-backups' }
        New-Item -ItemType Directory -Force -Path $BackupDirectory | Out-Null
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $backup = Join-Path $BackupDirectory ("{0}.{1}.bak" -f [System.IO.Path]::GetFileName($WorldDatabase), $stamp)
        Copy-Item -LiteralPath $WorldDatabase -Destination $backup
        $hash = Get-Sha256 $WorldDatabase
        if ((Get-Sha256 $backup) -ne $hash) { throw "the backup $backup does not match the database" }
        Write-Host "backup: $backup (sha256 $hash)"
        $wal = $WorldDatabase + '-wal'
        if ((Test-Path -LiteralPath $wal) -and (Get-Item -LiteralPath $wal).Length -gt 0) {
            # Committed pages not yet checkpointed: the backup is the database plus this file, so both copies are checked.
            Copy-Item -LiteralPath $wal -Destination ($backup + '-wal')
            $walHash = Get-Sha256 $wal
            if ((Get-Sha256 ($backup + '-wal')) -ne $walHash) { throw "the backup $backup-wal does not match $wal" }
            Write-Host "backup: $backup-wal (sha256 $walHash)"
        }
    }

    # 4. The refresh itself.
    $arguments = @('refresh', $Dump, '--database', $WorldDatabase, '--dbc-dir', $DbcDirectory, '--cooldown-unit', $CooldownUnit)
    if ($Report) { $arguments += @('--report', $Report) }
    if ($DryRun) { $arguments += '--dry-run' }
    if ($Migrate) { $arguments += '--migrate' }
    if ($Importer.EndsWith('.dll', [System.StringComparison]::OrdinalIgnoreCase)) {
        & dotnet $Importer @arguments
    }
    else {
        & $Importer @arguments
    }
    if ($LASTEXITCODE -ne 0) { throw "arcane-content-importer refresh exited $LASTEXITCODE (the database is unchanged)" }

    # 5. The optional client data keys (after the database: a refused refresh leaves the settings alone too).
    if ($AppSettings) {
        $optional = @{ AppSettings = $AppSettings; DbcDirectory = $DbcDirectory; Dump = $Dump }
        if ($DryRun) { $optional.DryRun = $true }
        & $optionalData @optional
    }
}
finally {
    if ($temporaryDbc) { Remove-Item -LiteralPath $temporaryDbc -Recurse -Force }
}
