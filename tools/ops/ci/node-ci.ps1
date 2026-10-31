<#
.SYNOPSIS
  Run ArcaneCore's CI (.github/workflows/ci.yml) for one commit on Node1's WSL Ubuntu 24.04. The MariaDB 10.11 / PostgreSQL 16
  provider tests are skipped unless -WithDbProviders is passed (then: same services and connection strings as the hosted runners).
  -BuildOnly stops after restore + Release build (compile gate, no tests).

.DESCRIPTION
  Ships `git archive <commit>` to the node, then runs a locked restore, Release build (warnings are errors via Directory.Build.props)
  and the two-stage test gate of test-stages.sh (shipped from this script's folder with every run, so the gate does not depend on the
  commit under test): stage main runs each suite (tests/ci/<Suite>.slnf, -m:1) with Category!=Timing, then the Rest-only MockClient
  self-test; stage timing runs Category=Timing serially, reruns each failure once alone, prints "FLAKY (passed on rerun): <test>" for a
  test that passes its rerun and fails the gate for one that fails twice or for a timing stage that runs 0 tests. The per-stage counts
  and the flaky tests are in the closing GATE SUMMARY; flaky tests are also appended to a local flake ledger (a markdown file) when
  NODE_CI_FLAKE_LEDGER names one. The full logs, .trx files and flake-ledger.txt stay on the node under
  /root/ci/<run>/logs.
  At most 2 full gates (no -Filter, no -BuildOnly) run at once: queued ones wait here (other node-ci.ps1 processes on this host) and on
  the node itself (two flock slots), and say so. Filtered and -BuildOnly runs queue separately (NODE_CI_MAX_FILTERED, default 6).
  One-time node setup (mariadb-server, postgresql-16, root/arcane and arcane/arcane logins) was done 2026-10-10. /opt/dotnet holds the
  10.0.401 and 11.0.100-rc.2 SDKs; the repository's global.json pins 11, so the run uses 11. If the services are not running, this
  script starts them.
  Time budgets: the gate stops a main-stage target after 20 minutes (a hard stop that names the slow tests) and fails a test class
  that adds up to over 10 minutes (see the header of test-stages.sh); -NoTimeBudget turns all of that off for slow hardware.
  Two copies of this script exist and must be kept in sync: this one and the operator's copy outside the repository (it runs commits
  that predate this file). They differ only in the OpsEnv.ps1 include, the examples and the default flake-ledger path. test-stages.sh
  beside each must be byte-identical to the repository's tools/ops/ci/test-stages.sh.

.EXAMPLE
  pwsh tools/ops/ci/node-ci.ps1 -Repo . -Commit d0e3606f
  pwsh tools/ops/ci/node-ci.ps1 -Repo . -Commit origin/integrate/wave17 -Suites Data,Rest
  pwsh tools/ops/ci/node-ci.ps1 -Repo . -Snapshot -BuildOnly
