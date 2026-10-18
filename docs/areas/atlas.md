# Area: Atlas 3D map, collision, navmesh and live world visualizer (`arcane-atlas`)

Branch `claude/atlas`. `arcane-atlas` (`tools/ArcaneCore.Atlas`) is the standalone 3D map, collision, navigation mesh,
client-versus-server diff, and live world-daemon visualizer for ArcaneCore. Every inspection, query, comparison, scene
export, and screenshot works identically from the browser UI, the command line (`ctl`, `run`, `script`), and the stdio
Model Context Protocol server (`mcp`).

Code: `tools/ArcaneCore.Atlas`. Tests: `tests/ArcaneCore.Atlas.Tests`. Live world-daemon feed:
[map-feed](map-feed.md) (`src/ArcaneCore.World/Ops/MapFeed`).

## Delivered

### 1. CLI verbs (`AtlasCli`)

| Verb | Usage | Meaning |
|---|---|---|
| `serve` | `arcane-atlas serve [--data DIR] [--client DIR] [--port N] [--open]` | Starts the loopback Kestrel server on `127.0.0.1` (random port unless `--port`), writes a discovery file under the Atlas state directory, and prints `{pid, port, url, discovery, version}` as one JSON line on stdout. `--open` launches Microsoft Edge or Google Chrome in app-window mode with the per-instance bearer token. |
| `ctl` | `arcane-atlas ctl [--pid N] [--compact] <command> [key=value ...] [--json OBJECT\|-]` | Sends one command over loopback HTTP to a running `serve` instance discovered via its state file. |
| `run` | `arcane-atlas run [--data DIR] [--client DIR] [--compact] <command> [key=value ...] [--json OBJECT\|-]` | Executes one command in-process without starting Kestrel or a browser; `render.capture` uses the software CPU rasterizer. |
| `script` | `arcane-atlas script [--attach [--pid N]] [--keep-going] [--data DIR] [--client DIR] <file.jsonl\|->` | Runs a JSONL command script (`{"command":"...","args":{...}}` per line), either headless or attached to a live `serve` instance. |
| `mcp` | `arcane-atlas mcp [--attach \| --headless] [--pid N] [--data DIR] [--client DIR]` | Runs a JSON-RPC 2.0 MCP server on stdio (`tools/list`, `tools/call`) exposing every registered Atlas command as an MCP tool. Attaches to a running `serve` instance when one is present unless `--headless` is specified. |
| `layers` | `arcane-atlas layers` | Prints the static catalog of all 23 visual layers (`id`, `group`, `view`, `title`). |
| `help` | `arcane-atlas help` | Prints CLI usage (`ctl help` and `run help` return the full command catalog as JSON). |

Exit codes: `0` success, `1` command or environment failure, `2` command-line usage error.

### 2. Views and layer catalog (`LayerCatalog`)

Atlas organizes layers into five view modes (`server`, `client`, `diff`, `live`, `split`) and four layer groups:

