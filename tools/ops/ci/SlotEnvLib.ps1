# Client-data test environment resolution for run-in-slot.ps1. Dot-source this file; it defines functions only.
# Self-test: pwsh -NoProfile -File tools/ops/ci/slot-env-selftest.ps1

Set-StrictMode -Version Latest

function Resolve-SlotClientEnv {
    <#
    .SYNOPSIS
      Resolves the client dir and the client-files dir for the tests. Per variable: the passed environment value, else the key of the
      first readable slot-env.local.json (WorkDir, then LanesRoot) that has it. Never throws for a bad config file.
    .OUTPUTS
      Hashtable: ClientDir, ClientFilesDir (string or $null) and Messages (string[], one line each, to be printed by the caller).
    #>
    param(
        [string]$EnvClientDir, [string]$EnvClientFilesDir,
        [Parameter(Mandatory)][string]$WorkDir, [Parameter(Mandatory)][string]$LanesRoot
    )
    $messages = New-Object System.Collections.Generic.List[string]
    $clientDir = if ([string]::IsNullOrWhiteSpace($EnvClientDir)) { $null } else { $EnvClientDir.Trim() }
    $clientFilesDir = if ([string]::IsNullOrWhiteSpace($EnvClientFilesDir)) { $null } else { $EnvClientFilesDir.Trim() }
    $filesSeen = 0
    if (-not $clientDir -or -not $clientFilesDir) {
        foreach ($dir in $WorkDir, $LanesRoot) {
            $cfg = Join-Path $dir 'slot-env.local.json'
            if (-not (Test-Path -LiteralPath $cfg -PathType Leaf)) { continue }
            $j = $null
            try {
                $j = Get-Content -LiteralPath $cfg -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
                if ($j -isnot [System.Management.Automation.PSCustomObject]) { throw 'not a JSON object' }
            } catch {
                $messages.Add("ignoring unreadable $cfg ($($_.Exception.Message))")
                continue
            }
            $filesSeen++
            foreach ($key in 'clientDir', 'clientFilesDir') {
                $prop = $j.PSObject.Properties[$key]
                if ($null -eq $prop) { continue }
                if ($prop.Value -isnot [string] -or [string]::IsNullOrWhiteSpace($prop.Value)) {
                    $messages.Add("ignoring $key in $cfg (not a non-empty string)")
                    continue
                }
                if ($key -eq 'clientDir' -and -not $clientDir) { $clientDir = $prop.Value.Trim() }
                if ($key -eq 'clientFilesDir' -and -not $clientFilesDir) { $clientFilesDir = $prop.Value.Trim() }
            }
        }
    }
    $where = if ($filesSeen -gt 0) { 'unset and not set in slot-env.local.json' } else { 'unset and no readable slot-env.local.json found' }
    if (-not $clientDir) { $messages.Add("client-data tests will skip: ARCANECORE_TEST_CLIENT_DIR $where (docs/ops/tooling.md)") }
    elseif (-not (Test-Path -LiteralPath $clientDir)) { $messages.Add("warning: client dir '$clientDir' does not exist; its tests will skip") }
    if (-not $clientFilesDir) { $messages.Add("client-data tests will skip: ARCANECORE_TEST_CLIENT_FILES_DIR $where (docs/ops/tooling.md)") }
    elseif (-not (Test-Path -LiteralPath $clientFilesDir)) { $messages.Add("warning: client files dir '$clientFilesDir' does not exist; its tests will skip") }
    return @{ ClientDir = $clientDir; ClientFilesDir = $clientFilesDir; Messages = $messages.ToArray() }
}

function ConvertTo-PsSingleQuoted {
    <#
    .SYNOPSIS
      The value as a PowerShell single-quoted string literal. Every character PowerShell reads as a single quote is doubled: the ASCII
      apostrophe and the typographic U+2018-U+201B, which are legal in Windows paths.
    #>
    param([Parameter(Mandatory)][string]$Value)
    return "'" + [System.Management.Automation.Language.CodeGeneration]::EscapeSingleQuotedStringContent($Value) + "'"
}
