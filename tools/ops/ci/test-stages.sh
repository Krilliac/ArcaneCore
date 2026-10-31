#!/usr/bin/env bash
# Two-stage test gate shared by .github/workflows/ci.yml and tools/ops/ci/node-ci.ps1 (which ships this file to Node1 with each run).
#
#   bash tools/ops/ci/test-stages.sh [--stage main|timing|all] [--logs DIR] [--results DIR] [--filter EXPR] [--commit SHA]
#                                    [--selftest] TARGET...
#
# TARGET is a solution, solution filter or test project, already built (dotnet test --no-build).
#   stage main   : every target with Category!=Timing, all targets at once, test collections in parallel as usual.
#   stage timing : every target with Category=Timing, serially (no parallel collections). A test that fails gets exactly one
#                  isolated rerun: passing it prints "FLAKY (passed on rerun): <test>" and appends it to <logs>/flake-ledger.txt
#                  (the gate still passes); failing it again fails the gate.
#   all          : main, then the MockClient self-test (--selftest), then timing.
# A run without --filter whose timing stage executes 0 tests FAILS: an empty filter must never read green. With --filter EXPR
# (ANDed with each stage's category) the rule is instead that the stages together executed at least one test.
# Exit 0 = pass, 1 = fail, 2 = usage. Env: GATE_WRAP prefixes every dotnet test (e.g. "timeout -k 60 3600"), GATE_WRAP_DATA
# additionally prefixes a target named Data.slnf (e.g. a flock), GATE_CONFIGURATION (default Release), GATE_MAIN_SERIAL=1 runs
# the main stage's targets one after another.
# Time budgets (0 turns a knob off; GATE_TIME_BUDGETS=off turns all of them off for slow hardware and keeps the timings as info):
#   GATE_SUITE_BUDGET_MIN (default 20)  HARD STOP: a main-stage target still running after this many minutes is aborted by VSTest
#       (RunConfiguration.TestSessionTimeout: kills the testhost, still writes the .trx of what finished) and FAILS the gate naming
#       the target, the budget and the tests still running. Applies with --filter too.
#   GATE_CLASS_BUDGET_SEC (default 600) fails a target whose test class's tests add up to more than that (xunit runs a class's tests
#       one after another, so the slowest class sets the floor of the run time). Not applied with --filter.
#   GATE_LONG_TEST_SEC (default 60) a test running longer is reported by xunit as "[Long Running Test]" and named in the summary.
# The summary lists the slowest classes and tests of a target that hit a budget.
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
    -h|--help) sed -n '2,25p' "$0"; exit 0 ;;
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
suite_budget_min=${GATE_SUITE_BUDGET_MIN:-20}
class_budget_sec=${GATE_CLASS_BUDGET_SEC:-600}
long_test_sec=${GATE_LONG_TEST_SEC:-60}
time_budgets=${GATE_TIME_BUDGETS:-on}
if [ "$time_budgets" = off ]; then suite_budget_min=0 class_budget_sec=0; fi
read -r -a wrap <<< "${GATE_WRAP:-}"
read -r -a wrap_data <<< "${GATE_WRAP_DATA:-}"
# Serial for the timing stage: no parallel collections, one thread, and xunit's diagnostic messages so the log shows it.
serial=(RunConfiguration.DisableParallelization=true xUnit.ParallelizeTestCollections=false xUnit.MaxParallelThreads=1 xUnit.DiagnosticMessages=true)
# Main stage: xunit's long-running-test lines ("[Long Running Test] 'name', Elapsed: ...", needs DiagnosticMessages) name what is slow.
main_settings=(xUnit.DiagnosticMessages=true "xUnit.LongRunningTestSeconds=$long_test_sec")

reasons=()
summary=()
fail() { reasons+=("$1"); }