| Layer ID | Group | Default View | Description |
|---|---|---|---|
| `terrain` | `server` | `server` | Server `.map` V9/V8 heightfield mesh (128x128 four-triangle cells per tile). |
| `terrain.liquid` | `server` | `server` | Server `.map` liquid surface quads colored by liquid type. |
| `terrain.holes` | `server` | `server` | Server `.map` hole bitmask squares (`16x16` chunk masks). |
| `terrain.areas` | `server` | `server` | Server `.map` area ID boundaries and chunk attribution. |
| `vmap.wmo` | `server` | `server` | Server `.vmtree`/`.vmtile`/`.vmo` static WMO group collision triangles. |
| `vmap.m2` | `server` | `server` | Server `.vmtile`/`.vmo` M2 doodad collision triangles. |
| `vmap.liquid` | `server` | `server` | Server WMO group liquid height planes (`WmoLiquid`). |
| `vmap.bounds` | `server` | `server` | Server BIH and `ModelSpawn` axis-aligned bounding boxes. |
| `nav.poly` | `server` | `server` | Server `.mmtile` Detour ground navigation polygons (`NavMeshTile`). |
| `nav.detail` | `server` | `server` | Server `.mmtile` detail-mesh height triangles (`dtPolyDetail`). |
| `nav.offmesh` | `server` | `server` | Server `.mmtile` off-mesh connection arcs. |
| `nav.portals` | `server` | `server` | Server `.mmtile` inter-polygon and cross-tile portal edges. |
| `nav.bv.rejected` | `server` | `server` | Server `.mmtile` bounding-volume nodes rejected by `HasUsableBvTree`. |
| `tiles` | `server` | `server` | Tile availability grid across `.map`, `.vmtile`, and `.mmtile`. |
| `ref.terrain` | `client` | `client` | Reference 1.12.1 client ADT `MCNK`/`MCVT`/`MCLQ` terrain and liquid mesh. |
| `ref.wmo` | `client` | `client` | Reference 1.12.1 client WMO root/group (`MOHD`/`MOGP`/`MOVT`/`MOVI`/`MOPY`) placed via `MODF`. |
| `ref.m2` | `client` | `client` | Reference 1.12.1 client M2 (`MD20` bounding triangles) placed via `MDDF` and WMO `MODD`. |
| `ref.nav` | `client` | `client` | Independent `DotRecast.Detour` parse of `.mmap`/`.mmtile` navigation tiles. |
| `diff.terrain` | `diff` | `diff` | Server `.map` vs client ADT terrain height, hole, liquid, and area comparison. |
| `diff.models` | `diff` | `diff` | Server `.vmtile` spawns vs client `MODF`/`MDDF` placement and floor comparison. |
| `diff.nav` | `diff` | `diff` | Server `NavMeshTile` vs `DotRecast.Detour` polygon, portal, and BV-tree comparison. |
| `live.entities` | `live` | `live` | Live world-daemon players, bots, creatures, and game objects. |
| `live.paths` | `live` | `live` | Live creature splines and playerbot navigation routes. |

### 3. Command catalog

Every command returns a JSON envelope `{"ok":true,"command":"...","data":{...}}` or `{"ok":false,"error":{"code":"...","message":"..."}}`.

#### Host, view, camera, and bookmarks (`Host/`)
- `status`, `help`, `perf.stats`
- `maps.list`, `map.select`, `tile.inspect`
- `view.set` (`mode`, `splitLeft`, `splitRight`), `layer.set` (`layer`, `visible`, `opacity`, `wireframe`)
- `camera.set` (`pos`, `target`, `mode`, `fov`, `clipNear`, `clipFar`, `sliceMinZ`, `sliceMaxZ`), `camera.frame` (`tile`, `bounds`, `point`, or `guid`)
- `bookmark.save`, `bookmark.list`, `bookmark.goto`

#### Server data and collision queries (`Server/`)
- `query.height` (`map`, `at`): server terrain, WMO/M2 model floor, combined `MapCollision` floor, liquid level, zone/area, and indoor/outdoor state.
- `query.los` (`map`, `a`, `b`): both spell LoS (`ignoreM2: true`, vmangos `ModelIgnoreFlags::M2`) and movement/camera LoS (`ignoreM2: false`), returning the first hit point and model metadata.
- `query.path` (`map`, `a`, `b`, `straight?`): Detour polygon corridor and smooth path via `DetourNavMeshQuery`.
- `query.floorstack` (`map`, `x`, `y`, `zMin?`, `zMax?`): all terrain, liquid, WMO/M2, and navmesh surfaces along a vertical column `(x, y)`.
- `query.undermap` (`map`, `at`): classification against terrain, model floors, and holes (`safe`, `on-model`, `in-hole`, `under-terrain`, `above-all`, `no-data`).

