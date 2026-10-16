#!/usr/bin/env bash
# Full test gate on GitHub Actions instead of this machine (ci.yml runs Game/World/Data/Rest suites, with MariaDB and
# PostgreSQL, on every push to integrate/**).
#   bash tools/ops/ci/ci-gate.sh <worktree> <name>
# Pushes the worktree's HEAD to integrate/<name>-<sha8>, waits for the ci.yml run for EXACTLY that SHA, prints each suite's result and the
# failing tests, and exits 0 only if every suite job succeeded. Exit 2 = no run appeared, 3 = run failed/cancelled, 4 = timeout.
# The CI runners have no terrain/DBC data: tests that need ARCANECORE_TEST_TERRAIN_DIR / _DBC_DIR skip there, so lanes must still
# run their real-data tests locally (tools/ops/ci/run-in-slot.ps1). The integrate branch is deleted again on success.
set -u
wt="$1"; name="$2"
# The GitHub repository of the worktree origin remote (owner/name), or ARCANECORE_GH_REPO when set.
repo="${ARCANECORE_GH_REPO:-$(cd "$wt" && gh repo view --json nameWithOwner --jq .nameWithOwner)}" || exit 1
sha=$(git -C "$wt" rev-parse HEAD) || exit 1
branch="integrate/$name-${sha:0:8}"   # unique per commit: never a force push
git -C "$wt" push -q origin "HEAD:refs/heads/$branch" || { echo "push to $branch failed"; exit 1; }
echo "pushed $sha to $branch; waiting for ci.yml"
run=""
for i in $(seq 1 40); do
  run=$(gh run list -R "$repo" --workflow ci.yml --branch "$branch" --limit 10 --json databaseId,headSha \
        --jq ".[] | select(.headSha==\"$sha\") | .databaseId" | head -1)
  [ -n "$run" ] && break
  sleep 15
done
[ -z "$run" ] && { echo "no ci.yml run appeared for $sha on $branch"; exit 2; }
echo "ci.yml run $run (https://github.com/$repo/actions/runs/$run)"
for i in $(seq 1 180); do   # up to 3 h
  st=$(gh run view "$run" -R "$repo" --json status --jq .status)
  [ "$st" = "completed" ] && break
  sleep 60
done
[ "$st" != "completed" ] && { echo "TIMEOUT: run $run still $st"; exit 4; }
gh run view "$run" -R "$repo" --json jobs --jq '.jobs[] | "\(.name): \(.conclusion)"'
gh run view "$run" -R "$repo" --log 2>/dev/null | grep -E "(Passed|Failed)!  - Failed:" | sed -E 's/^[^\t]*\t[^\t]*\t[^ ]* //'
concl=$(gh run view "$run" -R "$repo" --json conclusion --jq .conclusion)
if [ "$concl" != "success" ]; then
  echo "--- failing tests ---"
  gh run view "$run" -R "$repo" --log-failed 2>/dev/null | grep -E "^\S+\s+\S+\s+\S+\s+Failed [A-Za-z]" | sed -E 's/.*Failed /Failed /' | sort -u | head -40
  echo "CI gate: $concl"; exit 3
fi
git -C "$wt" push -q origin --delete "$branch" 2>/dev/null
echo "CI gate: success (every suite passed for $sha)"
