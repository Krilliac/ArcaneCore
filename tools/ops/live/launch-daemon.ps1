# Starts one live daemon (Realm or World) in this console, the way the local live server runs it: a Debug build from <LiveHome>\bin,
# started in the profile directory, with QuickEdit off (a stray click in the console would otherwise freeze its output and the daemon).
#   pwsh -NoExit -File tools/ops/live/launch-daemon.ps1 -Role World [-LiveHome <dir>] [-Profile <dir>]
# Uses ARCANECORE_LIVE_HOME and ARCANECORE_LIVE_PROFILE when the parameters are omitted (docs/ops/tooling.md).
# The window title names the build in the live home's bin directory: "ArcaneCore.World (Debug, live)" (Debug, Release, Mixed or unknown; LiveLaunch.ps1).
# -ClusterSupervisor (-Role World only; start-cluster.ps1 passes it) runs the World executable as the cluster host supervisor: the title
# says so and the process writes logs\supervisor.debug.* instead of the World's file, which its child world owns.
param(
    [Parameter(Mandatory)][ValidateSet('Realm', 'World')][string]$Role,
    [switch]$ClusterSupervisor,
    [string]$LiveHome,
    [Alias('Profile')][string]$LiveProfile,
    # The machine memory load (percent) at which the GC turns aggressive, for either role. See below.
    [ValidateRange(50, 99)][int]$GCHighMemPercent = 97,
    # Max: crash dump, hang stacks, counters CSV and the repo's diagnostic options at their most informative safe values
    # (docs/ops/live-diagnostics.md); Standard adds nothing. A variable already set in the environment is kept.
    [ValidateSet('Standard', 'Max')][string]$Diagnostics = 'Max',
    [ValidateSet('Full', 'Heap')][string]$DumpType = 'Full',
    [ValidateRange(10, 86400)][int]$HangStackIntervalSeconds = 60,
    # Disk caps: the oldest files beyond a cap are deleted at launch and every counters segment.
    [ValidateRange(1, 10000)][int]$MaxDumpFiles = 3, [ValidateRange(1, 10000)][int]$MaxDumpGB = 30,
    [ValidateRange(1, 100000)][int]$MaxHangStackFiles = 500, [ValidateRange(1, 1048576)][int]$MaxHangStackMB = 200,
    [ValidateRange(1, 10000)][int]$MaxCountersFiles = 48, [ValidateRange(1, 1048576)][int]$MaxCountersMB = 2048
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'LiveEnv.ps1')
. (Join-Path $PSScriptRoot 'LiveDiagnostics.ps1')
. (Join-Path $PSScriptRoot 'LiveLaunch.ps1')
if ($ClusterSupervisor -and $Role -ne 'World') { throw '-ClusterSupervisor runs the World executable: use -Role World' }
$LiveHome = Resolve-LiveHome $LiveHome
$LiveProfile = Resolve-LiveProfile $LiveProfile
$exe = Join-Path $LiveHome "bin\ArcaneCore.$Role.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "$exe not found" }

