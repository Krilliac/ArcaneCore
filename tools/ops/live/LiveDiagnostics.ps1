# Diagnostic capture settings for the local live daemons (docs/ops/live-diagnostics.md). Dot-source this file; it defines functions only.
# launch-daemon.ps1 applies them through the process environment (which overrides the profile's appsettings.json and is read by the
# runtime itself for the DOTNET_ keys), and diagnostics-sidecar.ps1 keeps the captured files bounded and collects counters.
#   Standard  nothing added: the profile and the repo defaults decide.
#   Max       the most information that is still safe for a live server with ~1000 bots: bounded disk, bounded pause time, no cost on the
#             world thread beyond a counter increment (docs/ops/live-diagnostics.md has the table and what was left out, and why).
# A variable that is already set in the launching environment is never replaced: that is how an operator overrides one value.

Set-StrictMode -Version Latest

function Find-DotnetTool {
    <#
    .SYNOPSIS
      The full path of a dotnet global tool (dotnet-stack, dotnet-counters), or $null. Looks in <DOTNET_ROOT>\tools, then the global
      tools directory of the user profile (%USERPROFILE%\.dotnet\tools).
    #>
    param([Parameter(Mandatory)][string]$Name, [string]$DotnetRoot, [string]$UserHome = $env:USERPROFILE)
    $dirs = [System.Collections.Generic.List[string]]::new()
    if ($DotnetRoot) { $dirs.Add((Join-Path $DotnetRoot 'tools')) }
    if ($UserHome) { $dirs.Add((Join-Path (Join-Path $UserHome '.dotnet') 'tools')) }
    foreach ($dir in $dirs) {
        foreach ($file in "$Name.exe", $Name) {
            $candidate = Join-Path $dir $file
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
        }
    }
    return $null
}

function Get-LiveDiagnosticsDirectories {
    <# .SYNOPSIS  Where each kind of capture goes under the live home. #>
    param([Parameter(Mandatory)][string]$LiveHome)
    return [ordered]@{
        HangStacks = Join-Path (Join-Path $LiveHome 'logs') 'hangstacks'
        Counters   = Join-Path (Join-Path $LiveHome 'logs') 'counters'
        Dumps      = Join-Path $LiveHome 'dumps'
    }
}

function Get-LiveFreeBytes {
    <# .SYNOPSIS  Free bytes on the volume that holds -Path; [long]::MaxValue when it cannot be read. #>
    param([Parameter(Mandatory)][string]$Path)
    try {
        $root = [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($Path))
        return [long][IO.DriveInfo]::new($root).AvailableFreeSpace
    } catch { return [long]::MaxValue }
}

