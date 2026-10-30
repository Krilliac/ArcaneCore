# Locations shared by the local live-server scripts in tools/ops/live and tools/ops/diag. Dot-source this file; it defines functions only.
# A parameter always wins; otherwise the value comes from the environment (docs/ops/tooling.md), and a missing one stops with a message
# that names the variable.
#   ARCANECORE_LIVE_HOME     the live install root: bin\ (the daemons run from here), logs\, snapshots\, dumps\
#   ARCANECORE_LIVE_PROFILE  the profile directory the daemons start in (appsettings.json, auth.db, characters.db, world.db)

Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\OpsEnv.ps1')

function Resolve-LiveHome {
    param([string]$LiveHome)
    if ($LiveHome) { return $LiveHome }
    return Get-OpsRequiredEnv 'ARCANECORE_LIVE_HOME' 'the live install root holding bin, logs, snapshots and dumps'
}

function Resolve-LiveProfile {
    param([string]$LiveProfile)
    if ($LiveProfile) { return $LiveProfile }
    $value = Get-OpsEnv 'ARCANECORE_LIVE_PROFILE'
    if ($null -eq $value) { $value = Get-OpsEnv 'ARCANECORE_LIVE_ROOT' }
    if ($null -eq $value) { throw 'ARCANECORE_LIVE_PROFILE is not set (the live server profile directory that holds appsettings.json). Set it, or pass -Profile; see docs/ops/tooling.md.' }
    return $value
}

function Get-LiveDaemon {
    <#
    .SYNOPSIS
      The running ArcaneCore.Realm or ArcaneCore.World process, or $null. Refuses to guess when more than one is running.
    #>
    param([Parameter(Mandatory)][ValidateSet('Realm', 'World')][string]$Role)
    $found = @(Get-Process "ArcaneCore.$Role" -ErrorAction SilentlyContinue)
    if ($found.Count -gt 1) { throw "$($found.Count) ArcaneCore.$Role processes are running ($($found.Id -join ', ')); pass the PID explicitly." }
    if ($found.Count -eq 0) { return $null }
    return $found[0]
}

function ConvertTo-LiveLogFormat {
    param([object]$Value)
    if ($null -eq $Value) { return $null }
    $text = ([string]$Value).Trim()
    if ($text -ieq 'Json') { return 'Json' }
    if ($text -ieq 'Text') { return 'Text' }
    return $null
}

function Read-LiveLogProfileSection {
    # The profile's appsettings.json Logging:ArcaneCore object, or $null (no profile, no file, unreadable, or no such section).
    param([string]$LiveProfile)
    if (-not $LiveProfile) {
        $LiveProfile = Get-OpsEnv 'ARCANECORE_LIVE_PROFILE'
        if ($null -eq $LiveProfile) { $LiveProfile = Get-OpsEnv 'ARCANECORE_LIVE_ROOT' }
    }
    if (-not $LiveProfile) { return $null }
    $settings = Join-Path $LiveProfile 'appsettings.json'
    if (-not (Test-Path -LiteralPath $settings)) { return $null }
    try {
        $root = Get-Content -Raw -LiteralPath $settings | ConvertFrom-Json
        if ($root.PSObject.Properties['Logging'] -and $root.Logging.PSObject.Properties['ArcaneCore']) { return $root.Logging.ArcaneCore }
    } catch { return $null }
    return $null
}

function Get-LiveLogFormatSetting {
    <#
    .SYNOPSIS
      'Json' or 'Text' only when Logging:ArcaneCore:Format is set explicitly; $null when it is unset (the legacy File/Json switches decide).
    .DESCRIPTION
      Environment Logging__ArcaneCore__Format first, else the profile's appsettings.json Logging:ArcaneCore:Format. Reads the environment and
      appsettings.json only (not appsettings.{Environment}.json, .conf files or the command line), unlike the daemon.
      -LiveProfile defaults to ARCANECORE_LIVE_PROFILE (or ARCANECORE_LIVE_ROOT).
    #>
    param([string]$LiveProfile)
    $format = ConvertTo-LiveLogFormat (Get-OpsEnv 'Logging__ArcaneCore__Format')
    if ($format) { return $format }
    $arcane = Read-LiveLogProfileSection -LiveProfile $LiveProfile
    if ($null -ne $arcane -and $arcane.PSObject.Properties['Format']) { return ConvertTo-LiveLogFormat $arcane.Format }
    return $null
}

