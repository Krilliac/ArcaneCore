<#
.SYNOPSIS
  Points a world server's appsettings.json at the optional client data (item sets, random suffixes, enchantments, readable pages,
  character appearance), after checking every file. Safe to run again: a second run changes nothing.

.DESCRIPTION
  Without these keys the world starts but logs, and runs without:

    ItemSets:DbcPath                                   ItemSet.dbc                     item set bonuses
    ItemRandomProperties:DbcPath                       ItemRandomProperties.dbc        random suffixes ("of the Owl")
    ItemRandomProperties:EnchantmentTemplateDumpPath   dump (item_enchantment_template)
    Enchanting:SpellItemEnchantmentDbcPath             SpellItemEnchantment.dbc        enchanting, and the stats of suffixes
    PageText:DumpPath                                  dump (page_text)                readable books, letters and plaques
    CharacterCreation:CharSectionsDbcPath              CharSections.dbc                the appearance check of a new character
    CharacterCreation:CharacterFacialHairStylesDbcPath CharacterFacialHairStyles.dbc

  The world reads the two dump tables straight from the classic-db dump at startup (about 1 s each for z2815); nothing is imported
  into the world database. The script

    1. checks each DBC in -DbcDirectory: present, a WDBC file with the build-5875 field count, and listed with the right SHA-256 in
       the directory's SHA256SUMS when it has one (D:\refs\client-dbc-5875-effective does);
    2. checks that -Dump holds INSERT rows for page_text and item_enchantment_template;
    3. copies appsettings.json to -BackupPath (default: appsettings.json.<stamp>.bak next to it) and checks the copy's SHA-256;
    4. sets the seven keys, keeps every other value, and reads the file back to confirm them.

  -DryRun runs the checks and prints the keys without writing anything. Nothing is downloaded. Stop the world server before running
  it for real, then start it: the keys are read at startup (they are not hot-reloadable).

.PARAMETER AppSettings
  The appsettings.json of the world server to configure.

.PARAMETER DbcDirectory
  A permanent directory holding the five build-5875 DBCs (the effective copies, patch-2 first), for example
  D:\refs\client-dbc-5875-effective. The world reads them at every start, so not a temporary directory.

.PARAMETER Dump
  The classic-db or vmangos world dump (.sql or .sql.gz). Default: D:\refs\classic-db\Full_DB\ClassicDB_1_12_1_z2815.sql.gz.

.PARAMETER BackupPath
  Where the pre-change copy of appsettings.json goes. Default: <AppSettings>.<yyyyMMdd-HHmmss>.bak.

.EXAMPLE
  powershell -NoProfile -File tools\content\set-optional-data.ps1 -AppSettings C:\srv\appsettings.json -DbcDirectory D:\refs\client-dbc-5875-effective
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AppSettings,
    [Parameter(Mandatory)][string]$DbcDirectory,
    [string]$Dump = 'D:\refs\classic-db\Full_DB\ClassicDB_1_12_1_z2815.sql.gz',
    [string]$BackupPath,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

