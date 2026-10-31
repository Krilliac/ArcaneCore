#!/usr/bin/env bash
# Self-test of test-stages.sh: puts a fake `dotnet` first on PATH that prints canned VSTest console output for each stage and rerun,
# runs the gate once per case and checks its exit code and the lines that decide it. Run: bash tools/ops/ci/test-stages-selftest.sh
# Exit 0 = every case behaved, 1 = a case did not (each one is printed). Needs only bash and coreutils; no dotnet, no build.
set -u
here=$(cd "$(dirname "$0")" && pwd)
gate=$here/test-stages.sh
work=$(mktemp -d); trap 'rm -rf "$work"' EXIT
mkdir -p "$work/bin"
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
      slowclass) mktrx Ns.Heavy.A 00:06:40 Ns.Heavy.B 00:05:00 Ns.Light.C 00:00:02; block 5 0 0 Successful; exit 0 ;;
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
check slowclass 1 "" 'test class Ns\.Heavy adds up to 700s, over the 600s class budget' 'Ns\.Light adds up'
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
[ $bad = 0 ] && echo "test-stages self-test: all cases behaved" || echo "test-stages self-test: FAILED"
exit $bad
