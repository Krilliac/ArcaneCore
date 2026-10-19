# Map feed: the world daemon's live map stream for Atlas

The map feed lets a visualizer on the same machine (Atlas, `tools/ArcaneCore.Atlas`) watch one map of a running world daemon and ask
the server's own collision questions about it. It is read-only: nothing a client sends changes the world, and a question never loads a
grid or a tile. It is off by default.

Code: `src/ArcaneCore.World/Ops/MapFeed` (the service is `WorldMapFeed`, registered by `AddWorldMapFeed` in the daemon's production
services). Tests: `tests/ArcaneCore.World.Tests/Ops/MapFeed`.

## Options

| Key | Default | Range | Meaning |
|---|---|---|---|
| `World:Control:MapFeed:Enabled` | `false` | | Serve the feed. |
| `World:Control:MapFeed:MaxHz` | `5` | 1 to 60 | The fastest frame rate any subscription gets; a client asking for more gets this. |
| `World:Control:MapFeed:MaxEntitiesPerFrame` | `4096` | 16 to 65536 | The most entities one frame carries; the rest are counted as `truncated`. |
| `World:Control:MapFeed:MaxConnections` | `2` | 1 to 16 | The most clients at once (one pipe instance each). |

A value outside its range keeps the feed off with a warning that names the key, and `check-config` reports it (an error while the feed
is enabled, a warning while it is off). The generated reference is `docs/reference/configuration.md`.

## The pipe

* Name `\\.\pipe\arcanecore-mapfeed-{pid}`, where `{pid}` is the world daemon's process id. It is not configurable.
* Built exactly like the control pipe (`World:Control:Pipe:Enabled`): `LocalPipeServer` with `ControlPipeSecurity`, so only the account
  the daemon runs as and SYSTEM may open it, NETWORK is denied, and `PIPE_REJECT_REMOTE_CLIENTS` is set and read back from the kernel
  before anything is served.
* All `MaxConnections` instances are created at start, the first with `FILE_FLAG_FIRST_PIPE_INSTANCE`. A name another process created
  first is refused (the feed stays off with a warning), and no other process can add an instance, so a client beyond the limit finds the
  pipe busy.
* Windows only: on other systems the feed stays off with a warning.

## Protocol 1

One JSON object per line (UTF-8, `\n`; a trailing `\r` is ignored), at most 4096 bytes. Client lines:

| Line | Meaning |
|---|---|
| `{"op":"hello","protocol":1}` | Must come first. Answered with `{"op":"hello","protocol":1,"sha":"...","pid":n,"maps":[{"map":0,"instance":0,"players":n,"creatures":n}]}`: the build's source revision, the daemon's process id, and every running map. A different protocol is refused with error `protocol` and the connection is closed. |
| `{"op":"subscribe","map":0,"instance":0,"hz":4,"kinds":["player","bot","creature","gameobject"],"paths":true}` | Watch one map. Every field but `map` is optional: `instance` 0, `hz` 4 (capped at `MaxHz`, at least 0.1), all kinds, paths on. A second subscribe replaces the first. |
| `{"op":"query","id":n,"kind":"los\|height\|path\|undermap","map":0,"instance":0,"a":[x,y,z],"b":[x,y,z]}` | One question; `b` only for `los` and `path`. |
| `{"op":"unsubscribe"}` | Stop the frames. |

Server lines besides `hello`:

* `frame`: `{"op":"frame","seq":n,"clockMs":n,"map":0,"instance":0,"grids":[[gx,gy],...],"counters":{...},"entities":[...]}`. `seq`
  counts this connection's frames from 1; `clockMs` is the clock of the map's creature system (the one creature splines run on); `grids`
  are the grids with their objects loaded. The counters are `players`, `bots`, `creatures`, `gameobjects` (totals on the map, whatever
  the kinds filter), `emitted`, `truncated`, `nonFinite` (entities or paths left out because a coordinate was not a finite number),
  `pathsOmitted`, `dropped` (frames this connection lost to backpressure), `online`, `diffMs`, and the creature system's path counters
  `deferredPaths`, `directFallbackPaths`, `noPathPaths`, `homePathTeleports`.
