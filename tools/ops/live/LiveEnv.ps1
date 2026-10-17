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

function Get-LiveDaemonLogPath {
    <#
    .SYNOPSIS
      The log a live daemon writes: <LiveHome>\logs\<role>.debug.log (text) or .jsonl (JSON lines). launch-daemon.ps1 sets these per role.
    #>
    param(
        [Parameter(Mandatory)][ValidateSet('Realm', 'World')][string]$Role,
        [Parameter(Mandatory)][string]$LiveHome,
        [ValidateSet('Text', 'Json')][string]$Kind = 'Text'
    )
    $extension = if ($Kind -eq 'Json') { 'jsonl' } else { 'log' }
    return Join-Path (Join-Path $LiveHome 'logs') "$($Role.ToLowerInvariant()).debug.$extension"
}

function Set-LiveDaemonLogEnvironment {
    <#
    .SYNOPSIS
      Points this process's Logging:ArcaneCore:File:Path and Json:Path (environment configuration, which overrides the profile's
      appsettings.json) at the role's own files, for the daemon started next from this process.
    .DESCRIPTION
      Both daemons start in the same profile directory and read the same appsettings.json. With one path for both, the daemon that starts
      second finds the file owned (a log file has one writer) and writes <name>.<process>.<ext> instead; redeploy.ps1 starts the Realm
      first, so the World would lose world.debug.log to it. Whether a sink is on stays the profile's choice (File:Enabled, Json:Enabled).
    #>
    param([Parameter(Mandatory)][ValidateSet('Realm', 'World')][string]$Role, [Parameter(Mandatory)][string]$LiveHome)
    $env:Logging__ArcaneCore__File__Path = Get-LiveDaemonLogPath -Role $Role -LiveHome $LiveHome -Kind Text
    $env:Logging__ArcaneCore__Json__Path = Get-LiveDaemonLogPath -Role $Role -LiveHome $LiveHome -Kind Json
}

function Get-LiveLogFallback {
    <#
    .SYNOPSIS
      The fallback files (<role>.debug.<process>[.<pid>].<ext> and their rolled segments) written at or after -SinceUtc. A writer
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
