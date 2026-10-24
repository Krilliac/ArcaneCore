# Area: Atlas 3D map, collision, navmesh and live world visualizer (`arcane-atlas`)

Branch `claude/atlas`. `arcane-atlas` (`tools/ArcaneCore.Atlas`) is the standalone 3D map, collision, navigation mesh,
client-versus-server diff, and live world-daemon visualizer for ArcaneCore. Every inspection, query, comparison, scene
export, and screenshot works identically from the browser UI, the command line (`ctl`, `run`, `script`), and the stdio
Model Context Protocol server (`mcp`).

Code: `tools/ArcaneCore.Atlas`. Tests: `tests/ArcaneCore.Atlas.Tests`. Live world-daemon feed:
[map-feed](map-feed.md) (`src/ArcaneCore.World/Ops/MapFeed`). Real client textures and models (BLP/M2/display tables, ATG1 v2, ATX1):
[atlas-models](atlas-models.md); the lanes that build on it: [terrain textures](atlas-terrain-textures.md),
[WMO and M2 materials](atlas-model-materials.md), [live models and characters](atlas-live-models.md).
Browser-side reader for a local game folder: [atlas-client-files](atlas-client-files.md).

## Delivered

### 1. CLI verbs (`AtlasCli`)

| Verb | Usage | Meaning |
|---|---|---|
| `serve` | `arcane-atlas serve [--data DIR] [--client DIR] [--port N] [--open] [--log-level LEVEL] [--log-file PATH]` | Starts the loopback Kestrel server on `127.0.0.1` (random port unless `--port`), writes a discovery file under the Atlas state directory, and prints `{pid, port, url, discovery, version}` as one JSON line on stdout. `--open` launches Microsoft Edge or Google Chrome in app-window mode with the per-instance token in the URL fragment (`#t=<token>`). |
| `ctl` | `arcane-atlas ctl [--pid N] [--compact] <command> [key=value ...] [--json OBJECT\|-]` | Sends one command over loopback HTTP (`POST /api/command`) to a running `serve` instance discovered via its state file. |
| `run` | `arcane-atlas run [--data DIR] [--client DIR] [--compact] [--log-level LEVEL] [--log-file PATH] <command> [key=value ...] [--json OBJECT\|-]` | Executes one command headless without starting Kestrel or a browser; `capture` and `render.png` use the CPU renderer (`CpuRenderer`). |
| `script` | `arcane-atlas script [--attach [--pid N]] [--keep-going] [--data DIR] [--client DIR] [--log-level LEVEL] [--log-file PATH] <file.jsonl\|->` | Runs a JSONL command script (`{"command":"...","args":{...}}` per line), either headless or attached to a live `serve` instance. |
| `mcp` | `arcane-atlas mcp [--attach \| --headless] [--pid N] [--data DIR] [--client DIR] [--log-level LEVEL] [--log-file PATH]` | Runs a JSON-RPC 2.0 MCP server on stdio (`tools/list`, `tools/call`) exposing every registered Atlas command as an MCP tool. Attaches to a running `serve` instance when one is present unless `--headless` is specified. |
| `layers` | `arcane-atlas layers` | Prints the static catalog of all 32 visual layers (`id`, `group`, `view`, `title`). |
| `help` | `arcane-atlas help` | Prints CLI usage (`ctl help` and `run help` return the full command catalog as JSON). |

Exit codes: `0` success, `1` command or environment failure, `2` command-line usage error.

Logging (`serve`, `run`, `script`, `mcp`): log lines go to standard error, never standard output. `--log-level LEVEL` (`trace`, `debug`, `information`/`info`,
`warning`/`warn`, `error`, `critical`, `none`) sets the lowest level written; the default is `information` for `serve` and `warning` for the others.
The per-request ASP.NET Core lines (`Microsoft.AspNetCore`: "Request starting" / "Request finished", one pair per poll of the web UI) are written from
`warning` up whatever the level, so a request that fails still shows; the `Microsoft.Hosting.Lifetime` lines ("Now listening on ...") stay at `information`.
`--log-file PATH` also writes the log to `PATH` as UTF-8 through Kernel's `RollingFileWriter` (rolls at 10 MB and daily, keeps five segments).

### 2. Views, compare modes, and layer catalog (`LayerCatalog`)