* An entity is `{"g":"<16 hex digits>","k":"player|bot|creature|gameobject","n":"name","x":..,"y":..,"z":..,"o":..}` plus,
  when it has them: `e` (template entry, emitted for `creature` and `gameobject` only; players and bots have no entry and omit `e`),
  `hp` (health percent), `mt` (creature motion: `idle`, `random`, `waypoint`, `confused`, `chase`, `home`, `point`,
  `fleeing`, `distract`, `follow`, `formation`, else `unknown`), `tg` (the unit chased, else the selected target), `home` (`[x,y,z]`),
  `sp` (the creature's current spline: `{"id":n,"start":[x,y,z],"pts":[[x,y,z],...],"t0":clockMs,"dur":ms,"run":bool}`, with `"cut":true`
  when its points were cut at 512; `clockMs - t0` is the elapsed time the server itself uses), and `route` (a bot's current route:
  `{"pts":[...],"next":i,"flags":["meshEndedShort","partialToGoal","chase","navigated","questReturn"]}`, `"cut":true` past 512 points with `next` clamped to `0..pts.length`).
  Players and bots come first, then creatures that move or are in an active motion, then the other creatures, then game objects.
* `result`: `{"op":"result","id":n,"ok":true,"kind":"...",...}` or `{"op":"result","id":n,"ok":false,"code":"...","message":"..."}`.
* `error`: `{"op":"error","code":"...","message":"..."}` for a line that is not a query.

Error codes: `bad-json`, `line-too-long`, `unknown-op`, `hello-required`, `protocol`, `bad-args`, `too-many-errors`, `idle-timeout`,
`map-not-loaded`, `not-loaded`, `timeout`, `world-stopping`, `query-failed`. A line over the limit, five refused lines in a row, or 120 s
of silence without a subscription close the connection (after the error line). Refusals are logged at Warning or below, never as errors.

## Capture

* The capture runs on the world thread from the post-barrier `WorldRuntime.Updated` event, after every map of the tick (serial or
  parallel, `World:Parallel:MapUpdateThreads`) has finished, so nothing is read while a map thread writes it.
* The handler is attached only while at least one subscription exists: an idle or disabled feed costs the world thread nothing.
* Each subscription is throttled by the game time the world reports, to its own rate; a long tick does not buy a burst. Subscriptions of
  the same view (map, instance, kinds, paths) share one capture per tick.
* Frames come from a fixed pool and hold values, never references into the world, so serialization and the pipe write happen on the
  connection's own tasks. A connection may hold two frames; a connection that is behind (or a spent pool) loses the frame, counted in
  `dropped`, and the world thread never waits.
* Bots are the players of the managed bot registry. Their routes come through `IBotRouteSource` (World.Contracts, `ContractsVersion` 2),
  which the playerbot pack registers while it is attached (`PlayerbotRouteSource`, read-only over `PlayerbotMotion`); without the pack
  there are no routes.

## Queries

Each query runs once on the world thread through `WorldRuntime.InvokeAsync` (10 s limit) against that map's `MapCollision`. Coordinates
are checked first: three finite numbers inside the map, else `bad-args`, so collision code never sees anything else. A point (for `path`,
both endpoints; for `los`, any grid the segment crosses) whose grid or terrain tile is not loaded is refused with `not-loaded`; the feed
never loads one.

| Kind | Answer |
|---|---|
| `los` | `clear` (the server's line of sight, doodads ignored), `clearDoodads` (doodads counted), `data` (model data loaded), and `hit` (the first model hit) when either is blocked. |
| `height` | `terrain`, `model` (the model floor below z + 2), `floor` (the server's chosen floor), `outdoors`; null where the data holds no answer. |
| `path` | `hasPath`, `type` (the path flags), `length`, `pts` (at most 1024, `"cut":true` beyond), `data` (navigation data for the map). Tile reads stay off: when an intermediate navmesh tile between the loaded endpoints is not loaded, the search stops at the loaded boundary and reports the partial path in `type` (`"incomplete"`). |
| `undermap` | `verdict` `ok`, `undermap` (with `depth` in yards) or `unknown`. |
