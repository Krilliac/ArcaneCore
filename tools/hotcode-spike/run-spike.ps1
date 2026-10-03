<#
.SYNOPSIS
  Repeatable proof of what `dotnet watch` hot reload does to a running .NET process, on this SDK.
  Backs the claims in docs/areas/code-hot-reload.md. Copies the spike to a temp directory, so
  the repository is never edited.

.DESCRIPTION
  Scenarios (each prints `RESULT <name> PASS|FAIL <detail>`):
    plain-run         no watch: MetadataUpdater.IsSupported is False, DOTNET_WATCH unset.
    debug-body-edit   dotnet watch -c Debug: a method-body edit is applied in the SAME process
                      (same pid, new value); the runtime reports IsSupported=True.
    debug-sig-edit    a signature edit (rude edit) restarts the process: NEW pid (non-interactive).
    release-body-edit dotnet watch -c Release: a body edit is NOT hot-applied: the process is
                      restarted (new pid) or never sees the new value in the same pid.

  Exit codes: 0 all scenarios passed, 1 a scenario failed, 77 skipped (no dotnet SDK on PATH).
  A skipped run proves nothing: look for the final `SPIKE SUMMARY` line.

.PARAMETER Scenario
  Subset to run (default: all).
.PARAMETER TimeoutSeconds
  Per-wait timeout (default 90). A first build can take a while on a busy machine.
#>
param(
    [ValidateSet('plain-run', 'debug-body-edit', 'debug-sig-edit', 'release-body-edit')]
    [string[]]$Scenario = @('plain-run', 'debug-body-edit', 'debug-sig-edit', 'release-body-edit'),
    [int]$TimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host 'SPIKE SUMMARY SKIPPED: no dotnet SDK on PATH'
    exit 77
}

$source = $PSScriptRoot
$results = New-Object System.Collections.Generic.List[object]
$tickPattern = 'TICK pid=(?<pid>\d+) value=(?<value>\S+) supported=(?<supported>\w+) watch=(?<watch>\S+) modifiable=(?<modifiable>\S+) applier=(?<applier>\w+) hooks=(?<hooks>\S*) vars=(?<vars>\S*)'