Atlas defines four viewport compare modes (`CompareMode`: `Server`, `Reference`, `Split`, `Diff`), five layer data views
(`LayerView`: `Server`, `Client`, `DotRecast`, `Diff`, `Live`), and 35 layers across eight panel groups (`LayerGroup`:
`World`, `Terrain`, `Liquid`, `Collision`, `Navmesh`, `Reference`, `Diff`, `Live`):

| Layer ID | Group | View | Title & Description |
|---|---|---|---|
| `tiles` | `World` | `Server` | **Tile grid**: tile borders colored by which data files exist and parse (`ok`, `empty`, `missing`, `rejected`). |
| `terrain` | `Terrain` | `Server` | **Terrain**: height grid as the server interpolates it (four triangles per square around the center sample). |
| `terrain.holes` | `Terrain` | `Server` | **Terrain holes**: squares the server treats as holes (no terrain height). |
| `terrain.noheight` | `Terrain` | `Server` | **Flat tiles**: tiles stored without heights (`MAP_HEIGHT_NO_HEIGHT`); the server answers their single grid height everywhere. |
| `area` | `Terrain` | `Server` | **Area cells**: area flag borders per 33.3 yd cell. |
| `liquid` | `Liquid` | `Server` | **Terrain liquid**: liquid surfaces per height square from the `.map` liquid section. |
| `vmap.liquid` | `Liquid` | `Server` | **Model liquid**: WMO liquid parsed from the vmaps (`WmoLiquid`). |
| `vmap.wmo` | `Collision` | `Server` | **WMO collision**: world-model triangles the server collides with, placed in world space (`indoor`/`outdoor`). |
| `vmap.m2` | `Collision` | `Server` | **M2 collision**: doodad triangles (spell line of sight ignores these; movement checks do not). |
| `vmap.bounds` | `Collision` | `Server` | **Model bounds**: spawn bounding boxes from the vmap tiles. |
| `nav.poly` | `Navmesh` | `Server` | **Navmesh polygons**: navmesh polygons colored by the flags the server filters on (`ground`, `steep`, `water`, `magma`, `slime`). |
| `nav.detail` | `Navmesh` | `Server` | **Navmesh detail**: detail triangles used for polygon heights. |
| `nav.portals` | `Navmesh` | `Server` | **Tile portals**: cross-tile connections the server derives from overlapping border edges. |
| `nav.bv` | `Navmesh` | `Server` | **BV tree rejected**: tiles whose BV tree failed `HasUsableBvTree` verification (nearest-polygon lookups scan linearly). |
| `ref.terrain` | `Reference` | `Client` | **Client terrain**: ADT heights straight from the client MPQs (9x9 + 8x8 samples per chunk). |
| `ref.liquid` | `Reference` | `Client` | **Client liquid**: every `MCLQ` liquid layer from the client, before the extractor merges them. |
| `ref.wmo` | `Reference` | `Client` | **Client WMOs**: every WMO group triangle from the client, split into collidable (`collide`) and render-only (`render`) faces. |
| `ref.m2.collision` | `Reference` | `Client` | **Client M2 collision**: M2 bounding (collision) triangles from the client. |
| `ref.m2.render` | `Reference` | `Client` | **Client M2 meshes**: M2 render meshes from the client, for seeing what has no collision. |
| `ref.terrain.tex` | `Reference` | `Client` | **Client terrain (textured)**: reserved for lane A2 (terrain textures); listed with `hasSource: false` until then. |
| `ref.wmo.tex` | `Reference` | `Client` | **Client WMOs (textured)**: reserved for lane A3 (WMO materials); listed with `hasSource: false` until then. |
| `ref.m2.tex` | `Reference` | `Client` | **Client M2 models (textured)**: reserved for lane A3 (static M2 doodads); listed with `hasSource: false` until then. |
| `ref.nav.poly` | `Reference` | `DotRecast` | **DotRecast polygons**: the same navmesh tiles read by `DotRecast.Detour`. |
| `ref.nav.links` | `Reference` | `DotRecast` | **DotRecast links**: Detour links between polygons, including off-mesh connections the server skips. |
| `diff.terrain` | `Diff` | `Diff` | **Terrain difference**: server height minus client height at every sample; holes that disagree (`only-ref`, `only-server`, `expected`). |
| `diff.models` | `Diff` | `Diff` | **Model difference**: collision triangles present in only one of the server vmaps and the client files (`only-ref`, `only-server`, `expected`). |
| `diff.nav` | `Diff` | `Diff` | **Navmesh difference**: polygons and links where the server reader and DotRecast disagree (`only-ref`, `only-server`, `expected`). |
| `live.players` | `Live` | `Live` | **Players**: connected players from the world daemon's map feed (untiled). |
| `live.bots` | `Live` | `Live` | **Bots**: managed bots from the map feed (untiled). |
| `live.creatures` | `Live` | `Live` | **Creatures**: creatures colored by motion (`idle`, `moving`, `chase`, untiled). |
| `live.gameobjects` | `Live` | `Live` | **Game objects**: spawned game objects from the map feed (untiled). |
| `live.splines` | `Live` | `Live` | **Movement splines**: creature movement splines with a head at the current progress (untiled). |
| `live.routes` | `Live` | `Live` | **Bot routes**: navmesh routes bots are following; partial routes (`partial`) are dashed (untiled). |
| `live.trails` | `Live` | `Live` | **Trails**: recent entity positions, fading with age (untiled). |
| `live.grids` | `Live` | `Live` | **Loaded grids**: grids the world daemon has loaded on this map (untiled). |