#>
param(
    [Parameter(Mandatory)] [string] $Repo,
    [string] $Commit,
    # Test the worktree as it is (HEAD + uncommitted + untracked, honouring .gitignore) without touching its branch or index.
    # The snapshot is made here, with a private index file, because agents doing it by hand in shared shells/temp files
    # crossed snapshots between concurrent lanes and staged into real indexes (2026-10-22).
    [switch] $Snapshot,
    [string[]] $Suites = @('Game', 'World', 'Data', 'Rest'),
    # Off by default (2026-10-22): the MariaDB/PostgreSQL provider tests make Data ~40-50 min and force Data suites
    # through one node-wide lock. Without the ARCANECORE_TEST_* variables those tests skip and Data runs in parallel.
    [switch] $WithDbProviders,
    # Compile gate only (2026-10-22: no tests while lanes are in progress; one full run before PRs).
    [switch] $BuildOnly,
    # dotnet test --filter expression ANDed into both stages (RED/GREEN proofs without whole-suite runs). Skips the MockClient
    # self-test, so a filtered run is never a full gate.
    [string] $Filter,
    # Sets GATE_TIME_BUDGETS=off for the gate: no hard stop at the suite time budget and no class-budget failure (timings stay in the
    # summary as info). For genuinely slow hardware.
    [switch] $NoTimeBudget,
    [string] $NodeHost = 'Node@10.77.0.2',
    [string] $Key = "$env:USERPROFILE\.ssh\sonder-node-ed25519"
)
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'OpsEnv.ps1')
$ledgerPath = Get-OpsEnv 'NODE_CI_FLAKE_LEDGER'
# Node1 (14 cores, ~39 GB in WSL) is CPU-bound, not memory-bound. 2026-10-28: full gates are capped at 2 at once, because the
# timing-sensitive tests fail on a starved node for reasons that have nothing to do with the change under test.
$maxConcurrent = if ($env:NODE_CI_MAX_CONCURRENT) { [int]$env:NODE_CI_MAX_CONCURRENT } else { 2 }
$maxFiltered = if ($env:NODE_CI_MAX_FILTERED) { [int]$env:NODE_CI_MAX_FILTERED } else { 6 }
# A full gate has no -Filter and no -BuildOnly. -BuildOnly (a compile check, no tests) shares the light queue with filtered runs.
$light = [bool]$Filter -or [bool]$BuildOnly
$fullGate = -not $light
$self = $PID
$ssh = 'C:\Windows\System32\OpenSSH\ssh.exe'
$scp = 'C:\Windows\System32\OpenSSH\scp.exe'
$opts = @('-i', $Key, '-o', 'BatchMode=yes', '-o', 'IdentitiesOnly=yes', '-o', 'StrictHostKeyChecking=yes', '-o', 'ServerAliveInterval=30', '-o', 'ServerAliveCountMax=4')
# A filtered run (one build plus a few tests) queues separately from full gates, or every RED/GREEN proof waits hours behind them.
# It still has its own cap: 2026-10-27, with no cap, 20+ concurrent filtered runs drove Node1 to load 124 with 1 GB free, and
# starved testhosts fail on timing, not bugs.
$myStart = (Get-CimInstance Win32_Process -Filter "ProcessId=$self").CreationDate
$cap = if ($light) { $maxFiltered } else { $maxConcurrent }
$queuedAt = $null; $lastNote = $null
while ($true) {
    $ahead = @(Get-CimInstance Win32_Process -Filter "Name='pwsh.exe'" | Where-Object {
            $_.ProcessId -ne $self -and $_.CommandLine -match 'node-ci\.ps1' -and $_.CommandLine -notmatch 'node-ci-queue' -and
            [bool]($_.CommandLine -match '\s-(Filter|BuildOnly)\b') -eq $light -and $_.CreationDate -lt $myStart }).Count
    if ($ahead -lt $cap) { break }
    $now = Get-Date
    if (-not $queuedAt) { $queuedAt = $now }
    if (-not $lastNote -or ($now - $lastNote).TotalMinutes -ge 10) {
        $kind = if ($light) { 'filtered/build-only run(s)' } else { 'full gate(s)' }
        Write-Output "queued: $ahead earlier $kind running from this host (max $cap concurrent); waiting for a slot"
        $lastNote = $now
    }
    Start-Sleep -Seconds 30
}
if ($queuedAt) { Write-Output "slot free after $([math]::Round(((Get-Date) - $queuedAt).TotalMinutes, 1))m" }
# Then wait for headroom on the node itself (runs from other hosts or shells share it): 1-min load under 3x its 14 cores and
# 6 GB available. A failed probe does not block; the run's own ssh will fail loudly instead.
while ($true) {
    $probe = & $ssh @opts $NodeHost 'wsl -d Ubuntu-24.04 -u root -e cat /proc/loadavg /proc/meminfo' 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $probe) { break }
    $load = [double](($probe | Select-Object -First 1) -split ' ')[0]
    $mem = @(($probe | Where-Object { $_ -match '^MemAvailable:' } | Select-Object -First 1) -split '\s+')
    if ($mem.Count -lt 2) { break }
    $availKb = [long]$mem[1]
    if ($load -lt 42 -and $availKb -gt 6GB / 1KB) { break }
    Write-Output "waiting for Node1 headroom: load $load, available $([math]::Round($availKb / 1MB, 1)) GB"
    Start-Sleep -Seconds 60
}

