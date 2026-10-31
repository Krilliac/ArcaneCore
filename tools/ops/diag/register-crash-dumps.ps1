<#
.SYNOPSIS
  Opt-in, run once from an ELEVATED PowerShell: makes Windows write a crash dump for ArcaneCore.World.exe, ArcaneCore.Realm.exe and
  ArcaneCore.Gateway.exe through Windows Error Reporting (WER) LocalDumps. Changes the machine registry (HKLM) unless -Plan / -WhatIf /
  -Status. Never elevates itself.
.DESCRIPTION
  Why: the in-process handlers and the runtime's DOTNET_DbgEnableMiniDump (which launch-daemon.ps1 sets for -Diagnostics Max, and which
  must be in the environment at process start) do not cover every crash. A stack overflow, a fatal runtime error and a native access
  violation can end the process before a managed handler or the runtime's own dump path runs. WER LocalDumps is applied by Windows to
  the crashed process, so it needs nothing from the process or its start-up environment.

  What it writes, per executable, under
    HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\<exe>
      DumpFolder  REG_EXPAND_SZ  where the dump goes (-DumpFolder)
      DumpType    REG_DWORD      1 = mini dump, 2 = full dump (-DumpType Mini | Full, default Full)
      DumpCount   REG_DWORD      the most dump files kept in the folder; the oldest is replaced when it is exceeded (-DumpCount, default 5)
      ArcaneCoreRegisteredBy  REG_SZ  a marker of this script (WER ignores it): -Unregister removes only keys that carry it
  The per-application key overrides the global LocalDumps values for that executable only. Value names, types, WER's own defaults
  (DumpCount 10, DumpType 1; this script always sets explicit values) and the per-application key are documented in Microsoft's
  "Collecting User-Mode Dumps" (the feature is off by default, needs administrator rights, and is not used by applications that do
  their own crash reporting):
  https://learn.microsoft.com/en-us/windows/win32/wer/collecting-user-mode-dumps

  THE PARENT KEY: the LocalDumps key itself is what switches the feature on. While it is absent, no program gets a local dump. If this
  script created it, WER's GLOBAL defaults would apply to every program that crashes on the machine (not only ArcaneCore). So an absent
  parent key is never created unless you pass -CreateParentKey; -Plan says which case applies. A parent key created by -CreateParentKey
  carries the marker too, and -Unregister removes it only when it has the marker and nothing else is left in it (no other subkey, no
  value other than the marker). A parent key that already existed is never modified or removed. -Status shows its global values.

  A key that already exists for one of the executables and was not created by this script (no marker) is never changed: the script
  stops before writing anything and names it.

  FAILURE: the marker is the first value written to each new key, so a half-written key is always removable by -Unregister. If a write
  fails, the script removes the keys, the parent key and the dump folder that THIS run created (reporting which executables had been
  completed and anything it could not remove), then rethrows. A key it only updated is not rolled back.

  The dump folder defaults to <live home>\dumps\wer, a folder of its own: the live daemons' diagnostics-sidecar.ps1 trims *.dmp files
  directly in <live home>\dumps (not in subfolders), and WER's DumpCount trims its own folder, so neither deletes the other's dumps.
  The live home is -LiveHome, else ARCANECORE_LIVE_HOME; with neither a folder nor the variable the script stops. The folder is created
  when missing. The process that crashes writes the dump, so the folder must be writable by the account the daemons run under; a folder
  under the live home inherits that home's permissions. A full dump of the World is several GB (the whole heap): DumpCount 5 can mean
  tens of GB.

  SECURITY: a dump is a copy of the process memory. It holds session keys, password-derived material, the gateway link secret and
  everything else in memory at the crash. Keep the folder access-controlled and never attach a dump to an issue or a public place.

  TWO DUMPS (UNVERIFIED): with LocalDumps registered and the launcher's DOTNET_DbgEnableMiniDump on (-Diagnostics Max), a managed crash
  MAY produce two dumps: the runtime's createdump file (<role>-<pid>-<time>.dmp, in <live home>\dumps) and WER's (<exe>.<pid>.dmp, in
  the WER folder). Whether both are written for one managed crash, and in which order, has not been observed. Provisional advice, to
  be confirmed by the recipe below: keep the createdump one for a managed crash (named by role); keep WER's for a stack overflow, a
  native access violation or any crash where createdump left nothing; delete the other. To get only WER's, start the daemons with
  DOTNET_DbgEnableMiniDump=0 in the launching environment (a variable that is already set is never replaced by the launcher;
  docs/ops/live-diagnostics.md). LiveDiagnostics behaviour is unchanged by this script.

  VERIFY IT ONCE (recipe, on a scratch machine or with a throwaway program, never a live daemon): build a console program Crash.exe with
  three modes: Environment.FailFast("x"); unbounded recursion (stack overflow); an unhandled exception thrown on a new worker thread.
  Register it: register-crash-dumps.ps1 -Executables Crash.exe -DumpFolder <wer dir> -DumpType Mini (add -CreateParentKey only if -Plan
  says the parent is absent). For each mode run it twice from a fresh shell: with DOTNET_DbgEnableMiniDump=0, and with
  DOTNET_DbgEnableMiniDump=1 plus DOTNET_DbgMiniDumpName=<other dir>\crash-%p.dmp. Count the files in each directory after each run:
  that shows which mechanism writes for which crash and whether one managed crash gives two dumps. Then -Unregister.

  -Unregister removes only the keys this script created (marker present) for -Executables, and the parent key under the conditions
  above; the dump folder and its files are left alone. A key without the marker is skipped and reported.
