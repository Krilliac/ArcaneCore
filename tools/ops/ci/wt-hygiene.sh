#!/usr/bin/env bash
# Remove clean lane worktrees whose branch is merged into origin/main; throttled for a shared drive.
#   tools/ops/ci/wt-hygiene.sh [repo-dir] [pattern]
set -u
if [ "${1:-}" = "--help" ] || [ "${1:-}" = "-h" ]; then
  echo "Usage: wt-hygiene.sh [repo-dir] [worktree-grep-pattern]"
  echo "  repo-dir defaults to git rev-parse --show-toplevel."
  exit 0
fi
repo="${1:-$(git rev-parse --show-toplevel)}"
pattern="${2:-/w[0-9]+-}"
git -C "$repo" fetch -q origin main
for wt in $(git -C "$repo" worktree list --porcelain | sed -n 's#^worktree ##p' | grep -E "$pattern"); do
  br=$(git -C "$wt" rev-parse --abbrev-ref HEAD)
  if [ -n "$(git -C "$wt" status --porcelain)" ]; then echo "KEEP dirty $wt"; continue; fi
  if ! git -C "$wt" merge-base --is-ancestor HEAD origin/main; then echo "KEEP unmerged $wt"; continue; fi
  [ "$wt" = "$repo" ] && { echo "LAST $wt"; continue; }
  git -C "$repo" worktree remove "$wt" && git -C "$repo" branch -D "$br" >/dev/null && echo "REMOVED $wt"
  sleep 20
done
echo HYGIENE-DONE
