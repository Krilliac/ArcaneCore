# Third-party source notices

## DotRecast (test oracle only)

- Repository: https://github.com/ikpil/DotRecast
- Commit: `6fce4ac0d59f4d4efdf9de0a9e5beae5784bca12`
- License: Zlib; the unmodified license text is at [tests/ThirdParty/DotRecast/LICENSE.txt](tests/ThirdParty/DotRecast/LICENSE.txt).
- Imported files: the 61 C# files under `src/DotRecast.Core/` and 65 C# files under `src/DotRecast.Detour/`, copied without source edits to the corresponding `tests/ThirdParty/DotRecast/` projects. The project files are ArcaneCore's test-only wrappers.
- Purpose: independent Detour format/path query cross-check in `ArcaneCore.Game.Tests`. Production projects do not reference these assemblies.