.PARAMETER DumpFolder
  Folder for the dumps (REG_EXPAND_SZ: environment variables such as %LOCALAPPDATA% are allowed and are expanded by Windows). Absolute only.
  Default <live home>\dumps\wer.
.PARAMETER LiveHome
  The live install root for the default folder; default ARCANECORE_LIVE_HOME (docs/ops/tooling.md).
.PARAMETER DumpType
  Mini (DumpType 1: stacks and referenced memory, small) or Full (DumpType 2: all process memory, large). Default Full.
.PARAMETER DumpCount
  The most dump files WER keeps in the folder, 1 to 1000. Default 5.
.PARAMETER Executables
  Image names to register, default ArcaneCore.World.exe, ArcaneCore.Realm.exe, ArcaneCore.Gateway.exe. A bare file name ending in .exe.
.PARAMETER CreateParentKey
  Allow creating the LocalDumps parent key when it is absent (see THE PARENT KEY). Without it an absent parent key is a refusal.
.PARAMETER Unregister
  Remove the keys this script created for -Executables (and the parent key if it qualifies).
.PARAMETER Status
  Read-only: show the parent key's global values and the LocalDumps values for -Executables, and whether this script created them.
  Needs no elevation.
.PARAMETER Plan
  Print the exact keys and values (or, with -Unregister, the keys that would be removed) and change nothing, not even the dump folder.
  -WhatIf is the same. Needs no elevation.
.EXAMPLE
  pwsh -NoProfile -File tools/ops/diag/register-crash-dumps.ps1 -Plan
.EXAMPLE
  # from an elevated PowerShell:
  pwsh -NoProfile -File tools/ops/diag/register-crash-dumps.ps1
.EXAMPLE
  pwsh -NoProfile -File tools/ops/diag/register-crash-dumps.ps1 -Status
.EXAMPLE
  # from an elevated PowerShell:
  pwsh -NoProfile -File tools/ops/diag/register-crash-dumps.ps1 -Unregister
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$DumpFolder,
    [string]$LiveHome,
    [ValidateSet('Mini', 'Full')][string]$DumpType = 'Full',
    [ValidateRange(1, 1000)][int]$DumpCount = 5,
    [string[]]$Executables = @('ArcaneCore.World.exe', 'ArcaneCore.Realm.exe', 'ArcaneCore.Gateway.exe'),
    [switch]$CreateParentKey,
    [switch]$Unregister,
    [switch]$Status,
    [switch]$Plan
)
$ErrorActionPreference = 'Stop'
# -WhatIf is the same as -Plan; the preference itself must not reach the cmdlets called below (they would skip their work silently).
$planOnly = $Plan -or $WhatIfPreference
$WhatIfPreference = $false
. (Join-Path (Join-Path (Split-Path -Parent $PSScriptRoot) 'live') 'LiveEnv.ps1')
. (Join-Path (Join-Path (Split-Path -Parent $PSScriptRoot) 'live') 'LiveDiagnostics.ps1')

