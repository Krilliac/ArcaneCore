<#
.SYNOPSIS
  Runs GM commands on a live ArcaneCore world through an operator account, then puts the account back as it found it.

.DESCRIPTION
  The server has no console command channel for '.' commands, so this logs a character in with arcane-mock live (the loopback
  protocol client, tools/ArcaneCore.MockClient) and sends the lines as chat, paced, while it prints every reply into a log.

    1. checks every command line (a GM command '.x', a '# comment', or '#wait N' to pause N seconds) and refuses the run otherwise;
    2. reads the operator account's security level (arcane-account list), so it can be restored exactly;
    3. sets a fresh random password (arcane-account set-password --password-stdin: the password goes through standard input, never
       the command line) and promotes the account to Administrator (arcane-account set-gmlevel);
    4. writes the password into a credentials file only the current user can read, for arcane-mock live --credentials-file;
    5. sends the lines one by one with -PaceMilliseconds between them, stopping at once when the -StopFile appears;
       a line is only 'sent' once arcane-mock has replayed it and logged '[<endpoint>] > <command>': appending it to the script file
       is not delivery, because the mock polls the file and can fall behind. After the last line the run waits (bounded) until the
       log shows every appended command, and only then keeps the reply window and ends the session;
    6. in a finally block, whatever happened: stops the session, deletes the credentials file, and demotes the account to the level
       it had (three tries). A demotion that fails is reported loudly and the exit code says so.

  The password is random, kept in memory and in the credentials file for the length of the run only, and is never printed or put in
  the log (the log is scanned for it afterwards as a second guard). The account keeps an unknown password afterwards; the next run
  sets a new one.

  Exit codes: 0 done and every command was delivered (counted from the log); 1 the session or a tool failed, or fewer commands were
  delivered than appended (the message names the first undelivered one); 2 refused before any change (bad argument, stop file already present, missing
  tool, unknown account); 3 stopped by the stop file; 4 the account could not be demoted (check it by hand: arcane-account list).

  arcane-account reads the auth database from the appsettings.json in its working directory, so -ProfileDirectory is the directory
  that holds the server profile's appsettings.json (and any appsettings.*.json); the scripts' own state (credentials, logs, the stop
  file) goes in its 'ops' subdirectory. -BinDirectory holds the published arcane-account and arcane-mock. A promoted account is
  Administrator only from its next world login (arcane-account says so); this script logs in after promoting, so that holds.

.PARAMETER ProfileDirectory
  The server profile directory: appsettings.json for arcane-account, and 'ops' for state and logs.
.PARAMETER BinDirectory
  Directory with arcane-account and arcane-mock (.exe on Windows).
.PARAMETER Command
  Command lines (repeatable). Use with or instead of -CommandFile.
.PARAMETER CommandFile
  A text file of command lines, one per line.
.PARAMETER Account
  The operator account (default OPSGM). It must exist, or pass -CreateAccount.
.PARAMETER Character
  The character the session logs in as (default Opsgm; arcane-mock creates it when the account has none of that name).
.PARAMETER Realm
  The realm server, loopback only (arcane-mock refuses anything else). Default 127.0.0.1:3724.
.PARAMETER PaceMilliseconds
  Pause after each command line (default 600; at least 450, because the client waits 400 ms for each reply).
.PARAMETER StopFile
  Creating this file stops the run before the next line. Default: <ProfileDirectory>/ops/stop. A stop file that exists at the start
  refuses the run.
.PARAMETER LogPath
  The session log. Default: <ProfileDirectory>/ops/run-<time>.log.
.PARAMETER AccountTool
  Overrides the arcane-account path (tests).
.PARAMETER MockTool
  Overrides the arcane-mock path (tests).
.PARAMETER CreateAccount
  Creates the account when it does not exist (with the random password; it is then restored to Player).
