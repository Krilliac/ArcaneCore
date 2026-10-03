<#
.SYNOPSIS
  Build a hot module, put it where the dev runner's module lane looks, and approve its SHA-256 (the allowlist)
  so `.hotmodule load <name>` / `.hotmodule reload <name>` accepts it.

.DESCRIPTION
  1. Builds the module project (Debug) into a staging folder and takes <Name>.dll (and its pdb).
  2. Copies them to <run>\modules\<Name>\ (the module directory of a dev runner started with dev-runner.ps1).
  3. Appends the dll's SHA-256 to <run>\modules-allowlist.txt unless it is already listed. That is the approval
     step: it happens here, on your machine, because the in-game command can neither add a hash nor name a path.
  4. Prints the in-game command to run next.

  The module project must be named like the module (AssemblyName = Name). tools\hotmodule-template\GmCommands is a
  working template. A module that uses private dependencies needs those dlls copied by hand into its folder.

.PARAMETER Project  The module's .csproj (or the folder holding it).
.PARAMETER Name     The module name = the assembly name. Default: the project's file name.
.PARAMETER Run      The dev runner run name (default dev) whose module directory to use.
.PARAMETER RunRoot  Parent of the run directory (default $env:TEMP).

.EXAMPLE
  powershell -File scripts/dev-module.ps1 -Project tools\hotmodule-template\GmCommands
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Project,
    [string]$Name = '',
    [string]$Run = 'dev',
    [string]$RunRoot = ''
)

$ErrorActionPreference = 'Stop'

function Fail([string]$message, [int]$code = 1) {
    [Console]::Error.WriteLine("dev-module: $message")
    exit $code
}

if (-not $RunRoot) { $RunRoot = $env:TEMP }
$runDir = Join-Path $RunRoot "ArcaneCore-dev-$Run"
$statePath = Join-Path $runDir 'runner.json'
if (-not (Test-Path -LiteralPath $statePath)) { Fail "no dev runner run named '$Run' ($runDir). Start one with scripts\dev-runner.ps1." 2 }

$projectPath = (Resolve-Path -LiteralPath $Project).Path
if (Test-Path -LiteralPath $projectPath -PathType Container) {
    $found = @(Get-ChildItem -LiteralPath $projectPath -Filter *.csproj)
    if ($found.Count -ne 1) { Fail "expected exactly one .csproj in $projectPath." 64 }
    $projectPath = $found[0].FullName
}
if (-not $Name) { $Name = [IO.Path]::GetFileNameWithoutExtension($projectPath) }
if ($Name -notmatch '^[A-Za-z][A-Za-z0-9_-]{0,63}(\.[A-Za-z0-9_-]{1,63})*$') { Fail "'$Name' is not a valid module name (letters, digits, '_', '-', '.'; starts with a letter)." 64 }

$staging = Join-Path $runDir "module-build\$Name"
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $staging | Out-Null

Write-Host "dev-module: building $projectPath (Debug) ..."
& dotnet build $projectPath -c Debug -m:1 --nologo -v:q -o $staging
if ($LASTEXITCODE -ne 0) { Fail "the module build failed (exit $LASTEXITCODE)." 3 }

$dll = Join-Path $staging "$Name.dll"
if (-not (Test-Path -LiteralPath $dll)) { Fail "the build produced no $Name.dll in $staging; the project's AssemblyName must equal the module name." 3 }

$target = Join-Path $runDir "modules\$Name"
New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item -LiteralPath $dll -Destination (Join-Path $target "$Name.dll") -Force
$pdb = Join-Path $staging "$Name.pdb"
if (Test-Path -LiteralPath $pdb) { Copy-Item -LiteralPath $pdb -Destination (Join-Path $target "$Name.pdb") -Force }

$sha = (Get-FileHash -LiteralPath (Join-Path $target "$Name.dll") -Algorithm SHA256).Hash
$allowlist = Join-Path $runDir 'modules-allowlist.txt'
if (-not (Test-Path -LiteralPath $allowlist)) { Set-Content -LiteralPath $allowlist -Encoding ascii -Value '# SHA-256 of each module dll that may be loaded with .hotmodule' }
$listed = Select-String -LiteralPath $allowlist -Pattern $sha -Quiet
if ($listed) {
    Write-Host "dev-module: $sha already approved"
} else {
    Add-Content -LiteralPath $allowlist -Encoding ascii -Value "$sha  $Name $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))"
    Write-Host "dev-module: approved $sha (added to $allowlist)"
}

Write-Host "dev-module: $target\$Name.dll is in place."
Write-Host "next, in game as an Administrator:  .hotmodule load $Name     (first time)   or   .hotmodule reload $Name     (after a rebuild)"
exit 0
