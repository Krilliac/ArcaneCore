#!/usr/bin/env bash
# Two-stage test gate shared by .github/workflows/ci.yml and tools/ops/ci/node-ci.ps1 (which ships this file to Node1 with each run).
#
#   bash tools/ops/ci/test-stages.sh [--stage main|timing|batch|all] [--logs DIR] [--results DIR] [--filter EXPR] [--commit SHA]
#                                    [--selftest] TARGET...
#
# TARGET is a solution, solution filter or test project, already built (dotnet test --no-build).
#   stage main   : every target with Category!=Timing, all targets at once, test collections in parallel as usual. A target whose
#                  failures are plain test failures (dotnet rc 1, no timeout, no "Test Run Aborted", no build/test-setup error in the
#                  log, every failure attributable to a "Failed <test>" line; any other rc, e.g. 124 / 137 / 255, always fails) and at most GATE_MAIN_RERUN_MAX (default 5) unique failed tests gets each of them rerun alone, serially, once.
#                  A pass prints "FLAKY (passed on rerun): <test>" and goes to the ledger; if every failure of the target passed, the
#                  target does not fail the gate. A test that fails its rerun too ("FAILED TWICE") fails it. More failures than the
#                  cap is a regression, not flakiness: no reruns, the gate fails. GATE_MAIN_RERUN=off (or 0) disables main reruns.
#   stage timing : every target with Category=Timing, serially (no parallel collections). A test that fails gets exactly one
#                  isolated rerun: passing it prints "FLAKY (passed on rerun): <test>" and appends it to <logs>/flake-ledger.txt
#                  (the gate still passes); failing it again fails the gate. Only a plain failure (rc 1, no setup error, attributable
#                  Failed lines) is rerun; any other rc fails the gate even if the rerun passes.
#   all          : main, then the MockClient self-test (--selftest), then timing, then batch.
#   stage batch  : the spell-batching leg of ci.yml (ARCANECORE_TEST_SPELL_BATCH_MS=GATE_BATCH_MS, default 400, retail-like batching on):
#                  every target whose name is in GATE_BATCH_TARGETS (default "Game World"), Category!=Timing, all at once. A failure fails
#                  the gate (no rerun: a batching-only failure is a real divergence, not timing). Part of "all"; skipped, with a line in
#                  the summary, when --filter is given, when no target is listed, or when the caller already sets
#                  ARCANECORE_TEST_SPELL_BATCH_MS (the main stage then already runs with that window). GATE_BATCH_LEG=off skips it.
# A run without --filter whose timing stage executes 0 tests FAILS: an empty filter must never read green. With --filter EXPR
# (ANDed with each stage's category) the rule is instead that the stages together executed at least one test.
# Exit 0 = pass, 1 = fail, 2 = usage. Env: GATE_WRAP prefixes every dotnet test (e.g. "timeout -k 60 3600"), GATE_WRAP_DATA
# additionally prefixes a target named Data.slnf (e.g. a flock), GATE_CONFIGURATION (default Release), GATE_MAIN_SERIAL=1 runs
# the main stage's targets one after another.
# Time budgets (0 turns a knob off; GATE_TIME_BUDGETS=off turns all of them off for slow hardware and keeps the timings as info):
#   GATE_SUITE_BUDGET_MIN (default 20)  HARD STOP, per test assembly (each project of a target is its own VSTest session, so each
#       gets the full budget): an assembly still running after this many minutes is aborted by VSTest (RunConfiguration.
#       TestSessionTimeout: kills the testhost, still writes the .trx of what finished) and the gate FAILS naming the target, the
#       budget and the tests still running; that target's counts are marked partial. Applies with --filter too. The wall-clock check
#       of a target (without --filter) times only the dotnet part, not the wait in GATE_WRAP/GATE_WRAP_DATA (e.g. a flock).
# GATE_BUDGET_MINUTE_SEC (1..60, default 60) is a test hook: the self-test shrinks a budget "minute" so it runs in seconds.
# Numeric knobs must be whole numbers within range (GATE_SUITE_BUDGET_MIN <= 1440, GATE_CLASS_BUDGET_SEC <= 86400,
# GATE_LONG_TEST_SEC 1..3600; else exit 2); GATE_TIME_BUDGETS is on or off. Two targets with the same name are exit 2.
#   GATE_CLASS_BUDGET_SEC (default 900) fails a target whose test class's tests add up to more than that (xunit runs a class's tests
#       one after another, so the slowest class sets the floor of the run time). Not applied with --filter.
#   GATE_LONG_TEST_SEC (default 60) a test running longer is reported by xunit as "[Long Running Test]" and named in the summary.
# GATE_MAIN_RERUN (on|off|1|0, default on) and GATE_MAIN_RERUN_MAX (whole number 0..1000, default 5) are the main-stage rerun knobs.
# GATE_ASSEMBLY_PARALLEL=0 runs a target's test assemblies one after another (-m:1) in the main stage too; the timing stage always does.
# The summary lists the slowest classes and tests of a target that hit a budget.
# Executed-test guard: a skipped test is not a passed one. With GATE_REQUIRE_EXECUTED (space separated test class names) a run without
# --filter FAILS when no test of such a class has a Passed or Failed result in the main-stage .trx files. An entry Class.Method names one
# test instead (needed for SpellContractSweepTests, whose plain harness tests run without DBCs and would satisfy a class-wide check).
# Unset, it defaults to SpellContractSweepTests.Sweep_NoExceptionsOrInvariantBreaks_AndContractsMatchTheBaseline when a target is named World and ARCANECORE_TEST_DBC_DIR holds Spell.dbc (the sweep skips without the client
# DBCs, e.g. on Node1, and then nothing enforces its ratchet); GATE_REQUIRE_EXECUTED="" turns the guard off.
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
    -h|--help) sed -n '2,/^set -u/{/^set -u/!p}' "$0"; exit 0 ;;
    --*) echo "test-stages: unknown option $1" >&2; exit 2 ;;
    *) targets+=("$1"); shift ;;
  esac
