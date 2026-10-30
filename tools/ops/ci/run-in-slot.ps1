# Runs one build/test command under the fleet's build-slot cap while the live server runs.
#   pwsh -NoProfile -File tools/ops/ci/run-in-slot.ps1 -Lane <name> -WorkDir <worktree> -Command '<pwsh command>' [-TimeoutMin 45] [-LanesRoot <dir>]
# - Waits for one of $Slots slot files (atomic CreateNew) AND >= $MinAvailableMB available memory.
# - Slot files live in <LanesRoot>\slot-locks, shared by every lane on the machine; LanesRoot defaults to ARCANECORE_LANES_ROOT.
# - Runs the command in a child pwsh at BelowNormal (its children inherit it), output to <LanesRoot>\slot-logs\<lane>-<n>.log.
# - Each `dotnet build|test|msbuild|publish|pack|restore` call in -Command gets -maxcpucount:$MaxCpuCount (0 = leave MSBuild's default;
#   decided per call, split at ; && || | and newlines: a call that sets -m/-maxcpucount itself before any `--` is left alone).
#   `dotnet run` without --no-build is not capped. The effective width decision is the first line of the lane log. At the default width every build fans out to one worker node
#   per core, so three slots ran 54 nodes (6.4 GB) and paged the live World. Directory.Build.rsp cannot do this: the CLI's
#   own bare -maxcpucount overrides it.
# - The child gets DOTNET_ROOT (default %USERPROFILE%\.dotnet) and, when ARCANECORE_DATA_ROOT (or ARCANECORE_TERRAIN_DIR /
#   ARCANECORE_DBC_DIR) is set, ARCANECORE_TEST_TERRAIN_DIR / ARCANECORE_TEST_DBC_DIR, so real-data tests run instead of skipping.
# - ARCANECORE_TEST_CLIENT_DIR / ARCANECORE_TEST_CLIENT_FILES_DIR are passed through when set; otherwise filled from the optional gitignored
#   slot-env.local.json ({"clientDir": "...", "clientFilesDir": "..."}) in WorkDir or LanesRoot; if still unset one line says client-data tests skip.
# - Always releases the slot (finally). A slot file whose holder pwsh is dead (or older than $StaleMin minutes) is abandoned and removed.
# - Exit code: 124 on timeout; else the last native command's code if non-zero; else 1 if the command threw or its last statement failed; else 0. Prints the log path and the log's last 60 lines.
param(
    [Parameter(Mandatory)] [string]$Lane,
    [Parameter(Mandatory)] [string]$WorkDir,
    [Parameter(Mandatory)] [string]$Command,
    [int]$TimeoutMin = 45,
    [int]$Slots = 2,
    [int]$MinAvailableMB = 2000,
    [int]$StaleMin = 75,
    [int]$MaxCpuCount = 6,
    [string]$LanesRoot
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\OpsEnv.ps1')
if (-not $LanesRoot) { $LanesRoot = Get-OpsRequiredEnv 'ARCANECORE_LANES_ROOT' 'the root that holds lane worktrees, slot-locks and slot-logs' }
$root = Join-Path $LanesRoot 'slot-locks'
$logs = Join-Path $LanesRoot 'slot-logs'
New-Item -ItemType Directory -Force $root, $logs | Out-Null
$dotnetRoot = if ($env:DOTNET_ROOT) { $env:DOTNET_ROOT } else { Join-Path $env:USERPROFILE '.dotnet' }
$dataRoot = Get-OpsEnv 'ARCANECORE_DATA_ROOT'
$terrainDir = Get-OpsEnv 'ARCANECORE_TERRAIN_DIR'
if (-not $terrainDir -and $dataRoot) { $terrainDir = Join-Path $dataRoot 'terrain-5875' }
$dbcDir = Get-OpsEnv 'ARCANECORE_DBC_DIR'
if (-not $dbcDir -and $dataRoot) { $dbcDir = Join-Path $dataRoot 'client-dbc-5875' }
# Client-data tests: ARCANECORE_TEST_CLIENT_DIR / _CLIENT_FILES_DIR when exported, else the optional gitignored slot-env.local.json
# ({ "clientDir": "...", "clientFilesDir": "..." }) found in WorkDir, then in LanesRoot.
. (Join-Path $PSScriptRoot 'SlotEnvLib.ps1')
$clientEnv = Resolve-SlotClientEnv -EnvClientDir (Get-OpsEnv 'ARCANECORE_TEST_CLIENT_DIR') -EnvClientFilesDir (Get-OpsEnv 'ARCANECORE_TEST_CLIENT_FILES_DIR') -WorkDir $WorkDir -LanesRoot $LanesRoot
$clientDir = $clientEnv.ClientDir
$clientFilesDir = $clientEnv.ClientFilesDir
foreach ($m in $clientEnv.Messages) { "[$Lane] $m" }

function Get-AvailableMB { [int]((Get-Counter '\Memory\Available MBytes').CounterSamples[0].CookedValue) }

$held = $null; $waited = 0
while (-not $held) {
    foreach ($f in Get-ChildItem $root -Filter 'slot-*.lock' -ErrorAction SilentlyContinue) {
        # A holder killed outright (taskkill, TaskStop) never runs its finally: free its slot as soon as its pwsh is gone,
        # not only after $StaleMin. A lock younger than 30 s may still be mid-write, so its pid is not read yet.
        $holderDead = $false
        if ($f.LastWriteTime -lt (Get-Date).AddSeconds(-30) -and (Get-Content $f.FullName -ErrorAction SilentlyContinue) -match 'pid=(\d+)') {
            $hp = Get-Process -Id ([int]$Matches[1]) -ErrorAction SilentlyContinue
            $holderDead = -not $hp -or $hp.ProcessName -notmatch '^pwsh' -or $hp.StartTime -gt $f.LastWriteTime
        }
        if ($holderDead -or $f.LastWriteTime -lt (Get-Date).AddMinutes(-$StaleMin)) { Remove-Item $f.FullName -ErrorAction SilentlyContinue }
    }
    if ((Get-AvailableMB) -ge $MinAvailableMB) {
        for ($i = 1; $i -le $Slots -and -not $held; $i++) {
            $path = Join-Path $root "slot-$i.lock"
            try {
                $fs = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
                $bytes = [Text.Encoding]::UTF8.GetBytes("$Lane pid=$PID since=$(Get-Date -Format s)")
                $fs.Write($bytes, 0, $bytes.Length); $fs.Dispose(); $held = $path
            } catch [System.IO.IOException] { }
        }
    }
    if (-not $held) {
        if ($waited % 300 -eq 0) { "[$Lane] waiting for a build slot (available $(Get-AvailableMB) MB, need $MinAvailableMB; holders: $((Get-ChildItem $root -Filter 'slot-*.lock' | ForEach-Object { Get-Content $_.FullName }) -join '; '))" }
        Start-Sleep -Seconds 20; $waited += 20
    }
}

$n = 1; while (Test-Path (Join-Path $logs "$Lane-$n.log")) { $n++ }
$log = Join-Path $logs "$Lane-$n.log"
"[$Lane] holding $(Split-Path $held -Leaf) after ${waited}s; log $log"
$rc = 1
try {
    $setup = "Set-Location -LiteralPath $(ConvertTo-PsSingleQuoted $WorkDir)`n`$env:DOTNET_ROOT=$(ConvertTo-PsSingleQuoted $dotnetRoot)`n`$env:PATH=`"`$env:DOTNET_ROOT;`$env:PATH`"`n"
    if ($terrainDir) { $setup += "`$env:ARCANECORE_TEST_TERRAIN_DIR=$(ConvertTo-PsSingleQuoted $terrainDir)`n" }
    if ($dbcDir) { $setup += "`$env:ARCANECORE_TEST_DBC_DIR=$(ConvertTo-PsSingleQuoted $dbcDir)`n" }
    if ($clientDir) { $setup += "`$env:ARCANECORE_TEST_CLIENT_DIR=$(ConvertTo-PsSingleQuoted $clientDir)`n" }
    if ($clientFilesDir) { $setup += "`$env:ARCANECORE_TEST_CLIENT_FILES_DIR=$(ConvertTo-PsSingleQuoted $clientFilesDir)`n" }
    $widthCommand = Add-MaxCpuCount -Command $Command -MaxCpuCount $MaxCpuCount
    $widthNote = if ($widthCommand -ne $Command) { "[$Lane] width capped at $MaxCpuCount; effective command: $widthCommand" }
                 elseif ($MaxCpuCount -le 0) { "[$Lane] width left alone: -MaxCpuCount 0" }
                 else { "[$Lane] width left alone: no uncapped dotnet build/test/msbuild/publish/pack/restore call (or -m/-maxcpucount found)" }
    $widthNote
    $Command = $widthCommand
    $setup += "Write-Output $(ConvertTo-PsSingleQuoted $widthNote)`n"
    # Honest exit code. $LASTEXITCODE only tracks native commands, so `exit $LASTEXITCODE` alone reported 0 for a thrown error, a failed
    # cmdlet, or a command that ran no native program (and an earlier native success masked a later PowerShell failure).
    # Child exit: the last native code if non-zero (so `cmd /c exit 3; Write-Output ok` is 3: a native failure is not forgiven by a later
    # success); else 1 on a terminating error (caught, written to stderr) or when the command's last statement failed ($? false); else 0.
    # $ErrorActionPreference is deliberately NOT 'Stop' here: that would turn benign non-terminating errors (a probe Get-Item, a native
    # program's stderr) into fatal ones and change what existing dotnet/bash commands do. Terminating errors are still caught.
    $script = "$setup`$global:LASTEXITCODE = 0`n`$__slotOk = `$true`ntry {`n$Command`n`$__slotOk = `$?`n} catch {`n[Console]::Error.WriteLine((`$_ | Out-String))`nexit 1`n}`nif (`$LASTEXITCODE -ne 0) { exit `$LASTEXITCODE }`nif (-not `$__slotOk) { exit 1 }`nexit 0"
    $enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
    $p = Start-Process pwsh -ArgumentList '-NoProfile', '-NonInteractive', '-EncodedCommand', $enc -PassThru -NoNewWindow -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    try { $p.PriorityClass = 'BelowNormal' } catch { }
    if (-not $p.WaitForExit($TimeoutMin * 60 * 1000)) {
        & taskkill.exe /T /F /PID $p.Id | Out-Null
        $rc = 124; "[$Lane] TIMEOUT after $TimeoutMin min; killed PID $($p.Id) and its children"
    } else { $p.WaitForExit(); $rc = $p.ExitCode }
} finally {
    Remove-Item $held -ErrorAction SilentlyContinue
    "[$Lane] released $(Split-Path $held -Leaf); exit $rc"
}
if (Test-Path "$log.err") { Get-Content "$log.err" | Add-Content $log; Remove-Item "$log.err" }
Get-Content $log -Tail 60
exit $rc
