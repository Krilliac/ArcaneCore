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
echo "watching $log from byte $pos"
while [ "$n" -lt "$max" ]; do
  sz=$(stat -c %s "$log")
  # A log that shrank was truncated or replaced: scan the new content from its start.
  if [ "$sz" -lt "$pos" ]; then pos=0; fi
  if [ "$sz" -gt "$pos" ]; then
    # Copy the new byte range to a file and grep the file: no pipe, so a segment larger than a pipe buffer cannot SIGPIPE a writer
    # (which pipefail would turn into a false condition and drop the hang line for good).
    seg="$out/.segment.$$"
    trap 'rm -f "$seg"' EXIT
    dd if="$log" of="$seg" iflag=skip_bytes,count_bytes skip="$pos" count=$((sz-pos)) status=none
    if grep "world tick has been running for" "$seg" > /dev/null; then
      f="$out/stack-$(date +%H%M%S).txt"
      "$USERPROFILE/.dotnet/tools/dotnet-stack.exe" report -p "$pid" > "$f" 2>&1
      n=$((n+1)); echo "captured $f ($(wc -l < "$f") lines)"
    fi
    rm -f "$seg"
    pos=$sz
  fi
  sleep 1
done
