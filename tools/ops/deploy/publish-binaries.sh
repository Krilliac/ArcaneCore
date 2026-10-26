#!/usr/bin/env bash
# Publishes the runnable server binaries for one runtime identifier into <out>/arcanecore-<rid>/, self-contained (no .NET install
# needed on the target), in the directory layout of the Docker images (apps/docker/Dockerfile) plus the *.conf.dist files (the images do
# not carry those):
#
#   world/       ArcaneCore.World + the Playerbots pack beside it + world.conf.dist + appsettings.json
#   realm/       ArcaneCore.Realm + realm.conf.dist + appsettings.json
#   tools/       arcane-content-importer
#   dbupgrade/   arcane-db
#   account/     arcane-account
#   LICENSE, THIRD_PARTY_NOTICES.md
#
# Usage: tools/ops/deploy/publish-binaries.sh <rid> <Debug|Release> <out-dir>      (run from the repository root; used by
# .github/workflows/release-binaries.yml).
#
# Supply chain: every restore is locked (--locked-mode against the committed packages.lock.json, Directory.Build.props), like CI and the
# Dockerfile, and NuGet audit stays on. The published executables declare the four release rids in RuntimeIdentifiers, so their
# packages.lock.json records the runtime-specific targets, and map -p:ReleaseRid=<rid> to RuntimeIdentifier + SelfContained (not -r,
# which would also reach the libraries, whose lock files have no rid targets). A rid added here needs the same RuntimeIdentifiers entry
# in those projects and a refreshed lock file (docs/guide/contributing.md). The publish steps then run with --no-restore. The runtime
# and apphost packs the SDK downloads for a self-contained publish are not part of packages.lock.json.
#
# Check afterwards: each daemon and tool is started with --help. That proves the self-contained host starts and the main assembly loads;
# it does NOT prove the daemon runs, binds its port, loads the Playerbots pack or reaches a database (the pack's presence is only a file
# check). .github/workflows/docker-smoke.yml is what starts the real stack.
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

# The Playerbots pack references World, so World cannot reference it: it is published on its own, framework-dependent and without a rid
# (the assembly is architecture-neutral, as in the Dockerfile), and its two files are placed beside the world executable, where Program.cs
# loads it (its packages are all World's). It goes first: the rid restores below rewrite the shared obj/project.assets.json of World.
dotnet restore src/ArcaneCore.Playerbots/ArcaneCore.Playerbots.csproj --locked-mode
dotnet publish src/ArcaneCore.Playerbots/ArcaneCore.Playerbots.csproj -c "$config" --no-restore -o "$scratch/playerbots"

publish() { # <project> <output dir>: a self-contained publish for $rid from a locked restore
  local project="$1" dest="$2"
  dotnet restore "$project" -p:ReleaseRid="$rid" --locked-mode
  dotnet publish "$project" -c "$config" -p:ReleaseRid="$rid" --no-restore -o "$dest"
}

publish src/ArcaneCore.World/ArcaneCore.World.csproj "$stage/world"
cp "$scratch/playerbots/ArcaneCore.Playerbots.dll" "$scratch/playerbots/ArcaneCore.Playerbots.pdb" "$stage/world/"
publish src/ArcaneCore.Realm/ArcaneCore.Realm.csproj "$stage/realm"
publish tools/ArcaneCore.ContentImporter/ArcaneCore.ContentImporter.csproj "$stage/tools"
publish tools/ArcaneCore.DbUpgrade/ArcaneCore.DbUpgrade.csproj "$stage/dbupgrade"
publish tools/ArcaneCore.AccountTool/ArcaneCore.AccountTool.csproj "$stage/account"
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

# Start check (see the header for what it proves): the daemons answer --help (exit 64) before anything binds or touches a database (Program.cs, DaemonCommandLine). It needs the
# runner to be able to execute this rid, so it is skipped when the caller says the binaries are cross-built.
if [ "${SKIP_RUN_CHECK:-0}" != "1" ]; then
  for daemon in world/ArcaneCore.World realm/ArcaneCore.Realm; do
    echo "== $daemon --help"
    # --help is answered with exit 64 (EX_USAGE, ConfStartup.TryAnswerEarly); any other code means the host did not start cleanly.
    status=0
    help_text="$(cd "$stage/$(dirname "$daemon")" && "./$(basename "$daemon")$ext" --help 2>&1)" || status=$?
    if [ "$status" -ne 64 ] || [ -z "$help_text" ]; then
      echo "$daemon --help exited $status (expected 64 with usage text)" >&2
      echo "$help_text" >&2
      exit 1
    fi
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
