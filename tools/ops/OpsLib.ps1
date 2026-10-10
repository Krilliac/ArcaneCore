# Shared helpers of the operator scripts (run-gm-commands.ps1, provision-bots.ps1). Dot-source this file; it defines functions only
# and does nothing else. PowerShell 7. Every function here is pure (no tool is run, no file is written) unless its name says so,
# so tests/ArcaneCore.World.Tests/Ops/OpsScriptTests.cs can call them directly.

Set-StrictMode -Version Latest

# The longest chat line the client may send (CMSG_MESSAGECHAT text, 255 characters in the 1.12.1 client). A command line longer
# than this would be cut by the client and could run as a different command, so it is refused.
$script:OpsMaxCommandLength = 255

# A WoW 1.12.1 character name is 2-12 letters (vmangos ObjectMgr::CheckPlayerName, MAX_PLAYER_NAME = 12).
$script:OpsMaxNameLength = 12

function New-OpsPassword {
    <#
    .SYNOPSIS
      A random password of $Length characters from letters and digits, from the cryptographic generator. Never logged by these scripts.
    #>
    param([int]$Length = 24)
    if ($Length -lt 16 -or $Length -gt 64) { throw "Password length must be 16..64, not $Length." }
    $alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'
    $chars = [char[]]::new($Length)
    for ($i = 0; $i -lt $Length; $i++) {
        $chars[$i] = $alphabet[[System.Security.Cryptography.RandomNumberGenerator]::GetInt32($alphabet.Length)]
    }
    return -join $chars
}

function Test-OpsCommandLine {
    <#
    .SYNOPSIS
      Returns $null when the line is a sendable GM command, comment or #wait directive, else the reason it is refused.
    #>
    param([AllowEmptyString()][string]$Line)
    if ([string]::IsNullOrWhiteSpace($Line)) { return 'a command line is empty' }
    if ($Line -match '[\x00-\x1f\x7f]') { return "a command line has a control character: '$($Line -replace '[\x00-\x1f\x7f]', '?')'" }
    if ($Line.Length -gt $script:OpsMaxCommandLength) { return "a command line is longer than $($script:OpsMaxCommandLength) characters" }
    if ($Line.StartsWith('#')) {
        if ($Line -match '^#waitfor\b') {
            # '#waitfor <seconds 1..99999> <regex>': wait until a log line written after this point matches; the run fails at the timeout.
            if ($Line -notmatch '^#waitfor\s+([1-9]\d{0,4})\s+(\S.*)$') { return "bad waitfor directive '$Line' (use '#waitfor <seconds 1..99999> <regex>')" }
            try { [void][regex]::new($Matches[2]) } catch { return "bad waitfor pattern in '$Line': $($_.Exception.Message)" }
            return $null
        }
        if ($Line -match '^#wait\s+\d{1,5}$') { return $null }
        if ($Line -match '^#wait\b') { return "bad wait directive '$Line' (use '#wait <seconds 0..99999>')" }
        return $null   # a comment
    }
    if (-not $Line.StartsWith('.')) { return "'$Line' is not a GM command (it must start with '.'), a comment ('#') or '#wait N'" }
    return $null
}

