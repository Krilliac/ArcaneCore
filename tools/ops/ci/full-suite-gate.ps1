# Local full gate for a worktree: restores the solution in locked mode, builds it, then runs every test suite and the MockClient
# self-test against that build.
# Run it from the worktree root, normally through the build-slot gate so it waits its turn and runs at BelowNormal:
#   pwsh -NoProfile -File tools/ops/ci/run-in-slot.ps1 -Lane <name> -WorkDir <worktree> -Command 'pwsh -NoProfile -File tools/ops/ci/full-suite-gate.ps1'
# -Suites names tests/ci/<name>.slnf files (default Game, World, Data, Rest = ci.yml's four).
# Prints RESTORE_EXIT, BUILD_EXIT, SUITE_EXIT <suite>=<rc> per suite, TIMING_EXIT and SELFTEST_EXIT. Exit 0 = all passed, 1 = a suite or the
# self-test failed, 2 = the restore or the build failed (nothing ran: a failed build must never read as "0 failures"). dotnet comes
# from DOTNET_ROOT, else PATH.
# A suite is a solution filter in tests/ci (the four of ci.yml's matrix, which together cover every test project once), run with ci.yml's
# Category!=Timing filter. The Category=Timing tests then run serially through tools/ops/ci/test-stages.sh --stage timing (ci.yml's
# own stage; one isolated rerun per failure), printing TIMING_EXIT. That stage needs bash on PATH: without it TIMING_EXIT=bash-missing
# and the gate fails, because timing tests that did not run are not a pass.
param(
    [string[]]$Suites = @('Game', 'World', 'Data', 'Rest')
)
$dotnet = if ($env:DOTNET_ROOT -and (Test-Path (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))) { Join-Path $env:DOTNET_ROOT 'dotnet.exe' } else { 'dotnet' }
$fail = 0
# Locked, as in ci.yml: a package graph that differs from the committed lock files fails here (NU1004) instead of being re-resolved
# and written back into the worktree, which the build's implicit restore would do. -m:2 as the build, to leave the live server headroom.
& $dotnet restore ArcaneCore.slnx --locked-mode -m:2 -nodeReuse:false -v:minimal
"RESTORE_EXIT=$LASTEXITCODE"
if ($LASTEXITCODE -ne 0) { exit 2 }
& $dotnet build ArcaneCore.slnx --no-restore -m:2 -nodeReuse:false -p:UseSharedCompilation=false -v:minimal
"BUILD_EXIT=$LASTEXITCODE"
if ($LASTEXITCODE -ne 0) { exit 2 }
foreach ($s in $Suites) {
    "=== SUITE $s ==="
    & $dotnet test "tests/ci/$s.slnf" --no-build --filter "Category!=Timing" --logger "console;verbosity=minimal"
    "SUITE_EXIT $s=$LASTEXITCODE"
    if ($LASTEXITCODE -ne 0) { $fail = 1 }
}
"=== TIMING ==="
$bash = Get-Command bash -ErrorAction SilentlyContinue | Where-Object { $_.Source -notmatch 'Windows.System32' } | Select-Object -First 1
if (-not $bash) {
    'TIMING_EXIT=bash-missing'
    $fail = 1
} else {
    # The build above is Debug (no -c), so point the stage at it; test-stages.sh defaults to Release.
    $env:GATE_CONFIGURATION = 'Debug'
    & $bash.Source tools/ops/ci/test-stages.sh --stage timing --logs timing-logs ($Suites | ForEach-Object { "tests/ci/$_.slnf" })
    "TIMING_EXIT=$LASTEXITCODE"
    if ($LASTEXITCODE -ne 0) { $fail = 1 }
}
"=== SELFTEST ==="
& $dotnet run --project tools/ArcaneCore.MockClient --no-build -- self-test
"SELFTEST_EXIT=$LASTEXITCODE"
if ($LASTEXITCODE -ne 0) { $fail = 1 }
exit $fail