# run_test <log> <trx dir> <filter> <serial 0|1> <target> [session timeout ms, main stage only]; returns dotnet's exit code.
# One `--` carries every runsettings argument, serial or not.
run_test() {
  local log=$1 trx=$2 flt=$3 ser=$4 target=$5 tmo=${6:-0} pre=("${wrap[@]}")
  [ "$(basename "$target")" = Data.slnf ] && pre=("${wrap_data[@]}" "${wrap[@]}")
  local cmd=("${pre[@]}" dotnet test "$target" -c "$config" --no-build -m:1 --verbosity normal --logger trx --results-directory "$trx" --filter "$flt")
  if [ "$ser" = 1 ]; then cmd+=(-- "${serial[@]}")
  else
    cmd+=(-- "${main_settings[@]}")
    [ "$tmo" -gt 0 ] && cmd+=("RunConfiguration.TestSessionTimeout=$tmo")
  fi
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

# class_times <trx dir> -> "<seconds> <class>" per test class, slowest first, summed from every result's duration.
class_times() {
  find "$1" -name '*.trx' -exec cat {} + 2>/dev/null | awk '
    { while (match($0, /<UnitTestResult [^>]*>/)) {
        tag = substr($0, RSTART, RLENGTH); $0 = substr($0, RSTART + RLENGTH)
        if (!match(tag, /testName="[^"]*"/)) continue; n = substr(tag, RSTART + 10, RLENGTH - 11)
        if (!match(tag, /duration="[^"]*"/)) continue; d = substr(tag, RSTART + 10, RLENGTH - 11)
        sub(/\(.*/, "", n); sub(/\.[^.]*$/, "", n); split(d, a, ":"); t[n] += a[1] * 3600 + a[2] * 60 + a[3] } }
    END { for (c in t) printf "%d %s\n", t[c], c }' | sort -rn
}

# test_times <trx dir> -> "<seconds> <test>" for every result, slowest first.
test_times() {
  find "$1" -name '*.trx' -exec cat {} + 2>/dev/null | awk '
    { while (match($0, /<UnitTestResult [^>]*>/)) {
        tag = substr($0, RSTART, RLENGTH); $0 = substr($0, RSTART + RLENGTH)
        if (!match(tag, /testName="[^"]*"/)) continue; n = substr(tag, RSTART + 10, RLENGTH - 11)
        if (!match(tag, /duration="[^"]*"/)) continue; d = substr(tag, RSTART + 10, RLENGTH - 11)
        split(d, a, ":"); printf "%.1f %s\n", a[1] * 3600 + a[2] * 60 + a[3], n } }' | sort -rn
}

# still_running <log> -> the "[Long Running Test]" tests (with elapsed) that never printed a Passed/Failed/Skipped line, comma-separated.
still_running() {
  awk '
    /^[[:space:]]+(Passed|Failed|Skipped) / { n = $0; sub(/^[[:space:]]+[A-Za-z]+ /, "", n); sub(/ \[[^]]*\]$/, "", n); done[n] = 1 }
    { if (match($0, /\[Long Running Test\] .*, Elapsed: [0-9:.]+/)) {
        l = substr($0, RSTART + 21, RLENGTH - 21); i = index(l, ", Elapsed: "); nm = substr(l, 1, i - 1)
        gsub(/^\047|\047$/, "", nm); el[nm] = substr(l, i + 11); if (!(nm in ord)) { ord[nm] = ++c; names[c] = nm } } }
    END { for (k = 1; k <= c; k++) if (!(names[k] in done)) { printf "%s%s (%s)", sep, names[k], el[names[k]]; sep = ", " } }' "$1"
}

# main_run <target> <label> <filter> <session timeout ms>: one main-stage target; writes <logs>/main-<label>.{log,rc,secs}.
main_run() {
  local st=$SECONDS rc
  run_test "$logs/main-$2.log" "$results/$2" "$3" 0 "$1" "$4"; rc=$?
  echo $rc > "$logs/main-$2.rc"; echo $((SECONDS - st)) > "$logs/main-$2.secs"
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
  [ "$time_budgets" = off ] && summary+=("  time budgets: OFF (GATE_TIME_BUDGETS=off): timings are info only, no hard stop")
  # The targets run at the same time (GATE_MAIN_SERIAL=1 runs them one after another), each with its own .trx folder so two
  # runs that start in the same second cannot overwrite each other's file; the results are then read in target order.
  pids=()
  hard_ms=$((suite_budget_min * 60000))
  for t in "${targets[@]}"; do
    l=$(label "$t")
    if [ "${GATE_MAIN_SERIAL:-0}" = 1 ]; then main_run "$t" "$l" "$flt" "$hard_ms"
    else main_run "$t" "$l" "$flt" "$hard_ms" & pids+=($!); fi
  done
  for pid in "${pids[@]}"; do wait "$pid"; done
  for t in "${targets[@]}"; do
    l=$(label "$t"); log=$logs/main-$l.log
    rc=$(cat "$logs/main-$l.rc" 2>/dev/null || echo 255)
    secs=$(cat "$logs/main-$l.secs" 2>/dev/null || echo 0)
    read -r p f s < <(counts "$log"); e=$((p + f))
    a_exec=$((a_exec + e)) a_pass=$((a_pass + p)) a_fail=$((a_fail + f)) a_skip=$((a_skip + s))
    echo "main $t rc=$rc executed $e (passed $p, failed $f), skipped $s"
    summary+=("  $t: rc=$rc executed $e passed $p failed $f skipped $s")
    # VSTest's own line when the session timeout fires: "Aborting test run: test run timeout of N milliseconds exceeded."
    timedout=0; grep -q 'test run timeout of [0-9]* milliseconds exceeded' "$log" && timedout=1
    over=$timedout
    running=$(still_running "$log")
    if [ $timedout = 1 ]; then
      fail "main $t was stopped at its ${suite_budget_min}m time budget (GATE_SUITE_BUDGET_MIN): still running: ${running:-nothing reported (each under ${long_test_sec}s)}"
    elif grep -q 'Test Run Aborted' "$log"; then fail "main $t: test run aborted"; fi
    if [ $rc -ne 0 ] || [ "$f" -gt 0 ]; then
      if [ $timedout = 0 ] || [ "$f" -gt 0 ]; then fail "main $t rc=$rc failed=$f"; fi
      failed_names "$log" | sort -u | head -30 | sed 's/^/  Failed /'
    fi
    summary+=("    run time $((secs / 60))m$((secs % 60))s")
    # Budgets that fail the gate: not with --filter (a subset's times say nothing about the suite) and not when switched off.
    if [ -z "$filter" ] && [ $timedout = 0 ] && [ "$suite_budget_min" -gt 0 ] && [ "$secs" -gt $((suite_budget_min * 60)) ]; then
      fail "main $t took $((secs / 60))m, over its ${suite_budget_min}m budget"; over=1
    fi
    classes=$(class_times "$results/$l")
    if [ -z "$filter" ] && [ "$class_budget_sec" -gt 0 ]; then
      while read -r cs cn; do
        [ -n "$cn" ] && [ "$cs" -gt "$class_budget_sec" ] || continue
        fail "main $t: test class $cn adds up to ${cs}s, over the ${class_budget_sec}s class budget (split it)"; over=1
      done <<< "$classes"
    fi
    # What is slow: listed when a budget was hit, and as info when the budgets are off or the run is filtered.
    if [ $over = 1 ] || [ "$time_budgets" = off ] || [ -n "$filter" ]; then
      [ -n "$running" ] && summary+=("    still running at the end: $running")
      lr=$(grep -h -o '\[Long Running Test\] .*' "$log" | sort -u | head -10)
      [ -n "$lr" ] && summary+=("    long-running tests reported by xunit:" "$(sed 's/^/      /' <<< "$lr")")
      st=$(test_times "$results/$l" | head -10)
      [ -n "$st" ] && summary+=("    slowest completed tests (s):" "$(sed 's/^/      /' <<< "$st")")
      [ -n "$classes" ] && summary+=("    slowest classes (s):" "$(head -5 <<< "$classes" | sed 's/^/      /')")
    else
      summary+=("    slowest class (s): $(head -1 <<< "${classes:-none}")")
    fi
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