.PARAMETER DryRun
  Checks everything and prints the plan. Runs no tool and changes nothing.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ProfileDirectory,
    [Parameter(Mandatory)][string]$BinDirectory,
    [string[]]$Command = @(),
    [string]$CommandFile,
    [string]$Account = 'OPSGM',
    [string]$Character = 'Opsgm',
    [string]$Realm = '127.0.0.1:3724',
    [int]$PaceMilliseconds = 600,
    [string]$StopFile,
    [string]$LogPath,
    [string]$AccountTool,
    [string]$MockTool,
    [int]$LoginTimeoutSeconds = 90,
    [switch]$CreateAccount,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'OpsLib.ps1')

function Stop-Refused([string]$Message) {
    [Console]::Error.WriteLine("run-gm-commands: $Message")
    exit 2
}

# ---- 1. arguments --------------------------------------------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $ProfileDirectory -PathType Container)) { Stop-Refused "profile directory '$ProfileDirectory' does not exist." }
if (-not (Test-Path -LiteralPath $BinDirectory -PathType Container)) { Stop-Refused "bin directory '$BinDirectory' does not exist." }
$ProfileDirectory = (Resolve-Path -LiteralPath $ProfileDirectory).Path
$BinDirectory = (Resolve-Path -LiteralPath $BinDirectory).Path
if ($Account -notmatch '^[A-Za-z0-9]{1,16}$') { Stop-Refused "account '$Account' must be 1-16 letters or digits." }
if ($Character -notmatch '^[A-Za-z]{2,12}$') { Stop-Refused "character '$Character' must be 2-12 letters." }
if ($PaceMilliseconds -lt 450 -or $PaceMilliseconds -gt 600000) { Stop-Refused "-PaceMilliseconds must be 450..600000." }
if ($LoginTimeoutSeconds -lt 5 -or $LoginTimeoutSeconds -gt 600) { Stop-Refused '-LoginTimeoutSeconds must be 5..600.' }

$lines = [System.Collections.Generic.List[string]]::new()
foreach ($c in $Command) { $lines.Add($c.Trim()) }
if ($CommandFile) {
    if (-not (Test-Path -LiteralPath $CommandFile -PathType Leaf)) { Stop-Refused "command file '$CommandFile' does not exist." }
    foreach ($c in [System.IO.File]::ReadAllLines((Resolve-Path -LiteralPath $CommandFile).Path)) {
        if ($c.Trim().Length -gt 0) { $lines.Add($c.Trim()) }
    }
}
if ($lines.Count -eq 0) { Stop-Refused 'no commands: pass -Command or -CommandFile.' }
foreach ($line in $lines) {
    $reason = Test-OpsCommandLine -Line $line
    if ($reason) { Stop-Refused $reason }
}
$sendable = @($lines | Where-Object { -not $_.StartsWith('#') })
if ($sendable.Count -eq 0) { Stop-Refused 'the commands are all comments or waits; nothing to send.' }

$opsDirectory = Join-Path $ProfileDirectory 'ops'
if (-not $StopFile) { $StopFile = Join-Path $opsDirectory 'stop' }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
if (-not $LogPath) { $LogPath = Join-Path $opsDirectory "run-$stamp.log" }
$exeSuffix = if ($IsWindows) { '.exe' } else { '' }
if (-not $AccountTool) { $AccountTool = Join-Path $BinDirectory "arcane-account$exeSuffix" }
if (-not $MockTool) { $MockTool = Join-Path $BinDirectory "arcane-mock$exeSuffix" }

$waitSeconds = 0
foreach ($line in $lines) { if ($line -match '^#wait(?:for)?\s+(\d+)(?:\s|$)') { $waitSeconds += [int]$Matches[1] } }
$estimateSeconds = [int]([math]::Ceiling($sendable.Count * ($PaceMilliseconds / 1000.0)) + $waitSeconds + $LoginTimeoutSeconds + 120)
$estimateSeconds = [math]::Min($estimateSeconds, 86400)

