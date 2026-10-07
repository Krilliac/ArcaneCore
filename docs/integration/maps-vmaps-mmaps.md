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

All tests use synthetic tiles; no real extraction has been read yet (vmap-los.md, "Known gaps").

What substitutes for the data now:

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
- `D:/server-Zero/run/{maps,vmaps,mmaps}` (258 MB, 553 MB, 794 MB) is a MaNGOS Zero extraction:
  `.map` "z1.5", "VMAP_4.0", mmap version 5. **ArcaneCore cannot read it** (expects z1.4, VMAP_7.0,
  mmap 6); its readers would log and treat it as missing.
- `D:/World of Warcraft Classic 1.12.1/Data` holds the 1.12.1 MPQs (`base`, `dbc`, `misc`, …) the
  extractors need.

## Ways to get the effect

1. **Extract once from Nathan's own 1.12.1 client with vmangos' tools** (`map_extractor`,
   `vmap_extractor` + `vmap_assembler`, `MoveMapGen`). Produces exactly the formats the readers
   expect. Cost: maps and vmaps take minutes; MoveMapGen takes hours for both continents (it is
   multi-threaded; the result here would be ~0.8 GB like the Zero set). Needs the vmangos tool build
   (C++), which is a download/build step not done here.
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

## Recommendation

1. Extract with vmangos' tools from `D:/World of Warcraft Classic 1.12.1` into a directory outside
   the repository (for example `D:/ArcaneCore-data/{maps,vmaps,mmaps}`), and point
   `World:Maps:DataDirectory` at it (`dev-runner.ps1 -ContentDir`). Do **not** reuse the MaNGOS Zero
   set. Priority by value: **maps** first (bots can route at all off the test floor, falling,
   under-map, liquids and server-side areas), then **vmaps** (line of sight for spells/aggro, indoor
   checks, floors in towns and caves), then **mmaps** (real paths; until then bots walk the probed
   straight-line routes and creatures chase in straight lines).
2. On the first run with real data, verify the readers against it (the startup `Terrain:`/`VMaps`/`MMaps` log lines, a
   spell cast through a wall, a bot route through Goldshire): all current tests are on synthetic
   tiles.
3. Later, for self-containment: an in-process `.map` extractor run on first start, and a terrain-grid
   A* fallback over `.map` heights for maps without mmaps. Navmesh generation stays an offline job.

Nothing was downloaded or extracted for this note.
