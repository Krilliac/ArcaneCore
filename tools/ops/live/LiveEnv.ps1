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