### 3. Command catalog (63 commands)

Every command returns a JSON envelope `{"command":"...","ok":true,"elapsedMs":...,"data":{...}}` or `{"command":"...","ok":false,"elapsedMs":...,"error":{"code":"...","message":"..."}}`.

#### Host, view, camera, picking, capture, and bookmarks (`Host/`)
- `status`, `help` (`command?`), `layers.list`, `maps.list`, `tiles.status` (`map`, `x?`, `y?`, `layer?`, `radius?`), `cache.clear` (`layer?`).
- `view.get`, `view.set` (`map?`, `camera?`, `layers?`, `compare?`, `split?`, `loadRadius?`, `theme?`, `hud?`, `follow?`, `textureRadius?`, `textureBudgetMb?`), `layer.set` (`id`, `visible?`, `color?`, `opacity?`, `mode?`, `colorByAttribute?`).
- `camera.goto` (`map?`, `at?`, `x?`, `y?`, `z?`, `yaw?`, `pitch?`, `distance?`, `mode?`), `camera.frame` (`tile?`, `bounds?`, `guid?`, `map?`).
- `bookmarks.list`, `bookmark.go` (`name`), `bookmark.add` (`name`, `note?`), `bookmark.remove` (`name`).
- `query.pick` (`map?`, `ray` or `at`, `layers?`, `maxDistance?`): exact ray/triangle and point/line picking across visible layers (`ChunkPicker`).
- `capture` (`width?`, `height?`, `out?`, `source?`, `view?`): renders a PNG via the connected browser UI (`source=ui`, `UiHub.RequestCaptureAsync`) or the headless CPU renderer (`source=headless`, `IHeadlessRenderer`), with `source=auto` selecting the UI when one is connected.

#### Server data and collision queries (`Server/`)
- `server.status`: loaded map, vmap, and mmap counts and parse cache status (`ServerEngine`, `ServerData`).
- `query.height` (`map`, `at` or `x`/`y`/`z`, `maxSearchDistance?`): server `.map` terrain, WMO/M2 model floor, navmesh height, combined `MapCollision` floor, liquid level, area/zone, indoor/outdoor state, and under-map classification.
- `query.los` (`map`, `a`, `b`): both spell LoS (`ignoreM2: true`, vmangos `ModelIgnoreFlags::M2`) and movement/camera LoS (`ignoreM2: false`), returning the first hit point and model metadata.
- `query.path` (`map`, `a`, `b`, `mover?`, `options?`): server `NavMeshPathfinder` / `NavMeshQuery` polygon corridor, smooth path, and BV-tree rejection diagnostics.
- `query.floorstack` (`map`, `x`, `y`, `zMin?`, `zMax?`): all server terrain, liquid, WMO/M2, and navmesh surfaces along a vertical column `(x, y)`, sorted by `z` descending.
- `query.profile` (`map`, `from`, `to`, `steps?`): elevation and floor cross-section sampled evenly between two points, with grade statistics and a 3D polyline overlay.