$localDumpsKey = 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps'
$markerName = 'ArcaneCoreRegisteredBy'
$markerValue = 'tools/ops/diag/register-crash-dumps.ps1'

function Test-CrashDumpElevated {
    # Reads the process token only. The tests replace it through Test-CrashDumpElevatedShim, honoured only when the process was started
    # with ARCANECORE_TEST_CRASHDUMPS_SHIM=1 (an operator's shell does not have it).
    if ((Get-OpsEnv 'ARCANECORE_TEST_CRASHDUMPS_SHIM') -eq '1' -and (Get-Command Test-CrashDumpElevatedShim -ErrorAction SilentlyContinue)) {
        return [bool](Test-CrashDumpElevatedShim)
    }
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { return $false }
    $principal = [System.Security.Principal.WindowsPrincipal]::new([System.Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-KeyValues {
    # The values of a key (name -> value) without the provider's own properties (PSPath, PSParentPath, PSChildName, PSDrive, PSProvider); $null when the key does not exist.
    param([Parameter(Mandatory)][string]$Key)
    if (-not (Test-Path -LiteralPath $Key)) { return $null }
    $values = [ordered]@{}
    $item = Get-ItemProperty -LiteralPath $Key
    foreach ($p in $item.PSObject.Properties) {
        if ($p.Name -cnotin 'PSPath', 'PSParentPath', 'PSChildName', 'PSDrive', 'PSProvider') { $values[$p.Name] = $p.Value }
    }
    return $values
}

function Get-DumpKeyState {
    # The registry state of one executable's key: Exists, Ours (marker present) and Values.
    param([Parameter(Mandatory)][string]$Executable)
    $key = "$localDumpsKey\$Executable"
    $values = Get-KeyValues $key
    $exists = $null -ne $values
    if (-not $exists) { $values = [ordered]@{} }
    return [pscustomobject]@{ Executable = $Executable; Key = $key; Exists = $exists; Ours = ($exists -and $values.Contains($markerName) -and $values[$markerName] -eq $markerValue); Values = $values }
}

function Get-ParentState {
    # The LocalDumps key: Exists, Ours (marker), Values and SubKeys (child key names).
    $values = Get-KeyValues $localDumpsKey
    $exists = $null -ne $values
    if (-not $exists) { $values = [ordered]@{} }
    $subKeys = @(if ($exists) { Get-ChildItem -LiteralPath $localDumpsKey | ForEach-Object { $_.PSChildName } })
    return [pscustomobject]@{ Exists = $exists; Ours = ($exists -and $values.Contains($markerName) -and $values[$markerName] -eq $markerValue); Values = $values; SubKeys = $subKeys }
}

function Get-ParentUnregisterFate {
    # What -Unregister does with the parent key once the child keys named in -Removed are gone: Absent, Remove, LeaveNotOurs or LeaveInUse.
    param([Parameter(Mandatory)]$Parent, [string[]]$Removed = @())
    if (-not $Parent.Exists) { return 'Absent' }
    if (-not $Parent.Ours) { return 'LeaveNotOurs' }
    $remaining = @($Parent.SubKeys | Where-Object { $name = $_; -not ($Removed | Where-Object { $_ -ieq $name }) })
    $otherValues = @($Parent.Values.Keys | Where-Object { $_ -ne $markerName })
    if ($remaining.Count -eq 0 -and $otherValues.Count -eq 0) { return 'Remove' }
    return 'LeaveInUse'
}

function Format-DumpTypeValue([object]$Value) {
    switch ([string]$Value) { '1' { '1 (Mini)' } '2' { '2 (Full)' } '0' { '0 (Custom)' } default { [string]$Value } }
}

# ---- validate everything before anything is touched ----------------------------------------------------------------------------------
if ($Status -and $Unregister) { throw '-Status and -Unregister cannot be combined: -Status only reads.' }
if ($CreateParentKey -and ($Status -or $Unregister)) { throw '-CreateParentKey only applies when registering.' }
$images = [System.Collections.Generic.List[string]]::new()
foreach ($exe in $Executables) {
    if ($exe -notmatch '^[A-Za-z0-9_.\-]+\.exe$') { throw "'$exe' is not a bare executable file name ending in .exe (letters, digits, '_', '.', '-'): it names a registry key." }
    if (-not ($images | Where-Object { $_ -ieq $exe })) { $images.Add($exe) }
}
if ($images.Count -eq 0) { throw '-Executables is empty.' }

$writes = -not $Status -and -not $Unregister
$folder = $null
if ($writes) {
    if ($DumpFolder) {
        $folder = $DumpFolder.Trim()
    } else {
        # Resolve-LiveHome stops with a message that names ARCANECORE_LIVE_HOME when neither -LiveHome nor the variable is given.
        $folder = Join-Path (Get-LiveDiagnosticsDirectories -LiveHome (Resolve-LiveHome $LiveHome)).Dumps 'wer'
    }
    if ([string]::IsNullOrWhiteSpace($folder)) { throw '-DumpFolder is empty. Pass -DumpFolder, -LiveHome or set ARCANECORE_LIVE_HOME.' }
    if (-not ($folder.StartsWith('%') -or [IO.Path]::IsPathRooted($folder))) { throw "-DumpFolder '$folder' is not an absolute path (an environment variable such as %LOCALAPPDATA% may start it): WER does not resolve relative folders." }
}
$typeNumber = if ($DumpType -eq 'Full') { 2 } else { 1 }

# ---- -Status -------------------------------------------------------------------------------------------------------------------------
if ($Status) {
    $parent = Get-ParentState
    Write-Host 'STATUS register-crash-dumps (read-only)'
    Write-Host "  parent key: $localDumpsKey ($(if ($parent.Exists) { if ($parent.Ours) { 'present, created by this script' } else { 'present' } } else { 'absent: LocalDumps is not configured, no program gets a local dump' }))"
    foreach ($name in $parent.Values.Keys) {
        if ($name -ne $markerName) { Write-Host ('      global {0,-15} {1}' -f $name, $(if ($name -eq 'DumpType') { Format-DumpTypeValue $parent.Values[$name] } else { [string]$parent.Values[$name] })) }
    }
    if ($parent.Exists) { Write-Host "      subkeys: $(if ($parent.SubKeys.Count -gt 0) { $parent.SubKeys -join ', ' } else { '(none)' })" }
    foreach ($exe in $images) {
        $state = Get-DumpKeyState $exe
        if (-not $state.Exists) { Write-Host "  $exe : not registered"; continue }
        Write-Host "  $exe : $(if ($state.Ours) { 'registered by this script' } else { 'present, NOT created by this script' })"
        foreach ($name in 'DumpFolder', 'DumpType', 'DumpCount') {
            $text = if (-not $state.Values.Contains($name)) { '(not set: the global or default value applies)' } elseif ($name -eq 'DumpType') { Format-DumpTypeValue $state.Values[$name] } else { [string]$state.Values[$name] }
            Write-Host ('      {0,-11} {1}' -f $name, $text)
        }
    }
    return
}

# ---- plan / apply --------------------------------------------------------------------------------------------------------------------
$parent = Get-ParentState
$states = @($images | ForEach-Object { Get-DumpKeyState $_ })
$foreign = @($states | Where-Object { $_.Exists -and -not $_.Ours })
$parentRefused = -not $Unregister -and -not $parent.Exists -and -not $CreateParentKey

if ($planOnly) {
    Write-Host "PLAN register-crash-dumps$(if ($Unregister) { ' -Unregister' }) (nothing was changed)"
}
if ($Unregister) {
    $removedNames = @($states | Where-Object { $_.Ours } | ForEach-Object { $_.Executable })
    $fate = Get-ParentUnregisterFate -Parent $parent -Removed $removedNames
    if ($planOnly) {
        foreach ($s in $states) {
            $action = if (-not $s.Exists) { 'absent: nothing to remove' } elseif ($s.Ours) { 'remove this key (created by this script)' } else { 'SKIP: exists but was not created by this script (no marker)' }
            Write-Host "  $($s.Key) -> $action"
        }
        $parentText = switch ($fate) {
            'Absent' { 'absent: nothing to remove' }
            'Remove' { 'remove this key (created by this script, and nothing else is left in it)' }
            'LeaveInUse' { 'left in place: created by this script, but other subkeys or values remain' }
            default { 'left in place: not created by this script' }
        }
        Write-Host "  $localDumpsKey -> $parentText"
        Write-Host '  left alone: the dump folder and the dump files'
        return
    }
} else {
    if ($planOnly) {
        Write-Host '  parent key (LocalDumps):'
        if ($parent.Exists) {
            Write-Host "      $localDumpsKey is present: not modified"
        } elseif ($CreateParentKey) {
            Write-Host "      $localDumpsKey is ABSENT: -CreateParentKey would create it and mark it ($markerName). Creating it turns LocalDumps on for the whole machine: WER's global defaults then apply to EVERY program that crashes, not only the ones below."
        } else {
            Write-Host "      $localDumpsKey is ABSENT: REFUSED without -CreateParentKey (creating it turns LocalDumps on for every crashing program with WER's global defaults)."
        }
        Write-Host "  dump folder: $folder (created if missing)"
        foreach ($s in $states) {
            $note = if (-not $s.Exists) { 'new key' } elseif ($s.Ours) { 'update: created by this script' } else { 'REFUSED: exists and was not created by this script' }
            Write-Host "  key: $($s.Key) ($note)"
            Write-Host "      $markerName  REG_SZ  $markerValue"
            Write-Host "      DumpFolder  REG_EXPAND_SZ  $folder"
            Write-Host "      DumpType    REG_DWORD      $(Format-DumpTypeValue $typeNumber)"
            Write-Host "      DumpCount   REG_DWORD      $DumpCount"
        }
        Write-Host '  a dump holds session keys and passwords: keep the folder access-controlled (script help: TWO DUMPS, SECURITY)'
    }
    if ($parentRefused) {
        throw "the LocalDumps parent key $localDumpsKey does not exist. Creating it would turn on WER's global defaults for every program that crashes on this machine, so it is not created by default (nothing was changed). Pass -CreateParentKey to allow it."
    }
    if ($foreign.Count -gt 0) {
        throw "refusing to change a LocalDumps key this script did not create: $(($foreign | ForEach-Object { $_.Key }) -join '; '). Remove or rename it yourself first (nothing was changed)."
    }
    if ($planOnly) { return }
}

if (-not (Test-CrashDumpElevated)) {
    throw 'this changes HKLM and must run from an elevated (Run as administrator) PowerShell. The script never elevates itself; nothing was changed. Use -Plan to see what it would write.'
}

if ($Unregister) {
    foreach ($s in $states) {
        if (-not $s.Exists) { Write-Host "$($s.Executable): not registered, nothing to remove"; continue }
        if (-not $s.Ours) { Write-Warning "$($s.Key) was not created by this script: left in place."; continue }
        Remove-Item -LiteralPath $s.Key -Force
        Write-Host "$($s.Executable): removed $($s.Key)"
    }
    # Re-read: the decision is made on what is really left.
    $after = Get-ParentState
    switch (Get-ParentUnregisterFate -Parent $after) {
        'Remove' { Remove-Item -LiteralPath $localDumpsKey -Force; Write-Host "removed $localDumpsKey (created by this script, nothing else in it)" }
        'LeaveInUse' { Write-Host "$localDumpsKey left in place: other subkeys or values remain" }
        'LeaveNotOurs' { Write-Host "$localDumpsKey left in place: not created by this script" }
    }
    return
}

$expanded = [Environment]::ExpandEnvironmentVariables($folder)
$createdKeys = [System.Collections.Generic.List[string]]::new()
$completed = [System.Collections.Generic.List[string]]::new()
$markedKeys = [System.Collections.Generic.List[string]]::new()
$parentCreated = $false
$parentMarked = $false
$folderCreated = $false
try {
    if (-not (Test-Path -LiteralPath $expanded)) {
        New-Item -ItemType Directory -Path $expanded | Out-Null
        $folderCreated = $true
    }
    if (-not $parent.Exists) {
        New-Item -Path $localDumpsKey | Out-Null
        $parentCreated = $true
        Set-ItemProperty -LiteralPath $localDumpsKey -Name $markerName -Value $markerValue -Type String
        $parentMarked = $true
    }
    foreach ($s in $states) {
        if (-not $s.Exists) {
            New-Item -Path $s.Key | Out-Null
            $createdKeys.Add($s.Key)
        }
        # The marker first: whatever happens next, -Unregister can remove this key.
        Set-ItemProperty -LiteralPath $s.Key -Name $markerName -Value $markerValue -Type String
        $markedKeys.Add($s.Key)
        Set-ItemProperty -LiteralPath $s.Key -Name DumpFolder -Value $folder -Type ExpandString
        Set-ItemProperty -LiteralPath $s.Key -Name DumpType -Value $typeNumber -Type DWord
        Set-ItemProperty -LiteralPath $s.Key -Name DumpCount -Value $DumpCount -Type DWord
        $completed.Add($s.Executable)
        Write-Host "$($s.Executable): LocalDumps -> $folder (DumpType $(Format-DumpTypeValue $typeNumber), DumpCount $DumpCount)"
    }
} catch {
    $failure = $_
    $leftOver = [System.Collections.Generic.List[string]]::new()
    for ($i = $createdKeys.Count - 1; $i -ge 0; $i--) {
        try { Remove-Item -LiteralPath $createdKeys[$i] -Force } catch { $leftOver.Add($createdKeys[$i]) }
    }
    $parentLeft = $false
    if ($parentCreated) {
        if ($leftOver.Count -eq 0) {
            try { Remove-Item -LiteralPath $localDumpsKey -Force } catch { $parentLeft = $true }
        } else {
            $parentLeft = $true
        }
    }
    # The folder is deleted only when it is empty (never through a prompt, never recursively); otherwise it is left and reported.
    $folderNote = $null
    if ($folderCreated) {
        try {
            if (@([IO.Directory]::EnumerateFileSystemEntries($expanded)).Count -eq 0) { [IO.Directory]::Delete($expanded, $false) }
            else { $folderNote = "the dump folder this run created is not empty, so it was left in place: $expanded" }
        } catch { $folderNote = "could not remove the dump folder this run created: $expanded" }
    }
    Write-Warning "registration failed ($($failure.Exception.Message)). Completed before the failure: $(if ($completed.Count -gt 0) { $completed -join ', ' } else { 'none' }). Rolled back what this run created, except as listed next."
    if ($folderNote) { Write-Warning $folderNote }
    $markedLeft = @($leftOver | Where-Object { $markedKeys.Contains($_) })
    $unmarkedLeft = @($leftOver | Where-Object { -not $markedKeys.Contains($_) })
    if ($parentLeft) { if ($parentMarked) { $markedLeft += $localDumpsKey } else { $unmarkedLeft += $localDumpsKey } }
    if ($markedLeft.Count -gt 0) { Write-Warning "could not remove: $($markedLeft -join '; '). They carry the marker, so register-crash-dumps.ps1 -Unregister removes them." }
    if ($unmarkedLeft.Count -gt 0) {
        Write-Warning "UNMARKED, could not remove: $($unmarkedLeft -join '; '). The marker write failed and so did the removal, so -Unregister will NOT remove these: delete them by hand (regedit)."
        if ($unmarkedLeft -contains $localDumpsKey) { Write-Warning 'An unmarked LocalDumps parent key leaves WER''s global dumps switched on for every program that crashes until it is deleted.' }
    }
    throw $failure
}
Write-Host 'Dumps hold session keys and passwords: keep the folder access-controlled. A managed FailFast may also leave the runtime''s own dump (see the script help, TWO DUMPS).'