function ConvertFrom-OpsTeleSpec {
    <#
    .SYNOPSIS
      Parses 'Westfall=60,Goldshire=25,Elwynn Forest=15' into ordered objects with Name and Weight. Names are game_tele names as
      '.tele name' takes them; a weight is a positive whole number. Anything else is refused (it would end up in a chat line).
    #>
    param([Parameter(Mandatory)][string]$Spec)
    $result = [System.Collections.Generic.List[object]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($part in $Spec.Split(',')) {
        $item = $part.Trim()
        if ($item.Length -eq 0) { throw "Empty entry in the teleport spec '$Spec'." }
        $name = $item
        $weight = 1
        $eq = $item.LastIndexOf('=')
        if ($eq -ge 0) {
            $name = $item.Substring(0, $eq).Trim()
            $text = $item.Substring($eq + 1).Trim()
            if ($text -notmatch '^\d{1,6}$' -or [int]$text -lt 1) { throw "Weight '$text' of '$name' is not a whole number from 1 to 999999." }
            $weight = [int]$text
        }
        if ($name -notmatch "^[A-Za-z][A-Za-z0-9 '\-]{0,59}$") { throw "Teleport name '$name' is not a game_tele name (letters, digits, space, apostrophe, hyphen)." }
        if (-not $seen.Add($name)) { throw "Teleport name '$name' is listed twice." }
        $result.Add([pscustomobject]@{ Name = $name; Weight = $weight })
    }
    return , $result.ToArray()
}

function Get-OpsWeightedAssignment {
    <#
    .SYNOPSIS
      Spreads $Count items over the weighted entries exactly: each entry gets floor(Count * weight / total), and the items left
      over go one each to the entries with the largest fractional parts (ties: the earlier entry). Deterministic, so a run can be
      repeated and tested. Returns, per item index, the entry name, interleaved so neighbours differ where the counts allow.
    #>
    param([Parameter(Mandatory)][object[]]$Entries, [Parameter(Mandatory)][int]$Count)
    if ($Count -lt 0) { throw 'Count must not be negative.' }
    if ($Entries.Count -eq 0) { throw 'No entries to assign to.' }
    $total = 0
    foreach ($e in $Entries) { $total += [int]$e.Weight }
    $quota = foreach ($i in 0..($Entries.Count - 1)) {
        $exact = [double]$Count * [double]$Entries[$i].Weight / [double]$total
        [pscustomobject]@{ Index = $i; Name = $Entries[$i].Name; Floor = [int][math]::Floor($exact); Fraction = $exact - [math]::Floor($exact) }
    }
    $left = $Count - ($quota | Measure-Object -Property Floor -Sum).Sum
    $byFraction = $quota | Sort-Object -Property @{ Expression = 'Fraction'; Descending = $true }, @{ Expression = 'Index'; Descending = $false }
    $counts = @{}
    foreach ($q in $quota) { $counts[$q.Index] = $q.Floor }
    foreach ($q in ($byFraction | Select-Object -First $left)) { $counts[$q.Index]++ }

    # Interleave: repeatedly take one from each entry that still has some, in entry order.
    $remaining = @{}
    foreach ($k in $counts.Keys) { $remaining[$k] = $counts[$k] }
    $out = [System.Collections.Generic.List[string]]::new()
    while ($out.Count -lt $Count) {
        foreach ($i in 0..($Entries.Count - 1)) {
            if ($remaining[$i] -gt 0) { $out.Add($Entries[$i].Name); $remaining[$i]-- }
        }
    }
    return , $out.ToArray()
}

function Get-OpsBotNames {
    <#
    .SYNOPSIS
      $Count distinct letters-only names: the prefix plus a base-26 letter suffix wide enough for the count (Wfbotaaa, Wfbotaab...).
    #>
    param([Parameter(Mandatory)][string]$Prefix, [Parameter(Mandatory)][int]$Count)
    if ($Prefix -notmatch '^[A-Za-z]{2,8}$') { throw "Name prefix '$Prefix' must be 2-8 letters." }
    if ($Count -lt 1 -or $Count -gt 17576) { throw "Bot count $Count is outside 1..17576." }
    $width = 1
    while ([math]::Pow(26, $width) -lt $Count) { $width++ }
    if ($Prefix.Length + $width -gt $script:OpsMaxNameLength) { throw "Prefix '$Prefix' plus a $width-letter suffix is longer than $($script:OpsMaxNameLength) letters." }
    $names = [System.Collections.Generic.List[string]]::new()
    for ($n = 0; $n -lt $Count; $n++) {
        $suffix = ''
        $v = $n
        for ($d = 0; $d -lt $width; $d++) { $suffix = [string][char](97 + ($v % 26)) + $suffix; $v = [int][math]::Floor([double]$v / 26) }   # [int]: Floor of an exact int quotient is a [decimal], which [char] cannot take
        $names.Add($Prefix.Substring(0, 1).ToUpperInvariant() + $Prefix.Substring(1).ToLowerInvariant() + $suffix)
    }
    return , $names.ToArray()
}

function ConvertFrom-OpsClassSpec {
    <#
    .SYNOPSIS
      Parses 'race:class,race:class' (numeric 1.12.1 ids) into objects with Race and Class. The default is the six classes a human can be.
    #>
    param([string]$Spec = '1:1,1:2,1:4,1:5,1:8,1:9')
    $pairs = foreach ($part in $Spec.Split(',')) {
        if ($part.Trim() -notmatch '^(\d{1,2}):(\d{1,2})$') { throw "Race:class pair '$part' is not <race>:<class> with numeric ids." }
        $race = [int]$Matches[1]; $class = [int]$Matches[2]
        if ($race -lt 1 -or $race -gt 11 -or $class -lt 1 -or $class -gt 11) { throw "Race:class pair '$part' is outside the 1.12.1 ids (1..11)." }
        [pscustomobject]@{ Race = $race; Class = $class }
    }
    return , @($pairs)
}

function New-OpsProvisionScript {
    <#
    .SYNOPSIS
      The command lines that provision $Count playerbots at $Level and spread them over the weighted game_tele names:
      create all, start all, wait for them to log in, boost all, teleport all, wait, then one '.playerbot status'.
      Returns the lines (including '#wait' directives and comments) and the names in order.
    #>
    param(
        [Parameter(Mandatory)][int]$Count,
        [Parameter(Mandatory)][int]$Level,
        [Parameter(Mandatory)][string]$NamePrefix,
        [Parameter(Mandatory)][string]$TeleSpec,
        [string]$ClassSpec = '1:1,1:2,1:4,1:5,1:8,1:9',
        [int]$StartWaitSeconds = 20,
        [int]$FinalWaitSeconds = 20,
        [switch]$AnchorHome,
        [int]$HomeRadius = 600,
        [int]$StatusWaitSeconds = 10)
    if ($Level -lt 1 -or $Level -gt 255) { throw "Level $Level is outside 1..255 (the server refuses a level above its maximum)." }
    if ($HomeRadius -lt 50 -or $HomeRadius -gt 5000) { throw "Home radius $HomeRadius is outside 50..5000 (the server's bounds, docs/areas/playbots.md)." }
    if ($StatusWaitSeconds -lt 0 -or $StatusWaitSeconds -gt 600) { throw "Status wait $StatusWaitSeconds is outside 0..600 seconds." }
    $names = Get-OpsBotNames -Prefix $NamePrefix -Count $Count
    $teles = Get-OpsWeightedAssignment -Entries (ConvertFrom-OpsTeleSpec -Spec $TeleSpec) -Count $Count
    $classes = ConvertFrom-OpsClassSpec -Spec $ClassSpec
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("# provision $Count bots at level $Level")
    for ($i = 0; $i -lt $Count; $i++) {
        $pair = $classes[$i % $classes.Count]
        $lines.Add(".playerbot create $($names[$i]) $($pair.Race) $($pair.Class)")
    }
    $lines.Add('# create is asynchronous: let the characters be written before starting them')
    $lines.Add('#wait 5')
    foreach ($name in $names) { $lines.Add(".playerbot start $name") }
    $lines.Add('# a boost needs the character in the world')
    $lines.Add("#wait $StartWaitSeconds")
    foreach ($name in $names) { $lines.Add(".character boost $name $Level") }
    for ($i = 0; $i -lt $Count; $i++) { $lines.Add(".tele name $($names[$i]) $($teles[$i])") }
    if ($AnchorHome) {
        # The home is the place the bot was teleported to (a game_tele name, so no bot position is needed) and one radius for all.
        $lines.Add("# anchor every bot to its place: the brain walks back when it strays past the radius")
        for ($i = 0; $i -lt $Count; $i++) { $lines.Add(".playerbot home $($names[$i]) $($teles[$i]) $HomeRadius") }
    }
    $lines.Add("#wait $FinalWaitSeconds")
    $lines.Add('.playerbot status')
    # The status of 200 bots is many chat lines; the runner closes the session 1.5 s after the last command, so wait for the reply:
    # poll the log for the reply's terminator line ('Playerbot status end: N', so a bot that was never created cannot hide it), up to
    # $StatusWaitSeconds; the run fails (exit 1) only if the terminator never comes; missing bots then reach the per-bot verification (exit 5).
    if ($StatusWaitSeconds -gt 0) { $lines.Add((Get-OpsStatusWaitLine -Seconds $StatusWaitSeconds)) }
    foreach ($line in $lines) {
        $reason = Test-OpsCommandLine -Line $line
        if ($reason) { throw "Generated an invalid line: $reason" }
    }
    return [pscustomobject]@{ Lines = $lines.ToArray(); Names = $names; Teleports = $teles }
}

function Get-OpsStatusWaitLine {
    <#
    .SYNOPSIS
      The '#waitfor' directive that waits up to $Seconds for the end of the '.playerbot status' reply: the 'Playerbot status end: N'
      line the server prints after the last bot (PlayerbotCommands.StatusTerminator), not a bot's own line, which a missing bot never has.
    #>
    param([Parameter(Mandatory)][int]$Seconds)
    return "#waitfor $Seconds \bPlayerbot status end: \d+"
}

function Wait-OpsLogMatch {
    <#
    .SYNOPSIS
      Polls the log file for text written after byte $StartOffset that matches $Pattern; $true when found, $false at the timeout or
      when $IsAlive (a script block) turns false. The file is opened shared, so the writer keeps going. Fails closed: no match is $false.
    #>
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Pattern,
        [Parameter(Mandatory)][int]$TimeoutSeconds,
        [long]$StartOffset = 0,
        [scriptblock]$IsAlive = { $true },
        [int]$PollMilliseconds = 250)
    $regex = [regex]::new($Pattern)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($true) {
        $text = ''
        try {
            $stream = [System.IO.FileStream]::new($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
            try {
                if ($stream.Length -gt $StartOffset) {
                    [void]$stream.Seek($StartOffset, [System.IO.SeekOrigin]::Begin)
                    $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::UTF8, $true, 4096, $true)
                    try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
                }
            }
            finally { $stream.Dispose() }
        }
        catch [System.IO.IOException] { $text = '' }
        if ($regex.IsMatch($text)) { return $true }
        if (-not (& $IsAlive) -or [DateTime]::UtcNow -ge $deadline) { return $false }
        Start-Sleep -Milliseconds $PollMilliseconds
    }
}

