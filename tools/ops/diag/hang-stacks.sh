#!/usr/bin/env bash
# Capture managed stacks of the live world while a TickHang is in progress (up to $3 captures).
#   tools/ops/diag/hang-stacks.sh <world pid> <world.log> [max=3] [out-dir]
set -euo pipefail
if [ "${1:-}" = "--help" ] || [ "${1:-}" = "-h" ] || [ $# -lt 2 ]; then
  echo "Usage: hang-stacks.sh <world-pid> <world.log> [max=3] [out-dir]"
  echo "  out-dir defaults to \$ARCANECORE_LANES_ROOT/_diag/hangstacks."
  exit 0
fi
pid=$1; log=$2; max=${3:-3}
out="${4:-${ARCANECORE_LANES_ROOT:+${ARCANECORE_LANES_ROOT}/_diag/hangstacks}}"
if [ -z "$out" ]; then
  echo "ARCANECORE_LANES_ROOT is not set and no out-dir argument was given." >&2
  exit 2
fi
mkdir -p "$out"
n=0
pos=$(stat -c %s "$log")
while [ "$n" -lt "$max" ]; do
  sz=$(stat -c %s "$log")
  if [ "$sz" -gt "$pos" ]; then
    if tail -c +$((pos+1)) "$log" | head -c $((sz-pos)) | grep -q "world tick has been running for"; then
      f="$out/stack-$(date +%H%M%S).txt"
      "$USERPROFILE/.dotnet/tools/dotnet-stack.exe" report -p "$pid" > "$f" 2>&1
      n=$((n+1)); echo "captured $f ($(wc -l < "$f") lines)"
    fi
    pos=$sz
  fi
  sleep 1
done
