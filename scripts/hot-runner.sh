#!/usr/bin/env bash
# Start the world daemon under `dotnet watch` so edits to the C# source are applied to the
# running process (docs/areas/code-hot-reload.md). Development / Staging runners only.
#
#   scripts/hot-runner.sh [--non-interactive] [--audit-log PATH] [--project PATH]
#
# Refuses to run when the environment is Production (or anything but Development / Staging).
# Interactive by default: dotnet watch asks before restarting on an edit it cannot apply live.
# --non-interactive restarts by itself; the restart skips the graceful save path.
set -euo pipefail

non_interactive=0
audit_log=""
project="src/ArcaneCore.World"
while [ "$#" -gt 0 ]; do
  case "$1" in
    --non-interactive) non_interactive=1 ;;
    --audit-log) audit_log="${2:?--audit-log needs a path}"; shift ;;
    --project) project="${2:?--project needs a path}"; shift ;;
    *) echo "hot-runner: unknown argument '$1'" >&2; exit 64 ;;
  esac
  shift
done

environment_name="${DOTNET_ENVIRONMENT:-${ASPNETCORE_ENVIRONMENT:-Development}}"
case "$environment_name" in
  Development|Staging) ;;
  *) echo "hot-runner refuses to start: the environment is '$environment_name'. Code hot reload runs only in Development or Staging." >&2; exit 2 ;;
esac

command -v dotnet >/dev/null 2>&1 || { echo "hot-runner needs the .NET SDK (dotnet watch); it is not on PATH." >&2; exit 3; }

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export DOTNET_ENVIRONMENT="$environment_name"
export World__HotCode__Enabled=true
[ -n "$audit_log" ] && export World__HotCode__AuditLogPath="$audit_log"

args=(watch --project "$repo/$project" -c Debug --no-launch-profile)
[ "$non_interactive" -eq 1 ] && args+=(--non-interactive)

echo "hot-runner: $environment_name, Debug, hot reload ON"
exec dotnet "${args[@]}"
