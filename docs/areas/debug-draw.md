# Area: debug drawing for GMs (`.debug vis`)

Branch `claude/debug-visualizers`. GM commands that draw what the server computes (line of sight, paths, creature waypoints, the
cell grid, floor heights, ranges, spawn points) as markers in the world, visible only to the GM who asked. Ported from the
`.debug vis` toolkit on Krilliac/server-Zero `feature/debug-visualizers` (MaNGOS Zero, C++), redesigned for ArcaneCore: the fork
summons real game objects that everyone nearby sees and needs a 512-row `gameobject_template` pool in the database; here the
markers are client-only objects that never exist on the server.

## Commands

All are under `.debug vis`, need a `GameMaster` account (retail level 3), and are listed in the
[GM command reference](../reference/gm-commands.md).

| Command | Draws | Data source |
|---|---|---|
| `.debug vis los` | Dots along `MapCollision.LineOfSightSegment(context.Player, target)`, from your collision height (`EyeHeightOf(player)`) to 1 yard above the selected target (`ObjectTargetHeight`, as `IsWithinLOSInMap`): green when clear; when blocked, red up to the hit, a shard and cannon-target reticle at the hit, small red jewels for the hidden rest | `map.Collision.IsInLineOfSight` (doodads ignored, as unit LoS) and `ILineOfSight.TryGetObjectHit(-0.5)` for the hit (doodads count there, so the hit can be on a doodad in front of the model that blocks); both vmaps |
| `.debug vis path` | The corners (blue crystal + glow) and dots of the path from you to the selected unit; red corners when it is `Incomplete`, `NoPath` or `NotUsingPath` (no navmesh: a straight line). Chat names the `PathType` and which pathfinder answered | `map.Collision.FindPath(..., Mover = PathMover.Player)`, the same seam creatures use (`WorldCollision.PathfinderFor`) |
| `.debug vis waypoints` | The selected creature's waypoint nodes (yellow crystal + glow) and the loop between them; chat lists point, position, wait and run | `CreatureContent.ResolveWaypointPath` (`creature_movement`, else `creature_movement_template`), as the waypoint generator |
| `.debug vis cells [#r]` | Flags on the cell corners (33.33 yards) within r cells (default 2, at most 5: 121 flags); a Horde flag where a grid corner (533.33 yards) is | `GridDefines`; each corner on the floor from `map.Collision.GetHeight` (at your height when no floor is known) |
| `.debug vis collision [#yd]` | A ray straight ahead at eye height (default 40, at most 533 yards) and the hit point | `ILineOfSight.TryGetObjectHit` |
| `.debug vis height` | A crystal and tall yellow column at the floor under you; markers for the terrain surface and water level when they differ. Chat prints floor, terrain, model and water heights, zone, area and indoor/outdoor | `MapCollision.GetHeight`, `TerrainInfo.GetHeight` / `GetLiquidLevel` / `GetZoneAndAreaId`, `GetModelHeight`, `IsOutdoors` |
| `.debug vis range [#yd]` | 36 jewels on a circle on the floor (default the 100-yard visibility distance) | `Map.VisibilityRange`, `VisibilityGreyDistance` |
| `.debug vis spawns [#yd]` | The spawn (home) points of creatures spawned within #yd (default 40), nearest 50; chat shows wander distance, movement type and how far each creature is from home | `CreatureMapSystem.Creatures`, `CreatureSpawn` |
| `.debug vis kit #id` | Plays a SpellVisualKit.dbc id on you, sent only to you (the fork's `.debug visual` broadcast it) | SMSG_PLAY_SPELL_VISUAL |
| `.debug vis follow` | Starts or stops following the selected creature or playerbot: its current path, redrawn whenever it changes (see [Live follow](#live-follow)) | `Creature.Spline`, `MotionMaster`; the playerbot pack's `IBotRouteSource` |
| `.debug vis follow nearby [#yd]` | Follows every creature and playerbot moving within #yd (default 40, at most `FollowNearbyMaxRadius`), nearest first, up to `MaxFollowedUnits`; chat says how many farther ones were left out | `CreatureMapSystem.CollectCreaturesNear`, `Map.Players` |
| `.debug vis follow off` | Stops every follow and removes their markers (`off` is an argument of `follow`: it changes only what your client shows, so it is not an audited write verb) | |
| `.debug vis follow list` | The units you follow, with their path points and markers | |
| `.debug vis list` | Your drawings, marker counts and seconds left | |
| `.debug vis clear` | Removes all your markers now and stops following | |

Every command prints a summary and its key markers' details to chat (at most 12 lines, then a count). Right-clicking a marker
prints that marker's line again, prefixed by the drawing (`[path] path corner 3/7 at (...)`); hovering shows its kind
(`DebugDraw: path corner`).

## How it works

- **Client-only objects** (`src/ArcaneCore.Game/DebugDraw/DebugMarkerPackets.cs`). A marker is a `GameObject` built only to write
  its create block with `UpdateBlockWriter.WriteCreateBlock` (the same layout as a real object) for the GM, sent in its own
  SMSG_UPDATE_OBJECT to that session. It is never added to a map, so it is in nobody's visibility set, takes no part in the
  simulation and is never saved; another player's client never receives it. SMSG_DESTROY_OBJECT removes it.
- **Reserved entries** (`DebugMarkerStyles`). Each marker kind has an entry in `0xFFFF00..0xFFFFFF` (`EntryBase` + kind). A game
  object GUID contains its entry, so a marker GUID can never equal a real object's whatever its counter. CMSG_GAMEOBJECT_QUERY for
  such an entry is answered with a synthetic goober template (type 10: the 1.12 client only shows the hover name of an interactive
  object; all data fields 0, so using it does nothing client-side). The names are fixed per kind, so the client's name cache
  (`gameobjectcache.wdb`) never shows a stale label; the fork's per-marker names in a 512-entry ring went stale once an entry was
  reused.
- **Use hook** (`GameObjectLootHandlers.Use`). CMSG_GAMEOBJ_USE with a marker GUID is answered by `DebugDrawFeature.OnMarkerUsed`
  (the marker's label in chat) and never reaches the game object system.
- **Lifetime and cleanup** (`src/ArcaneCore.World/Gm/DebugDraw/DebugDrawFeature.cs`). Markers are kept per GM in drawings (one per
  command). A drawing is destroyed when `LifetimeSeconds` pass (checked on `WorldRuntime.WorldTick`), by `.debug vis clear`, and
  when the GM logs out (`PlayerLoggingOut`, before the player leaves the map). On a map change the client has already dropped every
  object, so the drawings are forgotten without packets.
- **Bounds.** `MaxMarkersPerGm` (default 300; a glow companion counts) caps what one GM's client holds: a new drawing removes the
  oldest drawings to fit (a live follow's drawing last), and a drawing bigger than the cap is cut (key markers are listed first, so they survive). Each command
  is also bounded on its own: 60 dots per line, 120 fill dots per path, 100 waypoints, 121 cell corners, 36 ring points, 50 spawns.
  Only the dots of a line of sight or collision ray short of the target or a hit, the path's dots and the path's first corner skip
  the 3 yards nearest the GM; the other markers are placed where they fall (the height marker right under the GM).

## Live follow

`.debug vis follow` (`src/ArcaneCore.World/Gm/DebugDraw/DebugPathFollow.cs`) keeps a unit's path on screen while the unit moves. The
markers are the same client-only objects as every other drawing, so only the following GM sees them.

- **What is drawn.** The path's start and corners (blue crystal and glow; red when the path is incomplete or a straight line), the
  dots between them, and the goal as a glyphed crystal with a tall yellow glow (`PathGoal`). The goal is listed first, then the
  corners, then the dots, so a small share of the marker cap keeps the key markers. Each label names the unit, the path source and
  the corner, for example `Test Wolf (creature waypoint move, after Drawer): corner 2/4 at (...)` or
  `Walker (bot route, navmesh, partial to goal): goal at (...)`; right-click prints it, prefixed `[follow <name>]`.
- **A creature's path** is its active spline (`Creature.Spline`): where the spline started, then every point to its end. The source
  is `MotionMaster.CurrentType` (idle, random, waypoint, chase, home, point, fleeing, ...), with the target of a chase or follow
  (`MotionMaster.TargetedUnit`). The corners are red when the motion found its target unreachable (`MotionMaster.IsReachable`) or the
  map has no navmesh (`WorldCollision.PathfinderFor(map).Enabled` false: every move is a straight line).
- **A bot's path** is read through the `IBotRouteSource` seam (World.Contracts, `src/ArcaneCore.World.Contracts/Bots/BotRouteSource.cs`)
  that the playerbot pack registers on `ManagedBotRegistry` while it is attached (`PlayerbotRouteSource`, the same seam the
  [map feed](map-feed.md) uses). It answers the route the bot's motion walks now (`PlayerbotMotion`), whichever AI planned it (brain,
  group, battleground, economy, taxi walk, ...), so the core never sees the pack's route type. The source is the route's
  `BotRouteFlags`: `navmesh` or `direct steps`, `chase`, `mesh ended short`, `partial to goal`, `quest return`; the corners are red
  when the route is not navigated or ends short. Without the pack attached a bot has no route to draw (the GM is told).
- **Redraw.** Every `FollowIntervalMs` (500 ms) each followed unit's path is read into a reused buffer and compared with the one
  drawn: another point count, any point moved more than 0.25 yards, or another move kind (a creature's motion type and
  reachability, a bot's route flags) is a change. Only then are the old markers destroyed and the new ones created. A unit that stands
  (no spline, no route) has no markers until it moves again. A follow's drawing has no lifetime; when a GM's cap evicted it, it is
  drawn again on the next check.
- **End.** The follow holds GUIDs only and resolves them on the GM's map at each check, so it never keeps a unit alive or loaded. It
  ends, with a chat line, when the unit is no longer on that map (despawned, logged out, teleported away) and when a followed creature
  dies (its corpse does not move; a bot stays followed while dead, since its corpse run is a route). It ends silently when the GM logs
  out or changes map (the client dropped the markers), with `.debug vis follow off` and with `.debug vis clear`.
- **Limits.** One GM follows at most `MaxFollowedUnits` (10) units; they share `MaxMarkersPerGm` evenly (at least 4 markers each).
  Starting a follow shrinks the older follows' drawings to the new share first, so nothing is evicted and drawn twice. A path is drawn
  with at most 120 dots, and a bot route is read up to 4096 points.
- **Cost.** While nobody follows, the world tick returns at once (no read, no allocation). While following, a check reads and compares
  the path without allocating; only a changed path allocates (the snapshot, the labels, the packets).
- `.debug vis follow` is not tied to GM mode (`.gm on`): like the rest of `.debug vis`, it only needs a `GameMaster` account.

## Look

Models are GameObjectDisplayInfo.dbc ids of the 1.12.1 client; the paths below were read from the 5875 DBC. The fork's palette is
kept where it had one (green/red Un'Goro crystals for LoS, the Dire Maul shard and Darkmoon cannon target for hits, the purple
Silithus crystal for collision, the yellow crystal and very tall yellow aura for heights, Alliance flags for cells); path corners
are blue (the fork used green, the same as a clear LoS) and the new kinds use the remaining jewels and crystals.

| Kind | Model (display id) | Glow (display id) | Scale |
|---|---|---|---|
| LoS clear | UngoroCrystal_Green01 (2972) | AuraGreenShort (3993) | 0.5 |
| LoS blocked | UngoroCrystal_Red01 (2973) | AuraRedShort (1308) | 0.5 |
| Hidden beyond a hit | G_JewelRed (327) | - | 0.6 |
| Hit point | CorruptedCrystalShard (5746) | Carni_CannonTarget (6430) | 0.6 |
| Path corner / bad corner | UngoroCrystal_Blue01 (2971) / Red01 (2973) | AuraBlueShort (263) / AuraRedShort (1308) | 0.6 |
| Path dots | G_JewelBlue (2770) | - | 0.6 |
| Waypoint / waypoint path | UngoroCrystal_Yellow01 (2974) / G_JewelBlack (5811) | AuraYellowShort (1268) / - | 0.6 |
| Collision ray | FloatingPurpleCrystal01 (1667) | - | 0.4 |
| Floor height | UngoroCrystal_Yellow01 (2974) | AuraYellowVeryTall (266) | 0.6 |
| Cell / grid corner | AllianceCTFflag (5912) / HordeCTFflag (5913) | - | 1.0 / 1.5 |
| Range, terrain surface | G_JewelBlack (5811) | - | 1.0 |
| Spawn point | GlyphedCrystal (6431) | AuraPurpleShort (363) | 0.6 |
| Path goal (live follow) | GlyphedCrystal (6431) | AuraYellowVeryTall (266) | 0.8 |

## Configuration

`World:GmCommands:DebugDraw` ([configuration reference](../reference/configuration.md)): `MaxMarkersPerGm` (300, 1..2000),
`LifetimeSeconds` (120, 5..3600), `Spacing` (2 yards between dots, 0.5..50; a long line spreads its dots), `Glow` (true),
`FollowIntervalMs` (500, 100..10000), `MaxFollowedUnits` (10, 1..50), `FollowNearbyMaxRadius` (100 yards, 5..533).

Marker models (`DebugMarkerModels`, resolved once at startup):

- `Models:<Kind>` and `GlowModels:<Kind>` (the `DebugMarkerKind` name, e.g. `Models:Cell = 5811`, `GlowModels:LosClear = 0`)
  replace a kind's model or glow (glow 0 = none). An unknown kind name is a warning and is ignored.
- `GameObjectDisplayInfoDbcPath` (optional, the developer's own build-5875 GameObjectDisplayInfo.dbc, read by
  `GameObjectDisplayInfoDbcReader`): every built-in and overridden display id is checked against it. An override the file lacks is a
  warning and the kind keeps its built-in model; a built-in id the file lacks is a warning (there is nothing to fall back to). One
  info line reports the check. An unreadable or malformed file stops the daemon. Without the path the overrides are used unchecked.
- The model a marker shows is its object's display id field (the glow companions share their kind's entry and template already), so
  an override does not change the synthetic template.

Checked against the client-effective file (patch.MPQ copy, 1638 rows, identical to `<server-zero-run>\dbc`): all 19 distinct
built-in ids exist with the model paths in the table above, and each model's `.m2` is present in the client's MPQs.

## Tests

- `tests/ArcaneCore.Game.Tests/DebugDraw/`: segment and polyline sampling (spacing, caps, the skipped start, closed loops), the cell
  lattice against `GridDefines`, rings; reserved entries and GUIDs, the goober templates, and the create block (entry, model,
  scale, position, no flags, not in the viewer's visibility set).
- `tests/ArcaneCore.World.Tests/Gm/DebugDraw/`: over loopback with a GM and a bystander: the LoS markers reach the GM only and
  `.debug vis clear` destroys each one; a fake wall gives a hit point 0.5 yards in front of it; a fake pathfinder's corners are
  drawn; the no-navmesh path is marked bad; waypoints from `creature_movement` with the closing loop; right-click label and query
  answer; the cap evicts the oldest drawing; the lifetime expiry; logout destroys and forgets; the kit goes to the GM only.
  `DebugMarkerModelTests.cs`: the GameObjectDisplayInfo reader, model overrides applied or refused against a synthetic file (and drawn
  by `.debug vis los`), and, gated on `ARCANECORE_TEST_DBC_DIR`, every built-in id with its model path in the real client file.
  `DebugDrawCommandTests.Follow.cs`: a followed creature's spline drawn (goal, corners, dots), silent while unchanged, redrawn with every
  old marker destroyed when it walks a new path, bad corners without a navmesh, the end on despawn, on the GM's map change and logout;
  a followed player's route from a test `IBotRouteSource` (the pack detached) drawn and redrawn on a replan, the end when it logs out;
  `follow nearby` taking the nearest movers up to the limit and sharing the marker cap without evictions; no path read while nobody
  follows; the bystander never sees a marker.

## Limits

- Not verified in a real 1.12.1 client from this lane: that the client accepts a create for an object the server never had (the
  layout is byte-for-byte the one a real object gets), the look of each model and glow, the hover
  name and right-click on a goober marker, and how the client renders markers past its far clip. The fork's models were chosen in
  a client; the new ones (2971, 2770, 1268, 263, 1308, 5913, 6431, 363) were only checked to exist in the DBC.
- Markers are solid only if their model has collision; the 3-yard gap that the line, ray and path dots and the path's
  first corner keep from the GM is the fork's guard against standing inside one.
- Line of sight is the vmap static-model test (doodads ignored), not dynamic game objects such as doors.
- The path is computed for a walking, swimming player mover; a creature's own path (fliers, swimmers, the no-mmap terrain-step
  fallback the creature lane may install) can differ.
- Waypoints draw the static path; where the creature is on it, script pauses and `creature_movement_scripts` are not shown.
- The fork's `.debug perf` (world tick averages) is not ported: `.server info` already prints the tick timing (mean, p95, p99,
  max, late ticks) through `ServerInfoDiagnostics`.
- Live follow: the bot test drives the seam with a test route source and a loopback player; the pack's own `PlayerbotRouteSource` is
  exercised by the map feed tests, not here. A creature's path is the spline it walks now: the waypoint list ahead of it, a path
  waiting for search budget, and the navmesh `PathType` of the move are not shown (the creature does not keep them), so a creature's
  straight line is marked red only on a map without a navmesh. Following a player who is not a playerbot draws nothing.
- A content row with an entry in `0xFFFF00..0xFFFFFF` would be shadowed by the markers' query answer; no 1.12 content uses that
  range.
