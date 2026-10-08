# Maps, vmaps and mmaps: does ArcaneCore need client-extracted data?

Question (Nathan, 2026-10-07): do we need the map/vmap/mmap files extracted from the game client, or can
we get the same effect without them? Are they only a way to save the server work?

## Short answer

Yes for anything that has to respect the world's geometry, and they are not mainly a CPU saving. The
server has no geometry of its own: the ground, buildings, caves, bridges and water exist only in the
client's MPQ archives (ADT/WDT terrain, WMO buildings, M2 doodads). The extractors convert those into
compact server formats. The expensive part — building a navigation mesh — is done once, offline;
the runtime queries on the result are cheap. Without the data the server does not "compute it
itself" — it simply does not know where the floor or the walls are.

| Data | Comes from | Gives the server | Without it |
|---|---|---|---|
| **maps** (`maps/MMMXXYY.map`) | ADT/WDT terrain | Ground height grid, area/zone ids, liquid (water/lava/slime level) | No ground height, server-side zone/area, liquid or under-map checks |
| **vmaps** (`vmaps/*.vmtree`, `*.vmtile`, `*.vmo`) | WMO + M2 models | Line of sight, floors inside/on buildings and caves, indoor/outdoor, object hits | Everything is in line of sight; no floor inside buildings; everywhere is "outdoors" |
| **mmaps** (`mmaps/NNN.mmap`, `*.mmtile`) | Generated (Recast) from maps + vmaps | Walkable navigation mesh: paths around obstacles, slopes, water | Paths are straight lines |

## What ArcaneCore has today

Readers for all three exist and are fail-soft ("no data means open"; docs/integration/grid-terrain.md,
docs/integration/vmap-los.md, docs/areas/collision-pathing.md):

- `Maps/Terrain/TerrainManager` + `TerrainTile`: vmangos/cmangos-classic `.map` "z1.4" (heights, area
  flags, liquid). Directory: `World:Maps:DataDirectory/maps`.
- `Maps/Collision/VMaps/VMapManager`: "VMAP_7.0" (line of sight, model heights, area info). Directory:
  `World:Collision:VMapDirectory` (default `<DataDirectory>/vmaps`).
- `Maps/Collision/MMaps/NavMeshPathfinder`: managed Detour-compatible reader, Detour version 7 / mmap
  version 6 as vmangos' MoveMapGen writes (cMaNGOS' version 8 is refused with a message).
  Directory: `World:Collision:MMapDirectory` (default `<DataDirectory>/mmaps`).
- `MapCollision.GetHeight` combines terrain and model floors (vmangos `GetHeightStatic`).

