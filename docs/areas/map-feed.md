# Map feed: the world daemon's live map stream for Atlas

The map feed lets a visualizer on the same machine (Atlas, `tools/ArcaneCore.Atlas`) watch one map of a running world daemon and ask
the server's own collision questions about it. It is read-only: nothing a client sends changes the world, and a question never loads a
grid or a tile. It is off by default. The one exception is the separate, also-off-by-default [packet tools](#packet-tools): a developer
debugging aid that lets a local tool run console lines and inject, send and watch packets over the same pipe.

Code: `src/ArcaneCore.World/Ops/MapFeed` (the service is `WorldMapFeed`, registered by `AddWorldMapFeed` in the daemon's production
services). Tests: `tests/ArcaneCore.World.Tests/Ops/MapFeed`.

## Options

| Key | Default | Range | Meaning |
|---|---|---|---|
| `World:Control:MapFeed:Enabled` | `false` | | Serve the feed. |
| `World:Control:MapFeed:MaxHz` | `5` | 1 to 60 | The fastest frame rate any subscription gets; a client asking for more gets this. |
| `World:Control:MapFeed:MaxEntitiesPerFrame` | `4096` | 16 to 65536 | The most entities one frame carries; the rest are counted as `truncated`. |
| `World:Control:MapFeed:MaxConnections` | `2` | 1 to 16 | The most clients at once (one pipe instance each). |
| `World:Control:PacketTools:Enabled` | `false` | | Offer the [packet tools](#packet-tools) on this pipe (needs the feed on, and the Development environment unless the next key is set). Always refused when the process environment variable `ARCANECORE_LIVE_SERVER` is `1`: the local live server runs as Development, so the environment alone does not guard it, and `tools/ops/live/launch-daemon.ps1` sets that marker. |
| `World:Control:PacketTools:AllowOutsideDevelopment` | `false` | | The second, deliberate opt-in: let the tools start in any other environment, with a warning naming it. Never on a server players use; it does not lift the live-server refusal above. |

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

One JSON object per line (UTF-8, `\n`; a trailing `\r` is ignored), at most 4096 bytes (65536 while the packet tools are on, so an
injected payload of the largest client packet fits). Client lines:

| Line | Meaning |
|---|---|
| `{"op":"hello","protocol":1}` | Must come first. Answered with `{"op":"hello","protocol":1,"rev":2,"sha":"...","pid":n,"maps":[{"map":0,"instance":0,"players":n,"creatures":n}]}`: `rev` (the feed revision, see [atlas-insight](atlas-insight.md)), the build's source revision, the daemon's process id, and every running map. A different protocol is refused with error `protocol` and the connection is closed. |
| `{"op":"subscribe","map":0,"instance":0,"hz":4,"kinds":["player","bot","creature","gameobject"],"paths":true}` | Watch one map. Every field but `map` is optional: `instance` 0, `hz` 4 (capped at `MaxHz`, at least 0.1), all kinds, paths on. A second subscribe replaces the first. |
| `{"op":"query","id":n,"kind":"los\|height\|path\|undermap\|inspect\|anomalies","map":0,"instance":0,"a":[x,y,z],"b":[x,y,z]}` | One question; `b` only for `los` and `path`; `inspect` takes `guid` and `anomalies` takes `since` and `max` instead of `a` (see [atlas-insight](atlas-insight.md)). |
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
  `hp` (health percent), `cb` (`true` when the unit is in combat, rev 2; omitted otherwise), `d` (display id, rev 2; omitted when 0), `r` and `cl` (a player's or bot's race id and class id; omitted when 0; optional, no revision change), `mt` (creature motion: `idle`, `random`, `waypoint`, `confused`, `chase`, `home`, `point`,
  `fleeing`, `distract`, `follow`, `formation`, else `unknown`), `tg` (the unit chased, else the selected target), `home` (`[x,y,z]`),
  `sp` (the creature's current spline: `{"id":n,"start":[x,y,z],"pts":[[x,y,z],...],"t0":clockMs,"dur":ms,"run":bool}`, with `"cut":true`
  when its points were cut at 512; `clockMs - t0` is the elapsed time the server itself uses), and `route` (a bot's current route:
  `{"pts":[...],"next":i,"flags":["meshEndedShort","partialToGoal","chase","navigated","questReturn"]}`, `"cut":true` past 512 points with `next` clamped to `0..pts.length`).
  Players and bots come first, then creatures that move or are in an active motion, then the other creatures, then game objects.
* `result`: `{"op":"result","id":n,"ok":true,"kind":"...",...}` or `{"op":"result","id":n,"ok":false,"code":"...","message":"..."}`.
* `error`: `{"op":"error","code":"...","message":"..."}` for a line that is not a query.

Error codes: `bad-json`, `line-too-long`, `unknown-op`, `hello-required`, `protocol`, `bad-args`, `too-many-errors`, `idle-timeout`,
`map-not-loaded`, `not-loaded`, `timeout`, `world-stopping`, `query-failed`, and the packet tools' codes (below). A line over the limit, five refused lines in a row, or 120 s
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
| `inspect`, `anomalies` | Described in [atlas-insight](atlas-insight.md). |

## Packet tools

A developer debugging tool: it lets an agent or a script drive and observe a local world daemon headlessly through Atlas
(`server.console`, `live.inject`, `live.send`, `live.tap.*`; see [atlas](atlas.md), "Driving the server headlessly"). Over the map
feed's pipe a connected client may run a console line, hand a client opcode to an online session as if its client had sent it, queue a
server opcode to a session's client, and watch a session's packets. Code: `PacketTools.cs`, `MapFeedConnection.Tools.cs`,
`FeedPacketTap.cs` and `PacketToolOpcodes.cs` in `src/ArcaneCore.World/Ops/MapFeed`, and `src/ArcaneCore.World/Net/WorldSession.PacketTools.cs`.
Tests: `PacketToolsTests`, `PacketToolsOptionsTests` and `PacketToolsSessionTests` in `tests/ArcaneCore.World.Tests/Ops/MapFeed`.

### Security model

* **Off by default.** The tools exist only when `World:Control:PacketTools:Enabled` is `true` **and** the map feed is serving
  (`World:Control:MapFeed:Enabled`), decided once at start; `check-config` warns when the tools are on without the feed.
* **Development only.** In any host environment other than `Development` (`DOTNET_ENVIRONMENT`; unset counts as `Production`) the tools
  stay off with a Warning, unless `World:Control:PacketTools:AllowOutsideDevelopment` is also `true`; then they start with a Warning that
  names the environment. Every start of the tools logs a Warning that says what they allow. `check-config` warns when
  `AllowOutsideDevelopment` is on.
* **Never on the live server.** The local live server runs as `Development` (`tools/ops/live/launch-daemon.ps1` sets
  `DOTNET_ENVIRONMENT=Development`), so the environment alone does not guard it. Every live launcher sets
  `ARCANECORE_LIVE_SERVER=1` for the daemons it starts: that script (and `redeploy.ps1`, which starts the daemons through it),
  `tools/ops/deploy/deploy-live.py`, and the supervised restart of `tools/ops/diag/server-status.ps1 -Supervise` (a crash, a hang or a
  `.server restart`); and
  the World refuses the tools whenever that variable is `1`, whatever the configuration says (`Enabled` and `AllowOutsideDevelopment`
  included): the feed logs one Warning (`packet tools refused: this is the live server (ARCANECORE_LIVE_SERVER)`), the session gate is
  not created, and `check-config` warns when `Enabled` is set while the variable is. The decision reads the process environment once at
  start; nothing in `appsettings` can lift it.
* **Local pipe only.** No new endpoint: the requests ride on the map feed's pipe, with its access list (the daemon's account and SYSTEM,
  NETWORK denied), `PIPE_REJECT_REMOTE_CLIENTS` read back from the kernel, and `FILE_FLAG_FIRST_PIPE_INSTANCE` (see "The pipe").
  Whoever can open the pipe already runs as the daemon's account.
* **Audited.** Every console line is logged at Information (`Map feed console command: <line> (<client>)`, passwords and secrets masked
  by `ConsoleCommandRedactor`) and written to the GM audit as the console (player `Console`). Every injection and send is logged at
  Information with the client (pid and process name), the target's name and guid, the opcode name and the payload length, never the
  payload: an injection is logged when it is requested, before the session takes it (`Map feed inject requested: CMSG_QUERY_TIME (0 bytes) as 'Name' (guid ...)
  from client pid 1234 arcane-atlas`) and again with its verdict (`Map feed inject: CMSG_QUERY_TIME (0 bytes) as 'Name' (guid ...) from
  client pid 1234 arcane-atlas: Handled`), and counted in
  `arcanecore_tools_packets_total{op,result}` ([metrics](../ops/metrics.md)). Refusals are logged at Warning.
* **No handler bypass.** An injected packet goes through the session's own dispatcher (below), with every check a client's packet gets.
  Authentication and session-lifecycle opcodes are refused outright, in both directions.

Why the map feed's pipe and not the control pipe: the feed already speaks JSON with id-correlated results, serves several clients at
once and streams (a tap is a stream), and Atlas already has its client (`LiveFeed`); its pipe security is the control pipe's, verbatim.
The control pipe is one client at a time, plain text with a `#done` marker, and belongs to the redeploy scripts.

### Requests

The hello says `"tools":true` when the tools are on (a daemon without them says nothing). Every tool line carries an integer `id` and
is answered by one `result` of that id: `{"op":"result","id":n,"ok":true,"kind":"console|inject|send|tap|untap",...}`, or
`{"op":"result","id":n,"ok":false,"kind":"...","code":"...","message":"..."}`. A refusal by a tool (every code below except those
two) is an answer, not a strike against the connection. A malformed tool line with a readable id is answered the same way (`bad-args`);
with the tools off a well-formed tool line is refused with `tools-off`; both of those, like any refused line, count as a strike, and
5 refused lines in a row close the connection. A session is named by exactly one of `"target":"<character name>"` (any case) and
`"guid":"<player guid in hex>"`, real client or playerbot; an opcode by its number or its reference name (`"CMSG_QUERY_TIME"`, any
case); a payload by hex digits.

| Line | Answer |
|---|---|
| `{"op":"console","id":n,"line":".reload status"}` | Runs the line through the console executor on the world thread, as the server console (the control pipe's path; one console session per connection). `lines` (the reply, without the `[console] ` prefix), `settled` (false when a command that holds itself open, such as `.botctl wait`, did not finish within 2 minutes), `failed` (the command's failure, when it failed). Lines a command prints later arrive as `{"op":"consoleLine","id":<the latest console id>,"line":"..."}`. |
| `{"op":"inject","id":n,"target":"Name","opcode":"CMSG_PLAYED_TIME","payload":""}` | `target` (`name`, `guid`, `bot`), `opcode`, `name`, `length`, `handler` (`world` or `session`) and `verdict`: `queued` (a world-thread handler: on the session's world queue; it runs in the player's next map update), `handled` (a session-task handler: it ran to completion), `dropped` (over the connection's packet budget; dropped as a client's packet is, the session stays up) or `disconnected` (the dispatcher refused the packet the way it refuses a client's, or the session handler threw a fault that ends a client's session, and the session was disconnected as its reader would have done). |
| `{"op":"send","id":n,"guid":"0000000000000002","opcode":459,"payload":"68656C6C6F00"}` | `target`, `opcode`, `name`, `length`: the packet is queued to the client through the session's own `Send` (header cipher, outbound bound, observers, capture). |
| `{"op":"tap","id":n,"target":"Name","dir":"in\|out\|both","opcodes":[462,"SMSG_QUERY_TIME_RESPONSE"],"maxPackets":1000,"seconds":60,"bytes":256}` | `tap` (this id, carried by the stream lines), `target`, the bounds. Every field after the target is optional: both directions, every opcode (at most 64 listed), 1000 packets (1 to 100000), 60 s (1 to 3600), 256 payload bytes per packet as hex (0 to 4096). One tap per connection. |
| `{"op":"untap","id":n}` | `tap`; the tap's `tapEnd` (reason `stopped`) follows the result. |

A tap streams `{"op":"packet","tap":n,"seq":n,"t":<unix ms>,"dir":"in|out","opcode":n,"name":"...","len":n,"hex":"..."}` lines, with
`"cut":true` when the payload was longer than `bytes` and `"inj":true` for a packet a tool injected, then exactly one
`{"op":"tapEnd","tap":n,"reason":"count|time|stopped|closed","packets":<lines sent>,"dropped":<packets lost>}`: `count` after
`maxPackets` matching packets, `time` after `seconds`, `stopped` by `untap`, `closed` when the session or the connection ended. The tap
is detached from the session however it ends.

Codes: `tools-off`, `offline` (no online character by that name or guid, or it logged out meanwhile), `unknown-opcode` (not in the
build-5875 table), `wrong-direction` (a server opcode given to inject, a client opcode to send), `lifecycle-opcode` (the lists below),
`oversize` (an injected payload above 10236 bytes, the largest client packet: the size field counts the 4-byte opcode and the server
refuses a size above `0x2800`; a sent payload above 16384 bytes), `no-handler` (the server has no handler for the opcode), `wrong-state`
(the handler does not run in the session's state), `not-injectable` (a session-task opcode for a playerbot, which has no session task),
`busy` (the session's dispatcher stayed busy for 5 s), `tap-busy`, `no-tap`, plus `bad-args`, `timeout`, `world-stopping` and
`query-failed` as for queries.

Never injected (`PacketToolOpcodes.RefusedInject`): `CMSG_AUTH_SESSION`, `CMSG_WARDEN_DATA`, `CMSG_PING`, `CMSG_CHAR_ENUM`,
`CMSG_CHAR_CREATE`, `CMSG_CHAR_DELETE`, `CMSG_CHAR_RENAME`, `CMSG_PLAYER_LOGIN`, `CMSG_LOGOUT_REQUEST`, `CMSG_LOGOUT_CANCEL`,
`CMSG_PLAYER_LOGOUT`, `MSG_MOVE_TELEPORT_ACK`, `MSG_MOVE_WORLDPORT_ACK`.

Never sent (`PacketToolOpcodes.RefusedSend`): `SMSG_AUTH_CHALLENGE`, `SMSG_AUTH_RESPONSE`, `SMSG_WARDEN_DATA`, `SMSG_PONG`,
`SMSG_CHAR_ENUM`, `SMSG_CHAR_CREATE`, `SMSG_CHAR_DELETE`, `SMSG_CHAR_RENAME`, `SMSG_CHARACTER_LOGIN_FAILED`, `SMSG_LOGIN_VERIFY_WORLD`,
`SMSG_LOGOUT_RESPONSE`, `SMSG_LOGOUT_COMPLETE`, `SMSG_LOGOUT_CANCEL_ACK`, `SMSG_TRANSFER_PENDING`, `SMSG_NEW_WORLD`,
`SMSG_TRANSFER_ABORTED`, `MSG_MOVE_TELEPORT_ACK`, `MSG_MOVE_WORLDPORT_ACK`.

### How an injection runs

With the tools enabled, every session built from then on serializes its dispatcher (`WorldSession.DispatchAsync`) behind a per-session
gate. The session's own reader (socket or gateway) takes the gate for each packet it dispatches; an injection takes the same gate and
calls the same `DispatchAsync`. So the dispatcher keeps its single-producer rules (the world-queue bounds, the packet limiter, one
session handler at a time), and:

* an opcode with a world-thread handler (`OpcodeTable.OnWorld`) is put on the session's world queue and handled on the world thread in
  the player's map update, exactly like the client's own; this includes playerbots, whose world queue the map drains the same way;
* an opcode with a session-task handler (`OpcodeTable.OnSession`) runs its handler under the gate, on the feed connection's task instead
  of the reader's but never at the same time as the reader; a playerbot has no session task, so these are refused with `not-injectable`;
* the packet is offered to the session's packet capture and taps first, as the reader does, marked as injected;
* a state the handler does not accept, or an opcode with no handler, is reported (`wrong-state`, `no-handler`) instead of being dropped
  silently as the dispatcher would;
* an injected packet counts against the player's packet budgets like a client's (`Net:Protection:World*`, [netguard](../ops/netguard.md)):
  a burst of injections can be dropped and, past the flood limit, disconnects the session;
* a session handler that throws a fault the dispatcher does not contain ends the session, as it ends the reader's loop for a client's
  packet; the injection answers `disconnected` (the fault is logged at Error under the `session` fault site). A broken invariant
  (`InvariantViolationException`) also kicks the session and then propagates, as it does from the reader. A cancellation from the session's own
  kick answers `offline` (the session is closing); any other cancellation that is not the feed connection's own is a handler fault
  like any other.
* a packet over the connection's packet budget (`Net:Protection:World*`) is dropped as a client's is, and the injection answers
  `dropped`; the session stays up. A world-thread opcode queued on a session that closes meanwhile is discarded.

The gate follows the configuration (the options, the feed enabled, the environment, and not the live server), not whether the feed is
actually serving: a feed that is configured on but failed to start still gates sessions, which is harmless (one uncontended semaphore
per packet, and nothing can inject). A daemon with the tools off, or refused in this environment or as the live server, runs the
dispatcher exactly as before. The decision is
taken from the configuration when the server starts (`PacketToolsPermit`), not when the feed starts: the world host restores the
playerbots before the feed's own start-up, and those sessions get the gate too. A session is `not-injectable` only when it was built
while the tools were off.

### Taps never block

A tap is offered every packet at the session's capture point (`CapturePacket`: the reader for inbound, `Send` for outbound, on the world
thread or whichever thread sends), at the cost of one array read when no tap is attached. It filters, copies at most `bytes` of the
payload and tries a bounded queue of 256 packets; a packet that does not fit is dropped and counted (`dropped` in `tapEnd`). The
connection's own pump task writes the queue into `packet` lines through the connection's bounded outbound queue. A tool that stops
reading therefore loses packets; the world thread, the session's reader and its senders never wait for it.