done
case "$stage" in main|timing|batch|all) ;; *) echo "test-stages: --stage must be main, timing, batch or all" >&2; exit 2 ;; esac
[ ${#targets[@]} -gt 0 ] || { echo "test-stages: no test target given" >&2; exit 2; }
if [ -z "$commit" ]; then commit=${GITHUB_SHA:-$(git rev-parse HEAD 2>/dev/null || echo unknown)}; fi
mkdir -p "$logs" || exit 2
[ -n "$results" ] || results=$logs/trx
ledger=$logs/flake-ledger.txt
: >> "$ledger"
config=${GATE_CONFIGURATION:-Release}
suite_budget_min=${GATE_SUITE_BUDGET_MIN:-20}
class_budget_sec=${GATE_CLASS_BUDGET_SEC:-900}
long_test_sec=${GATE_LONG_TEST_SEC:-60}
time_budgets=${GATE_TIME_BUDGETS:-on}
main_rerun=${GATE_MAIN_RERUN:-on}
case "$main_rerun" in on|1) main_rerun=on ;; off|0) main_rerun=off ;; *) echo "test-stages: GATE_MAIN_RERUN must be on or off, not '$main_rerun'" >&2; exit 2 ;; esac
main_rerun_max=${GATE_MAIN_RERUN_MAX:-5}
case "$time_budgets" in on|off) ;; *) echo "test-stages: GATE_TIME_BUDGETS must be on or off, not '$time_budgets'" >&2; exit 2 ;; esac
minute_sec=${GATE_BUDGET_MINUTE_SEC:-60}   # test hook: the self-test shrinks a budget "minute" so it can run in seconds
for knob in GATE_BUDGET_MINUTE_SEC=$minute_sec GATE_SUITE_BUDGET_MIN=$suite_budget_min GATE_CLASS_BUDGET_SEC=$class_budget_sec GATE_LONG_TEST_SEC=$long_test_sec GATE_MAIN_RERUN_MAX=$main_rerun_max; do
  [[ "${knob#*=}" =~ ^(0|[1-9][0-9]*)$ ]] || { echo "test-stages: ${knob%%=*} must be a whole number, not '${knob#*=}'" >&2; exit 2; }
