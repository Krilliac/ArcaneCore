#!/usr/bin/env bash
# Self-test of test-stages.sh: puts a fake `dotnet` first on PATH that prints canned VSTest console output for each stage and rerun,
# runs the gate once per case and checks its exit code and the lines that decide it. Run: bash tools/ops/ci/test-stages-selftest.sh
# Exit 0 = every case behaved, 1 = a case did not (each one is printed). Needs only bash and coreutils; no dotnet, no build.
set -u
# Every case sets the knobs it tests. A gate that runs this self-test (TestStagesScriptTests) exports its own GATE_* settings, e.g.
# GATE_WRAP="timeout ..." on Node1 (the killedjob stub's parent becomes timeout, so the job lives on to write rc 137) or a local
# GATE_SUITE_BUDGET_MIN / GATE_TIME_BUDGETS=off (every budget case passes for the wrong reason). None of them may reach the cases.
while read -r v; do unset "$v"; done < <(compgen -e | grep '^GATE_')
# The sweep guard and the batching leg read these too: a host that has the client DBCs or a batching window must not change the cases.
unset ARCANECORE_TEST_DBC_DIR ARCANECORE_TEST_SPELL_BATCH_MS
here=$(cd "$(dirname "$0")" && pwd)
gate=$here/test-stages.sh
work=$(mktemp -d); trap 'rm -rf "$work"' EXIT
mkdir -p "$work/bin"
printf '#!/usr/bin/env bash
sleep 4
exec "$@"
' > "$work/bin/slowwrap"; chmod +x "$work/bin/slowwrap"
cat > "$work/bin/dotnet" <<'SHIM'
#!/usr/bin/env bash
flt=; prev=; trx=; tmo=
for a in "$@"; do
  [ "$prev" = --filter ] && flt=$a
  [ "$prev" = --results-directory ] && trx=$a
  case "$a" in RunConfiguration.TestSessionTimeout=*) tmo=${a#*=} ;; esac
  prev=$a
done
[ -n "${SIM_ARGS:-}" ] && echo "$*" >> "$SIM_ARGS"
# mktrx <test> <seconds>...: a .trx in the results directory with one result per "<test> <hh:mm:ss>" pair.
mktrx() {
  mkdir -p "$trx"; { echo "<TestRun><Results>"
    while [ $# -gt 1 ]; do echo "<UnitTestResult executionId=\"1\" testName=\"$1\" duration=\"$2\" outcome=\"Passed\" />"; shift 2; done
    echo "</Results></TestRun>"; } > "$trx/$RANDOM.trx"
}
[ -n "${SIM_BATCH:-}" ] && [ "$1" = test ] && echo "${ARCANECORE_TEST_SPELL_BATCH_MS:-unset} $2 $flt" >> "$SIM_BATCH"
# mktrx_skipped <test>: a .trx whose only result was skipped (xunit under VSTest reports it NotExecuted).
mktrx_skipped() {
  mkdir -p "$trx"; { echo "<TestRun><Results>"
    echo "<UnitTestResult executionId=\"1\" testName=\"$1\" duration=\"00:00:00\" outcome=\"NotExecuted\" />"
    echo "</Results></TestRun>"; } > "$trx/$RANDOM.trx"
}
[ "$1" = run ] && { echo "self-test: 59 checks passed"; exit 0; }
block() { echo "Test Run $4."; echo "Total tests: $(( $1 + $2 + $3 ))"; echo "     Passed: $1"; [ "$2" -gt 0 ] && echo "     Failed: $2"; echo "    Skipped: $3"; }
case "$flt" in
  *'!=Timing'*)
    case "$SIM_CASE" in
      sweep-skipped) mktrx_skipped Ns.W.SpellContractSweepTests.Sweep; mktrx Ns.W.Other.Quick 00:00:01; block 1 0 1 Successful; exit 0 ;;
      sweep-ran) mktrx Ns.W.SpellContractSweepTests.Sweep 00:00:01; block 10 0 0 Successful; exit 0 ;;
      batch-fail) if [ "${ARCANECORE_TEST_SPELL_BATCH_MS:-}" = 400 ]; then echo "  Failed Ns.G.Batchy [1 ms]"; block 9 1 0 Failed; exit 1; fi; block 10 0 0 Successful; exit 0 ;;
      main-fails) echo "  Failed Ns.A.Broken [1 ms]"; block 9 1 0 Failed; exit 1 ;;
      main-flaky|main-twice) echo "  Failed Ns.M.Flaky [1 ms]"; block 9 1 0 Failed; exit 1 ;;
      main-rc124) echo "  Failed Ns.M.Flaky [1 ms]"; block 9 1 0 Failed; exit 124 ;;   # GATE_WRAP's timeout: a kill, never a plain failure
      main-rc255) echo "  Failed Ns.M.Flaky [1 ms]"; block 9 1 0 Failed; exit 255 ;;
      main-setup) echo "  Failed Ns.M.Flaky [1 ms]"; echo "error MSB4057: The target \"VSTest\" does not exist in the project."; block 9 1 0 Failed; exit 1 ;;
      main-timeout) echo "  Failed Ns.M.Flaky [1 ms]"; echo "Aborting test run: test run timeout of ${tmo:-0} milliseconds exceeded."; block 9 1 0 Failed; exit 1 ;;
      main-crash) echo "  Failed Ns.M.Flaky [1 ms]"; echo "  Failed Ns.M.Other [1 ms]"; block 8 1 0 Failed; exit 1 ;;   # 2 lines, f=1: not attributable
      both) echo "  Failed Ns.M.Flaky [1 ms]"; block 9 1 0 Failed; exit 1 ;;
      main-many) for i in 1 2 3 4 5 6; do echo "  Failed Ns.M.Bad$i [1 ms]"; done; block 4 6 0 Failed; exit 1 ;;
      main-abort) echo "  Failed Ns.M.Flaky [1 ms]"; echo "Test Run Aborted."; block 3 1 0 Aborted; exit 1 ;;
      main-empty) echo "No test matches the given testcase filter \`$flt\` in a.dll"; exit 0 ;;
      hardstop)
        if [ -n "$tmo" ]; then
          mktrx Ns.Fast.Q 00:00:01
          echo "  Passed Ns.Fast.Q [1 s]"
          echo "[xUnit.net 00:01:05.00] probe: [Long Running Test] 'Ns.Slow.Sleeper', Elapsed: 00:01:05"
          echo "Aborting test run: test run timeout of $tmo milliseconds exceeded."
          echo "Test Run Aborted."; echo "Total tests: Unknown"; echo "     Passed: 1"; exit 1
        fi
        mktrx Ns.Slow.Sleeper 00:07:00; block 5 0 0 Successful; exit 0 ;;
      killedjob) block 10 0 0 Successful; kill -9 $PPID; exit 0 ;; # the gate's job for this target dies before it can write its rc
      slowclass) mktrx Ns.Heavy.A 00:10:00 Ns.Heavy.B 00:06:40 Ns.Light.C 00:00:02; block 5 0 0 Successful; exit 0 ;;
      concurrent)
        echo "start $2" >> "$SIM_EVENTS"; sleep 1; echo "end $2" >> "$SIM_EVENTS"
        case "$2" in
          *Bad.slnf) mktrx Ns.B.One 00:00:03; echo "  Failed Ns.B.Broken [1 ms]"; block 9 1 0 Failed; exit 1 ;;
          *) mktrx Ns.G.One 00:00:01; block 7 0 0 Successful; exit 0 ;;
        esac ;;
      *) block 10 0 1 Successful; exit 0 ;;
    esac ;;
  *'=Timing'*)
    echo "[xUnit.net 00:00:00.01]   Starting:    X (parallel mode = none, stop on fail = off, explicit = off)"
    case "$SIM_CASE" in
      flaky|twice|rerun-empty) echo "  Failed Ns.T.Flaky(x: 1) [5 ms]"; echo "  Failed Ns.T.Flaky(x: 2) [5 ms]"; block 2 2 0 Failed; exit 1 ;;
      two-flaky) echo "  Failed Ns.T.One [5 ms]"; echo "  Failed Ns.T.Two [5 ms]"; block 1 2 0 Failed; exit 1 ;;
      timing-rc124) echo "  Failed Ns.T.Flaky [5 ms]"; block 2 1 0 Failed; exit 124 ;;
      timing-rc255) echo "  Failed Ns.T.Flaky [5 ms]"; block 2 1 0 Failed; exit 255 ;;
      both) echo "  Failed Ns.T.Flaky [5 ms]"; block 2 1 0 Failed; exit 1 ;;
      timing-empty|filtered-timing-empty) echo "No test matches the given testcase filter \`$flt\` in t.dll"; exit 0 ;;
      killed) echo "partial output, then the testhost was killed"; exit 137 ;;
      aborted) echo "  Failed Ns.T.Flaky [5 ms]"; echo "Test Run Aborted."; block 0 1 0 Aborted; exit 1 ;;
      *) block 3 0 0 Successful; exit 0 ;;
    esac ;;
  FullyQualifiedName=*)
    case "$SIM_CASE" in
      flaky) block 2 0 0 Successful; exit 0 ;;
      two-flaky) cat > /dev/null; block 1 0 0 Successful; exit 0 ;; # swallows stdin: the gate must still rerun both
      twice) echo "  Failed Ns.T.Flaky(x: 1) [5 ms]"; block 1 1 0 Failed; exit 1 ;;
      rerun-empty) echo "No test matches the given testcase filter \`$flt\` in t.dll"; exit 0 ;;
      main-flaky|main-rc124|main-rc255|main-setup|timing-rc124|timing-rc255|both) block 1 0 0 Successful; exit 0 ;;
      main-many) block 1 0 0 Successful; exit 0 ;;
      main-twice) echo "  Failed Ns.M.Flaky [1 ms]"; block 0 1 0 Failed; exit 1 ;;
    esac ;;
