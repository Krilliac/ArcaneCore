<#
.SYNOPSIS
  Compute the SHA-256 of a hot module dll and (optionally) approve it in a module allowlist file, for a deployed server.

.DESCRIPTION
  The module lane (World:HotCode:Modules) refuses any dll whose SHA-256 is not in the allowlist file
  (World:HotCode:Modules:Allowlist). This script does the two operator steps for a production instance, without a dev runner:
    1. prints the hash of <ModuleDirectory>\<Name>\<Name>.dll (the exact bytes the server will hash and load);
    2. with -Allowlist, appends "<hash>  <Name> <date> <label>" to that file unless the hash is already listed, creating the file
       with a comment header if it does not exist. The file is read on every load, so no restart is needed.
  It never loads or runs the module. Keep the module directory and the allowlist writable only by the account that runs the server.

.PARAMETER Name             The module name = the assembly name = the folder name.
.PARAMETER ModuleDirectory  The World:HotCode:Modules:Directory of the server.
.PARAMETER Dll              Hash this file instead (it is expected to be, or become, <ModuleDirectory>\<Name>\<Name>.dll).
.PARAMETER Allowlist        Approve the hash in this file (World:HotCode:Modules:Allowlist).
.PARAMETER Label            Free text after the hash (default: the version of the file, if it has one).

.EXAMPLE
  powershell -File scripts\hotmodule-hash.ps1 -Name GmCommands -ModuleDirectory <modules-dir> -Allowlist <allowlist-file>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Name,
    [string]$ModuleDirectory = '',
    [string]$Dll = '',
    [string]$Allowlist = '',
    [string]$Label = ''
)

$ErrorActionPreference = 'Stop'

function Fail([string]$message, [int]$code = 1) {
    [Console]::Error.WriteLine("hotmodule-hash: $message")
    exit $code
}

if ($Name -notmatch '^[A-Za-z][A-Za-z0-9_-]{0,63}(\.[A-Za-z0-9_-]{1,63})*$') { Fail "'$Name' is not a valid module name (letters, digits, '_', '-', '.'; starts with a letter)." 64 }
if (-not $Dll) {
    if (-not $ModuleDirectory) { Fail 'give -ModuleDirectory or -Dll.' 64 }
    $Dll = Join-Path (Join-Path $ModuleDirectory $Name) "$Name.dll"
}
if (-not (Test-Path -LiteralPath $Dll -PathType Leaf)) { Fail "no such file: $Dll" 2 }

$hasher = [System.Security.Cryptography.SHA256]::Create()
try {
    $stream = [System.IO.File]::OpenRead((Resolve-Path -LiteralPath $Dll).ProviderPath)
    try { $sha = [System.BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '') } finally { $stream.Dispose() }
} finally { $hasher.Dispose() }
Write-Host "$sha  $Name  ($Dll)"

if ($Allowlist) {
    if (-not (Test-Path -LiteralPath $Allowlist)) {
        Set-Content -LiteralPath $Allowlist -Encoding ascii -Value '# SHA-256 of each module dll that may be loaded with .hotmodule (one per line; text after the hash is a label)'
    }
    if (Select-String -LiteralPath $Allowlist -Pattern $sha -Quiet) {
        Write-Host "hotmodule-hash: already approved in $Allowlist"
    } else {
        if (-not $Label) { $Label = (Get-Item -LiteralPath $Dll).VersionInfo.FileVersion }
        Add-Content -LiteralPath $Allowlist -Encoding ascii -Value "$sha  $Name $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss')) $Label"
        Write-Host "hotmodule-hash: approved (added to $Allowlist)"
    }
}