CI tests use synthetic tiles. Since 2026-10-07 the readers are also checked against a real
extraction (see [Verification](#verification) below); that found and fixed three vmap reader bugs.

What substitutes for the data when `World:Maps:DataDirectory` is empty:

- **Line of sight**: `OpenLineOfSight` — always clear (spells and melee through walls).
- **Pathing**: `StraightLinePathfinder` — a straight line flagged `NotUsingPath`; creatures chase in
  straight lines (`CreaturePathing`).
- **Heights**: none. `GetHeight` returns `TerrainTile.InvalidHeightValue`; falling, under-map and
  liquid observers have nothing to compare against; the client's reported positions and zone are
  trusted.
- **Playerbots** (what Codex built for collision): `PlayerbotNavigation.TryPlan` uses an mmap path
  when there is one and checks every corner's height and line of sight. Without mmaps it falls back
  to `TryTerrainRoute`: a straight line probed every yard, each probe needing a valid floor height
  within one yard of the previous one (no cliffs) and a clear line of sight. That fallback **needs
  `.map` heights** — without any terrain data every bot route is refused and bots stand still.
  `PlayerbotMotion` (this lane) validates each reported position the same way (floor within 2 yd,
  line of sight) and snaps it to the floor.

Observed on this machine (read only, 2026-10-07):

- The running world server (`work/verification/wave84-build-artifacts`) ships `appsettings.json` with
  `World:Maps:DataDirectory`, `VMapDirectory` and `MMapDirectory` empty. Since bots do move live, the
  data is presumably supplied by an environment override (`World__Maps__DataDirectory`, e.g.
  `scripts/dev-runner.ps1 -ContentDir`); the world log's first `Terrain:` line says which
  ("reading .map files from …" or "no data directory configured"). Worth confirming.
  (Later on 2026-10-07: the live profile's `World:Maps:DataDirectory` points at an earlier maps-only
  extraction, `playable-content-20261005/terrain-r1`: `.map` files only, no vmaps or mmaps.)
- `D:/server-Zero/run/{maps,vmaps,mmaps}` (258 MB, 553 MB, 794 MB) is a MaNGOS Zero extraction:
  `.map` "z1.5", "VMAP_4.0", mmap version 5. **ArcaneCore cannot read it** (expects z1.4, VMAP_7.0,
  mmap 6); its readers would log and treat it as missing.
- `D:/World of Warcraft Classic 1.12.1/Data` holds the 1.12.1 MPQs (`base`, `dbc`, `misc`, …) the
  extractors need.

## Ways to get the effect

1. **Extract once from Nathan's own 1.12.1 client with vmangos' tools** (`MapExtractor`,
   `VMapExtractor` + `VMapAssembler`, `MoveMapGenerator`). Produces exactly the formats the readers
   expect. **Done on 2026-10-07**, see [Extraction](#extraction-2026-10-07) and the
   [rebuild recipe](#rebuild-recipe): maps and vmaps take about four minutes, the continents' navmesh
   about 36 minutes with 6 threads.
2. **Extract on first run, in ArcaneCore** (a C# extractor reading the MPQs). `.map` generation is
   a contained job (MPQ + WDT/ADT `MCNK` heights, area ids, `MCLQ` liquid) and could run at first
   startup in minutes. vmaps need WMO/M2 parsing and BIH building (larger). Navmesh generation needs
   Recast; a managed port (e.g. DotRecast, zlib licence) would be a new dependency, and generation
   should run as a background/offline job with cached output, never on the world thread.
3. **Ship generated data.** Convenient, but the output is derived from Blizzard's assets: fine on
   Nathan's machines, not for the public repository or releases.
4. **Approximate without client data.** Possible pieces: a coarse heightfield interpolated from the
   world database's spawn Z coordinates, or learned from real players' movement packets; a grid A*
   over a heightfield with a slope limit. None of them knows about buildings, caves, bridges or
   water: bots and creatures would still cut through walls, path under bridges and fall through
   floors indoors. Useful only as a fallback where data is missing, not as a replacement.

## Extraction (2026-10-07)

Produced with vmangos `0e3ff01` (`D:/refs/vmangos`) from `D:/World of Warcraft Classic 1.12.1`
(read only) into `D:/ArcaneCore-lanes/terrain-out`:

| Directory | Files | Size | Format | Time |
|---|---|---|---|---|
| `maps/` | 2429 `.map` (687 Eastern Kingdoms, 1018 Kalimdor, 724 other maps) | 278 MB | `MAPS` `z1.4`, float heights | 20 s |
| `vmaps/` | 43 `.vmtree`, 1250 `.vmtile`, 3490 `.vmo`, 1298 NUL-named doodad models, `temp_gameobject_models` | 572 MB | `VMAP_7.0` | 1.5 min extract + 23 s assemble |
| `mmaps/` | 41 `.mmap`, 1731 `.mmtile` (470 Eastern Kingdoms, 704 Kalimdor, 557 for 39 other maps), 11 transport `go*.mmtile` | 2.0 GB | `MMAP`, Detour 7, mmap 6 | map 0: 20 min, map 1: 16 min, rest: 14.5 min (6 threads) |

`Buildings/` (262 MB) is the extractor's intermediate output; the server does not read it.
MoveMapGenerator writes no tile where a terrain tile has neither ground nor model geometry (open
sea), so the continents have fewer `.mmtile` files than `.map` files. Its working set stayed under
0.6 GB at 6 threads.

## Rebuild recipe

All of it is `tools/terrain/build-terrain-data.ps1` (run from the repository root):

```powershell
pwsh -File tools/terrain/build-terrain-data.ps1 -ToolsDir D:\terrain-tools -OutDir D:\ArcaneCore-data
# parts: -Steps Tools,Maps,VMaps,MMaps   continents only: -MMapRun '0','1'   one tile: -MMapRun '0 --tile 32,48'
```

The script was run end to end from a fresh copy into a second directory: its `maps/` and `vmaps/`
and the Northshire navmesh tile are byte-identical to the set above (the tools are deterministic).

What it does, step by step (the same commands work by hand):

1. **Copy the vmangos source** (without `.git`, `sql`, `bin`) to `<ToolsDir>/vmangos-src`. The
   vmangos build writes its executables into `<source>/bin`, so it must not run in the reference
   checkout. The script also copies `tools/terrain/vmap-oracle` in as `contrib/vmap_probe`. Keep
   `-ToolsDir` and `-OutDir` outside the ArcaneCore checkout: `DocsLinkTests` walks every `.md` file
   under it and fails on the vmangos copy's own READMEs.
2. **Build the tools** with MSVC and Ninja inside `vcvars64.bat`, compilers pinned (`CC=cl`,
   `CXX=cl`; a shell exporting another `CC` makes CMake pick the wrong compiler):
   `cmake -S <src> -B <ToolsDir>/build -G Ninja -DCMAKE_BUILD_TYPE=Release -DBUILD_EXTRACTORS=ON
   -DUSE_SCRIPTS=OFF -DENABLE_CPPTRACE=OFF -DBUILD_FOR_HOST_CPU=OFF`, then
   `cmake --build <ToolsDir>/build --target MapExtractor VMapExtractor VMapAssembler MoveMapGenerator VMapProbe -j 4`
   (187 compile steps; the Windows dependencies ship in `dep/windows`, nothing is downloaded).
3. **Run everything with the output directory as the working directory**, never the client folder:
   - `MapExtractor -i "<client>" -o <out> -e 1 -f 0 --silent` → `maps/` (`-e 1`: maps only, the
     server reads DBCs from elsewhere; `-f 0`: float heights).
   - `VMapExtractor -d "<client>/Data" --silent` → `Buildings/`, then
     `VMapAssembler Buildings vmaps` → `vmaps/` (it ends with "Press enter to close"; with stdin at
     EOF it exits 0).
   - Copy `contrib/mmap/config.json` and `offmesh.txt` next to the output, then
     `MoveMapGenerator 0 --silent --threads 6`, `MoveMapGenerator 1 --silent --threads 6`, and
     `MoveMapGenerator --silent --threads 6` for every other map plus the transport models. A rerun
     skips tiles that already exist, so an interrupted run resumes. `--tile X,Y` takes the tile Y
     index first: the Northshire tile `0004832.mmtile` is `MoveMapGenerator 0 --tile 32,48`.

Formats were checked against the readers before generating: vmangos' extractor writes `z1.4`
(`contrib/extractor/System.cpp`), the assembler `VMAP_7.0` (`src/game/vmap/VMapDefinitions.h`) and
MoveMapGenerator mmap version 6 with Detour 7 (`src/game/Maps/MoveMapSharedDefines.h`,
`dep/recastnavigation/Detour/Include/DetourNavMesh.h`), the values `TerrainTile`, `VMapFormat` and
`NavMeshFormat` accept. A different vmangos revision should be checked the same way first.

## Verification

`tests/ArcaneCore.Game.Tests/Collision/RealTerrainDataTests.cs` and `VMapNativeOracleTests.cs` read
the real files. They skip, visibly, unless `ARCANECORE_TEST_TERRAIN_DIR` names the data root (the
directory holding `maps/`, `vmaps/`, `mmaps/`); the oracle test also needs
`ARCANECORE_TEST_VMAP_ORACLE` = the `VMapProbe.exe` the script builds:

```powershell
$env:ARCANECORE_TEST_TERRAIN_DIR = 'D:\ArcaneCore-lanes\terrain-out'
$env:ARCANECORE_TEST_VMAP_ORACLE = '<ToolsDir>\vmangos-src\bin\VMapProbe.exe'
dotnet test tests/ArcaneCore.Game.Tests --filter "FullyQualifiedName~RealTerrainDataTests|FullyQualifiedName~VMapNativeOracleTests"
```

What they check (expected values come from the world database, not from the files):

- every continent `.map` parses; the five race start positions on open ground stand on the
  extracted ground (Northshire 83.531 vs spawn z 83.531);
- every `.vmtree`, `.vmtile`, `.vmo` and doodad model parses (6081 files);
- model floors: Northshire Abbey (82.125), Goldshire inn (56.963), the Deathknell crypt where the
  undead start 17 yd below the terrain surface (121.670, indoors), a Darnassus bed stored under a
  NUL-terminated name (1347.291);
- the abbey walls block sight between the trainer inside and the marshal outside; the same pair is
  clear from above the roof;
- every continent `.mmtile` parses with distinct Detour tile coordinates; a navmesh path walks out
  of the abbey around its walls, and one crosses tiles from Northshire to the Goldshire inn;
- 4900 random height / line-of-sight / area queries over Stormwind, Ironforge, Undercity,
  Goldshire, Northshire, Booty Bay, Orgrimmar, Thunder Bluff, Darnassus, two start valleys and six
  dungeons agree with vmangos' own `VMapManager2` (`tools/terrain/vmap-oracle`) on the same files:
  0 mismatches (914 model heights, 1019 blocked rays, 583 inside WMO groups).

With the readers as they were before this check, 8 of these tests fail and 1121 of the 4900 oracle
queries disagree. The three bugs (fixed): the BIH traversal refused vmangos' empty-left-child node
layout (whole subtrees unreachable), the WMO liquid chunk size vmangos writes 4 bytes short made 58
models unreadable, and 1298 doodads whose names are stored with a trailing NUL never loaded. Details
in [vmap-los.md](vmap-los.md).

## Using it on a server

Set `World:Maps:DataDirectory` to the data root; `World:Collision:VMapDirectory` and
`MMapDirectory` stay empty and resolve to `<DataDirectory>/vmaps` and `/mmaps`. The key is read at
startup (a restart is needed). The startup log confirms each part: `Terrain: reading .map files
from …`, `Collision: vmaps from … (line of sight on, heights on)` and `Collision: navmeshes from …`. The vmap fixes above are needed for correct
line of sight and model heights: a server built before them reads the same vmaps but misses models
silently (fail-soft: missing means open).

Later, for self-containment: an in-process `.map` extractor run on first start, and a terrain-grid
A* fallback over `.map` heights for maps without mmaps. Navmesh generation stays an offline job.