function Get-LiveLogFormat {
    <#
    .SYNOPSIS
      The log format the live daemons write: 'Json' or 'Text'. The daemons write one log file in one format.
    .DESCRIPTION
      Same order as the daemons: Get-LiveLogFormatSetting (environment Logging__ArcaneCore__Format, else the profile's appsettings.json),
      else the legacy inference when Format is unset (only Json enabled => Json; only File enabled => Text; both enabled => Json),
      else Json. Enabled flags come from the environment (Logging__ArcaneCore__File__Enabled / Json__Enabled) first, then the profile.
      -LiveProfile defaults to ARCANECORE_LIVE_PROFILE (or ARCANECORE_LIVE_ROOT); without a readable profile only the environment counts.
      Unlike the daemon this reads the environment and the profile's appsettings.json only: appsettings.{Environment}.json, .conf files
      and command-line arguments are not consulted, so a Format set there is not seen here.
    #>
    param([string]$LiveProfile)
    function ConvertTo-LogFlag([object]$Value) {
        if ($null -eq $Value) { return $null }
        $parsed = $false
        if ([bool]::TryParse(([string]$Value).Trim(), [ref]$parsed)) { return $parsed }
        return $null
    }
    $format = Get-LiveLogFormatSetting -LiveProfile $LiveProfile
    if ($format) { return $format }

    $arcane = Read-LiveLogProfileSection -LiveProfile $LiveProfile
    function Get-ProfileValue([string]$Section, [string]$Name) {
        if ($null -eq $arcane) { return $null }
        if (-not $arcane.PSObject.Properties[$Section] -or $null -eq $arcane.$Section) { return $null }
        $node = $arcane.$Section
        if ($node.PSObject.Properties[$Name]) { return $node.$Name }
        return $null
    }
    $fileOn = ConvertTo-LogFlag (Get-OpsEnv 'Logging__ArcaneCore__File__Enabled')
    if ($null -eq $fileOn) { $fileOn = ConvertTo-LogFlag (Get-ProfileValue 'File' 'Enabled') }
    $jsonOn = ConvertTo-LogFlag (Get-OpsEnv 'Logging__ArcaneCore__Json__Enabled')
    if ($null -eq $jsonOn) { $jsonOn = ConvertTo-LogFlag (Get-ProfileValue 'Json' 'Enabled') }
    if ($fileOn -eq $true -and $jsonOn -ne $true) { return 'Text' }
    return 'Json'
}

function Get-LiveDaemonLogPath {
    <#
    .SYNOPSIS
      The log a live daemon writes: <LiveHome>\logs\<role>.debug.jsonl (Json) or .log (Text). launch-daemon.ps1 sets these per role.
      -Kind Auto (the default) follows the configured format (Get-LiveLogFormat); Text or Json forces one.
      -LogStem names the file for a process that is neither role (the cluster supervisor: 'supervisor', a gateway child: 'gateway'); it
      replaces the role in <stem>.debug.<ext>.
    #>
    param(
        [Parameter(Mandatory)][ValidateSet('Realm', 'World')][string]$Role,
        [Parameter(Mandatory)][string]$LiveHome,
        [ValidateSet('Auto', 'Text', 'Json')][string]$Kind = 'Auto',
        [string]$LiveProfile,
        [string]$LogStem
    )
    if ($Kind -eq 'Auto') { $Kind = Get-LiveLogFormat -LiveProfile $LiveProfile }
    $extension = if ($Kind -eq 'Json') { 'jsonl' } else { 'log' }
    $stem = if ($LogStem) { $LogStem } else { $Role.ToLowerInvariant() }
    return Join-Path (Join-Path $LiveHome 'logs') "$stem.debug.$extension"
}

function Set-LiveDaemonLogEnvironment {
    <#
    .SYNOPSIS
      Points this process's log file configuration (environment configuration, which overrides the profile's appsettings.json)
      at the role's own file, for the daemon started next from this process.
    .DESCRIPTION
      Both daemons start in the same profile directory and read the same appsettings.json. With one path for both, the daemon that starts
      second finds the file owned (a log file has one writer) and writes <name>.<process>.<ext> instead; redeploy.ps1 starts the Realm
      first, so the World would lose world.debug.* to it. The extension follows the effective format (Get-LiveLogFormat).
      Format set (Get-LiveLogFormatSetting): Logging__ArcaneCore__File__Path is the role path and Logging__ArcaneCore__Json__Path is
      removed from this process's environment, so the daemon has no deprecated key to warn about.
      Format unset (legacy): File__Path is <role>.debug.log, and Json__Path is <role>.debug.jsonl only when Json:Enabled is visibly true
      (environment, else the profile's appsettings.json; otherwise Json__Path is removed). They are distinct on purpose: the
      legacy resolution picks the right one for the enabled section, and an older daemon binary (a snapshot rollback) that rejected
      File:Path equal to Json:Path with both enabled still starts. Neither Format nor Enabled is set: whether logging is on, and in
      which format, stays the profile's (or the operator's environment) choice. -LogStem gives a process that is not a plain role its own file
      name (start-cluster.ps1: the supervisor writes supervisor.debug.*, each child its own).
    #>
    param([Parameter(Mandatory)][ValidateSet('Realm', 'World')][string]$Role, [Parameter(Mandatory)][string]$LiveHome, [string]$LiveProfile, [string]$LogStem)
    if (Get-LiveLogFormatSetting -LiveProfile $LiveProfile) {
        $env:Logging__ArcaneCore__File__Path = Get-LiveDaemonLogPath -Role $Role -LiveHome $LiveHome -Kind Auto -LiveProfile $LiveProfile -LogStem $LogStem
        Remove-Item Env:Logging__ArcaneCore__Json__Path -ErrorAction SilentlyContinue
    } else {
        $env:Logging__ArcaneCore__File__Path = Get-LiveDaemonLogPath -Role $Role -LiveHome $LiveHome -Kind Text -LogStem $LogStem
        # Json__Path only when the legacy Json section is visibly on: it is the only case that needs a per-role JSON path, and a Format
        # set in a layer this script cannot see (appsettings.{Environment}.json, .conf) would otherwise get a deprecated key to warn about.
        $jsonOn = Get-OpsEnv 'Logging__ArcaneCore__Json__Enabled'
        if ($null -eq $jsonOn) {
            $arcane = Read-LiveLogProfileSection -LiveProfile $LiveProfile
            if ($null -ne $arcane -and $arcane.PSObject.Properties['Json'] -and $null -ne $arcane.Json -and $arcane.Json.PSObject.Properties['Enabled']) { $jsonOn = $arcane.Json.Enabled }
        }
        $parsed = $false
        if ($null -ne $jsonOn -and [bool]::TryParse(([string]$jsonOn).Trim(), [ref]$parsed) -and $parsed) {
            $env:Logging__ArcaneCore__Json__Path = Get-LiveDaemonLogPath -Role $Role -LiveHome $LiveHome -Kind Json -LogStem $LogStem
        } else {
            Remove-Item Env:Logging__ArcaneCore__Json__Path -ErrorAction SilentlyContinue
        }
    }
}

function Get-LiveLogFallback {
    <#
    .SYNOPSIS
      The fallback files (<role>.debug.<process>[.<pid>].<ext>, either extension, and their rolled segments) written at or after -SinceUtc. A writer
      creates one only when another writer already owns the role's log, so any result means the role's log is not the role's alone.
    #>
    param(
        [Parameter(Mandatory)][ValidateSet('Realm', 'World')][string]$Role,
        [Parameter(Mandatory)][string]$LiveHome,
        [Parameter(Mandatory)][DateTime]$SinceUtc
    )
    $stem = "$($Role.ToLowerInvariant()).debug"
    $own = '^' + [regex]::Escape($stem) + '\.(log|jsonl)$'
    $since = $SinceUtc.ToUniversalTime()
    Get-ChildItem -LiteralPath (Join-Path $LiveHome 'logs') -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name.StartsWith("$stem.", [StringComparison]::Ordinal) -and $_.Name -notmatch $own -and $_.Extension -ne '.lock' -and $_.LastWriteTimeUtc -ge $since }
}
