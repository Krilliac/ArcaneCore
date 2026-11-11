#!/usr/bin/env bash
# Build ArcaneCore with a stable .NET 10 SDK without changing repo-wide SDK settings.
set -euo pipefail

scripts_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
exec bash "$scripts_dir/build-target.sh" 10 "$@"
