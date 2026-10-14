#!/usr/bin/env bash
# Print the latest Node1 CI suite summary lines for a given commit prefix (run inside Node1 WSL or with ARCANECORE_NODE_CI_DIR).
#   tools/ops/ci/ci-peek.sh [sha-prefix]
set -u
if [ "${1:-}" = "--help" ] || [ "${1:-}" = "-h" ]; then
  echo "Usage: ci-peek.sh [sha-prefix]"
  echo "  Environment: ARCANECORE_NODE_CI_DIR (default: /root/ci)."
  exit 0
fi
ci_dir="${ARCANECORE_NODE_CI_DIR:-/root/ci}"
prefix="${1:-}"
d=$(ls -td "$ci_dir"/${prefix}* 2>/dev/null | head -1)
[ -n "$d" ] || { echo "no CI run matching ${ci_dir}/${prefix}*" >&2; exit 1; }
for f in "$d"/logs/*.log; do
  echo "== $f"
  grep -E "^Total tests|^ +(Passed|Failed|Skipped): |^(Passed|Failed)!" "$f" | tail -4
done