function Get-OpsBoostDeadNames {
    <#
    .SYNOPSIS
      The bots not boosted to $Level whose last boost refusal is 'the character is dead': they need '.revive <Name>' before the retry.
    #>
    param([Parameter(Mandatory)][AllowEmptyCollection()][AllowEmptyString()][string[]]$LogLines, [Parameter(Mandatory)][string[]]$Names, [Parameter(Mandatory)][int]$Level)
    foreach ($bot in @(Test-OpsProvisionLog -LogLines $LogLines -Names $Names -Level $Level)) {
        if (-not $bot.Boosted -and $bot.Refusal -match 'is dead') { $bot.Name }
    }
}

function Get-OpsBoostRetryNames {
    <#
    .SYNOPSIS
      The bots whose boost did not land and may land later: not boosted to $Level, and either no 'Boost refused' reply was seen or the
      refusal is a transient one (in combat, dead, settling a quest reward, not yet in the world). A permanent refusal (a level above
      the character's, missing content) is never retried. A bot that already reached $Level is never boosted again.
    #>
    param([Parameter(Mandatory)][AllowEmptyCollection()][AllowEmptyString()][string[]]$LogLines, [Parameter(Mandatory)][string[]]$Names, [Parameter(Mandatory)][int]$Level)
    $transient = 'is in combat|is dead|is settling a quest reward|is not in the world'
    foreach ($bot in @(Test-OpsProvisionLog -LogLines $LogLines -Names $Names -Level $Level)) {
        if ($bot.Boosted) { continue }
        if ($bot.Refusal -eq '' -or $bot.Refusal -match $transient) { $bot.Name }
    }
}

function New-OpsBoostRetryScript {
    <#
    .SYNOPSIS
      One retry pass: '.revive <Name>' for each of $ReviveNames (bots the boost refused as dead stay dead until revived), wait
      $WaitSeconds (a bot in combat needs time to leave that state, a revived one to stand up), boost each named bot again, then
      '.playerbot status' and a wait for the last bot's reply. Returns the lines.
    #>
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Names,
        [Parameter(Mandatory)][int]$Level,
        [int]$WaitSeconds = 30,
        [int]$StatusWaitSeconds = 10,
        [AllowEmptyCollection()][string[]]$ReviveNames = @())
    if ($Names.Count -eq 0) { throw 'A retry pass needs at least one bot.' }
    if ($Level -lt 1 -or $Level -gt 255) { throw "Level $Level is outside 1..255." }
    if ($WaitSeconds -lt 0 -or $WaitSeconds -gt 3600) { throw "Retry wait $WaitSeconds is outside 0..3600 seconds." }
    if ($StatusWaitSeconds -lt 0 -or $StatusWaitSeconds -gt 600) { throw "Status wait $StatusWaitSeconds is outside 0..600 seconds." }
    foreach ($dead in $ReviveNames) { if ($Names -notcontains $dead) { throw "Cannot revive '$dead': it is not one of the bots in this pass." } }
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("# boost retry for $($Names.Count) bots")
    if ($ReviveNames.Count -gt 0) {
        $lines.Add("# $($ReviveNames.Count) bots were refused as dead: revive them first")
        foreach ($dead in $ReviveNames) { $lines.Add(".revive $dead") }
    }
    $lines.Add("#wait $WaitSeconds")
    foreach ($name in $Names) { $lines.Add(".character boost $name $Level") }
    $lines.Add('.playerbot status')
    if ($StatusWaitSeconds -gt 0) { $lines.Add((Get-OpsStatusWaitLine -Seconds $StatusWaitSeconds)) }
    foreach ($line in $lines) {
        $reason = Test-OpsCommandLine -Line $line
        if ($reason) { throw "Generated an invalid line: $reason" }
    }
    return $lines.ToArray()
}

