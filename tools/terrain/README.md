# Terrain data tools

- `build-terrain-data.ps1`: builds vmangos' extraction tools from a local vmangos source and
  extracts `maps/`, `vmaps/` and `mmaps/` from a 1.12.1 client. Recipe, timings and how to point a
  server at the result: [docs/integration/maps-vmaps-mmaps.md](../../docs/integration/maps-vmaps-mmaps.md).
- `vmap-oracle/`: `VMapProbe`, a small C++ program linked against vmangos' own `vmap` library. It
  answers height, line-of-sight and area queries with vmangos' `VMapManager2`, so
  `VMapNativeOracleTests` can compare ArcaneCore's managed reader with it on the same files. It is
  built by the script (copied into the vmangos source as `contrib/vmap_probe`); it is not part of
  the .NET solution.

The extracted data is derived from Blizzard's assets. Keep it, and the tools directory, outside the
repository (`DocsLinkTests` walks every `.md` file under the checkout, the vmangos copy included).