esac
echo "fake dotnet: unexpected filter '$flt'"; exit 3
SHIM
chmod +x "$work/bin/dotnet"

bad=0
# check <case> <expected exit> <extra args> <grep -E pattern that must appear> [pattern that must not appear]
# Optional per-call globals: envs (VAR=value words for the gate), tgts (its targets), tag (output file / logs suffix; default the case).
envs= tgts=tests/ci/X.slnf tag=
check() {
  local name=$1 want=$2 extra=$3 must=$4 mustnot=${5:-} id=${tag:-$1}
  local out=$work/$id.out
  # shellcheck disable=SC2086
  env SIM_CASE=$name SIM_ARGS="$work/args-$id.txt" SIM_EVENTS="$work/events-$id.txt" SIM_BATCH="$work/batch-$id.txt" $envs PATH="$work/bin:$PATH" bash "$gate" --logs "$work/logs-$id" --commit selftest $extra --selftest $tgts > "$out" 2>&1
  local got=$?
  if [ "$got" -ne "$want" ] || ! grep -qE "$must" "$out" || { [ -n "$mustnot" ] && grep -qE "$mustnot" "$out"; }; then
    echo "FAIL $id: exit $got (want $want)"; sed 's/^/    /' "$out"; bad=1
  else
    echo "ok   $id: exit $got"
  fi
}
check pass 0 "" 'serial: confirmed'
check flaky 0 "" '^FLAKY \(passed on rerun\): Ns\.T\.Flaky$' 'FAILED TWICE'
[ "$(cut -f4 "$work/logs-flaky/flake-ledger.txt" 2>/dev/null)" = Ns.T.Flaky ] || { echo "FAIL flaky: ledger row missing"; bad=1; }
check two-flaky 0 "" 'timing reruns: 2 \(passed on rerun 2'
check twice 1 "" '^FAILED TWICE: Ns\.T\.Flaky'
check rerun-empty 1 "" '^FAILED TWICE: Ns\.T\.Flaky'
check timing-empty 1 "" 'timing stage matched 0 tests'
check killed 1 "" 'attributable failure lines'
check aborted 1 "" 'test run aborted'
envs="GATE_MAIN_RERUN=off"; tag=main-fails
check main-fails 1 "" 'main tests/ci/X.slnf rc=1' 'rerunning'
envs=; tag=
check main-empty 1 "" 'executed 0 tests'
check filtered-timing-empty 0 "--filter FullyQualifiedName~X" '^GATE RESULT: PASS$'

