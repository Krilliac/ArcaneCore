<#
.SYNOPSIS
  Send Ctrl+Break (or Ctrl+C) to the console a dev-runner window lives in, exactly as pressing the keys in that
  window would, so the daemons shut down gracefully (the world saves). Used by `dev-runner.ps1 -Stop`.

.DESCRIPTION
  A console control event can only be sent from a process attached to the target's console, so this runs in its own
  short-lived process: it detaches from its console, attaches to the target window's console, ignores the event
  itself, raises it for every process on that console and detaches again.

.PARAMETER ProcessId
  Any process whose console is the dev-runner window (the window's PowerShell host).
.PARAMETER Signal
  Break (default, CTRL_BREAK_EVENT) or C (CTRL_C_EVENT).
#>
param(
    [Parameter(Mandatory)][int]$ProcessId,
    [ValidateSet('Break', 'C')][string]$Signal = 'Break'
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class DevRunnerConsole
{
    public delegate bool Handler(uint ctrlType);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetConsoleCtrlHandler(Handler handler, bool add);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
}
"@

[void][DevRunnerConsole]::FreeConsole()
if (-not [DevRunnerConsole]::AttachConsole([uint32]$ProcessId)) {
    $code = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
    [Console]::Error.WriteLine("could not attach to the console of process $ProcessId (win32 error $code); it may already have exited")
    exit 1
}

# Keep the delegate referenced for as long as the handler is installed; returning true swallows the event here.
$handler = [DevRunnerConsole+Handler] { param([uint32]$type) $true }
[void][DevRunnerConsole]::SetConsoleCtrlHandler($handler, $true)
$event = if ($Signal -eq 'C') { 0 } else { 1 }
$sent = [DevRunnerConsole]::GenerateConsoleCtrlEvent([uint32]$event, 0)
Start-Sleep -Milliseconds 1500
[void][DevRunnerConsole]::SetConsoleCtrlHandler($handler, $false)
[void][DevRunnerConsole]::FreeConsole()
if (-not $sent) { exit 2 }
exit 0
