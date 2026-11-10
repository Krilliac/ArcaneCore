#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
rm -f -- "$repo_root/src/ArcaneCore.Cluster.Protocol/packages.lock.json"
rm -f -- "$repo_root/tests/ArcaneCore.Analyzers.Tests/packages.lock.json"
rm -f -- "$repo_root/tests/ArcaneCore.Cluster.E2E.Tests/packages.lock.json"
rm -f -- "$repo_root/tests/hotmodule-fixtures/HotDepSwallow/packages.lock.json"
rm -f -- "$repo_root/tests/hotmodule-fixtures/HotDepUse/packages.lock.json"