function Get-LiveDiagnosticsPlan {
    <#
    .SYNOPSIS
      The settings a launch applies for a diagnostics level, as data: Settings (Name, Value, Why, Kept), Active (the lines launch prints),
      Warnings, Directories, StackTool and CountersTool (paths or $null), DumpType. Reads the file system (tool lookup) and the
      environment (which variables are already set), nothing else; sets and creates nothing.
    #>
    param(
        [Parameter(Mandatory)][ValidateSet('Realm', 'World')][string]$Role,
        [Parameter(Mandatory)][string]$LiveHome,
        [ValidateSet('Standard', 'Max')][string]$Level = 'Max',
        [string]$DotnetRoot,
        [string]$UserHome = $env:USERPROFILE,
        # Full = every mapped page (the world heap is several GB: one dump is ~7 GB); Heap = managed and native heap, no images.
        [ValidateSet('Full', 'Heap')][string]$DumpType = 'Full',
        [ValidateRange(10, 86400)][int]$HangStackIntervalSeconds = 60,
        # A Full dump needs this much free space on the dumps volume, otherwise it is written as a Heap dump.
        [long]$FullDumpMinFreeBytes = 16GB,
        [long]$DumpFreeBytes = -1,
        [System.Collections.IDictionary]$Environment
    )
    $dirs = Get-LiveDiagnosticsDirectories -LiveHome $LiveHome
    $settings = [System.Collections.Generic.List[object]]::new()
    $active = [System.Collections.Generic.List[string]]::new()
    $warnings = [System.Collections.Generic.List[string]]::new()
    $plan = [ordered]@{
        Level = $Level; Role = $Role; Directories = $dirs; DumpType = $null; StackTool = $null; CountersTool = $null
        Settings = $settings; Active = $active; Warnings = $warnings
    }
    if ($Level -eq 'Standard') {
        $active.Add('diagnostics Standard: nothing added (the profile and the repo defaults decide)')
        return [pscustomobject]$plan
    }

    function Add-Setting([string]$Name, [string]$Value, [string]$Why) {
        $existing = if ($null -ne $Environment) { $Environment[$Name] } else { [System.Environment]::GetEnvironmentVariable($Name) }
        $kept = -not [string]::IsNullOrWhiteSpace([string]$existing)
        $settings.Add([pscustomobject]@{ Name = $Name; Value = $Value; Why = $Why; Kept = $kept; Existing = if ($kept) { [string]$existing } else { $null } })
    }

    $dumpDir = $dirs.Dumps
    $free = if ($DumpFreeBytes -ge 0) { $DumpFreeBytes } else { Get-LiveFreeBytes -Path $LiveHome }
    $effectiveDump = $DumpType
    if ($DumpType -eq 'Full' -and $free -lt $FullDumpMinFreeBytes) {
        $effectiveDump = 'Heap'
        $warnings.Add("only $([math]::Round($free / 1GB, 1)) GB free for the dumps: the crash dump is a Heap dump, not Full (needs $([math]::Round($FullDumpMinFreeBytes / 1GB, 1)) GB)")
    }
    $plan.DumpType = $effectiveDump

    # Crash dump by the runtime's createdump (Windows too): 4 = Full, 2 = Heap (MiniDumpType values of the runtime's DbgMiniDumpType).
    Add-Setting 'DOTNET_DbgEnableMiniDump' '1' 'write a dump when the process crashes (unhandled exception, FailFast, fatal runtime error)'
    Add-Setting 'DOTNET_DbgMiniDumpType' $(if ($effectiveDump -eq 'Full') { '4' } else { '2' }) "dump type $effectiveDump"
    Add-Setting 'DOTNET_DbgMiniDumpName' (Join-Path $dumpDir "$($Role.ToLowerInvariant())-%p-%t.dmp") 'dump file name (%p process id, %t time)'
    Add-Setting 'DOTNET_CreateDumpDiagnostics' '1' 'createdump logs what it does'
    Add-Setting 'DOTNET_CreateDumpVerboseDiagnostics' '1' 'createdump logs in detail'
    Add-Setting 'DOTNET_CreateDumpLogToFile' (Join-Path $dumpDir "$($Role.ToLowerInvariant())-createdump.log") 'createdump log goes to a file next to the dump'

    # Repo-side options both daemons read.
    Add-Setting 'Diagnostics__FaultLogLimit' '50' 'log the first 50 occurrences of each fault in full (default 10); the ledger counts every one'
    Add-Setting 'Ops__Watchdog__Counters__DumpIntervalSeconds' '60' 'counter registry line every minute (default 300)'
    Add-Setting 'Ops__Watchdog__Memory__SampleIntervalSeconds' '5' 'heap sample every 5 s on the monitoring thread (default 10)'
    Add-Setting 'Ops__Watchdog__HostLoad__HistorySeconds' '300' 'five minutes of host-load samples to difference a slow tick against (default 120)'
    Add-Setting 'Database__SlowTransactionWarnMs' '500' 'WARN a transaction open longer than 500 ms (default 2000)'
    Add-Setting 'Database__SlowCommandWarnMs' '500' 'WARN a database command longer than 500 ms (default 2000)'
    Add-Setting 'Database__Sqlite__SingleWriter__StatsIntervalMs' '60000' 'write-gate statistics line every minute (default 5 min)'

    if ($Role -eq 'World') {
        $stack = Find-DotnetTool -Name 'dotnet-stack' -DotnetRoot $DotnetRoot -UserHome $UserHome
        $plan.StackTool = $stack
        if ($stack) {
            Add-Setting 'Ops__Watchdog__TickMonitor__StackCaptureTool' $stack 'dotnet-stack report on a hung tick: every managed thread''s stack'
            Add-Setting 'Ops__Watchdog__TickMonitor__StackCaptureDirectory' $dirs.HangStacks 'hang stacks go under the live logs'
            Add-Setting 'Ops__Watchdog__TickMonitor__StackCaptureIntervalSeconds' ([string]$HangStackIntervalSeconds) "at most one capture per $HangStackIntervalSeconds s (default 300)"
        } else {
            $warnings.Add('dotnet-stack not found (looked in <DOTNET_ROOT>\tools and the user profile''s .dotnet\tools): hang stack capture stays as the profile configures it; install it with: dotnet tool install -g dotnet-stack')
        }

        Add-Setting 'Ops__Diagnostics__Dumps__Enabled' 'true' 'the .debug dump command may write a dump (administrator only)'
        Add-Setting 'Ops__Diagnostics__Dumps__Directory' $dumpDir '.debug dump writes beside the crash dumps, so one retention cap covers both'
    }

    $counters = Find-DotnetTool -Name 'dotnet-counters' -DotnetRoot $DotnetRoot -UserHome $UserHome
    $plan.CountersTool = $counters
    if (-not $counters) {
        $warnings.Add('dotnet-counters not found: no counters CSV; install it with: dotnet tool install -g dotnet-counters')
    }

    $active.Add("diagnostics Max ($Role)")
    $active.Add("crash dump: $effectiveDump -> $dumpDir")
    if ($Role -eq 'World') {
        $active.Add($(if ($plan.StackTool) { "hang stacks: $($plan.StackTool) every >= $HangStackIntervalSeconds s -> $($dirs.HangStacks)" } else { 'hang stacks: off (dotnet-stack not found)' }))
    }
    $active.Add($(if ($counters) { "counters: System.Runtime + ArcaneCore meters -> $($dirs.Counters)" } else { 'counters: off (dotnet-counters not found)' }))
    $active.Add('repo options: fault log limit 50, slow transaction/command 500 ms, write-gate stats 60 s, heap sample 5 s, counter dump 60 s')
    foreach ($s in $settings | Where-Object Kept) { $active.Add("kept from the environment: $($s.Name)=$($s.Existing)") }
    return [pscustomobject]$plan
}

