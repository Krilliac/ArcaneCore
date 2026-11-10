# SDK selection: .NET 10 LTS and .NET 11

ArcaneCore was originally developed against **.NET 11**. The repository-wide
`Directory.Build.props` defaults to `net10.0`, allowing developers to use
**.NET 10 LTS** without installing .NET 11. The project also supports a
separate .NET 11 build with its own SDK selector and NuGet lock snapshots. The scripts select SDKs independently; neither script edits
`global.json`, any `.csproj` file, nor the other framework's lock files.
Application and test projects inherit `<TargetFramework>` from `Directory.Build.props`.
The Roslyn analyzer (`src/ArcaneCore.Analyzers`) intentionally keeps
`netstandard2.0` in its `.csproj` because it runs inside the compiler, not the
application; `tools/hotcode-spike` has its own isolated `Directory.Build.props`.
The `check-framework-overrides.sh` guard detects hardcoded framework overrides
introduced by future upstream merges. The default lives in
`Directory.Build.props`; scripts pass `-p:ArcaneTargetFramework=net10.0` or
`-p:ArcaneTargetFramework=net11.0`. The property is resolved to `TargetFramework`
locally during project evaluation, avoiding the duplicate-build race triggered by
passing `-p:TargetFramework` globally to an entire solution. The standalone
`tools/hotcode-spike` project sets its default in its own `Directory.Build.props` so it
retains the original isolation from repository-wide analyzers.

## Build either version

Run from the repository root (or call either script using an absolute path):

```bash
./scripts/build-net10.sh
./scripts/build-net10.sh Release

./scripts/build-net11.sh
./scripts/build-net11.sh Release
```

Each script supports `[Debug|Release] [--update-locks] [--serial]`. For example,
`./scripts/build-net11.sh Release --update-locks` intentionally regenerates the
.NET 11 lock graph before a locked-mode verification and build. Do this only when
updating dependencies, and inspect the lock-file changes before committing.

**SDK selection:** `scripts/sdk/net10/global.json` selects a stable .NET 10 SDK;
`scripts/sdk/net11/global.json` selects the original .NET 11 RC 2 SDK or a
later .NET 11 SDK, including the stable `11.0.100` release (once installed).
The build scripts start the `dotnet` CLI in these selector directories and pass
an absolute solution path and `ArcaneTargetFramework` to restore/build. The target is
resolved in `Directory.Build.props` and never written to any project file. The `GmCommands` hot-module template also
resolves its server DLL reference directory using `$(TargetFramework)` so it can work
with either build output directory.
The repository does not include a root `global.json`. Ordinary `dotnet` commands
therefore use an SDK selected by the CLI, but the default target is `net10.0`.
Use the build scripts to force a matching SDK and framework. If only .NET 10
is installed, the .NET 11 script reports the missing SDK without changing anything.

**Lock files:** application, test and tool projects targeting `net10.0`/`net11.0`
use `packages.net10.lock.json`/`packages.net11.lock.json`.
`Directory.Build.targets` chooses the lock file
according to the target framework after the `.csproj` is evaluated. The .NET 10
script refreshes the migrated .NET 10 locks as the earlier script did; the
.NET 11 script checks its original saved lock graph in locked mode by default.
Neither script renames or replaces the other version's locks. The analyzer
keeps a single `packages.lock.json` for `netstandard2.0`. After merging upstream
projects, their original `net11.0` lock snapshots are migrated intact to
`packages.net11.lock.json`; run `./scripts/build-net10.sh` to generate their
new `packages.net10.lock.json` snapshots and commit them after reviewing the
NuGet diff. Never delete lock files to work around a failed restore.

**CI and containers:** `.github/workflows/dotnet.yml` checks both versions
using the corresponding build scripts. The upstream integration-test, Docker,
and release workflows continue using .NET 11 independently. The .NET 10
build does not require a .NET 11 SDK.

## First restore and troubleshooting

The first .NET 10 run regenerates the migrated `packages.net10.lock.json` files
using NuGet, then checks them with `--locked-mode`. Commit changes only after
checking that they reflect intentional dependency updates. Subsequent .NET 10
runs preserve the existing behavior and refresh those locks as necessary.

For .NET 11, `--locked-mode` may fail if the original .NET 11 RC dependencies no
longer match the current `.csproj` definitions or the stable SDK changes the
resolved graph. In that case inspect the diff, and use
`./scripts/build-net11.sh Release --update-locks` to regenerate only the .NET 11
snapshots. This does not guarantee source compilation on .NET 11: it simply makes
SDK, framework, and NuGet graph selection explicit.

To inspect SDK installations, use `dotnet --list-sdks` (works regardless of the
repository's global.json). Running `dotnet --version` from the repository root
shows the default .NET 10 SDK, **not** the .NET 11 SDK selected by the opt-in
script. On Windows, the build scripts require Bash (for example Git Bash).

The .NET 11 selector retains the original RC 2 minimum, allowing developers
with an existing RC SDK to keep working while newer .NET 11 SDKs can also be
used. .NET 11 reached general availability on November 10, 2026. Developers
should prefer the stable .NET 11 SDK when available.

If a build reports `MSB3026` file-copy retries or `MSB4018` with a file-in-use
`IOException`, first ensure no other `dotnet build`, IDE build, or running server is
using the same output paths. Re-run with `./scripts/build-net10.sh --serial` (or
`./scripts/build-net11.sh --serial`) to restrict the build to one MSBuild worker.
Serial mode is a diagnostic fallback, not a requirement for normal builds.

## Git conventions for the two target frameworks

Do **not** add `<TargetFramework>` or `<TargetFrameworks>` to ordinary application/test `.csproj` files.
The Roslyn analyzer must remain `netstandard2.0`.
Use `Directory.Build.props` for the ordinary default and the version-specific shell
scripts to select a different target. For a targeted, one-off solution invocation, pass
`-p:ArcaneTargetFramework=net11.0` or `-p:ArcaneTargetFramework=net10.0`
(using the matching SDK). Do **not** pass `-p:TargetFramework` to a solution build:
MSBuild can then build the same project concurrently with different property sets
and collide while writing output files. See dotnet/msbuild issue 8142.
Do not commit SDK changes just to switch between the two versions.

```bash
# Project files should never appear as changed merely from switching SDKs.
git status --short -- '*.csproj'
```

Only commit changes to `packages.net10.lock.json` or `packages.net11.lock.json` when a
specific version's dependency graph actually changes. In particular, the .NET 10
build script intentionally refreshes its lock files before verifying them; review
any resulting Git diff rather than including incidental NuGet changes in feature
commits.

## After an upstream merge

If a new upstream project puts `<TargetFramework>net11.0</TargetFramework>`
in its `.csproj`, the `check-framework-overrides.sh` guard fails before restore
instead of producing `NETSDK1045` during a .NET 10 build. Remove the hardcoded
framework (except the `netstandard2.0` analyzer) and keep framework selection
centralized. New upstream `packages.lock.json` files for `net11.0` projects
should be moved to `packages.net11.lock.json`; generate the matching .NET 10
lock files with `./scripts/build-net10.sh` and review them before committing.

When validating the effective framework, use the CLI-selected SDK in the
matching `scripts/sdk/net10` or `scripts/sdk/net11` directory and inspect the
project property with `dotnet msbuild -getProperty:TargetFramework` and
`-p:ArcaneTargetFramework=net10.0` or `net11.0`.