if ($DryRun) {
    Write-Host "run-gm-commands DRY RUN: nothing is run or changed."
    Write-Host "  profile      $ProfileDirectory"
    Write-Host "  bin          $BinDirectory"
    Write-Host "  account      $($Account.ToUpperInvariant())  character $Character  realm $Realm"
    Write-Host "  tools        $AccountTool$(if (-not (Test-Path -LiteralPath $AccountTool)) { '  (NOT FOUND)' })"
    Write-Host "               $MockTool$(if (-not (Test-Path -LiteralPath $MockTool)) { '  (NOT FOUND)' })"
    Write-Host "  stop file    $StopFile$(if (Test-Path -LiteralPath $StopFile) { '  (EXISTS: a real run would refuse)' })"
    Write-Host "  log          $LogPath"
    Write-Host "  lines        $($sendable.Count) commands, $waitSeconds s of waits, $PaceMilliseconds ms pace, session cap $estimateSeconds s"
    Write-Host '  would: read the account level; set a random password (stdin); promote to Administrator; log in; send the lines;'
    Write-Host '         then, in finally, stop the session, delete the credentials file and restore the level.'
    foreach ($line in ($lines | Select-Object -First 5)) { Write-Host "    $line" }
    if ($lines.Count -gt 5) { Write-Host "    ... and $($lines.Count - 5) more" }
    exit 0
}

foreach ($tool in @($AccountTool, $MockTool)) {
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { Stop-Refused "tool '$tool' does not exist." }
}
if (Test-Path -LiteralPath $StopFile) { Stop-Refused "stop file '$StopFile' exists; remove it first." }
New-Item -ItemType Directory -Force -Path $opsDirectory | Out-Null

# ---- tools ---------------------------------------------------------------------------------------------------------------
# arcane-account reads appsettings.json from its working directory. Its output is returned; the password only ever goes to stdin.
function Invoke-Account([string[]]$Arguments, [string]$StdIn) {
    Push-Location -LiteralPath $ProfileDirectory
    try {
        $global:LASTEXITCODE = 0
        $output = if ($PSBoundParameters.ContainsKey('StdIn') -and $null -ne $StdIn) { $StdIn | & $AccountTool @Arguments 2>&1 } else { & $AccountTool @Arguments 2>&1 }
        $code = $LASTEXITCODE
    }
    finally { Pop-Location }
    return [pscustomobject]@{ Code = $code; Output = @($output | ForEach-Object { "$_" }) }
}