if ($Snapshot) {
    if ($Commit) { throw 'pass -Commit or -Snapshot, not both' }
    $idx = Join-Path $env:TEMP "node-ci-$PID-$([guid]::NewGuid().ToString('N')).idx"
    $env:GIT_INDEX_FILE = $idx
    try {
        git -C $Repo read-tree HEAD; if ($LASTEXITCODE) { throw 'read-tree failed' }
        git -C $Repo add -A; if ($LASTEXITCODE) { throw 'add -A failed' }
        $tree = (git -C $Repo write-tree).Trim()
        $Commit = (git -C $Repo commit-tree $tree -p HEAD -m 'CI snapshot (not a branch commit)').Trim()
    } finally {
        Remove-Item Env:GIT_INDEX_FILE
        Remove-Item $idx, "$idx.lock" -ErrorAction SilentlyContinue
    }
    Write-Output "snapshot $Commit of $Repo (HEAD + working tree)"
} elseif (-not $Commit) { throw 'pass -Commit <rev> or -Snapshot' }
$sha = (git -C $Repo rev-parse --verify "$Commit^{commit}").Trim()
if ($LASTEXITCODE -ne 0 -or -not $sha) { throw "unknown commit $Commit" }
$short = $sha.Substring(0, 12)
$runId = "$short-$(Get-Date -Format yyyyMMddHHmmss)-$PID"
$stagesSrc = Join-Path $PSScriptRoot 'test-stages.sh'
if (-not (Test-Path $stagesSrc)) { throw "missing $stagesSrc (copy of the repository's tools/ops/ci/test-stages.sh)" }
$tar = Join-Path $env:TEMP "arcanecore-ci-$runId.tar"
git -C $Repo archive --format=tar -o $tar $sha
if ($LASTEXITCODE -ne 0) { throw 'git archive failed' }

# Node1's OpenSSH DefaultShell is cmd (found 2026-10-22): cmd acts on | < > & even inside quoted strings, so no remote
# command may contain shell metacharacters. mkdir works under cmd and PowerShell alike; "already exists" is expected.
& $ssh @opts $NodeHost 'mkdir C:\NewBuild\arcanecore-ci' 2>$null
& $scp @opts $tar "${NodeHost}:C:/NewBuild/arcanecore-ci/$runId.tar"
if ($LASTEXITCODE -ne 0) { throw 'scp failed' }
Remove-Item $tar

