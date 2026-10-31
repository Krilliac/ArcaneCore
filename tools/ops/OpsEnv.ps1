# Environment lookups shared by the PowerShell operator tools in tools/ops/**. Dot-source this file; it defines functions only.
# Every host-specific location comes from an environment variable, so nothing in the repository names a machine or a user. A tool that
# needs a value which is not set stops with a message that names the variable. Variables: ARCANECORE_LIVE_PROFILE,
# ARCANECORE_LANES_ROOT, ARCANECORE_DATA_ROOT, ARCANECORE_REFS_ROOT, ARCANECORE_DEPLOY_ROOT (docs/ops/tooling.md).

Set-StrictMode -Version Latest

function Get-OpsEnv {
    <#
    .SYNOPSIS
      The value of an environment variable, or $null when it is unset or blank.
    #>
    param([Parameter(Mandatory)][string]$Name)
    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($value)) { return $null }
    return $value.Trim()
}

function Get-OpsRequiredEnv {
    <#
    .SYNOPSIS
      The value of an environment variable; throws a message naming the variable and what it is for when it is unset.
      -Parameter is the name of the script parameter that supplies the same value; the message then names it ("or pass -LanesRoot").
    #>
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Purpose, [string]$Parameter)
    $value = Get-OpsEnv $Name
    if ($null -eq $value) {
        $instead = if ([string]::IsNullOrWhiteSpace($Parameter)) { 'Set it' } else { "Set it, or pass -$Parameter" }
        throw "$Name is not set ($Purpose). $instead; see docs/ops/tooling.md."
    }
    return $value
}
