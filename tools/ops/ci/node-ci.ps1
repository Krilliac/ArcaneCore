<#
.SYNOPSIS
  Run ArcaneCore's CI (.github/workflows/ci.yml) for one commit on Node1's WSL Ubuntu 24.04, with the same MariaDB 10.11 and
  PostgreSQL 16 services and connection strings as the hosted runners.

.DESCRIPTION
  Ships `git archive <commit>` to the node, then runs restore, Release build (warnings are errors via Directory.Build.props),
  each suite (tests/ci/<Suite>.slnf, -m:1) and the Rest-only MockClient self-test. Suites run one after another in one job,
  not as a matrix. Each suite's summary lines and exit codes are printed, and the full logs stay on the node under
  /root/ci/<sha>/logs. One-time node setup (mariadb-server, postgresql-16, .NET 10 SDK in /opt/dotnet, root/arcane and arcane/arcane
  logins) was done 2026-10-10. If the services are not running, this script starts them.

.EXAMPLE
  pwsh tools/ops/ci/node-ci.ps1 -Repo . -Commit d0e3606f
  pwsh tools/ops/ci/node-ci.ps1 -Repo . -Commit origin/integrate/wave17 -Suites Data,Rest
#>
param(
    [Parameter(Mandatory)] [string] $Repo,
    [Parameter(Mandatory)] [string] $Commit,
    [string[]] $Suites = @('Game', 'World', 'Data', 'Rest'),
    [string] $NodeHost = 'Node@10.77.0.2',
    [string] $Key = "$env:USERPROFILE\.ssh\sonder-node-ed25519"
)
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'OpsEnv.ps1')
# Node1 (14 cores, ~39 GB in WSL) is CPU-bound, not memory-bound: 9 parallel runs measured load ~57 with 28 GB still free
# (2026-10-12). Past MaxConcurrent runs each one just gets slower, so wait for a slot first.
$maxConcurrent = if ($env:NODE_CI_MAX_CONCURRENT) { [int]$env:NODE_CI_MAX_CONCURRENT } else { 5 }
$self = $PID
while (@(Get-CimInstance Win32_Process -Filter "Name='pwsh.exe'" | Where-Object {
            $_.ProcessId -ne $self -and $_.CommandLine -match 'node-ci\.ps1' -and $_.CommandLine -notmatch 'node-ci-queue' -and
            $_.CreationDate -lt (Get-CimInstance Win32_Process -Filter "ProcessId=$self").CreationDate }).Count -ge $maxConcurrent) {
    Start-Sleep -Seconds 30
}
$ssh = 'C:\Windows\System32\OpenSSH\ssh.exe'
$scp = 'C:\Windows\System32\OpenSSH\scp.exe'
$opts = @('-i', $Key, '-o', 'BatchMode=yes', '-o', 'IdentitiesOnly=yes', '-o', 'StrictHostKeyChecking=yes', '-o', 'ServerAliveInterval=30', '-o', 'ServerAliveCountMax=4')

$sha = (git -C $Repo rev-parse --verify "$Commit^{commit}").Trim()
if ($LASTEXITCODE -ne 0 -or -not $sha) { throw "unknown commit $Commit" }
$short = $sha.Substring(0, 12)
$runId = "$short-$(Get-Date -Format yyyyMMddHHmmss)-$PID"
$tar = Join-Path $env:TEMP "arcanecore-ci-$runId.tar"
git -C $Repo archive --format=tar -o $tar $sha
if ($LASTEXITCODE -ne 0) { throw 'git archive failed' }

& $ssh @opts $NodeHost 'powershell -NoProfile -Command "New-Item -ItemType Directory -Force C:\NewBuild\arcanecore-ci | Out-Null"'
& $scp @opts $tar "${NodeHost}:C:/NewBuild/arcanecore-ci/$runId.tar"
if ($LASTEXITCODE -ne 0) { throw 'scp failed' }
Remove-Item $tar

# 'pwsh -File ... -Suites Game,World' arrives as ONE string 'Game,World': split on commas and spaces.
$Suites = @($Suites | ForEach-Object { $_ -split '[,\s]+' } | Where-Object { $_ })
$suiteList = ($Suites -join ' ')
$dbPass = if ($env:ARCANECORE_NODE_TEST_DB_PASS) { $env:ARCANECORE_NODE_TEST_DB_PASS } else { 'arcane' }
$bash = @"
set -u
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export ARCANECORE_TEST_MARIADB="Server=127.0.0.1;Port=3306;User=root;Pwd=$dbPass;"
export ARCANECORE_TEST_POSTGRES="Host=127.0.0.1;Port=5432;Username=arcane;Pwd=$dbPass;"
service mariadb status >/dev/null 2>&1 || service mariadb start >/dev/null
service postgresql status >/dev/null 2>&1 || service postgresql start >/dev/null
W=/root/ci/$runId; rm -rf `$W; mkdir -p `$W/src `$W/logs; cd `$W/src
tar -xf /mnt/c/NewBuild/arcanecore-ci/$runId.tar && rm -f /mnt/c/NewBuild/arcanecore-ci/$runId.tar
echo "commit $sha run $runId"
dotnet restore ArcaneCore.slnx > `$W/logs/restore.log 2>&1; echo "restore rc=`$?"
dotnet build ArcaneCore.slnx -c Release --no-restore -nodeReuse:false > `$W/logs/build.log 2>&1; rc=`$?; echo "build rc=`$rc"; grep -E 'Warning\(s\)|Error\(s\)' `$W/logs/build.log | tail -2
if [ `$rc -ne 0 ]; then grep -E ' error ' `$W/logs/build.log | sort -u | head -20; exit 1; fi
fail=0
for s in $suiteList; do
  # Every run shares one MariaDB and one PostgreSQL: concurrent Data suites starve them into MySqlConnector "Command Timeout expired"
  # failures (102 of them, 2026-10-12). Data suites take a node-wide lock and run one at a time; the other suites stay parallel.
  lock=; [ "`$s" = Data ] && lock="flock -w 5400 /root/ci/data-suite.lock"
  # Bounded (2026-10-12): an abandoned run (local client killed) held the lock for hours and parked three later runs behind it.
  # The lock wait is capped at 90 min and each suite at 60 min (timeout -k kills a hung testhost), so a stuck run frees Node1 by itself.
  `$lock timeout -k 60 3600 dotnet test tests/ci/`$s.slnf -c Release --no-build -m:1 --verbosity normal > `$W/logs/`$s.log 2>&1; rc=`$?
  echo "suite `$s rc=`$rc"; grep -E '^(Passed|Failed|Skipped)!|^Total tests:|^ +(Passed|Failed|Skipped): ' `$W/logs/`$s.log
  if [ `$rc -ne 0 ]; then fail=1; grep -E '^\s+Failed [A-Za-z]' `$W/logs/`$s.log | head -30; fi
  if [ "`$s" = Rest ]; then dotnet run --project tools/ArcaneCore.MockClient -c Release --no-build -- self-test > `$W/logs/selftest.log 2>&1; rc=`$?; echo "selftest rc=`$rc"; tail -3 `$W/logs/selftest.log; [ `$rc -ne 0 ] && fail=1; fi
done
echo "CI RESULT: `$( [ `$fail -eq 0 ] && echo PASS || echo FAIL )"
exit `$fail
"@
$b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($bash))
& $ssh @opts $NodeHost "wsl -d Ubuntu-24.04 -u root -e bash -c `"echo $b64 | base64 -d | bash`"" 2>&1 | ForEach-Object { $_ -replace "`0", '' }
exit $LASTEXITCODE
