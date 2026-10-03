<#
.SYNOPSIS
  Start the world daemon under `dotnet watch` so edits to the C# source are applied to the
  running process (docs/areas/code-hot-reload.md). Development / Staging runners only.

.DESCRIPTION
  * Refuses to run when the environment is Production (or anything but Development / Staging).
  * Sets World__HotCode__Enabled=true for this process only; nothing is written to a config file.
  * Builds Debug: hot reload cannot patch an optimized (Release) build.
  * Interactive by default: when an edit cannot be applied live (a signature change, a rename ...)
    dotnet watch asks before it restarts the server. With -NonInteractive it restarts on its own
    (measured on SDK 10.0.401: the restart ran the host shutdown, "World saved and stopped", but the new process
    drops every connected client). scripts/dev-runner.ps1 is the full local test environment built on this.

.PARAMETER NonInteractive
  Do not prompt; restart the server whenever an edit needs a restart.
.PARAMETER AuditLog
  Optional path of an append-only audit file for hot-code decisions (World:HotCode:AuditLogPath).
.PARAMETER Project
  Project to run (default src/ArcaneCore.World).
#>
param(
    [switch]$NonInteractive,
    [string]$AuditLog = '',
    [string]$Project = 'src/ArcaneCore.World'
)

$ErrorActionPreference = 'Stop'

$environmentName = if ($env:DOTNET_ENVIRONMENT) { $env:DOTNET_ENVIRONMENT } elseif ($env:ASPNETCORE_ENVIRONMENT) { $env:ASPNETCORE_ENVIRONMENT } else { 'Development' }
if ($environmentName -notin @('Development', 'Staging')) {
    [Console]::Error.WriteLine("hot-runner refuses to start: the environment is '$environmentName'. Code hot reload runs only in Development or Staging.")
    exit 2
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    [Console]::Error.WriteLine('hot-runner needs the .NET SDK (dotnet watch); it is not on PATH.')
    exit 3
}

$repo = Split-Path -Parent $PSScriptRoot
$env:DOTNET_ENVIRONMENT = $environmentName
$env:World__HotCode__Enabled = 'true'
if ($AuditLog) { $env:World__HotCode__AuditLogPath = $AuditLog }

$watchArgs = @('watch', '--project', (Join-Path $repo $Project), '-c', 'Debug', '--no-launch-profile')
if ($NonInteractive) { $watchArgs += '--non-interactive' }

Write-Host "hot-runner: $environmentName, Debug, hot reload ON ($(if ($NonInteractive) { 'non-interactive: restarts on rude edits' } else { 'interactive: asks before restarting' }))"
& dotnet @watchArgs
exit $LASTEXITCODE
