#!/usr/bin/env bash
# Two-stage test gate shared by .github/workflows/ci.yml and tools/ops/ci/node-ci.ps1 (which ships this file to Node1 with each run).
#
#   bash tools/ops/ci/test-stages.sh [--stage main|timing|all] [--logs DIR] [--results DIR] [--filter EXPR] [--commit SHA]
#                                    [--selftest] TARGET...
#
# TARGET is a solution, solution filter or test project, already built (dotnet test --no-build).
#   stage main   : every target with Category!=Timing, test collections in parallel as usual.
#   stage timing : every target with Category=Timing, serially (no parallel collections). A test that fails gets exactly one
#                  isolated rerun: passing it prints "FLAKY (passed on rerun): <test>" and appends it to <logs>/flake-ledger.txt
#                  (the gate still passes); failing it again fails the gate.
#   all          : main, then the MockClient self-test (--selftest), then timing.
# A run without --filter whose timing stage executes 0 tests FAILS: an empty filter must never read green. With --filter EXPR
# (ANDed with each stage's category) the rule is instead that the stages together executed at least one test.
# Exit 0 = pass, 1 = fail, 2 = usage. Env: GATE_WRAP prefixes every dotnet test (e.g. "timeout -k 60 3600"), GATE_WRAP_DATA
# additionally prefixes a target named Data.slnf (e.g. a flock), GATE_CONFIGURATION (default Release).
set -u

stage=all logs=gate-logs results= filter= commit= selftest=0
targets=()
while [ $# -gt 0 ]; do
  case "$1" in
    --stage) stage=${2:-}; shift 2 ;;
    --logs) logs=${2:-}; shift 2 ;;
    --results) results=${2:-}; shift 2 ;;
    --filter) filter=${2:-}; shift 2 ;;
    --commit) commit=${2:-}; shift 2 ;;
    --selftest) selftest=1; shift ;;
    -h|--help) sed -n '2,17p' "$0"; exit 0 ;;
    --*) echo "test-stages: unknown option $1" >&2; exit 2 ;;
    *) targets+=("$1"); shift ;;
  esac
