#!/usr/bin/env bash
# Build ArcaneCore with .NET 11 RC or newer (including GA) without changing global.json.
set -euo pipefail

scripts_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
exec bash "$scripts_dir/build-target.sh" 11 "$@"
