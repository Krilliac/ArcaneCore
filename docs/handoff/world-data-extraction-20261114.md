# World data extraction: handoff, 14 November 2026

Branch `ccr-b49dfda7-mg2u1e` (Krilliac/ArcaneCore), four commits on top of `c8be4c7f`. No pull request is open.

This is written for a session that has the maintainer's machine: the 1.12.1 client, a vmangos extraction and the
reference checkouts. The work so far was done in a cloud container with none of those, so **nothing here has touched
real client data**. Your job is to run it against real data, fix what that shows, and report.

## 1. What this is

`arcane-atlas` (`tools/ArcaneCore.Atlas`) can now build a whole server data root from a 1.12.1 (5875) client without
building vmangos' C++ tools:

```
arcane-atlas run --client <wow-client-dir> extract.maps  out=<work-dir>
arcane-atlas run --client <wow-client-dir> extract.vmaps out=<work-dir>
arcane-atlas run --data <work-dir> extract.mmaps
```

| Command | Writes | Replaces | Fidelity target |
|---|---|---|---|
| `extract.maps` | `maps/MMMXXYY.map` (z1.4) | vmangos `MapExtractor -e 1 -f 0` | every file byte for byte |
| `extract.vmaps` | `vmaps/NNN.vmtree`, `NNN_YY_XX.vmtile`, model files (VMAP_7.0) | `VMapExtractor` + `VMapAssembler` | model files byte for byte; trees and tiles the same placements, not the same bytes |
| `extract.mmaps` | `mmaps/NNN.mmap`, `NNNXXYY.mmtile` (mmap 6, Detour 7) | `MoveMapGenerator` | same inputs, parameters and filters; Recast's own polygons |

The server's readers and file formats are unchanged. The original proposal's new `.bin` format, runtime compilation,
persistent cache and TTL were **not** built, on purpose (section 8).

The output folder must be an absolute path outside every git working tree and outside the client folder; the commands
refuse anything else. Never write extracted data into the checkout: the hygiene test rejects it and the repository is
public. The same check runs on the child folder the command writes (`maps`, `vmaps`, `mmaps`), so a link there that
lands in a working tree or the client folder is refused. Model names from the client become file names only when they are
plain file names; a placement with any other name is dropped and reported as a problem. Staged `.part` files are created
fresh (an existing entry, including a link, is deleted first) and removed when a write fails. A link swapped in after the
check (time of check versus time of use) is not covered.

## 2. Commits

| Commit | What |
|---|---|
| `e5ecf638` | `extract.maps`: `MapTileEncoder`, `AreaExploreFlags`, `MapExtractor`, the command, a directory variant of `ClientOutputPath.Check` |
| `40f39f0d` | `extract.vmaps`: `VmapBih` (port of vmangos `BIH::subdivide`), `VmapRotation`, `VmapModelLibrary`, `VmapExtractor` |
| `1f2c8a27` | Vendors DotRecast's `DotRecast.Recast` (58 files, commit `6fce4ac`, unmodified) under `tests/ThirdParty/DotRecast/` |
| `10acb359` | `extract.mmaps`: `MmapTerrain`, `MmapModels`, `MmapTileBuilder`, `MmapExtractor` |

Code: `tools/ArcaneCore.Atlas/Extract/` (13 files, about 4,800 lines). Tests: `tests/ArcaneCore.Atlas.Tests/Extract/`
(4 files, about 1,900 lines). User documentation: [atlas](../areas/atlas.md) (command descriptions and the full list of
limits) and [maps, vmaps and mmaps](../integration/maps-vmaps-mmaps.md).

Every rule in the code carries a `// ref: vmangos@0e3ff01 <file>:<lines>` comment. The cited files are identical at
vmangos `0e3ff01` and at `df69450` (checked by blob id), so the line numbers hold for the checkout under
`<refs-root>/vmangos` if it is at either.

## 3. State of verification

Passing in the cloud container, on a fresh full-solution Release build (`dotnet build ArcaneCore.slnx -c Release`):

| Suite | Result |
|---|---|
| `tests/ArcaneCore.Atlas.Tests` (non-timing) | 2310 passed, 0 failed, 145 skipped |
| of which `Extract` | 57 passed, 5 skipped |
| `tests/ArcaneCore.World.Tests --filter Docs` | 174 passed |
| `tests/ArcaneCore.Game.Tests`, the Hygiene and Collision tests | 394 passed, 66 skipped |