#### Reference client readers and DotRecast oracle (`Reference/`)
- `client.status`, `client.maps`, `client.files` (`glob?`, `limit?`), `client.read` (`path`, `maxBytes?`): pure-C# MPQ v1 archive reader (`MpqArchive`, `MpqFileSystem`, PKWARE DCL explode, zlib, bzip2, wave, Huffman, hash/block tables, patch chain in retail 1.12.1 priority order), extracted directory reader (`DirectoryClientFiles`), and build-5875 DBC readers (`DbcTable`, `ClientDatabase` for `Map.dbc`, `AreaTable.dbc`, `LiquidType.dbc`).
- `client.terrain.analyze` (`map`, `tileX`, `tileY`): one tile's client ADT terrain: elevation min/max/mean, steep-triangle count and ratio, holes, liquid coverage, unique area ids, and the steepest chunk as an overlay.
- `ref.adt.info` (`map`, `x`, `y`), `ref.query.height` (`map`, `x`, `y`): client `.wdt` (`MPHD`, `MAIN`, `MODF`) and `.adt` (`MHDR`, `MCNK`, `MCVT`, `MCLQ`, `MDDF`, `MODF`) parsers and exact client triangle height/hole/liquid interpolation (`ClientTerrainService`).
- `ref.model.info` (`path`), `ref.query.los` (`map`, `a`, `b`, `includeSkipped?`), `ref.query.floorstack` (`map`, `x`, `y`, `zMin?`, `zMax?`, `includeSkipped?`): client WMO v17 root/group (`WmoRootReader`, `WmoGroupReader`) and M2 v256 (`M2Reader`) readers, `MODF`/`MDDF`/`MODD` placement transforms (`ClientTransforms`), raycast LoS, and vertical column floor stack (`ReferenceModelQueries`).
- `ref.nav.tile` (`map`, `x`, `y`), `ref.query.path` (`map`, `a`, `b`, `includeFlags?`, `excludeFlags?`, `extents?`), `ref.query.nearest` (`map`, `at`, `includeFlags?`, `excludeFlags?`, `extents?`): independent `DotRecast.Detour` reader (`MmapFileReader`, `ReferenceNavMeshService`) over `.mmap`/`.mmtile` files.

#### Client textures, model packages and display tables (`Textures/`, `Reference/Client/Models/Packages/`, `Reference/Dbc/`)
- `texture.get` (`key` or `path`, `fmt?`, `maxSize?`, `mip?`, `out?`), `texture.stats`: BLP2 decode and the texture cache; see [atlas-models](atlas-models.md).
- `model.info` (`path` or `kind` + `id`), `model.package` (`key` or `path`, `view?`, `out?`): M2 model package summary and its ATG1 v2 bytes.
- `display.resolve` (`kind`, `id`, `gender?`): the DBC display chain of a creature (with scale and humanoid extra), game object, item or race (with `gender`) down to model and texture files.

#### Server vs client diff (`Diff/`)
- `diff.terrain` (`map`, `x`, `y`, `radius?`): compares every `129x129` V9 and `128x128` V8 height sample and `16x16` chunk hole mask between `.map` and client `.adt` (`TerrainDiff`), classifying documented extractor rules (`flat-no-height-tile`, `clamped-at-minus-500`) under `expectedCount` (`ColorKeys.Expected`) and any other discrepancy above `1e-3` yd or hole mismatch under `unexplainedCount` (`ColorKeys.OnlyServer` / `ColorKeys.OnlyReference`).
- `diff.models` (`map`, `x`, `y`, `radius?`): compares server `.vmtile` WMO/M2 collision triangles against client `MODF`/`MDDF` placed models (`ModelDiff`), classifying documented extractor filters (`m2-without-collision-hull`, `wmo-NonCollidable-material`, `wmo-antiportal-group`, `wmo-unreachable-group`, `wmo-liquid-degenerate-face`, `wmo-tilted-doodad-rotation`, `wmo-duplicate-face`) under `expectedCount` and unmatched triangles under `unexplainedCount`.
- `diff.nav` (`map`, `x`, `y`, `radius?`): compares server `NavMeshTile` against `DotRecast.Detour` (`NavDiff`), classifying `offmesh-ignored-by-server`, `bv-tree-rejected-by-server`, `portal-byte-quantization`, and `portal-slab-climb-rule` under `expectedCount` and any vertex, polygon, or unexpected link mismatch under `unexplainedCount`.

