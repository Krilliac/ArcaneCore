#!/usr/bin/env bash
# One-time migration of the pre-versioned .NET 10 NuGet lock files.
# Preserve locally regenerated dependencies; never replace a versioned lock file.
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"

# Every original .NET 10 lock has a matching saved .NET 11 snapshot. Ignore any
# unrelated legacy lock files, build outputs and directories outside the project tree.
mapfile -d '' old_locks < <(
    find "$repo_root/src" "$repo_root/tests" "$repo_root/tools" \
        -type d \( -name bin -o -name obj \) -prune -o \
        -type f -name 'packages.lock.json' -print0
)

# Check every destination first. A conflict must fail without partially migrating.
for old_lock in "${old_locks[@]}"; do
    project_dir="${old_lock%/*}"
    if [[ ! -f "$project_dir/packages.net11.lock.json" ]]; then
        continue
    fi

    if [[ -e "$project_dir/packages.net10.lock.json" ]]; then
        printf 'Cannot migrate: both %s and %s exist. Resolve this conflict manually.\n' \
            "$old_lock" "$project_dir/packages.net10.lock.json" >&2
        exit 1
    fi
done

count=0
for old_lock in "${old_locks[@]}"; do
    project_dir="${old_lock%/*}"
    if [[ ! -f "$project_dir/packages.net11.lock.json" ]]; then
        continue
    fi

    mv -- "$old_lock" "$project_dir/packages.net10.lock.json"
    ((count += 1))
done
printf 'Migrated %d NuGet lock file(s) to packages.net10.lock.json.\n' "$count"