function Get-OpsHomeSetCount {
    <#
    .SYNOPSIS
      The number of homes the server reported set: the sum of N over every 'Playerbot home: N set' reply. Zero for a log with none,
      so a home that was never confirmed is never counted.
    #>
    param([Parameter(Mandatory)][AllowEmptyCollection()][AllowEmptyString()][string[]]$LogLines)
    $total = 0
    foreach ($line in $LogLines) {
        if ($line -match 'Playerbot home: (\d+) set\b') { $total += [int]$Matches[1] }
    }
    return $total
}

function Test-OpsProvisionLog {
    <#
    .SYNOPSIS
      Reads the session log of a provision run and returns objects, one per bot: Name, State (from '.playerbot status'), Error,
      Boosted ($true when 'Boost <Name>: level a->b' was seen with b = Level), Refusal (the 'Boost refused for <Name>' reason, if any)
      and Ok. A bot absent from the status output has State '(missing)': the check never reports a pass for what it did not see.
    #>
    # A real session log has blank lines and may be empty; neither may abort the check (an empty log must report every bot missing).
    param([Parameter(Mandatory)][AllowEmptyCollection()][AllowEmptyString()][string[]]$LogLines, [Parameter(Mandatory)][string[]]$Names, [Parameter(Mandatory)][int]$Level)
    foreach ($name in $Names) {
        $escaped = [regex]::Escape($name)
        $state = '(missing)'; $err = ''
        # The last status line wins: a bot that was Starting and later Running ends Running.
        foreach ($line in $LogLines) {
            if ($line -match "\b$escaped state=(\w+) desired=(\w+) .*error=(\S+)") { $state = $Matches[1]; $err = $Matches[3] }
        }
        $boosted = $false; $refusal = ''
        foreach ($line in $LogLines) {
            if ($line -match "Boost ${escaped}: level \d+->(\d+)," -and [int]$Matches[1] -eq $Level) { $boosted = $true }
            elseif ($line -match "Boost refused for ${escaped}: (.*?)\.?\s*$") { $refusal = $Matches[1] }
        }
        [pscustomobject]@{
            Name = $name; State = $state; Error = $err; Boosted = $boosted; Refusal = $refusal
            Ok = ($state -eq 'Running' -and $err -eq 'none' -and $boosted)
        }
    }
}
