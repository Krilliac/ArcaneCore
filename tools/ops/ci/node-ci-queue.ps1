<#
.SYNOPSIS
  Run node-ci.ps1 for several branches one after another, waiting first for any node-ci.ps1 already running. Overlap is
  safe: each run has its own node directory, the tests create randomly named databases, and ServerProbe counts sessions
  per database. Use this queue only when you want runs serialized, e.g. to keep node load low. For parallel runs, launch
  node-ci.ps1 once per ref.
.EXAMPLE
  pwsh tools/ops/ci/node-ci-queue.ps1 -Repo . -Refs origin/claude/w19-a,origin/claude/w19-b
#>
param([Parameter(Mandatory)] [string] $Repo, [Parameter(Mandatory)] [string[]] $Refs)
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'OpsEnv.ps1')
$self = $PID
function Busy { @(Get-CimInstance Win32_Process -Filter "Name='pwsh.exe'" | Where-Object { $_.ProcessId -ne $self -and $_.CommandLine -match 'node-ci\.ps1' -and $_.CommandLine -notmatch 'node-ci-queue' }).Count -gt 0 }
git -C $Repo fetch -q origin
foreach ($ref in $Refs) {
    while (Busy) { Start-Sleep -Seconds 30 }
    Write-Output "===== $ref ($(git -C $Repo rev-parse --short=12 $ref)) $(Get-Date -Format HH:mm)"
    pwsh -NoProfile -File (Join-Path $PSScriptRoot 'node-ci.ps1') -Repo $Repo -Commit $ref
    Write-Output "===== $ref exit=$LASTEXITCODE"
}