function Set-LiveDiagnosticsEnvironment {
    <# .SYNOPSIS  Sets the plan's variables in this process (not those already set in the launching environment); creates the capture directories. Returns the names it set. #>
    param([Parameter(Mandatory)]$Plan)
    if ($Plan.Level -eq 'Standard') { return @() }
    foreach ($dir in $Plan.Directories.Values) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $set = [System.Collections.Generic.List[string]]::new()
    foreach ($s in $Plan.Settings) {
        if ($s.Kept) { continue }
        [System.Environment]::SetEnvironmentVariable($s.Name, $s.Value)
        $set.Add($s.Name)
    }
    return $set.ToArray()
}

function Invoke-DiagnosticsRetention {
    <#
    .SYNOPSIS
      Deletes the oldest files of -Directory matching -Filter until at most -MaxFiles remain and their total size is at most -MaxBytes
      (0 = no limit for that cap). The newest file is always kept. Returns the names it deleted; never throws for a file that is in use.
    #>
    param(
        [Parameter(Mandatory)][string]$Directory,
        [string]$Filter = '*',
        [int]$MaxFiles = 0,
        [long]$MaxBytes = 0
    )
    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) { return @() }
    $files = @(Get-ChildItem -LiteralPath $Directory -File -Filter $Filter -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc, Name -Descending)
    $deleted = [System.Collections.Generic.List[string]]::new()
    $keptCount = 0
    $keptBytes = 0L
    foreach ($f in $files) {
        $over = ($MaxFiles -gt 0 -and ($keptCount + 1) -gt $MaxFiles) -or ($MaxBytes -gt 0 -and ($keptBytes + $f.Length) -gt $MaxBytes)
        if ($over -and $keptCount -gt 0) {
            try { Remove-Item -LiteralPath $f.FullName -Force -ErrorAction Stop; $deleted.Add($f.Name) } catch { $keptCount++; $keptBytes += $f.Length }
        } else {
            $keptCount++
            $keptBytes += $f.Length
        }
    }
    return $deleted.ToArray()
}

