#!/usr/bin/env bash
# Publishes the runnable server binaries for one runtime identifier into <out>/arcanecore-<rid>/, self-contained (no .NET install
# needed on the target), in the layout of the Docker images (apps/docker/Dockerfile):
#
#   world/       ArcaneCore.World + the Playerbots pack beside it + world.conf.dist + appsettings.json
#   realm/       ArcaneCore.Realm + realm.conf.dist + appsettings.json
#   tools/       arcane-content-importer
#   dbupgrade/   arcane-db
#   account/     arcane-account
#   LICENSE, THIRD_PARTY_NOTICES.md
#
# Usage: tools/ops/deploy/publish-binaries.sh <rid> <Debug|Release> <out-dir>      (run from the repository root; used by
# .github/workflows/release-binaries.yml). Each daemon is started with --help afterwards; a daemon that does not start fails the script.
set -euo pipefail

if [ "$#" -ne 3 ]; then
  echo "usage: $0 <rid, e.g. linux-x64> <Debug|Release> <out-dir>" >&2
  exit 64
fi
rid="$1"
config="$2"
out="$3"
case "$config" in Debug | Release) ;; *) echo "configuration must be Debug or Release, got '$config'" >&2; exit 64 ;; esac

ext=""
case "$rid" in win-*) ext=".exe" ;; esac

stage="$out/arcanecore-$rid"
scratch="$out/.scratch-$rid"
rm -rf "$stage" "$scratch"
mkdir -p "$stage" "$scratch"

publish() { # <project> <output dir> [extra dotnet publish arguments]
  local project="$1" dest="$2"
  shift 2
  dotnet publish "$project" -c "$config" -r "$rid" -o "$dest" -p:NuGetAudit=false "$@"
}

publish src/ArcaneCore.World/ArcaneCore.World.csproj "$stage/world" --self-contained
# The Playerbots pack references World, so World cannot reference it: it is published on its own and its assembly placed beside the
# world executable, where Program.cs loads it (its packages are all World's). Only its two files are kept.
publish src/ArcaneCore.Playerbots/ArcaneCore.Playerbots.csproj "$scratch/playerbots" --no-self-contained
cp "$scratch/playerbots/ArcaneCore.Playerbots.dll" "$scratch/playerbots/ArcaneCore.Playerbots.pdb" "$stage/world/"
publish src/ArcaneCore.Realm/ArcaneCore.Realm.csproj "$stage/realm" --self-contained
publish tools/ArcaneCore.ContentImporter/ArcaneCore.ContentImporter.csproj "$stage/tools" --self-contained
publish tools/ArcaneCore.DbUpgrade/ArcaneCore.DbUpgrade.csproj "$stage/dbupgrade" --self-contained
publish tools/ArcaneCore.AccountTool/ArcaneCore.AccountTool.csproj "$stage/account" --self-contained
rm -rf "$scratch"

# The documented defaults of every key; copy to world.conf / realm.conf next to the daemon (docs/guide/configuration.md).
cp src/ArcaneCore.World/world.conf.dist "$stage/world/"
cp src/ArcaneCore.Realm/realm.conf.dist "$stage/realm/"
cp LICENSE THIRD_PARTY_NOTICES.md "$stage/"

test -f "$stage/world/ArcaneCore.Playerbots.dll"
for exe in world/ArcaneCore.World realm/ArcaneCore.Realm tools/arcane-content-importer dbupgrade/arcane-db account/arcane-account; do
  test -f "$stage/$exe$ext" || { echo "missing $exe$ext in the published output" >&2; exit 1; }
done
if [ "$ext" = "" ]; then
  chmod +x "$stage/world/ArcaneCore.World" "$stage/realm/ArcaneCore.Realm" "$stage/tools/arcane-content-importer" "$stage/dbupgrade/arcane-db" "$stage/account/arcane-account"
fi

# Start check: the daemons answer --help before anything binds or touches a database (Program.cs, DaemonCommandLine). It needs the
# runner to be able to execute this rid, so it is skipped when the caller says the binaries are cross-built.
if [ "${SKIP_RUN_CHECK:-0}" != "1" ]; then
  for daemon in world/ArcaneCore.World realm/ArcaneCore.Realm; do
    echo "== $daemon --help"
    help_text="$(cd "$stage/$(dirname "$daemon")" && "./$(basename "$daemon")$ext" --help)"
    echo "$help_text" | head -n 5
  done
  for tool in tools/arcane-content-importer dbupgrade/arcane-db account/arcane-account; do
    echo "== $tool starts"
    # The tools exit non-zero for usage output; what matters is that the self-contained host starts and prints something.
    printed="$(cd "$stage/$(dirname "$tool")" && "./$(basename "$tool")$ext" --help 2>&1 || true)"
    test -n "$printed" || { echo "$tool printed nothing" >&2; exit 1; }
    echo "$printed" | head -n 3
  done
fi
echo "published $stage"
