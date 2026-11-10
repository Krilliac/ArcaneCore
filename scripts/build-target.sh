#!/usr/bin/env bash
# Shared build flow for the repository's two supported target frameworks.
# Run through build-net10.sh or build-net11.sh, not directly.
set -euo pipefail

scripts_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$scripts_dir/.." && pwd)"

if (( $# < 1 )); then
    printf 'Use scripts/build-net10.sh or scripts/build-net11.sh.\n' >&2
    exit 2
fi

sdk_major="$1"
shift
case "$sdk_major" in
    10|11) ;;
    *) printf 'Unsupported SDK major version: %s\n' "$sdk_major" >&2; exit 2 ;;
esac

configuration="Debug"
update_locks=false
serial_build=false
if (( $# > 0 )); then
    case "$1" in
        Debug|Release) configuration="$1"; shift ;;
    esac
fi
for option in "$@"; do
    case "$option" in
        --update-locks) update_locks=true ;;
        --serial) serial_build=true ;;
        *)
            printf 'Usage: scripts/build-net%s.sh [Debug|Release] [--update-locks] [--serial]\n' "$sdk_major" >&2
            exit 2
            ;;
    esac
done

# Before restore, detect upstream merges that reintroduce hardcoded net11.0
# in a .csproj and defeat the net10.0 target selected by this script.
bash "$scripts_dir/check-framework-overrides.sh"

if ! command -v dotnet >/dev/null 2>&1; then
    printf '.NET SDK not found. Install .NET %s SDK to use this build script.\n' "$sdk_major" >&2
    exit 1
fi

sdk_directory="$scripts_dir/sdk/net$sdk_major"
if [[ ! -f "$sdk_directory/global.json" ]]; then
    printf 'Missing SDK selector: %s/global.json\n' "$sdk_directory" >&2
    exit 1
fi

# .NET SDK selection for dotnet CLI commands starts at the working directory.
# Running from the version-specific selector avoids editing the repository's
# global.json or accidentally using a .NET 10 SDK for a .NET 11 build.
# Pass the solution's absolute path so projects continue to resolve from the repo.
cd "$sdk_directory"
if ! sdk_version="$(dotnet --version)"; then
    printf 'Unable to resolve .NET %s SDK. Check installed SDKs using dotnet --list-sdks.\n' "$sdk_major" >&2
    exit 1
fi
case "$sdk_version" in
    "$sdk_major".*) printf 'Using .NET SDK %s (target net%s.0)\n' "$sdk_version" "$sdk_major" ;;
    *) printf 'Expected .NET %s SDK, got %s.\n' "$sdk_major" "$sdk_version" >&2; exit 1 ;;
esac

framework="net$sdk_major.0"
solution="$repo_root/ArcaneCore.slnx"
# Do not pass TargetFramework as a global property to a solution build: MSBuild's
# project-reference protocol can then build the same project concurrently with
# and without that property, writing to the same bin/obj files (dotnet/msbuild#8142).
# Directory.Build.props maps this repository-specific property to TargetFramework.
msbuild_properties=("-p:ArcaneTargetFramework=$framework")
if [[ "$sdk_major" == 11 ]]; then
    # The original .NET 11 project used preview language features in its RC era.
    msbuild_properties+=("-p:LangVersion=preview")
fi

# Preserve older local net10 locks when applying these scripts over an earlier
# checkout; never touch the separate packages.net11.lock.json snapshots.
if [[ "$sdk_major" == 10 ]]; then
    bash "$scripts_dir/migrate-nuget-lock-files.sh"
fi

# net10 lock files were migrated from the original net11 dependency graph, so
# keep the established net10 script behavior of refreshing them before verification.
# For net11, locked mode is the default to preserve the original RC snapshots;
# use --update-locks explicitly when a legitimate dependency update is needed.
if [[ "$sdk_major" == 10 || "$update_locks" == true ]]; then
    printf 'Refreshing %s NuGet locks...\n' "$framework"
    dotnet restore "$solution" "${msbuild_properties[@]}" --force-evaluate
fi

printf 'Verifying %s NuGet locks in locked mode...\n' "$framework"
dotnet restore "$solution" "${msbuild_properties[@]}" --locked-mode

build_options=()
if [[ "$serial_build" == true ]]; then
    # Diagnostic fallback for output-file locks caused by other processes or tools.
    build_options+=("-m:1")
fi

printf 'Building %s (%s)...\n' "$framework" "$configuration"
dotnet build "$solution" -c "$configuration" --no-restore "${msbuild_properties[@]}" "${build_options[@]}"