function Get-DiagnosticsRetentionRules {
    <# .SYNOPSIS  The retention rules (directory, filter, caps) for the three capture directories. #>
    param(
        [Parameter(Mandatory)][string]$LiveHome,
        [int]$MaxDumpFiles = 3, [int]$MaxDumpGB = 30,
        [int]$MaxHangStackFiles = 500, [int]$MaxHangStackMB = 200,
        [int]$MaxCountersFiles = 48, [int]$MaxCountersMB = 2048
    )
    $dirs = Get-LiveDiagnosticsDirectories -LiveHome $LiveHome
    return @(
        [pscustomobject]@{ Name = 'dumps'; Directory = $dirs.Dumps; Filter = '*.dmp'; MaxFiles = $MaxDumpFiles; MaxBytes = [long]$MaxDumpGB * 1GB }
        [pscustomobject]@{ Name = 'hangstacks'; Directory = $dirs.HangStacks; Filter = 'hang-stacks-*.txt'; MaxFiles = $MaxHangStackFiles; MaxBytes = [long]$MaxHangStackMB * 1MB }
        [pscustomobject]@{ Name = 'counters'; Directory = $dirs.Counters; Filter = '*.csv'; MaxFiles = $MaxCountersFiles; MaxBytes = [long]$MaxCountersMB * 1MB }
    )
}

function Invoke-DiagnosticsRetentionRules {
    <# .SYNOPSIS  Applies Get-DiagnosticsRetentionRules output; returns "<name>/<file>" for each deleted file. #>
    param([Parameter(Mandatory)][object[]]$Rules)
    foreach ($r in $Rules) {
        foreach ($name in Invoke-DiagnosticsRetention -Directory $r.Directory -Filter $r.Filter -MaxFiles $r.MaxFiles -MaxBytes $r.MaxBytes) { "$($r.Name)/$name" }
    }
}

function Get-CountersCollectArguments {
    <#
    .SYNOPSIS
      The dotnet-counters collect arguments for one CSV segment: System.Runtime plus the repo's meters, every -RefreshSeconds, for
      -SegmentMinutes (the tool has no rotation of its own; the sidecar starts the next segment).
    #>
    param(
        [Parameter(Mandatory)][int]$ProcessId,
        [Parameter(Mandatory)][string]$OutputPath,
        [int]$RefreshSeconds = 10,
        [int]$SegmentMinutes = 60
    )
    $providers = 'System.Runtime', 'ArcaneCore.Runtime', 'ArcaneCore.World', 'ArcaneCore.Faults', 'ArcaneCore.Anomalies', 'ArcaneCore.Playerbots.Qa', 'ArcaneCore.Playerbots.Qa.Findings'
    # --duration is dd:hh:mm:ss: a segment of a day or more needs the days field (24 in the hours field does not parse).
    $days = [math]::Floor($SegmentMinutes / 1440)
    $hours = [math]::Floor(($SegmentMinutes % 1440) / 60)
    $minutes = $SegmentMinutes % 60
    return @(
        'collect', '-p', [string]$ProcessId, '--format', 'csv', '-o', $OutputPath,
        '--refresh-interval', [string]$RefreshSeconds, '--counters', ($providers -join ','),
        '--maxTimeSeries', '2000', '--maxHistograms', '50',
        '--duration', ('{0:00}:{1:00}:{2:00}:00' -f $days, $hours, $minutes)
    )
}