#### Reference client readers and DotRecast oracle (`Reference/`)
- `ref.mpq.list`, `ref.mpq.extract`: pure-C# MPQ v1 archive reader (`MpqArchive`, PKWARE DCL explode, zlib, wave, Huffman, hash/block tables, patch chain in retail 1.12.1 priority order) and extracted directory fallback (`DirectoryClientFiles`).
- `ref.dbc.maps`, `ref.dbc.areas`, `ref.dbc.liquids`: build-5875 `Map.dbc`, `AreaTable.dbc`, and `LiquidType.dbc` readers (`WdbcFile`).
- `ref.adt.inspect`, `ref.query.height`: client `.wdt` (`MPHD`, `MAIN`, `MODF`) and `.adt` (`MHDR`, `MCNK`, `MCVT`, `MCLQ`, `MDDF`, `MODF`) parsers and exact triangle interpolation.
- `ref.wmo.inspect`, `ref.m2.inspect`, `ref.query.los`, `ref.query.floorstack`: client WMO v17 root/group and M2 v256 bounding-mesh readers, `MODF`/`MDDF`/`MODD` placement transforms, raycast LoS, and vertical column floor stack.
- `ref.nav.inspect`, `ref.query.path`, `ref.query.point`: independent `DotRecast.Detour` reader over `.mmap`/`.mmtile` files and path/nearest-point queries.

#### Server vs client diff (`Diff/`)
- `diff.terrain` (`map`, `x?`, `y?`, `radius?`, `threshold?`): compares every `129x129` V9 and `128x128` V8 height sample, hole bit, liquid square, and area ID between `.map` and client `.adt`, separating extractor uint16/uint8 quantization (`explainedCount`) from real discrepancies (`unexplainedCount`).
- `diff.models` (`map`, `x?`, `y?`, `radius?`): matches `.vmtile` `ModelSpawn` entries against client `MODF`/`MDDF` records by unique ID, model name, and position, classifying cross-tile BIH splits and missing/unplaceable models as explained and sampling vertical floor agreement on matched WMO spawns.
- `diff.nav` (`map`, `x?`, `y?`, `radius?`): compares server `NavMeshTile` polygons, vertices, portal links, off-mesh links, and BV-tree usability against `DotRecast.Detour`, classifying `offmesh-ignored-by-server`, `bv-tree-rejected-by-server`, `portal-byte-quantization`, and `portal-slab-climb-rule` as expected (`expectedCount`) and surfacing genuine discrepancies in `unexplainedCount`.

