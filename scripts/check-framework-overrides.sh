#!/usr/bin/env bash
# Guard against upstream merges overriding ArcaneCore's centrally selected framework.
# The Roslyn analyzer is intentionally netstandard2.0 and is the only exception.
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
failed=false
while IFS= read -r -d '' project; do
    if [[ "$project" == "$repo_root/src/ArcaneCore.Analyzers/ArcaneCore.Analyzers.csproj" ]]; then
        continue
    fi

    if grep -nE '<TargetFrameworks?([[:space:]>])' "$project"; then
        printf 'Framework override found in %s.
' "${project#"$repo_root"/}" >&2
        failed=true
    fi
done < <(
    find "$repo_root/src" "$repo_root/tests" "$repo_root/tools" \
        -type d \( -name obj -o -name bin \) -prune -o \
        -type f -name '*.csproj' -print0
)

if [[ "$failed" == true ]]; then
    printf '\nThe build selects net10.0/net11.0 via Directory.Build.props and ArcaneTargetFramework.\n' >&2
    printf 'Remove the project-level framework override before building; preserve the netstandard2.0 analyzer exception.\n' >&2
    exit 1
fi