function Get-Sha256([string]$Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try { return ([System.BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') }
        finally { $sha.Dispose() }
    }
    finally { $stream.Dispose() }
}

# Windows PowerShell 5.1 indents ConvertTo-Json output by column; re-indent it with two spaces per level (strings are copied as they are).
function Format-Json([string]$Json) {
    $out = New-Object System.Text.StringBuilder
    $depth = 0
    $inString = $false
    $escaped = $false
    for ($i = 0; $i -lt $Json.Length; $i++) {
        $c = $Json[$i]
        if ($inString) {
            [void]$out.Append($c)
            if ($escaped) { $escaped = $false } elseif ($c -eq '\') { $escaped = $true } elseif ($c -eq '"') { $inString = $false }
            continue
        }
        if ([char]::IsWhiteSpace($c)) { continue }
        switch ($c) {
            '"' { $inString = $true; [void]$out.Append($c) }
            { $_ -eq '{' -or $_ -eq '[' } {
                $j = $i + 1
                while ($j -lt $Json.Length -and [char]::IsWhiteSpace($Json[$j])) { $j++ }
                if ($j -lt $Json.Length -and ($Json[$j] -eq '}' -or $Json[$j] -eq ']')) { [void]$out.Append($c).Append($Json[$j]); $i = $j }
                else { $depth++; [void]$out.Append($c).Append("`n").Append('  ' * $depth) }
            }
            { $_ -eq '}' -or $_ -eq ']' } { $depth--; [void]$out.Append("`n").Append('  ' * $depth).Append($c) }
            ',' { [void]$out.Append(",`n").Append('  ' * $depth) }
            ':' { [void]$out.Append(': ') }
            default { [void]$out.Append($c) }
        }
    }
    return $out.ToString() + "`n"
}

# The build-5875 layouts the world's readers insist on (vmangos DBCfmt.h): a file with another field count refuses startup.
$dbcs = [ordered]@{
    'ItemSet.dbc'                   = @{ Fields = 45; Key = 'ItemSets:DbcPath' }
    'ItemRandomProperties.dbc'      = @{ Fields = 16; Key = 'ItemRandomProperties:DbcPath' }
    'SpellItemEnchantment.dbc'      = @{ Fields = 24; Key = 'Enchanting:SpellItemEnchantmentDbcPath' }
    'CharSections.dbc'              = @{ Fields = 10; Key = 'CharacterCreation:CharSectionsDbcPath' }
    'CharacterFacialHairStyles.dbc' = @{ Fields = 9;  Key = 'CharacterCreation:CharacterFacialHairStylesDbcPath' }
}
$dumpKeys = @('ItemRandomProperties:EnchantmentTemplateDumpPath', 'PageText:DumpPath')
$dumpTables = @('page_text', 'item_enchantment_template')

$AppSettings = [System.IO.Path]::GetFullPath($AppSettings)
$DbcDirectory = [System.IO.Path]::GetFullPath($DbcDirectory)
$Dump = [System.IO.Path]::GetFullPath($Dump)
if (-not (Test-Path -LiteralPath $AppSettings -PathType Leaf)) { throw "appsettings not found: $AppSettings" }
if (-not (Test-Path -LiteralPath $DbcDirectory -PathType Container)) { throw "DBC directory not found: $DbcDirectory" }
if (-not (Test-Path -LiteralPath $Dump -PathType Leaf)) { throw "dump not found: $Dump" }

# 1. The DBCs.
$manifest = Join-Path $DbcDirectory 'SHA256SUMS'
$expected = @{}
if (Test-Path -LiteralPath $manifest -PathType Leaf) {
    foreach ($line in Get-Content -LiteralPath $manifest) {
        if ($line -match '^([0-9A-Fa-f]{64}) [ *]?(.+)$') { $expected[$Matches[2].Trim()] = $Matches[1].ToUpperInvariant() }
    }
}
$values = [ordered]@{}
foreach ($name in $dbcs.Keys) {
    $file = Join-Path $DbcDirectory $name
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "$name is missing from $DbcDirectory" }
    $bytes = New-Object byte[] 20
    $stream = [System.IO.File]::OpenRead($file)
    try { $read = $stream.Read($bytes, 0, 20) } finally { $stream.Dispose() }
    if ($read -ne 20 -or [System.Text.Encoding]::ASCII.GetString($bytes, 0, 4) -ne 'WDBC') { throw "$file is not a WDBC file" }
    $records = [System.BitConverter]::ToUInt32($bytes, 4)
    $fields = [System.BitConverter]::ToUInt32($bytes, 8)
    if ($fields -ne $dbcs[$name].Fields) { throw "$file has $fields fields, the build-5875 layout has $($dbcs[$name].Fields)" }
    $actual = Get-Sha256 $file
    if ($expected.Count -gt 0) {
        if (-not $expected.ContainsKey($name)) { throw "$name is not listed in $manifest" }
        if ($expected[$name] -ne $actual) { throw "$name does not match $manifest (sha256 $actual)" }
        Write-Host "$name $records rows, $fields fields, sha256 $actual (matches SHA256SUMS)"
    }
    else {
        Write-Host "$name $records rows, $fields fields, sha256 $actual"
    }
    $values[$dbcs[$name].Key] = $file
}

# 2. The dump: both tables must have rows (the world would otherwise load 0 pages or 0 suffix groups without failing).
$dumpFile = [System.IO.File]::OpenRead($Dump)
try {
    $source = if ($Dump.EndsWith('.gz', [System.StringComparison]::OrdinalIgnoreCase)) {
        New-Object System.IO.Compression.GZipStream($dumpFile, [System.IO.Compression.CompressionMode]::Decompress)
    }
    else { $dumpFile }
    $reader = New-Object System.IO.StreamReader($source)
    $missing = New-Object System.Collections.Generic.HashSet[string]
    foreach ($table in $dumpTables) { [void]$missing.Add($table) }
    while ($missing.Count -gt 0 -and $null -ne ($line = $reader.ReadLine())) {
        if (-not $line.StartsWith('INSERT INTO `')) { continue }
        foreach ($table in @($missing)) {
            if ($line.StartsWith("INSERT INTO ``$table``")) { [void]$missing.Remove($table) }
        }
    }
    $reader.Dispose()
}
finally { $dumpFile.Dispose() }
if ($missing.Count -gt 0) { throw "$Dump has no rows for: $([string]::Join(', ', @($missing)))" }
Write-Host "$Dump has page_text and item_enchantment_template rows (sha256 $(Get-Sha256 $Dump))"
foreach ($key in $dumpKeys) { $values[$key] = $Dump }

# 3-4. The settings.
$json = Get-Content -LiteralPath $AppSettings -Raw | ConvertFrom-Json
$changed = 0
foreach ($key in $values.Keys) {
    $section, $name = $key.Split(':')
    $node = $json.PSObject.Properties[$section]
    if (-not $node) {
        $json | Add-Member -NotePropertyName $section -NotePropertyValue ([pscustomobject]@{})
        $node = $json.PSObject.Properties[$section]
    }
    $current = $node.Value.PSObject.Properties[$name]
    if ($current -and $current.Value -eq $values[$key]) {
        Write-Host "$key = $($values[$key]) (unchanged)"
        continue
    }
    $was = if ($current) { $current.Value } else { '(not set)' }
    if ($current) { $current.Value = $values[$key] }
    else { $node.Value | Add-Member -NotePropertyName $name -NotePropertyValue $values[$key] }
    Write-Host "$key = $($values[$key]) (was $was)"
    $changed++
}

if ($DryRun) {
    Write-Host "dry run: $changed key(s) would change; $AppSettings is unchanged"
    return
}
if ($changed -eq 0) {
    Write-Host "every key is already set; $AppSettings is unchanged"
    return
}

if (-not $BackupPath) { $BackupPath = '{0}.{1}.bak' -f $AppSettings, (Get-Date -Format 'yyyyMMdd-HHmmss') }
Copy-Item -LiteralPath $AppSettings -Destination $BackupPath
$hash = Get-Sha256 $AppSettings
if ((Get-Sha256 $BackupPath) -ne $hash) { throw "the backup $BackupPath does not match $AppSettings" }
Write-Host "backup: $BackupPath (sha256 $hash)"

$text = Format-Json ($json | ConvertTo-Json -Depth 64)
[System.IO.File]::WriteAllText($AppSettings, $text, (New-Object System.Text.UTF8Encoding($false)))

# Read it back: every key must hold its value (and the file must still parse).
$check = Get-Content -LiteralPath $AppSettings -Raw | ConvertFrom-Json
foreach ($key in $values.Keys) {
    $section, $name = $key.Split(':')
    if ($check.$section.$name -ne $values[$key]) { throw "$key did not read back from $AppSettings (restore $BackupPath)" }
}
Write-Host "optional data: $changed key(s) written to $AppSettings; restart the world server to load them"