# Main-stage reruns: a test that fails once and passes alone is FLAKY (gate passes, ledger row); failing alone too fails the gate; more
# unique failures than GATE_MAIN_RERUN_MAX, an aborted run and GATE_MAIN_RERUN=off never rerun and fail as before.
check main-flaky 0 "--stage main" '^FLAKY \(passed on rerun\): Ns\.M\.Flaky$' 'FAILED TWICE|main tests/ci/X.slnf rc=1 failed'
[ "$(cut -f4 "$work/logs-main-flaky/flake-ledger.txt" 2>/dev/null)" = Ns.M.Flaky ] || { echo "FAIL main-flaky: ledger row missing"; bad=1; }
grep -q 'main tests/ci/X.slnf rc=1 executed 10 (passed 9, failed 1)' "$work/main-flaky.out" || { echo "FAIL main-flaky: first-run counts rewritten"; bad=1; }
grep -q '^  Failed Ns.M.Flaky' "$work/main-flaky.out" && grep -q 'main reruns: 1 (passed on rerun 1, failed twice 0)' "$work/main-flaky.out" && grep -q 'flaky tests (passed their rerun): Ns.M.Flaky' "$work/main-flaky.out" || { echo "FAIL main-flaky: Failed line or summary lines missing"; bad=1; }
grep -q 'FullyQualifiedName=Ns.M.Flaky' "$work/args-main-flaky.txt" && [ -f "$work/logs-main-flaky/main-rerun-1.log" ] || { echo "FAIL main-flaky: rerun not run alone / log name"; bad=1; }
check main-twice 1 "--stage main" '^FAILED TWICE: Ns\.M\.Flaky.*' 'FLAKY'
grep -q 'main tests/ci/X.slnf rc=1 failed=1' "$work/main-twice.out" && grep -q 'main reruns: 1 (passed on rerun 0, failed twice 1)' "$work/main-twice.out" || { echo "FAIL main-twice: fail reason or summary missing"; bad=1; }
[ -z "$(cat "$work/logs-main-twice/flake-ledger.txt")" ] || { echo "FAIL main-twice: ledger row written"; bad=1; }
check main-many 1 "--stage main" 'no rerun: 6 failed tests is over GATE_MAIN_RERUN_MAX=5 \(a regression' 'rerunning|FLAKY'
grep -q 'main reruns: 0 (passed on rerun 0, failed twice 0)' "$work/main-many.out" || { echo "FAIL main-many: reruns were made"; bad=1; }
envs="GATE_MAIN_RERUN_MAX=6"; tag=main-many-cap6
check main-many 0 "--stage main" 'main reruns: 6 \(passed on rerun 6' 'FAILED TWICE'
envs="GATE_MAIN_RERUN_MAX=0"; tag=main-flaky-cap0
check main-flaky 1 "--stage main" 'no rerun: 1 failed tests is over GATE_MAIN_RERUN_MAX=0' 'rerunning'
envs="GATE_MAIN_RERUN=off"; tag=main-flaky-off
check main-flaky 1 "--stage main" 'no rerun: reruns are off' 'rerunning|FLAKY'
envs="GATE_MAIN_RERUN=0"; tag=main-flaky-zero
check main-flaky 1 "--stage main" 'main tests/ci/X.slnf rc=1 failed=1' 'rerunning'
envs=; tag=
check main-abort 1 "--stage main" 'main tests/ci/X.slnf: test run aborted' 'rerunning|FLAKY'
grep -q 'no rerun: the test run aborted' "$work/main-abort.out" || { echo "FAIL main-abort: no explanation"; bad=1; }
# Only a plain failure (dotnet rc 1, no build/setup error, attributable Failed lines) is ever cleared by a passing rerun: a kill or timeout
# (rc 124 from GATE_WRAP's timeout, 137, 255 when the job died), a setup error, a timed-out run and a crash all fail the gate, even
# though the stub's rerun of the test would pass. Both stages.
check main-rc124 1 "--stage main" 'no rerun: rc=124 is not a plain test failure' 'rerunning|FLAKY'
check main-rc255 1 "--stage main" 'no rerun: rc=255 is not a plain test failure' 'rerunning|FLAKY'
check main-setup 1 "--stage main" 'no rerun: the log shows a build or test-setup error' 'rerunning|FLAKY'
check main-timeout 1 "--stage main" 'no rerun: the run was stopped at its time budget' 'rerunning|FLAKY'
check main-crash 1 "--stage main" 'no rerun: 1 failed but 2 attributable failure lines' 'rerunning|FLAKY'
check timing-rc124 1 "--stage timing" 'timing tests/ci/X.slnf rc=124 with 1 failed: not a plain test failure' 'rerunning|FLAKY'
check timing-rc255 1 "--stage timing" 'timing tests/ci/X.slnf rc=255 with 1 failed: not a plain test failure' 'rerunning|FLAKY'
# One run with a flaky test in each stage: one flaky summary line naming both, main first.
tag=flaky-line
check flaky 0 "" 'flaky tests \(passed their rerun\): Ns\.T\.Flaky$'
tag=
check both 0 "" '^FLAKY \(passed on rerun\): Ns\.M\.Flaky$' 'FAILED TWICE'
[ "$(grep -c '^flaky tests (passed their rerun)' "$work/both.out")" = 1 ] && grep -q '^flaky tests (passed their rerun): Ns.M.Flaky, Ns.T.Flaky$' "$work/both.out" && grep -q '^FLAKY (passed on rerun): Ns.T.Flaky$' "$work/both.out" || { echo "FAIL both: flaky summary line is not one line naming both stages' tests"; bad=1; }
[ "$(cut -f4 "$work/logs-both/flake-ledger.txt" | tr '
' ' ')" = "Ns.M.Flaky Ns.T.Flaky " ] || { echo "FAIL both: ledger rows"; bad=1; }

# Time budgets. Main-stage runs carry ONE `--` with the xunit diagnostics and the session timeout; the timing stage never gets one.
check hardstop 1 "" 'main tests/ci/X.slnf was stopped at its 20m time budget.*still running: Ns\.Slow\.Sleeper \(00:01:05\)'
[ "$(grep -c 'TestSessionTimeout=1200000' "$work/args-hardstop.txt")" = 1 ] || { echo "FAIL hardstop: main run did not get TestSessionTimeout=1200000"; bad=1; }
[ "$(grep -c 'TestSessionTimeout' "$work/args-hardstop.txt")" = 1 ] || { echo "FAIL hardstop: the timing stage got a session timeout"; bad=1; }
grep -q 'xUnit.LongRunningTestSeconds=60' "$work/args-hardstop.txt" || { echo "FAIL hardstop: no LongRunningTestSeconds"; bad=1; }
[ "$(head -1 "$work/args-hardstop.txt" | grep -o ' -- ' | wc -l)" = 1 ] || { echo "FAIL hardstop: main run did not have exactly one --"; bad=1; }
grep -q 'slowest completed tests' "$work/hardstop.out" && grep -q 'Ns.Fast.Q' "$work/hardstop.out" || { echo "FAIL hardstop: slowest tests not listed"; bad=1; }
# Assemblies of a suite run in parallel in the main stage (no -m:1); the timing stage and GATE_ASSEMBLY_PARALLEL=0 keep -m:1.
[ "$(head -1 "$work/args-hardstop.txt" | grep -c -- ' -m:1 ')" = 0 ] || { echo "FAIL hardstop: main stage still passes -m:1"; bad=1; }
[ "$(tail -1 "$work/args-hardstop.txt" | grep -c -- ' -m:1 ')" = 1 ] || { echo "FAIL hardstop: timing stage lost -m:1"; bad=1; }
envs="GATE_SUITE_BUDGET_MIN=3"; tag=hardstop-3min
check hardstop 1 "" 'was stopped at its 3m time budget'
grep -q 'TestSessionTimeout=180000' "$work/args-hardstop-3min.txt" || { echo "FAIL hardstop-3min: knob not honoured"; bad=1; }
envs="GATE_ASSEMBLY_PARALLEL=0"; tag=asm-serial
check pass 0 "" 'serial: confirmed'
[ "$(grep -c -- ' -m:1 ' "$work/args-asm-serial.txt")" = "$(grep -c '^test ' "$work/args-asm-serial.txt")" ] || { echo "FAIL asm-serial: -m:1 missing from a run"; bad=1; }
envs="GATE_TIME_BUDGETS=off"; tag=hardstop-off
check hardstop 0 "" 'time budgets: OFF' 'was stopped'
grep -q 'TestSessionTimeout' "$work/args-hardstop-off.txt" && { echo "FAIL hardstop-off: a session timeout was still passed"; bad=1; }
envs="GATE_SUITE_BUDGET_MIN=0"; tag=hardstop-zero
check hardstop 0 "" '^GATE RESULT: PASS$'
envs="GATE_CLASS_BUDGET_SEC=0"; tag=slowclass-zero
check slowclass 0 "" '^GATE RESULT: PASS$'
envs=; tag=
check slowclass 1 "" 'test class Ns\.Heavy adds up to 1000s, over the 900s class budget' 'Ns\.Light adds up'
tag=slowclass-filtered
check slowclass 0 "--filter FullyQualifiedName~X" '^GATE RESULT: PASS$' 'class budget'
tag=slowclass-offswitch; envs="GATE_TIME_BUDGETS=off"
check slowclass 0 "" 'slowest classes' 'class budget'
tag=hardstop-filtered; envs=
check hardstop 1 "--filter FullyQualifiedName~X" 'was stopped at its 20m time budget'
# Two targets at once: each result is read from its own log and trx folder, and the failure names only the target that failed.
tag=concurrent; tgts="tests/ci/Good.slnf tests/ci/Bad.slnf"
check concurrent 1 "--stage main" 'Bad.slnf: rc=1 executed 10 passed 9 failed 1' 'main tests/ci/Good.slnf rc=1|Good.slnf: rc=1'
grep -q 'Good.slnf: rc=0 executed 7 passed 7 failed 0' "$work/concurrent.out" || { echo "FAIL concurrent: Good.slnf counts missing"; bad=1; }
[ "$(head -2 "$work/events-concurrent.txt" | cut -d' ' -f1 | tr '
' ' ')" = "start start " ] || { echo "FAIL concurrent: the targets did not overlap"; cat "$work/events-concurrent.txt"; bad=1; }
tag=concurrent-serial; envs="GATE_MAIN_SERIAL=1"
check concurrent 1 "--stage main" 'Bad.slnf: rc=1 executed 10'
[ "$(head -2 "$work/events-concurrent-serial.txt" | cut -d' ' -f1 | tr '
' ' ')" = "start end " ] || { echo "FAIL concurrent-serial: GATE_MAIN_SERIAL=1 overlapped"; bad=1; }
# Nothing of an earlier run may be read as this run's: a stale rc=0 (and a stale trx) sit in the folders, the job dies before writing
# its rc (rc 255 path), and a job that never ran at all has no rc either.
tag=stalerc; envs=; tgts=tests/ci/X.slnf
mkdir -p "$work/logs-stalerc"; echo 0 > "$work/logs-stalerc/main-X.rc"; echo 5 > "$work/logs-stalerc/main-X.secs"
check killedjob 1 "--stage main" 'main tests/ci/X.slnf rc=255'
tag=norc
check killedjob 1 "--stage main" 'main tests/ci/X.slnf rc=255'
tag=stale-trx
mkdir -p "$work/logs-stale-trx/trx/X"; echo '<UnitTestResult testName="Ns.Old.A" duration="00:20:00" />' > "$work/logs-stale-trx/trx/X/old.trx"
check pass 0 "" '^GATE RESULT: PASS$' 'Ns.Old'
# The wall-clock budget times only dotnet: a GATE_WRAP_DATA wait (a flock) longer than the budget does not fail a Data run.
tag=wrapwait; tgts=tests/ci/Data.slnf; envs="GATE_WRAP_DATA=$work/bin/slowwrap GATE_BUDGET_MINUTE_SEC=1 GATE_SUITE_BUDGET_MIN=2"
check pass 0 "" '^GATE RESULT: PASS$' 'budget'
# A stopped target's counts are marked partial, and Test Run Aborted / a non-zero rc are still reported beside the timeout.
tag=hardstop-partial; tgts=tests/ci/X.slnf; envs=
check hardstop 1 "" 'skipped 0 \(partial: stopped at budget\)'
grep -q 'test run aborted' "$work/hardstop-partial.out" && grep -q 'rc=1 failed=0' "$work/hardstop-partial.out" || { echo "FAIL hardstop-partial: aborted / rc not reported beside the timeout"; bad=1; }
# Executed-test guard: a required class whose tests were all skipped fails the gate; a run of it passes; the default needs a World target
# and Spell.dbc in ARCANECORE_TEST_DBC_DIR; GATE_REQUIRE_EXECUTED= turns it off; a filtered run is not judged.
tag=sweep-skipped; envs="GATE_REQUIRE_EXECUTED=SpellContractSweepTests"; tgts=tests/ci/World.slnf
check sweep-skipped 1 "--stage main" 'no SpellContractSweepTests test executed' 'ran [1-9]'
grep -q 'executed-test guard: SpellContractSweepTests ran 0 test' "$work/sweep-skipped.out" || { echo "FAIL sweep-skipped: guard line missing"; bad=1; }
tag=sweep-ran
check sweep-ran 0 "--stage main" 'executed-test guard: SpellContractSweepTests ran 1 test' 'no SpellContractSweepTests'
mkdir -p "$work/dbc"; : > "$work/dbc/Spell.dbc"
tag=sweep-default; envs="ARCANECORE_TEST_DBC_DIR=$work/dbc"
check sweep-skipped 1 "--stage main" 'no SpellContractSweepTests test executed'
tag=sweep-default-ran
check sweep-ran 0 "--stage main" 'executed-test guard: SpellContractSweepTests ran 1 test'
tag=sweep-default-off; envs="ARCANECORE_TEST_DBC_DIR=$work/dbc GATE_REQUIRE_EXECUTED="
check sweep-skipped 0 "--stage main" '^GATE RESULT: PASS$' 'executed-test guard'
tag=sweep-nodbc; envs=
check sweep-skipped 0 "--stage main" '^GATE RESULT: PASS$' 'executed-test guard'
tag=sweep-notworld; envs="ARCANECORE_TEST_DBC_DIR=$work/dbc"; tgts=tests/ci/X.slnf
check sweep-skipped 0 "--stage main" '^GATE RESULT: PASS$' 'executed-test guard'
tag=sweep-filtered; envs="GATE_REQUIRE_EXECUTED=SpellContractSweepTests"; tgts=tests/ci/World.slnf
check sweep-skipped 0 "--filter FullyQualifiedName~X" '^GATE RESULT: PASS$' 'executed-test guard'
# Spell-batching leg: the Game/World targets run again with ARCANECORE_TEST_SPELL_BATCH_MS=400 (the main stage without it); a failure that
# only batching causes fails the gate and is not rerun; the leg is skipped (and says so) for unlisted targets, a caller's own window,
# GATE_BATCH_LEG=off; --stage batch with nothing to run fails.
tag=batch-pass; envs=; tgts=tests/ci/Game.slnf
check batch-pass 0 "" 'batch tests/ci/Game.slnf rc=0 executed 10' 'stage batch: skipped'
grep -q '^unset tests/ci/Game.slnf Category!=Timing$' "$work/batch-batch-pass.txt" && grep -q '^400 tests/ci/Game.slnf Category!=Timing$' "$work/batch-batch-pass.txt" || { echo "FAIL batch-pass: main run did not go without the window or the leg with 400"; cat "$work/batch-batch-pass.txt"; bad=1; }
tag=batch-fail
check batch-fail 1 "" 'batch tests/ci/Game.slnf \(spell batching 400 ms\) rc=1 failed=1' 'rerunning|FLAKY'
grep -q '^  Failed Ns.G.Batchy' "$work/batch-fail.out" || { echo "FAIL batch-fail: Failed line missing"; bad=1; }
tag=batch-unlisted; tgts=tests/ci/X.slnf
check pass 0 "" 'stage batch: skipped \(no target named in GATE_BATCH_TARGETS'
tag=batch-env-set; envs="ARCANECORE_TEST_SPELL_BATCH_MS=0"; tgts=tests/ci/Game.slnf
check batch-pass 0 "" 'stage batch: skipped \(the caller already sets ARCANECORE_TEST_SPELL_BATCH_MS=0\)' 'batch tests/ci/Game.slnf rc'
tag=batch-off; envs="GATE_BATCH_LEG=off"
check batch-pass 0 "" 'stage batch: skipped \(GATE_BATCH_LEG=off\)' 'batch tests/ci/Game.slnf rc'
tag=batch-stage-only; envs=
check batch-pass 0 "--stage batch" 'batch tests/ci/Game.slnf rc=0 executed 10' '^main |^timing '
tag=batch-stage-nothing; tgts=tests/ci/X.slnf
check pass 1 "--stage batch" 'batch stage ran nothing: no target named'
envs=; tag=; tgts=tests/ci/X.slnf
# Usage errors (exit 2): duplicate target names, knobs that are not whole numbers.
usage() { # <name> <env words> <targets>
  env $2 PATH="$work/bin:$PATH" bash "$gate" --logs "$work/logs-$1" $3 > "$work/$1.out" 2>&1; local got=$?
  if [ $got -eq 2 ]; then echo "ok   $1: exit 2"; else echo "FAIL $1: exit $got (want 2)"; sed 's/^/    /' "$work/$1.out"; bad=1; fi
}
usage dup-targets "A=1" "a/X.slnf b/X.slnf"
usage knob-text "GATE_SUITE_BUDGET_MIN=twenty" "tests/ci/X.slnf"
usage knob-negative "GATE_CLASS_BUDGET_SEC=-5" "tests/ci/X.slnf"
usage knob-huge-suite "GATE_SUITE_BUDGET_MIN=99999999999999999999" "tests/ci/X.slnf"
usage knob-suite-cap "GATE_SUITE_BUDGET_MIN=1441" "tests/ci/X.slnf"
usage knob-class-cap "GATE_CLASS_BUDGET_SEC=86401" "tests/ci/X.slnf"
usage knob-long-cap "GATE_LONG_TEST_SEC=3601" "tests/ci/X.slnf"
usage knob-minute-cap "GATE_BUDGET_MINUTE_SEC=61" "tests/ci/X.slnf"
usage knob-minute-zero "GATE_BUDGET_MINUTE_SEC=0" "tests/ci/X.slnf"
usage knob-rerun-text "GATE_MAIN_RERUN=maybe" "tests/ci/X.slnf"
usage knob-rerun-max-text "GATE_MAIN_RERUN_MAX=five" "tests/ci/X.slnf"
usage knob-rerun-max-cap "GATE_MAIN_RERUN_MAX=1001" "tests/ci/X.slnf"
usage empty-label "A=1" ".csproj"
usage knob-batch-leg "GATE_BATCH_LEG=maybe" "tests/ci/X.slnf"
usage knob-batch-ms "GATE_BATCH_MS=0" "tests/ci/X.slnf"
usage knob-empty-budgets "GATE_TIME_BUDGETS=maybe" "tests/ci/X.slnf"
[ $bad = 0 ] && echo "test-stages self-test: all cases behaved" || echo "test-stages self-test: FAILED"
exit $bad