function New-Workspace {
    $dir = Join-Path ([IO.Path]::GetTempPath()) ('hotcode-spike-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $dir | Out-Null
    foreach ($name in 'HotCodeSpike.csproj', 'Directory.Build.props', 'Program.cs', 'Probe.cs') {
        Copy-Item (Join-Path $source $name) $dir
    }
    return $dir
}

function Start-Spike([string]$dir, [string[]]$dotnetArgs) {
    $out = Join-Path $dir 'out.txt'
    $err = Join-Path $dir 'err.txt'
    # Hot reload variables inherited from a parent watch/debug session would leak into the runs.
    foreach ($v in 'DOTNET_WATCH', 'DOTNET_MODIFIABLE_ASSEMBLIES', 'DOTNET_STARTUP_HOOKS', 'DOTNET_HOTRELOAD_NAMEDPIPE_NAME') {
        Remove-Item "Env:$v" -ErrorAction SilentlyContinue
    }
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_WATCH_SUPPRESS_EMOJIS = '1'
    $proc = Start-Process dotnet -ArgumentList $dotnetArgs -WorkingDirectory $dir -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $out -RedirectStandardError $err
    return [pscustomobject]@{ Process = $proc; Out = $out; Err = $err; Dir = $dir }
}

function Stop-Spike($spike) {
    if ($spike.Process -and -not $spike.Process.HasExited) {
        & taskkill /PID $spike.Process.Id /T /F 2>&1 | Out-Null
    }
}

# Read the whole output through an independent handle (never the child's file position).
function Read-Lines([string]$path) {
    if (-not (Test-Path $path)) { return @() }
    $fs = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')
    try {
        $sr = New-Object IO.StreamReader($fs)
        $text = $sr.ReadToEnd()
    } finally { $fs.Dispose() }
    return $text -split "`r?`n"
}

function Get-Ticks($spike) {
    foreach ($line in Read-Lines $spike.Out) {
        $m = [regex]::Match($line, $tickPattern)
        if ($m.Success) {
            [pscustomobject]@{
                Pid = [int]$m.Groups['pid'].Value; Value = $m.Groups['value'].Value
                Supported = $m.Groups['supported'].Value; Watch = $m.Groups['watch'].Value
                Modifiable = $m.Groups['modifiable'].Value; Applier = $m.Groups['applier'].Value
                Hooks = $m.Groups['hooks'].Value; Vars = $m.Groups['vars'].Value
            }
        }
    }
}

function Wait-Tick($spike, [scriptblock]$predicate, [string]$what) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $hit = @(Get-Ticks $spike | Where-Object $predicate) | Select-Object -First 1
        if ($hit) { return $hit }
        if ($spike.Process.HasExited -and $spike.Process.ExitCode -ne 0) {
            throw "process exited ($($spike.Process.ExitCode)) while waiting for $what`n$((Read-Lines $spike.Out | Select-Object -Last 15) -join "`n")`n$((Read-Lines $spike.Err | Select-Object -Last 15) -join "`n")"
        }
        Start-Sleep -Milliseconds 250
    }
    throw "timed out after ${TimeoutSeconds}s waiting for $what`n$((Read-Lines $spike.Out | Select-Object -Last 15) -join "`n")"
}

function Edit-Probe([string]$dir, [string]$from, [string]$to) {
    $path = Join-Path $dir 'Probe.cs'
    $text = [IO.File]::ReadAllText($path)
    if (-not $text.Contains($from)) { throw "Probe.cs does not contain '$from'" }
    [IO.File]::WriteAllText($path, $text.Replace($from, $to), (New-Object Text.UTF8Encoding($false)))
}

function Edit-ProbeSignature([string]$dir) {
    $path = Join-Path $dir "Probe.cs"
    $text = [IO.File]::ReadAllText($path)
    $text = $text.Replace('Value()', 'Value(int unused = 0)')
    $text = [regex]::Replace($text, '=> "v\d"', '=> "v3"')
    [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding($false)))
}

function Add-Result([string]$name, [bool]$pass, [string]$detail) {
    $results.Add([pscustomobject]@{ Name = $name; Pass = $pass; Detail = $detail })
    Write-Host ("RESULT {0} {1} {2}" -f $name, $(if ($pass) { 'PASS' } else { 'FAIL' }), $detail)
}

function Invoke-Scenario([string]$name, [scriptblock]$body) {
    $dir = New-Workspace
    $spike = $null
    try {
        & $body $dir ([ref]$spike)
    } catch {
        Add-Result $name $false ($_.Exception.Message -replace "`r?`n", ' | ')
    } finally {
        if ($spike) { Stop-Spike $spike }
        Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
    }
}

$project = 'HotCodeSpike.csproj'

if ($Scenario -contains 'plain-run') {
    Invoke-Scenario 'plain-run' {
        param($dir, $ref)
        $ref.Value = Start-Spike $dir @('run', '--project', $project, '-c', 'Debug', '--no-launch-profile')
        $t = Wait-Tick $ref.Value { $true } 'first tick'
        $ok = ($t.Supported -eq 'False') -and ($t.Watch -eq '-')
        Add-Result 'plain-run' $ok "supported=$($t.Supported) watch=$($t.Watch) applier=$($t.Applier)"
    }
}

if ($Scenario -contains 'debug-body-edit' -or $Scenario -contains 'debug-sig-edit') {
    Invoke-Scenario 'debug-watch' {
        param($dir, $ref)
        $ref.Value = Start-Spike $dir @('watch', '--project', $project, '--non-interactive', '-c', 'Debug', '--no-launch-profile')
        $first = Wait-Tick $ref.Value { $_.Value -eq 'v1' } 'first v1 tick'
        if ($Scenario -contains 'debug-body-edit') {
            Edit-Probe $dir '"v1"' '"v2"'
            $second = Wait-Tick $ref.Value { $_.Value -eq 'v2' } 'v2 tick after a body edit'
            $ok = ($second.Pid -eq $first.Pid) -and ($first.Supported -eq 'True') -and ($first.Watch -eq '1')
            Add-Result 'debug-body-edit' $ok "pid $($first.Pid) -> $($second.Pid); supported=$($first.Supported) watch=$($first.Watch) modifiable=$($first.Modifiable) applier=$($first.Applier) hooks=$($first.Hooks) vars=$($first.Vars)"
        }
        if ($Scenario -contains 'debug-sig-edit') {
            $before = @(Get-Ticks $ref.Value)[-1]
            Edit-ProbeSignature $dir
            $third = Wait-Tick $ref.Value { $_.Value -eq 'v3' } 'v3 tick after a signature edit'
            Add-Result 'debug-sig-edit' ($third.Pid -ne $before.Pid) "pid $($before.Pid) -> $($third.Pid) (a rude edit restarts the process)"
        }
    }
}

if ($Scenario -contains 'release-body-edit') {
    Invoke-Scenario 'release-body-edit' {
        param($dir, $ref)
        $ref.Value = Start-Spike $dir @('watch', '--project', $project, '--non-interactive', '-c', 'Release', '--no-launch-profile')
        $first = Wait-Tick $ref.Value { $_.Value -eq 'v1' } 'first v1 tick'
        Edit-Probe $dir '"v1"' '"v2"'
        $second = Wait-Tick $ref.Value { $_.Value -eq 'v2' } 'v2 tick after a body edit'
        # In Release the delta is refused, so the only way to see v2 is a restart (new pid).
        Add-Result 'release-body-edit' ($second.Pid -ne $first.Pid) "pid $($first.Pid) -> $($second.Pid) (Release is never hot-applied)"
    }
}

$failed = @($results | Where-Object { -not $_.Pass })
$expected = $Scenario.Count
if ($expected -eq 0 -or $results.Count -lt $expected) {
    Write-Host "SPIKE SUMMARY FAIL: only $($results.Count) of $expected scenarios produced a result"
    exit 1
}
if ($failed.Count -gt 0) {
    Write-Host "SPIKE SUMMARY FAIL: $($failed.Count) of $($results.Count) scenarios failed"
    exit 1
}
Write-Host "SPIKE SUMMARY PASS: $($results.Count) of $($results.Count) scenarios"
exit 0