#### Headless CPU renderer and scene exporters (`Render/`)
- `render.png` (`width?`, `height?`, `out`, `view?`): renders a view (with optional RFC 7396 merge patch) to a PNG file using the deterministic software renderer (`CpuRenderer`, `PngWriter`).
- `export.obj` (`map`, `x?`, `y?`, `radius?`, `layers?`, `out`): exports tiled layer geometry around a point as Wavefront `.obj` + `.mtl` (`ObjExporter`).
- `export.json` (`map`, `x?`, `y?`, `radius?`, `layers?`, `out`): exports tiled layer geometry, world-space positions, indices, and attributes as JSON (`JsonExporter`).

#### Live world-daemon connection (`Live/`)
- `live.status`, `live.connect` (`pid?`), `live.disconnect`, `live.subscribe` (`map`, `instance?`, `hz?`, `kinds?`, `paths?`), `live.entities` (`kind?`, `near?`, `radius?`, `name?`, `limit?`, `map?`, `instance?`, `guid?`, `trails?`), `live.follow` (`guid?`), `live.query` (`kind`, `a`, `b?`, `map?`, `instance?`): connects `LiveFeed` to `\\.\pipe\arcanecore-mapfeed-{pid}` (when `World:Control:MapFeed:Enabled` is `true` on the world daemon; see [map-feed](map-feed.md)), stores live frames and trails in `LiveStore`, and forwards live collision queries (`los`, `height`, `path`, `undermap`) to the world thread.
- `live.find` (`map?`, `query?`, `kind?`, `inCombat?`, `limit?`) searches the live store by name substring, exact guid or entry, kind, map and combat state; `live.hotspots` (`map?`, `cellSize?`, `limit?`) buckets live entities into cells and returns the busiest combat and player clusters.
- Packet tools, attached only (`serve` with `ctl`, `script --attach` or `mcp --attach`; `run`, headless `script` and `mcp --headless` answer `Unavailable`), and only when the world daemon has its [packet tools](map-feed.md#packet-tools) on (`World:Control:PacketTools:Enabled`): `server.console` (`line`) runs one console line and returns its reply lines; `live.inject` (`target` or `guid`, `opcode`, `payload?` or `fields?`) hands a client opcode to an online session through its own dispatcher; `live.send` (same arguments) queues a server opcode to its client; `live.tap.start` (`target` or `guid`, `dir?`, `opcodes?`, `maxPackets?`, `seconds?`, `bytes?`), `live.tap.read` (`max?`, `wait?`) and `live.tap.stop` watch a session's packets (`PacketToolCommands`, `PacketToolTraffic`).
- `opcodes.find` (`query?`, `side?`, `limit?`): searches the build-5875 world opcode table (`WorldOpcodeNames`); works offline and headless.
- `fields` builds a payload from typed values written little-endian one after another, each `type:value` with type `u8`, `u16`, `u32`, `u64`, `f32`, `guid` (a `u64`) or `cstring` (UTF-8 then a 0 byte); numbers take decimal, negative decimal (two's complement) or `0x` hex (`PacketFields`). These are the server's own writers (`src/ArcaneCore.Protocol/PacketWriter.cs:27-55` for integers and floats, `:105-117` for `WriteCString`) and the reference core's: vmangos@0e3ff01 `src/framework/Utilities/ByteConverter.h:51-56` converts nothing on a little-endian host and `src/shared/ByteBuffer.h:180-185` appends a string with a trailing 0. On the `ctl` command line a list is split at commas, so a `cstring` holding a comma goes through `--json`.

### 4. Driving the world daemon headlessly

Agents and tools can drive and watch a local development world daemon with no game client and no browser, through `serve` and
`ctl` (or `mcp --attach`, where every command below is an MCP tool). The daemon needs the map feed and its packet tools on, in the
Development environment (`World:Control:MapFeed:Enabled=true`, `World:Control:PacketTools:Enabled=true`):

```
arcane-atlas serve                                   # prints {pid, port, ...}; keep it running
arcane-atlas ctl live.connect                        # finds the daemon's feed pipe; hello says tools:true
arcane-atlas ctl server.console line=".server info"
arcane-atlas ctl opcodes.find query=standstate side=client
arcane-atlas ctl live.tap.start target=Botname maxPackets=200 seconds=30
arcane-atlas ctl live.inject target=Botname opcode=CMSG_STANDSTATECHANGE fields=u32:1
arcane-atlas ctl live.send target=Botname --json "{\"opcode\":\"SMSG_NOTIFICATION\",\"fields\":[\"cstring:hello, world\"]}"
arcane-atlas ctl live.tap.read wait=2
arcane-atlas ctl live.tap.stop
```

`live.inject` answers how the packet was handled: `handler` `session` (it ran under the session's dispatcher before the answer) or
`world` (it was queued for the world thread, like the client's own world opcodes); `live.tap.read` shows the reply packets. A refusal
comes back as `Failed` with the daemon's code (`offline`, `lifecycle-opcode`, `no-handler`, `wrong-state`, `not-injectable`,
`oversize`, `busy`, `tap-busy`, `no-tap`; see [map-feed](map-feed.md#packet-tools)), `BadArguments` for an opcode or payload Atlas
can reject itself, and `Unavailable` when the daemon's packet tools are off or no daemon is connected.

Security model: the commands add no new way in. They speak only to the map feed's local named pipe, which keeps its access list
(the daemon's own account and SYSTEM, NETWORK denied), `PIPE_REJECT_REMOTE_CLIENTS` and first-instance rule; the daemon serves them
only when `World:Control:PacketTools:Enabled` is set and the environment is Development (or
`World:Control:PacketTools:AllowOutsideDevelopment` is also set, with a startup warning); both default to `false`. The local live
server runs as Development, so it is kept out by a marker instead: every live launcher (`tools/ops/live/launch-daemon.ps1`,
`tools/ops/deploy/deploy-live.py` and the supervised restart of `tools/ops/diag/server-status.ps1 -Supervise`) sets
`ARCANECORE_LIVE_SERVER=1`,
and the daemon refuses the tools then, whatever the configuration says. The daemon logs
every console line (redacted), injection and send (opcode, length, target, client process; never the payload) and counts injections
and sends in `arcanecore_tools_packets_total`. Atlas itself only relays what its loopback, token-authenticated `serve` instance is
asked.

### 5. Binary geometry wire format (`ATG1`)

Browser and headless consumers read tile geometry chunks in the compact `ATG1` little-endian binary format (`GeometryWire`):
- 48-byte header (`GeometryWire.HeaderBytes`): `u32 magic` `0x31475441` (`"ATG1"`), `u16 version` `1`, `u16 flags` (`bit0` triangles, `bit1` lines, `bit2` points), `f64 originX`, `f64 originY`, `f64 originZ` (subtracted before `float32` vertex encoding to preserve sub-millimeter precision across the 34,133 yd map), `u32 vertexCount`, `u32 triangleCount`, `u32 lineCount`, `u32 pointCount`.
- 4-byte-aligned payload arrays: `f32[3 * vertexCount] positions`, `u32[3 * triangleCount] triangles`, `u32[triangleCount] triangleAttributes`, `u32[2 * lineCount] lines`, `u32[lineCount] lineAttributes`, `u32[pointCount] points`, `u32[pointCount] pointAttributes`, followed by `u32 jsonBytes` and `jsonBytes` of UTF-8 JSON metadata (`{"layer","tile":[map,x,y],"part","attributes":{"id":{...}},"stats":{...}}`).

Chunks that carry normals, texture coordinates, vertex colours, skinning data or material batches are written as `ATG1` version 2
(a 64-byte header with a stream mask and a batch count; chunks without any of these stay byte-identical version 1). The texture
wire (`ATX1`) and the binary route `GET /api/bin/{name}` that serves both are described in [atlas-models](atlas-models.md).

### 6. Security and loopback posture

- `serve` binds strictly to `127.0.0.1` (`AtlasServer`) and validates the `Host` and `Origin` headers against `127.0.0.1:<port>` / `localhost:<port>`.
- Every `/api/*` HTTP request requires an `Authorization: Bearer <token>` header matching the per-instance token in the user-only discovery file (`0600` on POSIX, current-user ACL directory on Windows).
- The browser opens with `#t=<token>` in the URL fragment (never in query strings or server logs), moves the token into `sessionStorage`, strips the fragment via `history.replaceState`, and authenticates `/api/ws` via a first-frame `{"op":"auth","token":"..."}` message (unauthenticated sockets close with code `4401`).
- Browser UI assets under `tools/ArcaneCore.Atlas/Web/` use a self-contained WebGL2 renderer (`js/renderer.js`, `js/scene.js`, `js/viewport.js`) with `Content-Security-Policy` locked to `'self'` and zero external CDN requests.
- File output commands (`render.png`, `export.obj`, `export.json`) validate target paths through `OutputPaths` so outputs cannot overwrite files inside any git checkout.

## Environment variables

| Variable | Meaning |
|---|---|
| `ARCANECORE_TEST_TERRAIN_DIR` | Default `--data` directory containing server `maps/`, `vmaps/`, and `mmaps/`. |
| `ARCANECORE_TEST_CLIENT_DIR` | Default `--client` directory containing a 1.12.1 client (`Data/*.MPQ`) or extracted client files. |
| `ARCANECORE_TEST_CLIENT_FILES_DIR` | Extracted 1.12.1 client root (`DBFilesClient/`, `World/`) used by real-data tests when present. |
| `ARCANECORE_ATLAS_STATE_DIR` | Override for the per-user state directory holding `instances/`, `captures/`, and `bookmarks.json`. |

## Tests

`tests/ArcaneCore.Atlas.Tests` covers all Atlas modules with synthetic unit tests and environment-gated real-data verification over Goldshire (`0/49/31`):
- `Core/`: `WorldGrid`, `GeometryChunk`/`ATG1` round-trip (`GeometryWire`), `LayerCatalog`, `CommandRegistry`, `ViewState`.
- `Reference/`: PKWARE/zlib/bzip2/Huffman/wave decompression vectors, patch-chain precedence (`MpqFileSystem`), `DbcTable` and `ClientDatabase`, `.wdt`/`.adt` terrain and liquid (`ClientTerrainService`), WMO root/group and M2 readers, `MODF`/`MDDF`/`MODD` placement transforms (`ClientTransforms`), raycast LoS and `ref.query.floorstack` (`ReferenceModelQueries`), and `DotRecast.Detour` oracle queries (`ReferenceNavMeshService`).
- `Server/`: `.map`, `.vmtree`/`.vmtile`/`.vmo`, and `.mmtile` layers, multi-tile WMO/M2 ownership, WMO liquid heights, and `query.height`/`los`/`path`/`floorstack` (`ServerEngine`, `ServerData`).
- `Diff/`: synthetic and real-data `diff.terrain`, `diff.models`, and `diff.nav` classification tests (`TerrainDiff`, `ModelDiff`, `NavDiff`), verifying `unexplainedCount == 0` across Goldshire (`0/49/31`) and Booty Bay (`0/58/30`, including large M2 doodads up to `23.62x` scale and sloped cross-tile portals).
- `Render/`: `CpuRenderer`, `PngWriter`, `ObjExporter`, `JsonExporter`, `OutputPaths`.
- `Host/`: loopback Kestrel server (`AtlasServer`), bearer token and WebSocket `auth` handshake, discovery lifecycle, CLI verbs (`serve`, `ctl`, `run`, `script`, `mcp`, `layers`), `ChunkPicker`, and MCP stdio protocol (`McpServer`).
- `Web/`: Node.js + headless Edge/Chrome browser tests (`WebScriptTests`, `WebAssetsTests`, `browser.test.mjs`) verifying `ATG1` scene decoding, picking, split-screen, and WebGL2 canvas capture.
- `Live/`: `LiveFeed`, `LiveStore` spline interpolation and trails, `live.*` commands, and live geometry layers.

## Limits

- Textured terrain, WMO and M2 layers are not implemented yet (lanes A2 and A3 of [atlas-models](atlas-models.md)): the shipped layers render untextured attribute-colored collision, bounding, and render geometry with surface-normal shading and wireframe overlays. The texture, material and model-package foundation is in place.
- `live.connect` discovery of running daemons requires Windows when `pid` is omitted because the world daemon's map feed uses Windows named pipes (`\\.\pipe\arcanecore-mapfeed-{pid}` with `ControlPipeSecurity` and `PIPE_REJECT_REMOTE_CLIENTS`). All offline server, client, diff, render, export, and MCP commands are cross-platform.
