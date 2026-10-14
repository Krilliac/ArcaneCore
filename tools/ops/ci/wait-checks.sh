#!/usr/bin/env bash
# Wait until every check of a PR (or every job of a run) has finished, then print the results.
#   tools/ops/ci/wait-checks.sh pr <number> [max minutes=120]
#   tools/ops/ci/wait-checks.sh run <run id> [max minutes=120]
# Uses gh's built-in --jq: there is no jq binary on this box (a loop piping into jq never sees a result
# and only times out; that silently broke two waits on 2026-10-12).
set -u
if [ "${1:-}" = "--help" ] || [ "${1:-}" = "-h" ] || [ $# -lt 2 ]; then
  echo "Usage: wait-checks.sh <pr|run> <id> [max-minutes=120]"
  echo "  Environment: ARCANECORE_GH_REPO (default: Krilliac/ArcaneCore)."
  exit 0
fi
kind=$1; id=$2; max=${3:-120}; repo="${ARCANECORE_GH_REPO:-Krilliac/ArcaneCore}"
for ((i = 0; i < max; i++)); do
  if [ "$kind" = pr ]; then
    pending=$(gh pr checks "$id" --repo "$repo" --json bucket --jq '[.[]|select(.bucket=="pending")]|length' 2>/dev/null)
    total=$(gh pr checks "$id" --repo "$repo" --json bucket --jq 'length' 2>/dev/null)
    if [ -n "$total" ] && [ "$total" -gt 0 ] && [ "$pending" = 0 ]; then
      gh pr checks "$id" --repo "$repo" --json name,bucket --jq '.[]|"\(.name): \(.bucket)"'
      echo "head $(gh pr view "$id" --repo "$repo" --json headRefOid --jq .headRefOid)"
      exit 0
    fi
  else
    st=$(gh run view "$id" --repo "$repo" --json status --jq .status 2>/dev/null)
    if [ "$st" = completed ]; then
      gh run view "$id" --repo "$repo" --json conclusion,headSha,jobs \
        --jq '"conclusion \(.conclusion) head \(.headSha)", (.jobs[]|"\(.name): \(.conclusion)")'
      exit 0
    fi
  fi
  sleep 60
done
echo "TIMEOUT waiting for $kind $id after $max min"
exit 1