$buildConfiguration = (Get-LiveBuildConfiguration -BinDir (Join-Path $LiveHome 'bin')).Configuration
$titleName = if ($ClusterSupervisor) { "ArcaneCore.$Role supervisor" } else { "ArcaneCore.$Role" }
try { $Host.UI.RawUI.WindowTitle = Get-LiveWindowTitle -Name $titleName -Configuration $buildConfiguration } catch { }
Add-Type -Name ConsoleModeHelper -Namespace Win32 -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr GetStdHandle(int nStdHandle);
[DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
[DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
[DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetConsoleCtrlHandler(IntPtr handlerRoutine, bool add);
public static void DisableQuickEdit() {
    IntPtr h = GetStdHandle(-10);
    if (GetConsoleMode(h, out uint mode)) {
        SetConsoleMode(h, (mode & ~0x0040u) | 0x0080u);
    }
}
'@ -ErrorAction SilentlyContinue
try { [Win32.ConsoleModeHelper]::DisableQuickEdit() } catch { }
# A process started from a tree that ignores Ctrl-C (a background shell, a service wrapper) inherits that flag, and the daemon would then
# never see redeploy.ps1's Ctrl-C (2026-10-17: the World ran on for minutes after it). Clearing it here makes the daemon inherit a
# working Ctrl-C.
try { [Win32.ConsoleModeHelper]::SetConsoleCtrlHandler([IntPtr]::Zero, $false) | Out-Null } catch { }

if (-not $env:DOTNET_ROOT) { $env:DOTNET_ROOT = Join-Path $env:USERPROFILE '.dotnet' }
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
$env:DOTNET_ENVIRONMENT = 'Development'
# The live server runs as Development, so the environment alone does not keep the map feed's packet tools (console, inject, send, tap)
# off it. This marker does: the World refuses the tools whenever it is "1", whatever the configuration says (WorldPacketToolsOptions).
$env:ARCANECORE_LIVE_SERVER = '1'
# Each daemon its own log (<LiveHome>\logs\<role>.debug.jsonl or .log by the configured Logging:ArcaneCore:Format): both read the profile's appsettings.json, and a log file has one
# writer, so with one shared path the daemon started second (the World, in redeploy.ps1) would write a fallback name instead.
Set-LiveDaemonLogEnvironment -Role $Role -LiveHome $LiveHome -LiveProfile $LiveProfile -LogStem $(if ($ClusterSupervisor) { 'supervisor' } else { '' })
# The GC turns aggressive (blocking compacting gen2, 2-15 s ticks) when the MACHINE's memory load passes its high-memory threshold
# (default 90%). On a shared box the load comes from builds and tests, not the daemon, so compacting the daemon cannot relieve it.
# Both roles: a Realm left at 90% compacts first and stalls logins (2026-10-25: Realm MemoryPressure at 88% while the World sat at 97%).
# Verify: the MemoryPressure log line prints the GC's high-load threshold.
$env:DOTNET_GCHighMemPercent = '0x{0:X}' -f $GCHighMemPercent
# A launcher's NO_COLOR (agent shells set it) would turn off the console logger's level colours.
Remove-Item Env:NO_COLOR -ErrorAction SilentlyContinue
$diagPlan = Get-LiveDiagnosticsPlan -Role $Role -LiveHome $LiveHome -Level $Diagnostics -DotnetRoot $env:DOTNET_ROOT -DumpType $DumpType -HangStackIntervalSeconds $HangStackIntervalSeconds
[void](Set-LiveDiagnosticsEnvironment -Plan $diagPlan)
foreach ($line in $diagPlan.Active) { Write-Host "[diagnostics] $line" }
foreach ($line in $diagPlan.Warnings) { Write-Warning "[diagnostics] $line" }
if ($Diagnostics -eq 'Max') {
    $rules = Get-DiagnosticsRetentionRules -LiveHome $LiveHome -MaxDumpFiles $MaxDumpFiles -MaxDumpGB $MaxDumpGB -MaxHangStackFiles $MaxHangStackFiles `
        -MaxHangStackMB $MaxHangStackMB -MaxCountersFiles $MaxCountersFiles -MaxCountersMB $MaxCountersMB
    foreach ($removed in Invoke-DiagnosticsRetentionRules -Rules $rules) { Write-Host "[diagnostics] retention removed $removed" }
    # The sidecar (hidden) trims the capture directories each counters segment and collects the counters; it ends when the daemon,
    # a child of this console, ends. Failing to start it must not stop the daemon.
    try {
        # One pre-quoted argument string: Start-Process joins an array with spaces without quoting, which breaks on a path with a space.
        $sidecarParts = @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'diagnostics-sidecar.ps1'), '-Role', $Role, '-LiveHome', $LiveHome, '-ParentPid', $PID,
            '-MaxDumpFiles', $MaxDumpFiles, '-MaxDumpGB', $MaxDumpGB, '-MaxHangStackFiles', $MaxHangStackFiles, '-MaxHangStackMB', $MaxHangStackMB,
            '-MaxCountersFiles', $MaxCountersFiles, '-MaxCountersMB', $MaxCountersMB)
        if ($diagPlan.CountersTool) { $sidecarParts += @('-CountersTool', $diagPlan.CountersTool) }
        $sidecarArgs = ConvertTo-LiveArgumentString $sidecarParts
        # Without the daemon's dump variables: the sidecar and the .NET tools it runs must not dump into the daemon's dump directory.
        $sidecar = Invoke-WithoutDumpEnvironment { Start-Process pwsh -ArgumentList $sidecarArgs -WindowStyle Hidden -PassThru }
        [void]$sidecar.Handle # open the handle now: ExitCode is $null for a process whose handle was never opened
        if ($sidecar.WaitForExit(1000)) {
            Write-Warning "[diagnostics] the sidecar (pid $($sidecar.Id)) exited at once with code $($sidecar.ExitCode): no retention trimming or counters for this run"
        } else {
            Write-Host "[diagnostics] sidecar pid $($sidecar.Id) (ends with the daemon)"
        }
    } catch { Write-Warning "[diagnostics] the sidecar did not start: $($_.Exception.Message)" }
}
Set-Location -LiteralPath $LiveProfile
& $exe
