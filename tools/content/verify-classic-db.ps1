<#
.SYNOPSIS
  Imports a real classic-db / vmangos dump into a scratch SQLite database and checks that every
  source row an importer reads arrived.

.DESCRIPTION
  The dump is GPL data with Blizzard copyright material and is never committed, so this check
  runs against the developer's own copy and is not part of the test suite. It runs
  `arcane-content-importer plan`, then `import`, then `verify`, and compares the row counts of the
  two JSON reports table by table: a table whose imported count differs from its source count
  (after the documented skips) fails the script. Nothing is quoted from the dump; only counts.

  The database and reports go to -WorkDir, which must be outside any git work tree (the importer
  refuses otherwise) and short: SQLite on Windows fails on paths near MAX_PATH once the journal
  suffix is added.

.PARAMETER Dump
  Path to the dump, e.g. D:\refs\classic-db\Full_DB\ClassicDB_1_12_1_z2815.sql.gz (.sql or .sql.gz).

.PARAMETER WorkDir
  Scratch directory for world.db and the reports (created if missing; an existing world.db is replaced).

.PARAMETER Importer
  Path to arcane-content-importer(.exe). Default: the Release build under tools/ArcaneCore.ContentImporter.

.EXAMPLE
  powershell -NoProfile -File tools\content\verify-classic-db.ps1 -Dump D:\refs\classic-db\Full_DB\ClassicDB_1_12_1_z2815.sql.gz -WorkDir C:\Temp\acimp
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Dump,
    [Parameter(Mandatory)][string]$WorkDir,
    [string]$Importer
)

$ErrorActionPreference = 'Stop'

if (-not $Importer) {
    $repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $Importer = Join-Path $repo 'tools\ArcaneCore.ContentImporter\bin\Release\net10.0\arcane-content-importer.exe'
}
if (-not (Test-Path -LiteralPath $Importer)) { throw "importer not found: $Importer (build the solution in Release first)" }
if (-not (Test-Path -LiteralPath $Dump)) { throw "dump not found: $Dump" }

New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$database = Join-Path $WorkDir 'world.db'
foreach ($suffix in '', '-journal', '-wal', '-shm') {
    $file = $database + $suffix
    if (Test-Path -LiteralPath $file) { [System.IO.File]::Delete($file) }
}
$planReport = Join-Path $WorkDir 'plan.json'
$importReport = Join-Path $WorkDir 'import.json'

function Invoke-Importer([string[]]$Arguments) {
    & $Importer @Arguments
    if ($LASTEXITCODE -ne 0) { throw "arcane-content-importer $($Arguments[0]) exited $LASTEXITCODE" }
}

Invoke-Importer @('plan', $Dump, '--report', $planReport)
Invoke-Importer @('import', $Dump, '--database', $database, '--report', $importReport, '--level-stats-file', (Join-Path $WorkDir 'levelstats.csv'))

$plan = Get-Content -LiteralPath $planReport -Raw | ConvertFrom-Json
$import = Get-Content -LiteralPath $importReport -Raw | ConvertFrom-Json

# source table -> imported table, for the tables where every source row is imported.
$expected = [ordered]@{
    'creature_template'           = 'creature_template'
    'creature'                    = 'creature_spawn'
    'creature_movement'           = 'creature_movement'
    'creature_model_info'         = 'creature_model_info'
    'creature_addon'              = 'creature_addon'
    'creature_ai_scripts'         = 'creature_ai_scripts'
    'gameobject_template'         = 'gameobject_template'
    'gameobject'                  = 'gameobject_spawn'
    'gameobject_questrelation'    = 'gameobject_questrelation'
    'gameobject_involvedrelation' = 'gameobject_involvedrelation'
    'item_template'               = 'item_template'
    'quest_template'              = 'quest_template'
    'creature_questrelation'      = 'creature_questrelation'
    'creature_involvedrelation'   = 'creature_involvedrelation'
    'creature_onkill_reputation'  = 'creature_onkill_reputation'
    'playercreateinfo'            = 'player_create_info'
    'playercreateinfo_spell'      = 'playercreateinfo_spell'
    'spell_target_position'       = 'spell_target_position'
    'player_levelstats'           = 'level_stats_rows'
    'areatrigger_teleport'        = 'areatrigger_teleport'
    'game_tele'                   = 'game_tele'
}

$failures = 0
foreach ($source in $expected.Keys) {
    $target = $expected[$source]
    $sourceRows = [long]$plan.tables.$source.rows
    $importedRows = [long]$import.imported.$target
    $status = if ($sourceRows -eq $importedRows) { 'ok' } else { $failures++; 'MISMATCH' }
    '{0,-30} source {1,9}  imported {2,9}  {3}' -f $source, $sourceRows, $importedRows, $status
}

$lootSource = 0L
foreach ($table in 'creature_loot_template', 'gameobject_loot_template', 'item_loot_template', 'skinning_loot_template', 'reference_loot_template') {
    $lootSource += [long]$plan.tables.$table.rows
}
$lootImported = [long]$import.imported.loot_template_rows
$lootStatus = if ($lootSource -eq $lootImported) { 'ok' } else { $failures++; 'MISMATCH' }
'{0,-30} source {1,9}  imported {2,9}  {3}' -f 'loot templates (5 tables)', $lootSource, $lootImported, $lootStatus

Invoke-Importer @('verify', '--database', $database)

if ($failures -gt 0) { throw "$failures table(s) did not import every source row" }
'verify-classic-db: every source row of the imported tables arrived'
