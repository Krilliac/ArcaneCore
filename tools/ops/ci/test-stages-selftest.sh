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
flt=; prev=
for a in "$@"; do [ "$prev" = --filter ] && flt=$a; prev=$a; done
[ "$1" = run ] && { echo "self-test: 59 checks passed"; exit 0; }
block() { echo "Test Run $4."; echo "Total tests: $(( $1 + $2 + $3 ))"; echo "     Passed: $1"; [ "$2" -gt 0 ] && echo "     Failed: $2"; echo "    Skipped: $3"; }
case "$flt" in
  *'!=Timing'*)
    case "$SIM_CASE" in
      main-fails) echo "  Failed Ns.A.Broken [1 ms]"; block 9 1 0 Failed; exit 1 ;;
      main-empty) echo "No test matches the given testcase filter \`$flt\` in a.dll"; exit 0 ;;
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
check() {
  local name=$1 want=$2 extra=$3 must=$4 mustnot=${5:-}
  local out=$work/$name.out
  # shellcheck disable=SC2086
  SIM_CASE=$name PATH="$work/bin:$PATH" bash "$gate" --logs "$work/logs-$name" --commit selftest $extra --selftest tests/ci/X.slnf > "$out" 2>&1
  local got=$?
  if [ "$got" -ne "$want" ] || ! grep -qE "$must" "$out" || { [ -n "$mustnot" ] && grep -qE "$mustnot" "$out"; }; then
    echo "FAIL $name: exit $got (want $want)"; sed 's/^/    /' "$out"; bad=1
  else
    echo "ok   $name: exit $got"
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
[ $bad = 0 ] && echo "test-stages self-test: all cases behaved" || echo "test-stages self-test: FAILED"
exit $bad