Every skip is a test that needs real data. Not run at all: the rest of the local merge gate in `CLAUDE.md` (the other
suites, the timing stage, the MockClient self-test). Only Atlas, its tests, the vendored project and docs changed, so no
store, wire format or server code is involved.

What the synthetic tests do prove: the server's own `TerrainTile`, `VMapManager` and `NavMeshPathfinder` load the
files and answer correctly on small made-up worlds (heights and liquid; line of sight through a wall, a scaled doodad, a
WMO's doodad, a dungeon's global WMO; a path around a wall and across four tiles). What they cannot prove: that real
ADT, WMO and M2 files, with all their quirks, come out the way vmangos' tools turn them out.

## 4. First task: run the real-data tests

They need two variables:

- `ARCANECORE_TEST_TERRAIN_DIR`: a vmangos extraction of the same client, holding `maps/`, `vmaps/` and `mmaps/`
  (the reference output the tests compare against).
- `ARCANECORE_TEST_CLIENT_FILES_DIR`: an **extracted** client tree (`DBFilesClient\Map.dbc`, `World\Maps\...`). The
  existing `ClientTerrainRealDataTests` use the same variable, so if those run on this machine it is already set up. It
  is not the MPQ client folder; that one is `ARCANECORE_TEST_CLIENT_DIR` and is what the `--client` option takes.

```
dotnet test tests/ArcaneCore.Atlas.Tests -c Release --filter "FullyQualifiedName~ArcaneCore.Atlas.Tests.Extract"
```

Read the per-test results, not the exit code: a run with both variables missing also exits 0 with five skips.

| Test | Asserts | Cost |
|---|---|---|
| `MapExtractorRealDataTests.Encode_EveryTileOfEveryMap_EqualsTheReferenceExtractorsFile` | every reference `.map` file is reproduced byte for byte | seconds to a minute |
| `MapExtractorRealDataTests.Extract_WritesExactlyTheReferenceExtractorsFileSet` | a full run writes exactly the reference's file names | about a minute |
| `VmapExtractorRealDataTests.ModelFiles_EqualTheReferenceAssemblersFiles` | every model file written exists in the reference and is byte-identical | shares one full vmap extraction, minutes, writes about 0.6 GB to the temp folder |
| `VmapExtractorRealDataTests.TreesAndTiles_HoldTheReferencesPlacements` | the same tree and tile file names; per file the same spawns (name, flags, adt id, scale; position, rotation and bounds within 0.01) | same run |
| `MmapExtractorRealDataTests.Tiles_CoverTheGroundTheReferenceGeneratorsTilesCover` | Goldshire (0/49/31), Northshire (0/48/32) and a Stormwind tile (0/48/30): walkable area per polygon flag within 10% of vmangos' tile | unknown; each tile is 625 Recast sub-tiles |

Use `--logger "console;verbosity=detailed"` to see the numbers the vmap and mmap tests print.

## 5. What I expect to fail, and where to look

Ordered by how likely I think a failure is. In every case the reference source is the arbiter: read the cited lines
and make the C# do what vmangos does. Do not loosen a byte-for-byte assertion to get green.

### Maps (most likely to pass)

