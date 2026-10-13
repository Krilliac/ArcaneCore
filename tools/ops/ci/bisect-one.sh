#!/usr/bin/env bash
# Build and run a filtered ArcaneCore.World.Tests run on one commit in a bisect worktree.
#   tools/ops/ci/bisect-one.sh <sha> [filter]
set -u
if [ "${1:-}" = "--help" ] || [ "${1:-}" = "-h" ] || [ $# -lt 1 ]; then
  echo "Usage: bisect-one.sh <sha> [filter=FullyQualifiedName~PlayerbotGroupStallTests]"
  echo "  Environment: ARCANECORE_BISECT_WORKTREE (default: current repo), ARCANECORE_DATA_ROOT, TMPDIR."
  exit 0
fi
sha="$1"
filter="${2:-FullyQualifiedName~PlayerbotGroupStallTests}"
S="${ARCANECORE_BISECT_LOG_DIR:-${TMPDIR:-/tmp}/arcanecore-bisect}"
mkdir -p "$S"
W="${ARCANECORE_BISECT_WORKTREE:-$(git rev-parse --show-toplevel)}"
data_root="${ARCANECORE_DATA_ROOT:?ARCANECORE_DATA_ROOT is not set (the directory that holds terrain-5875 and client-dbc-5875)}"
cd "$W" || exit 99
git checkout -q -- . && git clean -fdq -e bin -e obj 2>/dev/null
git checkout -q --detach "$sha" || { echo "CHECKOUT FAILED $sha"; exit 98; }
dll=$(ls -1 tests/ArcaneCore.World.Tests/bin/Release/*/ArcaneCore.World.Tests.dll 2>/dev/null | head -1)
before=$(stat -c %Y "$dll" 2>/dev/null)
log="$S/build-$sha.log"
dotnet build tests/ArcaneCore.World.Tests -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false > "$log" 2>&1
brc=$?
echo "BUILD rc=$brc sha=$sha log=$log"
grep -E "error (CS|MSB|NU|CA)[0-9]+" "$log" | sed -E 's/\[.*//' | sort -u | head -10
grep -E "Build succeeded|Build FAILED|Error\(s\)" "$log" | tail -3
dll=$(ls -1 tests/ArcaneCore.World.Tests/bin/Release/*/ArcaneCore.World.Tests.dll 2>/dev/null | head -1)
echo "DLL=$dll before=$before after=$(stat -c %Y "$dll" 2>/dev/null)"
[ $brc -eq 0 ] || exit 1
tlog="$S/test-$sha.log"
ARCANECORE_TEST_TERRAIN_DIR="${ARCANECORE_TERRAIN_DIR:-$data_root/terrain-5875}" \
ARCANECORE_TEST_DBC_DIR="${ARCANECORE_DBC_DIR:-$data_root/client-dbc-5875}" \
  dotnet test tests/ArcaneCore.World.Tests -c Release --no-build --filter "$filter" > "$tlog" 2>&1
echo "TEST rc=$? log=$tlog"
grep -E "^(Passed|Failed|Skipped|Total)!?|Passed!|Failed!|Total tests|No test matches" "$tlog" | tail -4