# How many of the Commands, from the first, arcane-mock live has logged as sending, in order: its '<HH:mm:ss.fff> [<endpoint>] > <command>'
# lines (OpsLib Get-OpsMockEchoPattern, which a '#waitfor' skips) written after StartOffset. "Delivered" means the mock logged the send;
# a send that fails ends the mock non-zero, which the finally block turns into exit 1. Replies are '< ' lines; a multi-line reply's
# continuation lines are written raw without a timestamp, so they never match, even when they look like '[x] > y'. Endpoint is the
# session's local endpoint from its 'IN WORLD' line (IPv4 or '[::1]:port'); without one, any IPv4 or bracketed IPv6 endpoint is accepted.
function Get-OpsDeliveredCount([string]$Path, [long]$StartOffset, [string[]]$Commands, [string]$Endpoint) {
    $stream = [System.IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite')
    try {
        if ($stream.Length -le $StartOffset) { return 0 }
        [void]$stream.Seek($StartOffset, 'Begin')
        $text = [System.IO.StreamReader]::new($stream).ReadToEnd()
    }
    finally { $stream.Dispose() }
    $echo = [regex]::new((Get-OpsMockEchoPattern -Endpoint $Endpoint))
    $count = 0
    foreach ($row in $text -split "\r?\n") {
        if ($count -ge $Commands.Count) { break }
        $m = $echo.Match($row)
        if ($m.Success -and $m.Groups[1].Value.TrimEnd() -ceq $Commands[$count]) { $count++ }
    }
    return $count
}

function Get-AccountSecurity([string]$Name) {
    $result = Invoke-Account -Arguments @('list')
    if ($result.Code -ne 0) { throw "arcane-account list failed ($($result.Code)): $($result.Output -join ' | ')" }
    foreach ($row in $result.Output) {
        if ($row -match '^\s*\d+\s+(\S+)\s+\S+\s+(\S+)\s*$' -and $Matches[1] -ieq $Name) { return $Matches[2] }
    }
    return $null
}

# ---- 2-6. the run --------------------------------------------------------------------------------------------------------
$accountName = $Account.ToUpperInvariant()
$exit = 0
$promoted = $false
$original = $null
$password = $null
$credentials = Join-Path $opsDirectory "session-$PID.credentials"
$scriptFile = Join-Path $opsDirectory "session-$PID.commands"
$job = $null
$createdStop = $false
$stopRequested = $false

try {
    $original = Get-AccountSecurity -Name $accountName
    if ($null -ne $original -and $original -notin @('Player', 'Moderator', 'GameMaster', 'Administrator')) {
        throw "REFUSED: account '$accountName' has security '$original', which is not one of Player, Moderator, GameMaster, Administrator; it could not be restored safely, so it is not promoted."
    }
    $password = New-OpsPassword
    if ($null -eq $original) {
        if (-not $CreateAccount) { throw "REFUSED: account '$accountName' does not exist (pass -CreateAccount to create it)." }
        $made = Invoke-Account -Arguments @('create', $accountName, '--password-stdin') -StdIn $password
        if ($made.Code -ne 0) { throw "arcane-account create failed ($($made.Code)): $($made.Output -join ' | ')" }
        $original = 'Player'
        Write-Host "created account $accountName"
    }
    else {
        $set = Invoke-Account -Arguments @('set-password', $accountName, '--password-stdin') -StdIn $password
        if ($set.Code -ne 0) { throw "arcane-account set-password failed ($($set.Code)): $($set.Output -join ' | ')" }
    }
    Write-Host "account $accountName was $original; promoting to Administrator for this run"

    $promote = Invoke-Account -Arguments @('set-gmlevel', $accountName, 'administrator')
    $promoted = $true   # even a half-applied promotion is undone below
    if ($promote.Code -ne 0) { throw "arcane-account set-gmlevel failed ($($promote.Code)): $($promote.Output -join ' | ')" }

    # The credentials file: created empty, restricted to the current user, then filled.
    New-Item -ItemType File -Path $credentials -Force | Out-Null
    if ($IsWindows) {
        $acl = Get-Acl -LiteralPath $credentials
        $acl.SetAccessRuleProtection($true, $false)
        foreach ($rule in @($acl.Access)) { [void]$acl.RemoveAccessRule($rule) }
        $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
        $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new($me, 'FullControl', 'Allow'))
        Set-Acl -LiteralPath $credentials -AclObject $acl
    }
    else {
        [System.IO.File]::SetUnixFileMode($credentials, [System.IO.UnixFileMode]::UserRead -bor [System.IO.UnixFileMode]::UserWrite)
    }
    [System.IO.File]::WriteAllText($credentials, "account=$accountName`npassword=$password`n")
    New-Item -ItemType File -Path $scriptFile -Force | Out-Null
    New-Item -ItemType File -Path $LogPath -Force | Out-Null

    # Stop file present now (it appeared while the account was being promoted): nothing is sent.
    $mockArguments = @('live', '--realm', $Realm, '--account', $accountName, '--credentials-file', $credentials, '--character', $Character,
        '--script-file', $scriptFile, '--stop-file', $StopFile, '--interval-ms', '250', '--duration-s', "$estimateSeconds")
    $job = Start-Job -ScriptBlock {
        param($Tool, $ToolArguments, $Log)
        & $Tool @ToolArguments *>> $Log
        $LASTEXITCODE
    } -ArgumentList $MockTool, $mockArguments, $LogPath
    Write-Host "session started; log $LogPath"

    # Wait for the character to be in the world.
    $deadline = [DateTime]::UtcNow.AddSeconds($LoginTimeoutSeconds)
    $inWorld = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $StopFile) { $stopRequested = $true; break }
        if ($job.State -ne 'Running') { break }
        if ((Get-Content -LiteralPath $LogPath -Raw -ErrorAction SilentlyContinue) -match 'IN WORLD as ') { $inWorld = $true; break }
        Start-Sleep -Milliseconds 250
    }
    if (-not $inWorld -and -not $stopRequested) { throw "the session did not reach the world (see $LogPath)." }

    # arcane-mock live logs each replayed line as '<time> [<endpoint>] > <command>'. Everything before this offset is not ours.
    $deliveryOffset = (Get-Item -LiteralPath $LogPath).Length
    $localEndpoint = $null
    if ((Get-Content -LiteralPath $LogPath -Raw -ErrorAction SilentlyContinue) -match 'IN WORLD as [^\r\n]*? connection (\S+) ->') { $localEndpoint = $Matches[1] }
    $sent = 0
    $lastSendOffset = $null
    foreach ($line in $lines) {
        if ($stopRequested -or (Test-Path -LiteralPath $StopFile)) { $stopRequested = $true; break }
        if ($job.State -ne 'Running') { throw "the session ended early after $sent commands (see $LogPath)." }
        if ($line.StartsWith('#')) {
            if ($line -match '^#waitfor\s+(\d+)\s+(.+)$') {
                # Wait for a reply: only log text written from here on counts. Fails closed: a timeout ends the run with exit 1.
                $waitSpan = [int]$Matches[1]; $waitPattern = $Matches[2]
                # The offset is the log length taken just before the last command was appended to the script file, not the length now:
                # the pace sleep after that command can outlast the reply, and a reply already in the log must still count.
                $offset = if ($null -ne $lastSendOffset) { $lastSendOffset } else { (Get-Item -LiteralPath $LogPath).Length }
                # The mock's own '> <command>' echo lines are left out: a pattern that is also in the command ('.lookup item Linen',
                # then '#waitfor 10 Linen') would otherwise match the command as soon as it was sent, before the reply arrived.
                $found = Wait-OpsLogMatch -Path $LogPath -Pattern $waitPattern -TimeoutSeconds $waitSpan -StartOffset $offset `
                    -SkipLinePattern (Get-OpsMockEchoPattern -Endpoint $localEndpoint) `
                    -IsAlive { $job.State -eq 'Running' -and -not (Test-Path -LiteralPath $StopFile) }
                if (-not $found -and -not (Test-Path -LiteralPath $StopFile)) {
                    if ($job.State -ne 'Running') { throw "the session ended during '$line' (see $LogPath)." }
                    throw "no log line matched '$waitPattern' within $waitSpan s (see $LogPath)."
                }
                continue
            }
            if ($line -match '^#wait\s+(\d+)$') {
                $until = [DateTime]::UtcNow.AddSeconds([int]$Matches[1])
                while ([DateTime]::UtcNow -lt $until) {
                    if (Test-Path -LiteralPath $StopFile) { $stopRequested = $true; break }
                    if ($job.State -ne 'Running') { throw "the session ended early during a wait (see $LogPath)." }
                    Start-Sleep -Milliseconds 250
                }
            }
            continue
        }
        $lastSendOffset = (Get-Item -LiteralPath $LogPath).Length
        [System.IO.File]::AppendAllText($scriptFile, "$line`n")
        $sent++
        Start-Sleep -Milliseconds $PaceMilliseconds
    }

    # Appending is not sending: wait until the mock's log shows every appended command. Bounded by the backlog at the pace the mock
    # drains it (each reply costs it 400 ms) plus 10 s; ends early when the session dies or the stop file appears.
    $delivered = Get-OpsDeliveredCount -Path $LogPath -StartOffset $deliveryOffset -Commands $sendable -Endpoint $localEndpoint
    $backlog = $sent - $delivered
    if ($backlog -gt 0 -and -not $stopRequested) {
        $until = [DateTime]::UtcNow.AddMilliseconds($backlog * ($PaceMilliseconds + 1000) + 10000)
        while ($delivered -lt $sent -and [DateTime]::UtcNow -lt $until) {
            if (Test-Path -LiteralPath $StopFile) { $stopRequested = $true; break }
            if ($job.State -ne 'Running') { break }
            Start-Sleep -Milliseconds 100
            $delivered = Get-OpsDeliveredCount -Path $LogPath -StartOffset $deliveryOffset -Commands $sendable -Endpoint $localEndpoint
        }
    }
    if (-not $stopRequested -and (Test-Path -LiteralPath $StopFile)) { $stopRequested = $true }   # the user's stop file wins
    if (-not $stopRequested) {
        Start-Sleep -Milliseconds 1500   # the last reply's window
        if (Test-Path -LiteralPath $StopFile) { $stopRequested = $true }
    }
    $delivered = Get-OpsDeliveredCount -Path $LogPath -StartOffset $deliveryOffset -Commands $sendable -Endpoint $localEndpoint
    Write-Host "$(if ($stopRequested) { 'stop file seen: ' })sent $delivered of $($sendable.Count) commands"
    if ($stopRequested) { $exit = 3 }
    elseif ($delivered -lt $sent) {
        throw "only $delivered of $sent appended commands reached the world (first undelivered: '$($sendable[$delivered])'); arcane-mock live never sent it (see $LogPath)."
    }
}
catch {
    $message = $_.Exception.Message
    if ($message.StartsWith('REFUSED: ')) { $message = $message.Substring(9); $exit = 2 } else { $exit = 1 }
    [Console]::Error.WriteLine("run-gm-commands: $message")
}
finally {
    # Stop the session: the stop file ends arcane-mock live cleanly (it logs out); then the job, if it is still there.
    if ($null -ne $job) {
        if (-not (Test-Path -LiteralPath $StopFile)) {
            New-Item -ItemType File -Path $StopFile -Force | Out-Null
            $createdStop = $true
        }
        [void](Wait-Job -Job $job -Timeout 90)
        if ($job.State -eq 'Running') { Stop-Job -Job $job }
        $jobOutput = @(Receive-Job -Job $job -ErrorAction SilentlyContinue)
        Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
        $mockCode = if ($jobOutput.Count -gt 0) { $jobOutput[-1] } else { $null }
        if ($exit -eq 0 -and $mockCode -ne 0) {
            [Console]::Error.WriteLine("run-gm-commands: arcane-mock live exited with $mockCode (see $LogPath).")
            $exit = 1
        }
    }
    # The stop file is consumed by this run (ours, or the operator's), so the next run starts clean.
    Remove-Item -LiteralPath $StopFile -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $credentials, $scriptFile -Force -ErrorAction SilentlyContinue

    # Second guard: the password must not be in the log. Redact it if it is.
    if ($null -ne $password -and (Test-Path -LiteralPath $LogPath)) {
        $text = [System.IO.File]::ReadAllText($LogPath)
        if ($text.Contains($password)) {
            [System.IO.File]::WriteAllText($LogPath, $text.Replace($password, '********'))
            [Console]::Error.WriteLine('run-gm-commands: the password was found in the log and redacted.')
        }
    }

    # Demote: whatever happened above. Three tries.
    if ($promoted) {
        $restore = ([string]$original).ToLowerInvariant()
        $demoted = $false
        for ($try = 1; $try -le 3 -and -not $demoted; $try++) {
            try {
                $r = Invoke-Account -Arguments @('set-gmlevel', $accountName, $restore)
                if ($r.Code -eq 0) { $demoted = $true } else { [Console]::Error.WriteLine("run-gm-commands: demotion try ${try} failed ($($r.Code)): $($r.Output -join ' | ')") }
            }
            catch { [Console]::Error.WriteLine("run-gm-commands: demotion try ${try} failed: $($_.Exception.Message)") }
            if (-not $demoted) { Start-Sleep -Seconds 1 }
        }
        if ($demoted) { Write-Host "account $accountName restored to $restore" }
        else {
            [Console]::Error.WriteLine("run-gm-commands: ACCOUNT $accountName IS STILL ADMINISTRATOR. Run: arcane-account set-gmlevel $accountName $restore")
            $exit = 4
        }
    }
}
exit $exit