- The encoder re-expresses rules that `ClientTerrainOracle` already proved against real `.map` files, so sample-level
  differences are unlikely. A byte difference with equal samples would point at header packing in
  `MapTileEncoder.Encode` (section offsets and sizes, the liquid header's eight bytes).
- A tile reported as failed means `AdtFile.Parse` refused the ADT. The reference tool trusts the file; decide per case
  whether the reader is too strict.

### Vmaps (most likely to fail somewhere)

1. **Which WMO a name gets.** `VmapModelLibrary`'s constructor lists every client path containing `.wmo` (case
   sensitive, as the reference's `find(".wmo")`) and keeps the first per fixed plain name in ordinal path order.
   vmangos keeps the first in its archive listing order. Each collision is in the report's problems. If
   `ModelFiles_Equal...` fails for a WMO whose name is in a collision line, this is why; the fix is to follow the MPQ
   listing order (`MpqFileSystem`) instead of path order.
2. **M2 name resolution.** `VmapModelLibrary.Doodad` tries the fixed-name path, then the stored path, then underscores
   turned into spaces (`gameobject_extract.cpp:19-77`). Missing model files in the "no reference file" or
   "spawn count" messages point here.
3. **Model file bytes.** `VmapBih.Build` must give the reference's tree word for word. It is checked on hand-worked
   inputs and structurally on 400 boxes, not against a real vmangos tree. If only some files differ, diff one small
   file: a difference in the `MBIH` or `GBIH` words is the tree builder (float operation order, tie handling in
   `PrimaryAxis`, `FuzzyEq`); a difference before them is group selection, bounds, vertex or index order, or liquid
   (`VmapModelLibrary.ReadWmo`, `Liquid`, `ReadM2`). An empty tree's bounds are written as the quiet NaN `0x7FC00000`.
4. **WMO liquid.** Kept only when `MLIQ` is the last chunk of the group file (the reference clears its liquid state
   before every chunk, `wmo.cpp:194-195`). The type conversion is `wmo.cpp:433-506`.
5. **Placements.** Doodad-set positions and rotations go through `VmapRotation` (G3D's element and operation order)
   and Euler angles; the 0.01 tolerance may be too tight near the Euler poles. Spawns are paired by sorting on name
   then position, so two near-identical spawns can pair wrongly and report a false difference: check before believing
   a message.
6. **Sanity thresholds are guesses**: "more than 3000 model files" and "more than 50000 spawn records" in the two
   vmap tests. Replace them with the real counts once known.
7. **Known, accepted differences** (documented in [atlas](../areas/atlas.md), not bugs): placement ids are handed out
   in map id order rather than Map.dbc record order; `temp_gameobject_models` and models only game objects use are
   not written (the tests already allow reference-only model files).

### Mmaps (least known)

- The 10% area tolerance and the 1400 sq yd floor are first guesses. If the numbers are close but outside, report
  them rather than widening silently; the maintainer should agree the threshold.
- Check the Stormwind tile index (0/48/30 was computed from the trade district's coordinates, not looked up).
- Build time and peak memory in managed code are unmeasured. Record them: seconds per tile for the three test tiles,
  and if time allows a whole map (`maps=1` or a dungeon) with the thread count.
- `MmapModels` ports one rule exactly as vmangos has it although it looks like a slip there: a WMO's liquid type is
  read as `type & 3` (`TerrainBuilder.cpp:693-704`) while the vmaps store LiquidType.dbc entries, so WMO ocean meshes
  as magma. Check on a real tile with WMO water (Stormwind canals, 0/48/30 or its neighbour) whether vmangos' own
  tile really shows magma there (`arcane-atlas` layer `nav.poly` colours by flag). If it does, the port is right and
  the maintainer decides whether to deviate behind an option that defaults to the reference.
- DotRecast's span height limit is 20 bits against vmangos' patched 16; no behaviour difference is expected.

## 6. Second task: the stronger check, the server's own real-data tests on the new data

After the comparisons, build a complete data root with the three commands (section 1) into a scratch folder, point
`ARCANECORE_TEST_TERRAIN_DIR` at it, and run the existing real-data tests that until now only ever ran on vmangos'
output:

```
dotnet test tests/ArcaneCore.Game.Tests -c Release --filter "FullyQualifiedName~RealTerrainDataTests|FullyQualifiedName~VMapNativeOracleTests|FullyQualifiedName~GroundSnapRealTerrainTests|FullyQualifiedName~NavMeshPointQueryRealTerrainTests|FullyQualifiedName~NavRegionGraphRealTerrainTests|FullyQualifiedName~UnderMapRealTerrainTests|FullyQualifiedName~WmoAreaRealTerrainTests"
dotnet test tests/ArcaneCore.Atlas.Tests -c Release --filter "FullyQualifiedName~DiffRealDataTests|FullyQualifiedName~NavRealDataTests"
```

`VMapNativeOracleTests` (needs `ARCANECORE_TEST_VMAP_ORACLE`, the `VMapProbe` built by
`tools/terrain/build-terrain-data.ps1`) compares ArcaneCore's reader with vmangos' own library on the same files: on
the new vmaps it should still report 0 mismatches over its 4,900 queries. `RealTerrainDataTests` checks start-position
heights, abbey floors and walls, and navmesh paths out of the abbey. Compare pass, fail and skip counts with the same
run on the vmangos data root; tests whose expected values were measured on vmangos' polygons may shift slightly on the
new mmaps, and each such case needs a look, not a blanket tolerance.

Atlas can show differences directly: `arcane-atlas serve --data <new-root> --client <wow-client-dir> --open`, then the
`diff.terrain`, `diff.models` and `diff.nav` commands on a tile, or `query.path` on both data roots.

## 7. Third task, only if 4 to 6 are clean

- Start a development world server with `World:Maps:DataDirectory` set to the new data root and check the three
  startup log lines (terrain, vmaps, navmeshes), then creature pathing in a city and a dungeon. This makes the area
  *implemented*, not *accepted*: real-client acceptance is the maintainer's
  ([client acceptance](../guide/client-acceptance.md)).
- Update the "has not been run yet" sentences in [atlas](../areas/atlas.md),
  [maps, vmaps and mmaps](../integration/maps-vmaps-mmaps.md) and `CHANGELOG.md` with what was actually run, on which
  data, with counts.
- Run the full local merge gate from `CLAUDE.md` before a pull request. Open one only when the maintainer asks.

## 8. Decisions already taken, and what was left out

| Decision | Reason |
|---|---|
| Keep vmangos' file formats; no new `.bin` format | the server's readers are verified against them (4,900 oracle queries, 0 mismatches), and the same formats give a file-level oracle for the extractors |
| Extractors live in Atlas, as commands | Atlas already had the MPQ, ADT, WDT, WMO and M2 readers; moving them into a library is a large rename that the repository's rules ask to avoid |
| DotRecast.Recast vendored beside the existing copy under `tests/ThirdParty/DotRecast/` | it must compile against that copy's `DotRecast.Core`; Atlas already references the Detour project there; no project under `src/` references it |
| No runtime compilation, cache or TTL | nothing is validated on real data yet; a compiler inside the world server would need the DotRecast tree under `src/`; `maps-vmaps-mmaps.md` already records "navmesh generation stays an offline job" |

Not delivered, each stated as a limit in [atlas](../areas/atlas.md):

- `extract.maps`: packed 8 and 16 bit heights (`-f 1`), unlimited heights (`-h 0`).
- `extract.vmaps`: `temp_gameobject_models`, the reduced-size mode (`-s`), resume.
- `extract.mmaps`: transport meshes (`goNNNN.mmtile`), off-mesh connections (vmangos' `offmesh.txt` is GPL data and
  must not be copied; the server does not read connections anyway), `config.json` overrides.
- From the original proposal: on-demand compilation, the persistent cache with fingerprints, TTL eviction, region
  classes and prewarming, dynamic obstacles (doors). Dynamic gameobject line of sight is a separate, existing gap.

Open question for the maintainer once real numbers exist: is on-demand compilation worth building at all? The measured
vmangos mmaps for 1.12.1 are 2.0 GB and the full native build took about 50 minutes on 6 threads; the managed build
time from section 5 is the missing input.

## 9. Rules that apply (short form; `CLAUDE.md` and [contributing](../guide/contributing.md) bind)

- Reference projects are GPL and read-only: re-express behaviour, cite the lines, never copy code or data.
- Never commit client data or extracted output. Write only to folders outside the checkout.
- A rule that deliberately differs from the reference sits behind an option that defaults to the reference.
- No TODOs or stubs; what is not delivered is written down as a limit.
- Build and test with the same `-c` and no `-p:Platform`; `--no-build` runs whatever is in `bin`, so rebuild after
  changing the target framework or restoring for another one.
- A change to a project reference or package refreshes `packages.net10.lock.json` and `packages.net11.lock.json` of
  every affected project in the same commit (`dotnet restore ArcaneCore.slnx`, then again with
  `-p:ArcaneTargetFramework=net11.0`, then confirm `--locked-mode` passes for both).
- Work stays on `ccr-b49dfda7-mg2u1e` unless the maintainer names another branch. Never commit to `main`.

## 10. What to report back

1. Per real-data test: passed or failed, with the counts and the first messages.
2. For each failure: the cause, the vmangos lines that settle it, and the fix made (or why it is left).
3. The mmap numbers: polygons and area per flag against the reference for the three tiles, seconds per tile, peak
   memory, thread count.
4. The results of section 6 against the same tests on the vmangos data root.
5. Anything in the documented limits that turned out to matter on real data.