#### Headless software renderer and scene exporters (`Render/`)
- `render.capture` (`out?`, `width?`, `height?`, `layers?`, `supersample?`, `hud?`): renders the active view (or explicit layer list) to a valid PNG file. When `serve` has an attached browser client, captures the WebGL2 canvas; otherwise runs the deterministic CPU software rasterizer (`SoftwareRasterizer`, tile-binned depth buffer, edge-function MSAA 1x/2x, line capsules, bitmap HUD overlay, pure-C# PNG encoder).
- `export.obj` and `export.gltf` (`out?`, `map?`, `tiles?`, `layers?`): exports tile layer chunks to Wavefront `.obj` or binary glTF 2.0 `.glb`.

#### Live world-daemon connection (`Live/`)
- `live.connect` (`pid?`, `pipe?`, `map?`, `instance?`, `hz?`, `kinds?`, `paths?`), `live.disconnect`, `live.follow` (`guid`), `live.query` (`kind`, `a`, `b?`, `map?`, `instance?`): connects to `\\.\pipe\arcanecore-mapfeed-{pid}` (when `World:Control:MapFeed:Enabled` is `true` on the world daemon; see [map-feed](map-feed.md)), streams entity frames into `LiveStateStore`, and forwards live collision queries to the world thread.

### 4. Binary geometry wire format (`ATG1`)

Browser and headless consumers read tile geometry chunks in the compact `ATG1` little-endian binary format (`GeometryWire`):
- 44-byte header: magic `0x31475441` (`"ATG1"`), version `1`, `mapId`, `tileX`, `tileY`, `vertexCount`, `triangleIndexCount`, `lineIndexCount`, `pointIndexCount`, `jsonByteCount`, `flags`.
- Payload streams: `positions` (`float32 x 3`), `normals` (`float32 x 3`), `colors` (`uint8 x 4` RGBA), `triangles` (`uint32`), `triangleAttributes` (`uint32`), `lines` (`uint32`), `lineAttributes` (`uint32`), `points` (`uint32`), `pointAttributes` (`uint32`), followed by `jsonByteCount` bytes of UTF-8 JSON containing `layer`, `attributes`, and `stats`.

### 5. Security and loopback posture

- `serve` binds strictly to `127.0.0.1` (`AtlasServer`).
- Every state-mutating request (`POST /api/command`) and WebSocket upgrade (`/ws`) requires an `X-Atlas-Token` header (or `?token=` on `/ws`) matching a 32-byte CSPRNG per-instance token stored in the user-only discovery file (`0600` on POSIX, current-user ACL directory).
- Browser UI assets under `tools/ArcaneCore.Atlas/Web/` use a self-contained WebGL2 renderer (`js/renderer.js`, `js/shaders.js`, `js/scene.js`) with `Content-Security-Policy` locked to `'self'` and zero external CDN requests.
- File output commands (`render.capture`, `export.obj`, `export.gltf`, `ref.mpq.extract`) validate target paths through `OutputPaths` so relative paths resolve inside the Atlas state directory and path traversal (`..`) is rejected.

## Environment variables

| Variable | Meaning |
|---|---|
| `ARCANECORE_TEST_TERRAIN_DIR` | Default `--data` directory containing server `maps/`, `vmaps/`, and `mmaps/`. |
| `ARCANECORE_TEST_CLIENT_DIR` | Default `--client` directory containing a 1.12.1 client (`Data/*.MPQ`) or extracted client files. |
| `ARCANECORE_TEST_CLIENT_FILES_DIR` | Extracted 1.12.1 client root (`DBFilesClient/`, `World/`) used by real-data tests when present. |
| `ARCANECORE_ATLAS_STATE_DIR` | Override for the per-user state directory holding `instances/`, `captures/`, `exports/`, and `bookmarks.json`. |

## Tests

`tests/ArcaneCore.Atlas.Tests` covers all 11 Atlas modules with synthetic unit tests and environment-gated real-data verification over Goldshire (`0/49/31`):
- `Core/`: `WorldGrid`, `GeometryChunk`/`ATG1` round-trip, `LayerCatalog`, `CommandRegistry`, `ViewState`.
- `Reference/Mpq/` & `Reference/Dbc/`: PKWARE/zlib/Huffman/wave decompression vectors, patch-chain precedence, `WdbcFile` and `ClientMapDirectory`.
- `Reference/Client/Terrain/`: `.wdt`/`.adt` parsing, `MCNK`/`MCVT`/`MCLQ` geometry, and real-data oracle agreement against `.map` files.
- `Reference/Client/Models/`: WMO root/group and M2 readers, `MODF`/`MDDF`/`MODD` placement transforms, raycast LoS, and `ref.query.floorstack`.
- `Reference/Navmesh/`: `MmapTileReader` and `DotRecast.Detour` oracle queries (`ref.nav.inspect`, `ref.query.path`, `ref.query.point`).
- `Server/`: `.map`, `.vmtree`/`.vmtile`/`.vmo`, and `.mmtile` layers, multi-tile WMO/M2 ownership, WMO liquid heights, and `query.height`/`los`/`path`/`floorstack`/`undermap`.
- `Diff/`: synthetic and real-data `diff.terrain`, `diff.models`, and `diff.nav` classification tests, verifying `unexplainedCount == 0` on Goldshire (`0/49/31`).
- `Render/`: `SoftwareRasterizer`, `BitmapFont` HUD, `PngWriter`, `ObjExporter`, `GlbExporter`.
- `Host/`: loopback Kestrel server, bearer token, discovery lifecycle, CLI verbs (`serve`, `ctl`, `run`, `script`, `mcp`, `layers`), and MCP stdio protocol.
- `Web/`: Node.js + headless Edge/Chrome browser tests (`WebScriptTests`, `WebAssetsTests`) verifying `ATG1` scene decoding, picking, split-screen, and WebGL2 canvas capture.
- `Live/`: `LiveFeedClient`, `LiveStateStore` spline interpolation, `live.*` commands, and live geometry layers.

## Limits

- Textured WMO/M2 rendering is not implemented: Atlas renders untextured vertex-colored collision and bounding geometry (`MOVT`/`MOVI` and `MD20` bounding triangles) with surface-normal shading and wireframe overlays.
- `live.connect` requires Windows because the world daemon's map feed uses Windows named pipes (`\\.\pipe\arcanecore-mapfeed-{pid}` with `ControlPipeSecurity` and `PIPE_REJECT_REMOTE_CLIENTS`). All offline server, client, diff, render, export, and MCP commands are cross-platform.