done
case "$stage" in main|timing|all) ;; *) echo "test-stages: --stage must be main, timing or all" >&2; exit 2 ;; esac
[ ${#targets[@]} -gt 0 ] || { echo "test-stages: no test target given" >&2; exit 2; }
if [ -z "$commit" ]; then commit=${GITHUB_SHA:-$(git rev-parse HEAD 2>/dev/null || echo unknown)}; fi
mkdir -p "$logs" || exit 2
[ -n "$results" ] || results=$logs/trx
ledger=$logs/flake-ledger.txt
: >> "$ledger"
config=${GATE_CONFIGURATION:-Release}
read -r -a wrap <<< "${GATE_WRAP:-}"
read -r -a wrap_data <<< "${GATE_WRAP_DATA:-}"
# Serial for the timing stage: no parallel collections, one thread, and xunit's diagnostic messages so the log shows it.
serial=(-- RunConfiguration.DisableParallelization=true xUnit.ParallelizeTestCollections=false xUnit.MaxParallelThreads=1 xUnit.DiagnosticMessages=true)

reasons=()
summary=()
fail() { reasons+=("$1"); }

# run_test <log> <trx dir> <filter> <serial 0|1> <target>; returns dotnet's exit code.
run_test() {
  local log=$1 trx=$2 flt=$3 ser=$4 target=$5 pre=("${wrap[@]}")
  [ "$(basename "$target")" = Data.slnf ] && pre=("${wrap_data[@]}" "${wrap[@]}")
  local cmd=("${pre[@]}" dotnet test "$target" -c "$config" --no-build -m:1 --verbosity normal --logger trx --results-directory "$trx" --filter "$flt")
  [ "$ser" = 1 ] && cmd+=("${serial[@]}")
  # stdin from /dev/null: the rerun loop below reads failed-test names from its stdin, and a child that read it would swallow them.
  "${cmd[@]}" > "$log" 2>&1 < /dev/null
}

# counts <log> -> "passed failed skipped" summed over every test run block in the log.
counts() {
  awk '/^[[:space:]]*Passed:[[:space:]]+[0-9]+[[:space:]]*$/ {p+=$2} /^[[:space:]]*Failed:[[:space:]]+[0-9]+[[:space:]]*$/ {f+=$2}
       /^[[:space:]]*Skipped:[[:space:]]+[0-9]+[[:space:]]*$/ {s+=$2} END {print p+0, f+0, s+0}' "$1"
}

# failed_names <log>: one line per "Failed <test>" result line (theory cases repeat their method), as fully qualified names.
failed_names() {
  grep -E '^[[:space:]]+Failed [A-Za-z_]' "$1" | sed -E 's/^[[:space:]]+Failed ([^ (]+).*/\1/'
}

label() { basename "$1" | sed -E 's/\.(slnf|slnx|sln|csproj)$//'; }

a_exec=0 a_pass=0 a_fail=0 a_skip=0 b_exec=0 b_pass=0 b_fail=0 b_skip=0
selftest_line="not run"
rerun_n=0 rerun_pass=0 rerun_fail=0
flaky=()
serial_seen=0

if [ "$stage" != timing ]; then
  flt='Category!=Timing'; [ -n "$filter" ] && flt="($filter)&Category!=Timing"
  summary+=("stage main (parallel, $flt):")
  for t in "${targets[@]}"; do
    log=$logs/main-$(label "$t").log
    run_test "$log" "$results" "$flt" 0 "$t"; rc=$?
    read -r p f s < <(counts "$log"); e=$((p + f))
    a_exec=$((a_exec + e)) a_pass=$((a_pass + p)) a_fail=$((a_fail + f)) a_skip=$((a_skip + s))
    echo "main $t rc=$rc executed $e (passed $p, failed $f), skipped $s"
    summary+=("  $t: rc=$rc executed $e passed $p failed $f skipped $s")
    if [ $rc -ne 0 ] || [ "$f" -gt 0 ]; then fail "main $t rc=$rc failed=$f"; failed_names "$log" | sort -u | head -30 | sed 's/^/  Failed /'; fi
    grep -q 'Test Run Aborted' "$log" && fail "main $t: test run aborted"
    [ -z "$filter" ] && [ "$e" -eq 0 ] && fail "main $t executed 0 tests"
  done
  summary[0]="stage main (parallel, $flt): executed $a_exec (passed $a_pass, failed $a_fail), skipped $a_skip"
fi

if [ "$selftest" = 1 ] && [ "$stage" != timing ]; then
  "${wrap[@]}" dotnet run --project tools/ArcaneCore.MockClient -c "$config" --no-build -- self-test > "$logs/selftest.log" 2>&1 < /dev/null; rc=$?
  echo "selftest rc=$rc"; tail -3 "$logs/selftest.log"
  selftest_line="rc=$rc"
  [ $rc -ne 0 ] && fail "self-test rc=$rc"
fi
summary+=("self-test: $selftest_line")

if [ "$stage" != main ]; then
  flt='Category=Timing'; [ -n "$filter" ] && flt="($filter)&Category=Timing"
  bhead=${#summary[@]}; summary+=("")
  for t in "${targets[@]}"; do
    log=$logs/timing-$(label "$t").log
    run_test "$log" "$logs/trx-timing" "$flt" 1 "$t"; rc=$?
    read -r p f s < <(counts "$log"); e=$((p + f))
    b_exec=$((b_exec + e)) b_pass=$((b_pass + p)) b_fail=$((b_fail + f)) b_skip=$((b_skip + s))
    # xunit's own line for the run (DiagnosticMessages): "Starting: <assembly> (parallel mode = none, ...)".
    grep -qE 'Starting: .*\((parallel mode = none|parallel test collections = off)' "$log" && serial_seen=1
    echo "timing $t rc=$rc executed $e (passed $p, failed $f), skipped $s"
    summary+=("  $t: rc=$rc executed $e passed $p failed $f skipped $s")
    # Every target must bring timing tests of its own (a target that lost its tags would otherwise hide behind another's).
    [ -z "$filter" ] && [ "$e" -eq 0 ] && fail "timing stage of $t matched 0 tests"
    if grep -q 'Test Run Aborted' "$log"; then fail "timing $t: test run aborted (not every test ran)"; continue; fi
    [ $rc -eq 0 ] && [ "$f" -eq 0 ] && continue
    lines=$(failed_names "$log" | grep -c .)
    if [ "$lines" -eq 0 ] || [ "$lines" -ne "$f" ]; then
      fail "timing $t rc=$rc with $f failed but $lines attributable failure lines (crash or kill?)"; continue
    fi
    want=$(failed_names "$log" | sort -u | grep -c .); before=$rerun_n
    while read -r name <&3; do
      rerun_n=$((rerun_n + 1)); rlog=$logs/rerun-$rerun_n.log
      echo "timing: rerunning $name alone"
      run_test "$rlog" "$logs/trx-rerun" "FullyQualifiedName=$name" 1 "$t"; rrc=$?
      read -r rp rf rs < <(counts "$rlog")
      if [ $rrc -eq 0 ] && [ "$rf" -eq 0 ] && [ $((rp + rf)) -ge 1 ]; then
        rerun_pass=$((rerun_pass + 1)); flaky+=("$name")
        echo "FLAKY (passed on rerun): $name"
        printf '%s\t%s\t%s\t%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$commit" "$t" "$name" >> "$ledger"
      else
        rerun_fail=$((rerun_fail + 1))
        echo "FAILED TWICE: $name (rerun rc=$rrc executed $((rp + rf)) failed $rf)"
        fail "timing $name failed its rerun too"
      fi
    done 3< <(failed_names "$log" | sort -u)
    [ $((rerun_n - before)) -eq "$want" ] || fail "timing $t: $want failed tests but $((rerun_n - before)) reruns"
  done
  sline="NOT confirmed in log"; [ $serial_seen = 1 ] && sline=confirmed
  summary[$bhead]="stage timing (serial, $flt): executed $b_exec (passed $b_pass, failed $b_fail), skipped $b_skip; serial: $sline"
  summary+=("timing reruns: $rerun_n (passed on rerun $rerun_pass, failed twice $rerun_fail)")
  if [ ${#flaky[@]} -gt 0 ]; then summary+=("flaky tests (passed their rerun): $(IFS=,; echo "${flaky[*]}" | sed 's/,/, /g')"); else summary+=("flaky tests (passed their rerun): none"); fi
  [ -z "$filter" ] && [ $b_exec -eq 0 ] && fail "timing stage matched 0 tests"
fi

[ -n "$filter" ] && [ $((a_exec + b_exec)) -eq 0 ] && fail "filter matched 0 tests"

result=PASS; [ ${#reasons[@]} -gt 0 ] && result=FAIL
{
  echo "===== GATE SUMMARY (commit $commit) ====="
  printf '%s\n' "${summary[@]}"
  if [ $result = PASS ]; then echo "GATE RESULT: PASS"; else echo "GATE RESULT: FAIL ($(IFS=';'; echo "${reasons[*]}" | sed 's/;/; /g'))"; fi
} | tee "$logs/summary.txt"
if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then { echo '```'; cat "$logs/summary.txt"; echo '```'; } >> "$GITHUB_STEP_SUMMARY"; fi
[ $result = PASS ]
