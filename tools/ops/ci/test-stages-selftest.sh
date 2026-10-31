#!/usr/bin/env bash
# Self-test of test-stages.sh: puts a fake `dotnet` first on PATH that prints canned VSTest console output for each stage and rerun,
# runs the gate once per case and checks its exit code and the lines that decide it. Run: bash tools/ops/ci/test-stages-selftest.sh
# Exit 0 = every case behaved, 1 = a case did not (each one is printed). Needs only bash and coreutils; no dotnet, no build.
set -u
# Every case sets the knobs it tests. A gate that runs this self-test (TestStagesScriptTests) exports its own GATE_* settings, e.g.
# GATE_WRAP="timeout ..." on Node1 (the killedjob stub's parent becomes timeout, so the job lives on to write rc 137) or a local
# GATE_SUITE_BUDGET_MIN / GATE_TIME_BUDGETS=off (every budget case passes for the wrong reason). None of them may reach the cases.
while read -r v; do unset "$v"; done < <(compgen -e | grep '^GATE_')
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
[ "$1" = run ] && { echo "self-test: 59 checks passed"; exit 0; }
block() { echo "Test Run $4."; echo "Total tests: $(( $1 + $2 + $3 ))"; echo "     Passed: $1"; [ "$2" -gt 0 ] && echo "     Failed: $2"; echo "    Skipped: $3"; }
case "$flt" in
  *'!=Timing'*)
    case "$SIM_CASE" in
      main-fails) echo "  Failed Ns.A.Broken [1 ms]"; block 9 1 0 Failed; exit 1 ;;
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
  env SIM_CASE=$name SIM_ARGS="$work/args-$id.txt" SIM_EVENTS="$work/events-$id.txt" $envs PATH="$work/bin:$PATH" bash "$gate" --logs "$work/logs-$id" --commit selftest $extra --selftest $tgts > "$out" 2>&1
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
check main-fails 1 "" 'main tests/ci/X.slnf rc=1'
check main-empty 1 "" 'executed 0 tests'
check filtered-timing-empty 0 "--filter FullyQualifiedName~X" '^GATE RESULT: PASS$'

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
usage empty-label "A=1" ".csproj"
usage knob-empty-budgets "GATE_TIME_BUDGETS=maybe" "tests/ci/X.slnf"
[ $bad = 0 ] && echo "test-stages self-test: all cases behaved" || echo "test-stages self-test: FAILED"
exit $bad
