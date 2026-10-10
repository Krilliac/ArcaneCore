<#
.SYNOPSIS
  Provisions N geared playerbots at level L on a live world, spread over weighted game_tele places, and checks the result.

.DESCRIPTION
  Builds the command lines (OpsLib.ps1 New-OpsProvisionScript), runs them through run-gm-commands.ps1, and then reads the session
  log back. Per bot the lines are

    .playerbot create <Name> <race> <class>     (asynchronous; the script waits 5 s)
    .playerbot start <Name>                     (the bot logs in; the script waits -StartWaitSeconds)
    .character boost <Name> <Level>             (level, trainer and class-quest spells, gear, action bars, money)
    .tele name <Name> <place>                   (places are game_tele names, spread by weight)

  then one '.playerbot status' after -FinalWaitSeconds. Verification: every bot must appear in that status as state=Running with
  error=none, and its boost reply must show the level. Anything else is listed and the exit code is 5. A bot absent from the status
  output counts as a failure: the check never passes for what it did not see.

  The race:class pairs rotate over the bots. The default is the six classes a human can be (warrior, paladin, rogue, priest, mage,
  warlock); the geared kit is what each class can wear from the loaded vendor, quest and loot data (docs/areas/character-boost.md).

  Pacing and stopping, the account promotion and demotion, and the exit codes 0-4 are run-gm-commands.ps1's (see its help). Create the
  stop file to stop between two lines; bots already created or started stay (stop them with '.playerbot stop <Name>').

.PARAMETER ProfileDirectory
  The server profile directory (appsettings.json of the auth database; 'ops' holds the logs). See run-gm-commands.ps1.
.PARAMETER BinDirectory
  Directory with arcane-account and arcane-mock.
.PARAMETER Count
  How many bots (1..17576, default 200).
.PARAMETER Level
  The level to boost them to (default 14). The server refuses a level above its maximum.
.PARAMETER TeleSpec
  Weighted game_tele names, 'Westfall=70,Goldshire=20,SentinelHill=10' (default 'Westfall' alone). Counts are exact: largest remainder.
.PARAMETER NamePrefix
  2-8 letters; names are the prefix plus a letter suffix (default Wfbot -> Wfbotaa, Wfbotab...).
.PARAMETER ClassSpec
  'race:class,race:class' ids (default '1:1,1:2,1:4,1:5,1:8,1:9').
.PARAMETER DryRun
  Prints the plan and the first lines and runs nothing.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ProfileDirectory,
    [Parameter(Mandatory)][string]$BinDirectory,
    [int]$Count = 200,
    [int]$Level = 14,
    [string]$TeleSpec = 'Westfall',
    [string]$NamePrefix = 'Wfbot',
    [string]$ClassSpec = '1:1,1:2,1:4,1:5,1:8,1:9',
    [int]$StartWaitSeconds = 20,
    [int]$FinalWaitSeconds = 20,
    [int]$PaceMilliseconds = 600,
    [string]$Account = 'OPSGM',
    [string]$Character = 'Opsgm',
    [string]$Realm = '127.0.0.1:3724',
    [string]$StopFile,
    [string]$AccountTool,
    [string]$MockTool,
    [switch]$CreateAccount,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'OpsLib.ps1')

try {
    $plan = New-OpsProvisionScript -Count $Count -Level $Level -NamePrefix $NamePrefix -TeleSpec $TeleSpec -ClassSpec $ClassSpec `
        -StartWaitSeconds $StartWaitSeconds -FinalWaitSeconds $FinalWaitSeconds
}
catch {
    [Console]::Error.WriteLine("provision-bots: $($_.Exception.Message)")
    exit 2
}

$distribution = $plan.Teleports | Group-Object | Sort-Object -Property Count -Descending | ForEach-Object { "$($_.Name)=$($_.Count)" }
Write-Host "provision-bots: $Count bots at level $Level; places: $($distribution -join ', ')"

$opsDirectory = Join-Path $ProfileDirectory 'ops'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$logPath = Join-Path $opsDirectory "provision-$stamp.log"
$commandFile = Join-Path $opsDirectory "provision-$stamp.commands"

$runArguments = @{
    ProfileDirectory = $ProfileDirectory
    BinDirectory = $BinDirectory
    Account = $Account
    Character = $Character
    Realm = $Realm
    PaceMilliseconds = $PaceMilliseconds
}
if ($StopFile) { $runArguments.StopFile = $StopFile }
if ($AccountTool) { $runArguments.AccountTool = $AccountTool }
if ($MockTool) { $runArguments.MockTool = $MockTool }
if ($CreateAccount) { $runArguments.CreateAccount = $true }

if ($DryRun) {
    # The command file goes to a temp path, so a dry run writes nothing into the profile.
    $temp = Join-Path ([System.IO.Path]::GetTempPath()) "provision-dry-$PID.commands"
    $dryCode = 1
    try {
        [System.IO.File]::WriteAllLines($temp, $plan.Lines)
        & (Join-Path $PSScriptRoot 'run-gm-commands.ps1') @runArguments -CommandFile $temp -DryRun
        $dryCode = $LASTEXITCODE
    }
    finally { Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue }
    exit $dryCode
}

if (-not (Test-Path -LiteralPath $ProfileDirectory -PathType Container)) {
    [Console]::Error.WriteLine("provision-bots: profile directory '$ProfileDirectory' does not exist.")
    exit 2
}
New-Item -ItemType Directory -Force -Path $opsDirectory | Out-Null
[System.IO.File]::WriteAllLines($commandFile, $plan.Lines)
& (Join-Path $PSScriptRoot 'run-gm-commands.ps1') @runArguments -CommandFile $commandFile -LogPath $logPath
$code = $LASTEXITCODE
if ($code -ne 0) {
    [Console]::Error.WriteLine("provision-bots: the command run ended with exit code $code (log $logPath); not verifying.")
    exit $code
}

$log = [System.IO.File]::ReadAllLines($logPath)
$results = @(Test-OpsProvisionLog -LogLines $log -Names $plan.Names -Level $Level)
$bad = @($results | Where-Object { -not $_.Ok })
Write-Host "provision-bots: $($results.Count - $bad.Count) of $($results.Count) bots Running at level $Level"
foreach ($b in ($bad | Select-Object -First 25)) {
    Write-Host "  FAILED $($b.Name): state=$($b.State) error=$($b.Error) boosted=$($b.Boosted)$(if ($b.Refusal) { " refused: $($b.Refusal)" })"
}
if ($bad.Count -gt 25) { Write-Host "  ... and $($bad.Count - 25) more (see $logPath)" }
if ($bad.Count -gt 0) { exit 5 }
exit 0
