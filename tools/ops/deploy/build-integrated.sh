#!/usr/bin/env bash
# Build an integrated-wNN deploy artifact from a worktree whose tree equals the merged main.
#   tools/ops/deploy/build-integrated.sh <worktree> <wNN> "<source note, e.g. main <sha> (PR #N, train N)>" [deploy-root]
# Produces <deploy-root>/integrated-<wNN>/{bin,obj,tools,SOURCE.txt}.
# deploy-live.py preflight requires tools/content/*.ps1, which the dotnet build does not emit.
set -euo pipefail
if [ "${1:-}" = "--help" ] || [ "${1:-}" = "-h" ] || [ $# -lt 3 ]; then
  echo "Usage: build-integrated.sh <worktree> <tag> <source-note> [deploy-root]"
  echo "  deploy-root defaults to \$ARCANECORE_DEPLOY_ROOT or \$ARCANECORE_LANES_ROOT/_deploy."
  exit 0
fi
wt=$1; tag=$2; note=$3
deploy_root="${4:-${ARCANECORE_DEPLOY_ROOT:-${ARCANECORE_LANES_ROOT:+${ARCANECORE_LANES_ROOT}/_deploy}}}"
if [ -z "$deploy_root" ]; then
  echo "ARCANECORE_DEPLOY_ROOT (or ARCANECORE_LANES_ROOT) is not set, and no deploy-root argument was given." >&2
  exit 2
fi
out="$deploy_root/integrated-$tag"
log="$deploy_root/integrated-$tag.build.log"
[ -e "$out" ] && { echo "refusing: $out already exists" >&2; exit 2; }
[ -z "$(git -C "$wt" status --porcelain)" ] || { echo "refusing: $wt is dirty" >&2; exit 2; }
avail=$(powershell.exe -NoProfile -Command "[int](Get-Counter '\Memory\Available MBytes').CounterSamples[0].CookedValue" | tr -d '\r')
[ "$avail" -ge 3000 ] || { echo "refusing: only ${avail} MB available" >&2; exit 2; }
(cd "$wt" && dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false \
  -p:NuGetAudit=false --artifacts-path "$out") > "$log" 2>&1 || { tail -20 "$log"; exit 1; }
grep -q " 0 Error(s)" "$log" || { echo "no '0 Error(s)' line in $log" >&2; exit 1; }
cp -r "$wt/tools" "$out/tools"
for f in tools/content/refresh-world-content.ps1 tools/content/set-optional-data.ps1 \
         bin/ArcaneCore.World/release/ArcaneCore.World.dll bin/ArcaneCore.Realm/release/ArcaneCore.Realm.dll; do
  [ -f "$out/$f" ] || { echo "missing $out/$f" >&2; exit 1; }
done
printf '%s tree %s\nbuilt %s\n' "$note" "$(git -C "$wt" rev-parse HEAD^{tree})" "$(date -u +%FT%TZ)" > "$out/SOURCE.txt"
tail -3 "$log"; cat "$out/SOURCE.txt"
