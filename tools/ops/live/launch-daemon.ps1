# Starts one live daemon (Realm or World) in this console, the way the local live server runs it: a Debug build from <LiveHome>\bin,
# started in the profile directory, with QuickEdit off (a stray click in the console would otherwise freeze its output and the daemon).
#   pwsh -NoExit -File tools/ops/live/launch-daemon.ps1 -Role World [-LiveHome <dir>] [-Profile <dir>]
# Uses ARCANECORE_LIVE_HOME and ARCANECORE_LIVE_PROFILE when the parameters are omitted (docs/ops/tooling.md).
param(
    [Parameter(Mandatory)][ValidateSet('Realm', 'World')][string]$Role,
    [string]$LiveHome,
    [Alias('Profile')][string]$LiveProfile,
    # World only: the machine memory load (percent) at which the GC turns aggressive. See below.
    [ValidateRange(50, 99)][int]$GCHighMemPercent = 97
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'LiveEnv.ps1')
$LiveHome = Resolve-LiveHome $LiveHome
$LiveProfile = Resolve-LiveProfile $LiveProfile
$exe = Join-Path $LiveHome "bin\ArcaneCore.$Role.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "$exe not found" }

try { $Host.UI.RawUI.WindowTitle = "ArcaneCore.$Role (DEBUG live)" } catch { }
Add-Type -Name ConsoleModeHelper -Namespace Win32 -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr GetStdHandle(int nStdHandle);
[DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
[DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
public static void DisableQuickEdit() {
    IntPtr h = GetStdHandle(-10);
    if (GetConsoleMode(h, out uint mode)) {
        SetConsoleMode(h, (mode & ~0x0040u) | 0x0080u);
    }
}
'@ -ErrorAction SilentlyContinue
try { [Win32.ConsoleModeHelper]::DisableQuickEdit() } catch { }

if (-not $env:DOTNET_ROOT) { $env:DOTNET_ROOT = Join-Path $env:USERPROFILE '.dotnet' }
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
$env:DOTNET_ENVIRONMENT = 'Development'
if ($Role -eq 'World') {
    # The GC turns aggressive (blocking compacting gen2, 2-15 s ticks) when the MACHINE's memory load passes its high-memory threshold
    # (default 90%). On a shared box the load comes from builds and tests, not the world, so compacting the world cannot relieve it.
    # Verify: the MemoryPressure log line prints the GC's high-load threshold.
    $env:DOTNET_GCHighMemPercent = '0x{0:X}' -f $GCHighMemPercent
}
# A launcher's NO_COLOR (agent shells set it) would turn off the console logger's level colours.
Remove-Item Env:NO_COLOR -ErrorAction SilentlyContinue
Set-Location -LiteralPath $LiveProfile
& $exe
