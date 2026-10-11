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
    .playerbot home <Name> <place> <radius>     (only with -Home: the bot is anchored to the place it was sent to)

  then one '.playerbot status' after -FinalWaitSeconds, and a wait of up to -StatusWaitSeconds for its reply: the runner polls the
  session log for the reply's 'Playerbot status end: N' line ('#waitfor') and fails the run (exit 1, nothing verified) if it never comes. A boost
  refused because the bot is dead is retried after '.revive <Name>'. A boost the server refuses because the bot is in combat, dead,
  settling a quest reward or not yet in the world is retried: up to -BoostRetries more passes, each after -RetryWaitSeconds, each
  boosting only the bots still not boosted. A permanent refusal is never retried.

  Verification: every bot must appear in the last status as state=Running with error=none, and its boost reply must show the level;
  with -Home the server must also have confirmed one home per bot ('Playerbot home: N set'). Anything else is listed and the exit
  code is 5. A bot absent from the status output counts as a failure: the check never passes for what it did not see.

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
.PARAMETER Home
  Anchors every bot to the place it is teleported to (.playerbot home <Name> <place> <radius>), so the brain walks back when it strays
  further than -HomeRadius from it (docs/areas/playbots.md, Home area). Without it bots follow their own goals anywhere.
.PARAMETER HomeRadius
  The home radius in yards (50..5000, default 600).
.PARAMETER StatusWaitSeconds
  The longest to wait for the reply of '.playerbot status' (default 10); the wait ends when the last bot's line is in the log, and the run fails at the timeout.
.PARAMETER BoostRetries
  How many more boost passes to run for bots whose boost was refused for a transient reason (0..10, default 3).
.PARAMETER RetryWaitSeconds
  The pause before each retry pass (default 30).
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
    [int]$StatusWaitSeconds = 10,
    [int]$BoostRetries = 3,
    [int]$RetryWaitSeconds = 30,
    [int]$HomeRadius = 600,
    [string]$Account = 'OPSGM',
    [string]$Character = 'Opsgm',
    [string]$Realm = '127.0.0.1:3724',
    [string]$StopFile,
    [string]$AccountTool,
    [string]$MockTool,
    [switch]$CreateAccount,
    # $Home is a read-only PowerShell variable, so the parameter has another name and answers to -Home.
    [Alias('Home')][switch]$AnchorHome,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'OpsLib.ps1')

if ($BoostRetries -lt 0 -or $BoostRetries -gt 10) { [Console]::Error.WriteLine('provision-bots: -BoostRetries must be 0..10.'); exit 2 }
if ($RetryWaitSeconds -lt 0 -or $RetryWaitSeconds -gt 3600) { [Console]::Error.WriteLine('provision-bots: -RetryWaitSeconds must be 0..3600.'); exit 2 }
try {
    $plan = New-OpsProvisionScript -Count $Count -Level $Level -NamePrefix $NamePrefix -TeleSpec $TeleSpec -ClassSpec $ClassSpec `
        -StartWaitSeconds $StartWaitSeconds -FinalWaitSeconds $FinalWaitSeconds -AnchorHome:$AnchorHome -HomeRadius $HomeRadius `
        -StatusWaitSeconds $StatusWaitSeconds
}
catch {
    [Console]::Error.WriteLine("provision-bots: $($_.Exception.Message)")
    exit 2
}

$distribution = $plan.Teleports | Group-Object | Sort-Object -Property Count -Descending | ForEach-Object { "$($_.Name)=$($_.Count)" }
Write-Host "provision-bots: $Count bots at level $Level; places: $($distribution -join ', ')$(if ($AnchorHome) { "; home radius $HomeRadius" })"

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

$logs = [System.Collections.Generic.List[string]]::new()
$logs.Add($logPath)
$log = [System.IO.File]::ReadAllLines($logPath)

# Retry the boosts the server refused for a transient reason. Each pass is its own session (the log is read between passes); the last
# status line and any successful boost in any pass decide the verdict below.
for ($pass = 1; $pass -le $BoostRetries; $pass++) {
    $retry = @(Get-OpsBoostRetryNames -LogLines $log -Names $plan.Names -Level $Level)
    if ($retry.Count -eq 0) { break }
    $dead = @(Get-OpsBoostDeadNames -LogLines $log -Names $plan.Names -Level $Level)
    Write-Host "provision-bots: boost retry $pass of ${BoostRetries}: $($retry.Count) bots"
    $retryLog = Join-Path $opsDirectory "provision-$stamp-retry$pass.log"
    $retryFile = Join-Path $opsDirectory "provision-$stamp-retry$pass.commands"
    [System.IO.File]::WriteAllLines($retryFile, (New-OpsBoostRetryScript -Names $retry -Level $Level -WaitSeconds $RetryWaitSeconds -StatusWaitSeconds $StatusWaitSeconds -ReviveNames $dead))
    & (Join-Path $PSScriptRoot 'run-gm-commands.ps1') @runArguments -CommandFile $retryFile -LogPath $retryLog
    $retryCode = $LASTEXITCODE
    if ($retryCode -ne 0) {
        [Console]::Error.WriteLine("provision-bots: retry pass $pass ended with exit code $retryCode (log $retryLog); not verifying.")
        exit $retryCode
    }
    $logs.Add($retryLog)
    $log = @($log) + [System.IO.File]::ReadAllLines($retryLog)
}

$results = @(Test-OpsProvisionLog -LogLines $log -Names $plan.Names -Level $Level)
$bad = @($results | Where-Object { -not $_.Ok })
Write-Host "provision-bots: $($results.Count - $bad.Count) of $($results.Count) bots Running at level $Level"
foreach ($b in ($bad | Select-Object -First 25)) {
    Write-Host "  FAILED $($b.Name): state=$($b.State) error=$($b.Error) boosted=$($b.Boosted)$(if ($b.Refusal) { " refused: $($b.Refusal)" })"
}
if ($bad.Count -gt 25) { Write-Host "  ... and $($bad.Count - 25) more (see $($logs -join ', '))" }
$homesMissing = $false
if ($AnchorHome) {
    $homesSet = Get-OpsHomeSetCount -LogLines $log
    Write-Host "provision-bots: $homesSet of $Count homes confirmed set"
    if ($homesSet -lt $Count) { $homesMissing = $true }
}
if ($bad.Count -gt 0 -or $homesMissing) { exit 5 }
exit 0