# 'pwsh -File ... -Suites Game,World' arrives as ONE string 'Game,World': split on commas and spaces.
$Suites = if ($BuildOnly) { @() } else { @($Suites | ForEach-Object { $_ -split '[,\s]+' } | Where-Object { $_ }) }
$targets = ($Suites | ForEach-Object { "tests/ci/$_.slnf" }) -join ' '
$dbFlag = if ($WithDbProviders) { 1 } else { 0 }
if ($Filter -and $Filter.Contains("'")) { throw 'filter must not contain a single quote' }
$filterArg = if ($Filter) { "--filter '$Filter'" } else { '' }
$budgetEnv = if ($NoTimeBudget) { 'GATE_TIME_BUDGETS=off ' } else { '' }
$selftestArg = if (-not $Filter -and $Suites -contains 'Rest') { '--selftest' } else { '' }
$runTests = if ($BuildOnly) { 0 } else { 1 }
$full = if ($fullGate) { 1 } else { 0 }
$dbPass = if ($env:ARCANECORE_NODE_TEST_DB_PASS) { $env:ARCANECORE_NODE_TEST_DB_PASS } else { 'arcane' }
$dbSetup = if ($WithDbProviders) { @"
export ARCANECORE_TEST_MARIADB='Server=127.0.0.1;Port=3306;User=root;Password=$dbPass;'
export ARCANECORE_TEST_POSTGRES='Host=127.0.0.1;Port=5432;Username=arcane;Password=$dbPass;'
service mariadb status >/dev/null 2>&1 || service mariadb start >/dev/null
service postgresql status >/dev/null 2>&1 || service postgresql start >/dev/null
"@ } else { 'echo "db providers: skipped (MariaDB/PostgreSQL tests skip; pass -WithDbProviders to run them)"' }
$bash = @"
set -u
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
S=/mnt/c/NewBuild/arcanecore-ci/$runId
trap 'rm -f "`$0"' EXIT
# At most 2 full gates on the node at once, whichever host or shell started them: two flock slots, held by this script while it runs
# the gate as a child without the slot's descriptors (a process the gate leaves running, such as the compiler server, must not keep
# the slot after the gate ends).
if [ $full = 1 ] && [ -z "`${NODE_CI_INNER:-}" ]; then
  mkdir -p /root/ci; exec 8>/root/ci/full-gate-slot1.lock 9>/root/ci/full-gate-slot2.lock
  slot=; t0=`$(date +%s); last=-1
  while [ -z "`$slot" ]; do
    if flock -n 8; then slot=1; exec 9>&-; elif flock -n 9; then slot=2; exec 8>&-; else
      w=`$(( `$(date +%s) - t0 ))
      if [ `$w -ge 21600 ]; then echo "CI RESULT: FAIL (no full-gate slot in 6 h)"; rm -f `$S.tar `$S-stages.sh; exit 1; fi
      if [ `$last -lt 0 ] || [ `$(( w - last )) -ge 300 ]; then echo "queued on Node1: both full-gate slots busy (max 2 concurrent full gates); waiting (`${w}s so far)"; last=`$w; fi
      sleep 20
    fi
  done
  echo "got full-gate slot `$slot after `$(( `$(date +%s) - t0 ))s"
  NODE_CI_INNER=1 bash "`$0" 8>&- 9>&-; exit `$?
fi
$dbSetup
W=/root/ci/$runId; rm -rf `$W; mkdir -p `$W/src `$W/logs; cd `$W/src
tar -xf `$S.tar && rm -f `$S.tar
echo "commit $sha run $runId"
# Locked, as in ci.yml: a package graph that differs from the committed lock files fails the run here (NU1004) instead of being
# re-resolved and written back, and the build never runs on a restore that failed. The error lines come first: at minimal verbosity
# the NU1004 line is near the top and the tail is "Restored <project>" lines. A failure with no ' error ' line (no SDK) prints the tail.
dotnet restore ArcaneCore.slnx --locked-mode > `$W/logs/restore.log 2>&1; rc=`$?; echo "restore rc=`$rc"
if [ `$rc -ne 0 ]; then if grep -qE ' error ' `$W/logs/restore.log; then grep -E ' error ' `$W/logs/restore.log | sort -u | head -20; else tail -20 `$W/logs/restore.log; fi; rm -f `$S-stages.sh; echo "CI RESULT: FAIL"; exit 1; fi
dotnet build ArcaneCore.slnx -c Release --no-restore -nodeReuse:false > `$W/logs/build.log 2>&1; rc=`$?; echo "build rc=`$rc"; grep -E 'Warning\(s\)|Error\(s\)' `$W/logs/build.log | tail -2
if [ `$rc -ne 0 ]; then grep -E ' error ' `$W/logs/build.log | sort -u | head -20; rm -f `$S-stages.sh; echo "CI RESULT: FAIL"; exit 1; fi
fail=0
if [ $runTests = 1 ]; then
  # Every run shares one MariaDB and one PostgreSQL: concurrent Data suites starve them into MySqlConnector "Command Timeout expired"
  # failures (102 of them, 2026-10-12), so with the providers on, Data takes a node-wide lock. 2026-10-22: with them the Data suite
  # takes ~50 min, so the lock waits up to 5 h. Each dotnet test is capped at 60 min (timeout -k kills a hung testhost), so an
  # abandoned or stuck run frees Node1 by itself.
  lock=; [ $dbFlag = 1 ] && lock="flock -w 18000 /root/ci/data-suite.lock"
  ${budgetEnv}GATE_WRAP="timeout -k 60 3600" GATE_WRAP_DATA="`$lock" bash `$S-stages.sh --stage all --logs `$W/logs --commit $sha $filterArg $selftestArg $targets || fail=1
fi
rm -f `$S-stages.sh
echo "CI RESULT: `$( [ `$fail -eq 0 ] && echo PASS || echo FAIL )$( if ($Filter) { ' (FILTERED, not a full gate)' } elseif ($BuildOnly) { ' (BUILD ONLY, no tests)' } )"
# 2026-10-24: every run left its ~1 GB src/ tree (bin, obj) behind; 119 runs grew the WSL disk until Node1's C: hit 0 bytes
# and WSL stopped starting. Keep the logs, drop the tree, and prune run dirs older than a week.
cd /; rm -rf `$W/src; find /root/ci -mindepth 1 -maxdepth 1 -type d -mtime +7 -exec rm -rf {} + 2>/dev/null
exit `$fail
"@
# Ship the scripts as files (LF endings) and run them by path: the old `echo <b64> | base64 -d | bash` broke once the node's
# ssh shell became cmd, which ran `base64` itself. A CRLF line makes Linux bash fail ("set: -\r: invalid option").
$sh = Join-Path $env:TEMP "arcanecore-ci-$runId.sh"
[IO.File]::WriteAllText($sh, ($bash -replace "`r", ''), [Text.UTF8Encoding]::new($false))
$stagesTmp = Join-Path $env:TEMP "arcanecore-ci-$runId-stages.sh"
[IO.File]::WriteAllText($stagesTmp, ([IO.File]::ReadAllText($stagesSrc) -replace "`r", ''), [Text.UTF8Encoding]::new($false))
& $scp @opts $sh "${NodeHost}:C:/NewBuild/arcanecore-ci/$runId.sh"
if ($LASTEXITCODE -ne 0) { throw 'scp of the CI script failed' }
& $scp @opts $stagesTmp "${NodeHost}:C:/NewBuild/arcanecore-ci/$runId-stages.sh"
if ($LASTEXITCODE -ne 0) { throw 'scp of test-stages.sh failed' }
Remove-Item $sh, $stagesTmp
$flaky = [Collections.Generic.List[string]]::new()
& $ssh @opts $NodeHost "wsl -d Ubuntu-24.04 -u root -e bash /mnt/c/NewBuild/arcanecore-ci/$runId.sh" 2>&1 | ForEach-Object {
    $line = "$_" -replace "`0", ''
    if ($line -cmatch '^FLAKY \(passed on rerun\): (.+)$') { $flaky.Add($Matches[1].Trim()) }
    $line
}
$rc = $LASTEXITCODE
# The node keeps flake-ledger.txt with the run's logs; this keeps a running record across runs on this host.
if ($flaky.Count -gt 0) {
    if ($ledgerPath) {
        if (-not (Test-Path $ledgerPath)) {
            $header = "# Flake ledger`n`nTiming-stage tests that failed once and passed their isolated rerun; node-ci.ps1 appends one row per flaky test.`n`n| When (UTC) | Commit | Run | Test |`n|---|---|---|---|`n"
            [IO.File]::WriteAllText($ledgerPath, $header, [Text.UTF8Encoding]::new($false))
        }
        $when = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm')
        $rows = ($flaky | ForEach-Object { "| $when | $short | $runId | $_ |" }) -join "`n"
        [IO.File]::AppendAllText($ledgerPath, "$rows`n", [Text.UTF8Encoding]::new($false))
        Write-Output "flake ledger: $($flaky.Count) row(s) appended to $ledgerPath"
    } else {
        Write-Output 'flake ledger: set NODE_CI_FLAKE_LEDGER to keep a local record'
    }
}
exit $rc