done
# Ranges keep every product below inside bash's 64-bit arithmetic and inside an int of VSTest's millisecond timeout.
[ "$long_test_sec" -ge 1 ] && [ "$long_test_sec" -le 3600 ] || { echo "test-stages: GATE_LONG_TEST_SEC must be 1..3600" >&2; exit 2; }
[ "$minute_sec" -ge 1 ] && [ "$minute_sec" -le 60 ] || { echo "test-stages: GATE_BUDGET_MINUTE_SEC must be 1..60" >&2; exit 2; }
[ "$main_rerun_max" -le 1000 ] || { echo "test-stages: GATE_MAIN_RERUN_MAX must be at most 1000" >&2; exit 2; }
batch_leg=${GATE_BATCH_LEG:-on}
case "$batch_leg" in on|off) ;; *) echo "test-stages: GATE_BATCH_LEG must be on or off, not '$batch_leg'" >&2; exit 2 ;; esac
batch_ms=${GATE_BATCH_MS:-400}
[[ "$batch_ms" =~ ^[1-9][0-9]{0,4}$ ]] || { echo "test-stages: GATE_BATCH_MS must be a whole number 1..99999, not '$batch_ms'" >&2; exit 2; }
batch_targets=${GATE_BATCH_TARGETS-Game World}
[ "$suite_budget_min" -le 1440 ] || { echo "test-stages: GATE_SUITE_BUDGET_MIN must be at most 1440" >&2; exit 2; }
[ "$class_budget_sec" -le 86400 ] || { echo "test-stages: GATE_CLASS_BUDGET_SEC must be at most 86400" >&2; exit 2; }
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
  local cmd=("${pre[@]}" dotnet test "$target" -c "$config" --no-build --verbosity normal --logger trx --results-directory "$trx" --filter "$flt")
  # -m:1 runs a suite's test assemblies one after another; the main stage drops it unless GATE_ASSEMBLY_PARALLEL=0.
  if [ "$ser" = 1 ] || [ "${GATE_ASSEMBLY_PARALLEL:-1}" = 0 ]; then cmd+=(-m:1); fi
  if [ "$ser" = 1 ]; then cmd+=(-- "${serial[@]}")
  else
    cmd+=(-- "${main_settings[@]}")
    [ "$tmo" -gt 0 ] && cmd+=("RunConfiguration.TestSessionTimeout=$tmo")
  fi
  # stdin from /dev/null: the rerun loop below reads failed-test names from its stdin, and a child that read it would swallow them.
  # The marker holds the epoch second at which dotnet itself starts (after any GATE_WRAP / GATE_WRAP_DATA prefix has let it through,
  # e.g. a flock wait), so a run's time is the dotnet part only. bash -c is the innermost command of the wrapped line. Main stage only:
  # the timing stage and its reruns are not timed.
  local n=${#pre[@]} full=()
  if [ "$ser" = 0 ]; then full=("${cmd[@]:0:n}" bash -c 'date +%s > "$0"; exec "$@"' "$log.start" "${cmd[@]:n}"); else full=("${cmd[@]}"); fi
  "${full[@]}" > "$log" 2>&1 < /dev/null
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

# setup_error <log>: succeeds when the log shows a build or test-setup error (MSBuild/compiler error, a test dll that is missing or
# cannot be loaded), which is never a test failure: a gate must not call such a run flaky even if a rerun of one test passes.
setup_error() {
  grep -qE 'error MSB[0-9]+|^[[:space:]]*error( [A-Za-z]+[0-9]+)?:|The test source file .*(does not exist|was not found)|Could not find .*(testhost|\.dll|\.exe)|Could not load file or assembly' "$1"
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

# executed_count <trx dir> <required> -> how many results matching <required> are Passed or Failed. <required> is a test class name
# (matches ".Class." in the test name) or Class.Method (matches ".Class.Method" followed by the end of the name or a theory "(").
# A skipped test is NotExecuted and does not count. Name the method when the class also holds plain tests that run without the data.
executed_count() {
  find "$1" -name '*.trx' -exec cat {} + 2>/dev/null | awk -v req="$2" '
    BEGIN { m = index(req, ".") > 0; pat = m ? "." req : "." req "." }
    { while (match($0, /<UnitTestResult [^>]*>/)) {
        tag = substr($0, RSTART, RLENGTH); $0 = substr($0, RSTART + RLENGTH)
        if (!match(tag, /testName="[^"]*"/)) continue; n = substr(tag, RSTART + 10, RLENGTH - 11)
        if (!match(tag, /outcome="[^"]*"/)) continue; o = substr(tag, RSTART + 9, RLENGTH - 10)
        if (o != "Passed" && o != "Failed") continue
        i = index(n, pat); if (i == 0) continue
        if (m) { r = substr(n, i + length(pat), 1); if (r != "" && r != "(") continue }
        c++ } }
    END { print c + 0 }'
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
# Nothing of an earlier run in the same folders may be read as this one's: its rc, time and trx files are removed first, so a job that
# dies before writing its rc reads as rc 255 (failed), never as a leftover 0.
main_run() {
  local rc st st0
  st0=$(date +%s)
  rm -f "$logs/main-$2.rc" "$logs/main-$2.secs" "$logs/main-$2.log.start"; rm -rf "${results:?}/$2"
  run_test "$logs/main-$2.log" "$results/$2" "$3" 0 "$1" "$4"; rc=$?
  st=$(cat "$logs/main-$2.log.start" 2>/dev/null || echo "$st0")   # no marker: dotnet never started; time from the job's start, never 0
  echo $(( $(date +%s) - st )) > "$logs/main-$2.secs.tmp"; mv "$logs/main-$2.secs.tmp" "$logs/main-$2.secs"
  echo $rc > "$logs/main-$2.rc.tmp"; mv "$logs/main-$2.rc.tmp" "$logs/main-$2.rc"
}

label() { basename "$1" | sed -E 's/\.(slnf|slnx|sln|csproj)$//'; }
# Each target's logs, trx folder and rc file are named by its label: two with the same label would overwrite each other.
dups=$(for t in "${targets[@]}"; do label "$t"; done | sort | uniq -d)
for t in "${targets[@]}"; do [ -n "$(label "$t")" ] || { echo "test-stages: target '$t' has an empty name" >&2; exit 2; }; done
[ -z "$dups" ] || { echo "test-stages: targets share the name(s): $(echo $dups) (their logs and results would collide)" >&2; exit 2; }
sweep_method=Sweep_NoExceptionsOrInvariantBreaks_AndContractsMatchTheBaseline   # the [RealClientDbcFact]; the class's other tests need no DBCs
# The executed-test guard (see the header): explicit list, else the sweep when the host has the client DBCs and a World target is run.
if [ -n "${GATE_REQUIRE_EXECUTED+x}" ]; then require_executed=$GATE_REQUIRE_EXECUTED
else
  require_executed=
  for t in "${targets[@]}"; do
    [ "$(label "$t")" = World ] && [ -f "${ARCANECORE_TEST_DBC_DIR:-/nonexistent}/Spell.dbc" ] && require_executed=SpellContractSweepTests.$sweep_method
  done
fi

a_exec=0 a_pass=0 a_fail=0 a_skip=0 b_exec=0 b_pass=0 b_fail=0 b_skip=0
selftest_line="not run"
rerun_n=0 rerun_pass=0 rerun_fail=0
m_rerun_n=0 m_rerun_pass=0 m_rerun_fail=0
flaky=()
serial_seen=0
killed_any=0

if [ "$stage" = main ] || [ "$stage" = all ]; then
  flt='Category!=Timing'; [ -n "$filter" ] && flt="($filter)&Category!=Timing"
  summary+=("stage main (parallel, $flt):")
  [ "$time_budgets" = off ] && summary+=("  time budgets: OFF (GATE_TIME_BUDGETS=off): timings are info only, no hard stop")
  # The targets run at the same time (GATE_MAIN_SERIAL=1 runs them one after another), each with its own .trx folder so two
  # runs that start in the same second cannot overwrite each other's file; the results are then read in target order.
  pids=()
  hard_ms=$((suite_budget_min * minute_sec * 1000))
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
    # VSTest's own line when the session timeout fires: "Aborting test run: test run timeout of N milliseconds exceeded."
    timedout=0; grep -q 'test run timeout of [0-9]* milliseconds exceeded' "$log" && timedout=1
    part=; [ $timedout = 1 ] && part=" (partial: stopped at budget)"
    # A suite killed by GATE_WRAP's timeout (124, 137 after its -k) or a TERM prints no totals, so its "executed 0" means the totals are lost, not that nothing ran.
    killed=0; killed_why=
    case "$rc" in 124|137|143) if [ "$e" -eq 0 ]; then
      killed=1; killed_any=1
      killed_why="main $t was killed (rc=$rc) before it reported totals (the log has $(grep -cE '^[[:space:]]+Passed ' "$log") Passed and $(grep -cE '^[[:space:]]+Failed ' "$log") Failed result lines)"
      part=" (killed: no totals)"
    fi ;; esac
    echo "main $t rc=$rc executed $e (passed $p, failed $f), skipped $s$part"
    summary+=("  $t: rc=$rc executed $e passed $p failed $f skipped $s$part")
    over=$timedout
    running=$(still_running "$log")
    if [ $timedout = 1 ]; then
      fail "main $t was stopped at its ${suite_budget_min}m time budget (GATE_SUITE_BUDGET_MIN): still running: ${running:-nothing reported (each under ${long_test_sec}s)}"
    fi
    aborted=0; grep -q 'Test Run Aborted' "$log" && aborted=1
    [ $aborted = 1 ] && fail "main $t: test run aborted"
    if [ $rc -ne 0 ] || [ "$f" -gt 0 ]; then
      failed_names "$log" | sort -u | head -30 | sed 's/^/  Failed /'
      # One isolated rerun per failed test, only for plain failures: a timeout, an abort, a crash or kill (failures that are not
      # attributable to "Failed <test>" lines) or a mass failure is never flakiness.
      lines=$(failed_names "$log" | grep -c .); want=$(failed_names "$log" | sort -u | grep -c .)
      cleared=0; why=
      if [ "$main_rerun" = off ]; then why="reruns are off (GATE_MAIN_RERUN)"
      elif [ $timedout = 1 ]; then why="the run was stopped at its time budget"
      elif [ $aborted = 1 ]; then why="the test run aborted"
      elif [ "$lines" -eq 0 ] || [ "$lines" -ne "$f" ]; then why="$f failed but $lines attributable failure lines (crash or kill?)"
      elif [ $rc -ne 1 ]; then why="rc=$rc is not a plain test failure (rc 1), e.g. a kill, a timeout or a target that never ran"
      elif setup_error "$log"; then why="the log shows a build or test-setup error"
      elif [ "$want" -gt "$main_rerun_max" ]; then why="$want failed tests is over GATE_MAIN_RERUN_MAX=$main_rerun_max (a regression, not flakiness)"
      else
        all_passed=1; before=$m_rerun_n
        while read -r name <&3; do
          m_rerun_n=$((m_rerun_n + 1)); rlog=$logs/main-rerun-$m_rerun_n.log
          echo "main: rerunning $name alone"
          run_test "$rlog" "$logs/trx-main-rerun" "FullyQualifiedName=$name" 1 "$t"; rrc=$?
          read -r rp rf rs < <(counts "$rlog")
          if [ $rrc -eq 0 ] && [ "$rf" -eq 0 ] && [ $((rp + rf)) -ge 1 ]; then
            m_rerun_pass=$((m_rerun_pass + 1)); flaky+=("$name")
            echo "FLAKY (passed on rerun): $name"
            printf '%s\t%s\t%s\t%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$commit" "$t" "$name" >> "$ledger"
          else
            m_rerun_fail=$((m_rerun_fail + 1)); all_passed=0
            echo "FAILED TWICE: $name (rerun rc=$rrc executed $((rp + rf)) failed $rf)"
            fail "main $name failed its rerun too"
          fi
        done 3< <(failed_names "$log" | sort -u)
        [ $((m_rerun_n - before)) -eq "$want" ] || { all_passed=0; fail "main $t: $want failed tests but $((m_rerun_n - before)) reruns"; }
        [ $all_passed = 1 ] && cleared=1
      fi
      [ -n "$why" ] && echo "main $t: no rerun: $why"
      [ $cleared = 1 ] || fail "${killed_why:-main $t rc=$rc failed=$f}"
    fi
    summary+=("    run time $((secs / 60))m$((secs % 60))s")
    # Budgets that fail the gate: not with --filter (a subset's times say nothing about the suite) and not when switched off.
    if [ -z "$filter" ] && [ $timedout = 0 ] && [ "$suite_budget_min" -gt 0 ] && [ "$secs" -gt $((suite_budget_min * minute_sec)) ]; then
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
    [ -z "$filter" ] && [ "$e" -eq 0 ] && [ $killed = 0 ] && fail "main $t executed 0 tests"
  done
  # Executed-test guard: a class that is required to run and only skipped (no client DBCs on this host) must not read as a pass.
  if [ -z "$filter" ] && [ -n "$require_executed" ]; then
    for c in $require_executed; do
      n=$(executed_count "$results" "$c")
      summary+=("executed-test guard: $c ran $n test(s) in the main stage")
      [ "$n" -ge 1 ] || fail "main stage: no $c test executed (skipped or absent); set the data it needs, or GATE_REQUIRE_EXECUTED= to turn the guard off"
    done
  elif [ -z "$filter" ]; then
    for t in "${targets[@]}"; do
      [ "$(label "$t")" = World ] && summary+=("sweep NOT enforced on this host: SpellContractSweepTests.$sweep_method needs the client DBCs (ARCANECORE_TEST_DBC_DIR with Spell.dbc) and may have been skipped; a green gate does not cover it")
    done
  fi
  summary[0]="stage main (parallel, $flt): executed $a_exec (passed $a_pass, failed $a_fail), skipped $a_skip"
  summary+=("main reruns: $m_rerun_n (passed on rerun $m_rerun_pass, failed twice $m_rerun_fail)")
fi

if [ "$selftest" = 1 ] && { [ "$stage" = main ] || [ "$stage" = all ]; }; then
  "${wrap[@]}" dotnet run --project tools/ArcaneCore.MockClient -c "$config" --no-build -- self-test > "$logs/selftest.log" 2>&1 < /dev/null; rc=$?
  echo "selftest rc=$rc"; tail -3 "$logs/selftest.log"
  selftest_line="rc=$rc"
  [ $rc -ne 0 ] && fail "self-test rc=$rc"
fi
summary+=("self-test: $selftest_line")

if [ "$stage" = timing ] || [ "$stage" = all ]; then
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
    if [ $rc -ne 1 ] || setup_error "$log"; then
      fail "timing $t rc=$rc with $f failed: not a plain test failure (rc 1, no build/setup error), no rerun"; continue
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
  [ -z "$filter" ] && [ $b_exec -eq 0 ] && fail "timing stage matched 0 tests"
fi
# Spell-batching leg (ci.yml's "Game/World, spell batching" lanes): the same non-timing tests with ARCANECORE_TEST_SPELL_BATCH_MS=$batch_ms.
# No rerun and no flake ledger: a test that fails only with batching on is a divergence to fix, not timing noise.
if [ "$stage" = batch ] || [ "$stage" = all ]; then
  btargets=()
  for t in "${targets[@]}"; do for w in $batch_targets; do [ "$(label "$t")" = "$w" ] && btargets+=("$t"); done; done
  why=
  if [ "$batch_leg" = off ]; then why="GATE_BATCH_LEG=off"
  elif [ -n "$filter" ]; then why="--filter given"
  elif [ -n "${ARCANECORE_TEST_SPELL_BATCH_MS+x}" ]; then why="the caller already sets ARCANECORE_TEST_SPELL_BATCH_MS=$ARCANECORE_TEST_SPELL_BATCH_MS"
  elif [ ${#btargets[@]} -eq 0 ]; then why="no target named in GATE_BATCH_TARGETS ($batch_targets)"
  fi
  if [ -n "$why" ]; then
    echo "batch: skipped ($why)"
    summary+=("stage batch: skipped ($why)")
    [ "$stage" = batch ] && fail "batch stage ran nothing: $why"
  else
    bflt='Category!=Timing'
    bhard_ms=$((suite_budget_min * minute_sec * 1000))
    summary+=("stage batch (parallel, $bflt, ARCANECORE_TEST_SPELL_BATCH_MS=$batch_ms):")
    bpids=()
    for t in "${btargets[@]}"; do
      l=$(label "$t")
      rm -f "$logs/batch-$l.rc" "$logs/batch-$l.log.start"; rm -rf "${results:?}/batch-$l"
      ( export ARCANECORE_TEST_SPELL_BATCH_MS=$batch_ms; run_test "$logs/batch-$l.log" "$results/batch-$l" "$bflt" 0 "$t" "$bhard_ms"; echo $? > "$logs/batch-$l.rc.tmp"; mv "$logs/batch-$l.rc.tmp" "$logs/batch-$l.rc" ) &
      bpids+=($!)
    done
    for pid in "${bpids[@]}"; do wait "$pid"; done
    for t in "${btargets[@]}"; do
      l=$(label "$t"); log=$logs/batch-$l.log
      rc=$(cat "$logs/batch-$l.rc" 2>/dev/null || echo 255)
      read -r p f s < <(counts "$log"); e=$((p + f))
      echo "batch $t rc=$rc executed $e (passed $p, failed $f), skipped $s"
      summary+=("  $t: rc=$rc executed $e passed $p failed $f skipped $s")
      if grep -q 'test run timeout of [0-9]* milliseconds exceeded' "$log"; then fail "batch $t was stopped at its ${suite_budget_min}m time budget (GATE_SUITE_BUDGET_MIN)"; fi
      grep -q 'Test Run Aborted' "$log" && fail "batch $t: test run aborted"
      if [ $rc -ne 0 ] || [ "$f" -gt 0 ]; then
        failed_names "$log" | sort -u | head -30 | sed 's/^/  Failed /'
        fail "batch $t (spell batching ${batch_ms} ms) rc=$rc failed=$f"
      fi
      [ "$e" -eq 0 ] && fail "batch $t executed 0 tests"
    done
  fi
fi
# Flaky tests of both stages (main first, then timing).
if [ ${#flaky[@]} -gt 0 ]; then summary+=("flaky tests (passed their rerun): $(IFS=,; echo "${flaky[*]}" | sed 's/,/, /g')"); else summary+=("flaky tests (passed their rerun): none"); fi

[ -n "$filter" ] && [ $((a_exec + b_exec)) -eq 0 ] && [ $killed_any = 0 ] && fail "filter matched 0 tests"

result=PASS; [ ${#reasons[@]} -gt 0 ] && result=FAIL
{
  echo "===== GATE SUMMARY (commit $commit) ====="
  printf '%s\n' "${summary[@]}"
  if [ $result = PASS ]; then echo "GATE RESULT: PASS"; else echo "GATE RESULT: FAIL ($(IFS=';'; echo "${reasons[*]}" | sed 's/;/; /g'))"; fi
} | tee "$logs/summary.txt"
if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then { echo '```'; cat "$logs/summary.txt"; echo '```'; } >> "$GITHUB_STEP_SUMMARY"; fi
[ $result = PASS ]
