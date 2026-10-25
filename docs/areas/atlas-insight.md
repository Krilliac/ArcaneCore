# Area: Atlas live insight (inspect, anomalies, recorder, heatmap, watch)

Live-debugging features of `arcane-atlas` ([atlas](atlas.md)) on top of the world daemon's [map feed](map-feed.md). The
world side is in `src/ArcaneCore.World/Ops/MapFeed` (`MapFeedInspect`, `MapFeedAnomalyRing`, `MapFeedInsightJson`); the
tool side is in `tools/ArcaneCore.Atlas/Insight` (commands, recorder, heatmap, watch) and `tools/ArcaneCore.Atlas/Web/js/insight`
(panel and performance HUD). Nothing here adds a GM or server command.

## Feed revision

The hello reply carries `rev` (an integer 1..1000; the world writes `MapFeedProtocol.Revision`, 2). Absent or malformed means rev 1.
Rev 2 adds two optional entity fields, written by `MapFeedJson` from `MapFeedCapture`: `cb:true` for a unit with the in-combat unit
flag (omitted when false) and `d`, the unit's display id (omitted when 0), and the two insight query kinds. Both fields are additive,
so a rev 1 consumer ignores them. Atlas reads `rev` in `FeedSession` (`FeedSession.FeedRevision`, exposed as `LiveFeed.FeedRevision`); the shared
`HelloInfo` record is unchanged. `live.heatmap metric:combat` needs rev 2 and fails `unavailable` against an older world. An older
world answers the new query kinds with "unknown query kind"; Atlas remembers that per connection and reports `unavailable` with an
update hint.

## Wire: `inspect` and `anomalies`

Both are ordinary `query` requests (`kind`, `id`), answered on the world thread like `los` and `path`, and written by
`MapFeedInsightJson`, never through `MapFeedJson.WriteResult`.

### `inspect`

Request: `{"op":"query","id":N,"kind":"inspect","map":M,"instance":I,"guid":"HEX"}`. The map must be running (refused
`map-not-loaded`, never created) and the object must be on it (`not-found`); no grid is loaded for the lookup. The result carries
the guid, entity kind, name, position `p`, and for units: level, hp, power, faction, unit flags, stand state, target, victim,
in-combat, the top 10 of the threat list, auras (spell id, stacks, remaining ms, caster), the motion generator stack, the AI name and,
when the playerbot pack's route seam is attached, the bot's route and goal. Spell names are not on the wire; Atlas adds them from
`Spell.dbc` (`SpellNames`) when a client directory is configured.

### `anomalies`

Request: `{"kind":"anomalies","since":S,"max":K}`. The answer lists the notices with a sequence number above `S`, oldest first, at
most `K`, plus `latest` (the newest sequence issued) and `lost` (notices that fell out of the ring between `S` and the oldest kept).
When `S` is above `latest` (the daemon restarted, so the sequence numbers started over) the answer is empty and carries `"reset":true`
(omitted otherwise); `live.anomalies` then asks again from 0 and reports `reset:true`.
Each item has `seq`, `t`, `key`, `subsystem`, `code`, `severity`, `subject`, `detail`, `count` and, when the subject names a live
object, `pos:"current"`, `map`, `instance` and `p`. Positions are the subject's position at query time, not at report time.

The subject forms that resolve to a guid:

| Subject | Guid |
|---|---|
| `creature [entry ]E spawn S` | `ObjectGuid.WithEntry(HighGuid.Unit, E, S)`; the creature reporters write the `entry ` form (`CreatureAnomalies.Subject`) |
| `gameobject [entry ]E spawn S` | `HighGuid.GameObject`, and when no such object is on a map, `HighGuid.Transport` (an elevator or tram, template type 11, carries the transport high part: `GameObject.HighGuidOf`); the elevator reporters in `GameObjectMapSystem.Elevators.cs` write this form |
| `bot NAME (guid H)` | `ObjectGuid.Player(H)`, H in hex (`PlayerbotAnomalies.SubjectOf`, `PlayerbotGroupAnomalies.ReportNamed`) |
| `player NAME (guid D)` | `ObjectGuid.Player(D)`, D in decimal (`ServerAnomalies.SubjectOf`, `MapCombat.SubjectOf`, the falling, under-map, flag, instance and loot observers); `SpellSystem.FormatUnitSubject` writes the player counter, which equals the decimal low part |

Reporter forms with no position: `creature E (guid C)` and `creature entry E (guid C)` (`SpellSystem`, `ShipTransport`; they name an entry
and a counter, not a spawn), `unit ...`, `spell N`, `instance ...` and every free-text subject. The two player forms differ only in
the prefix, so a new reporter must write `bot` with a hex guid or `player` with a decimal one.

### The ring

`MapFeedAnomalyRing` holds the last 4,096 notices this world reported. It subscribes to the process-wide `AnomalyLedger` while
the feed listens, copies only immutable values into a preallocated slot under a short lock (it never touches a world object), and is
disposed and cleared when the feed stops (an `anomalies` query then answers `bad-args`).

A notice is kept when its owner is this world's identity token. A notice with no owner is kept only when
`WorldMapFeedOptions.AnomaliesUnowned` is true (the default, since the daemon runs one world per process); tests that run several
worlds in one process set it false.

## Commands (Atlas)

| Command | Meaning |
|---|---|
| `live.inspect guid [map] [instance]` | One `inspect` query; draws a ring on the entity. |
| `live.anomalies [since] [map] [subsystem] [severity] [limit]` | The anomaly list plus one coloured marker per positioned row (info blue, warning amber, error red). |
| `live.heatmap metric [cellSize] [windowSec] [map]` | Cell counts of `anomalies`, `deaths`, `density` or `combat` drawn as at most 1,300 boxes; the rest merge into "other". |
| `live.highlight` (filters) | Rings the live entities matching all filters (at least one required). |
| `live.record [on] [maxMb] [maxMinutes]` | Starts, stops or shows the recorder. |
| `live.replay`, `live.replay.entities`, `live.replay.diff` | Show, list and compare recorded frames. |
| `perf.report`, `perf.stats` | The UI posts frame numbers; `perf.stats` returns host, feed, recorder and UI numbers. |
| `insight.watch` | Re-runs watchable commands on live frames. |

`live.anomalies` returns the newest `limit` notices (default 256, up to 4,096), not the oldest. The ring answers oldest-first, so when
the first page is full and newer notices exist Atlas asks again for the newest page, and `skipped` says how many sequence numbers above `since` that jump passed over (0 when it did
not jump; some of them may also have left the ring, see `lost`). The `map`, `subsystem` and `severity` filters
run in Atlas, so a filtered call fetches the widest page (4,096) and keeps the newest `limit` matches; `total` and `truncated`
describe what that page held, not the whole history. The result also carries the ring's `lost` and `reset` (see above).

## Recorder

`live.record` keeps live frames in a bounded ring: a byte budget (default 256 MB, at most 1,024) and an age bound on the recording
clock (default 15 minutes, at most 120); the oldest frames go first. Changing the size while recording keeps what was recorded.
Stopping keeps the recording for replay. A 16 MB tail of recent frames is always kept (it feeds `deaths` and `combat` heatmaps
when nothing was recorded). The tail is filled on the feed reader thread for every frame (one parse of the frame, bounded by its
size and a 30 second age); it is not lazy because `deaths` needs the frames from before the first call.

What replays: positions (interpolated between recorded frames), hp, kind, the combat flag, the motion generator type (`mt`, interned; the world does not send movement flags) and the display id `d`. What does not
replay: spline paths, routes, targets and animation state. While a replay is on, the UI ignores live frames and shows a "replay"
badge. Playback speed is above 0 and at most 60.

## Watches

`insight.watch action:start` re-runs up to 8 commands per watch on live frames, at most 2 times a second (default 1); up to 8 watches
at once. Only commands that cap their own result can be watched: `live.anomalies`, `live.heatmap`, `live.highlight`. Watched
results are capped at 1,500 rows and 15,000 JSON nodes and say so with `truncated` and `total`. The cap follows
`CommandOrigin.Script`, so an ordinary script run of those commands is capped too; a direct call is not.
`action:list` shows the watches and `action:stop` ends one by `id`, or all.

## Web UI

The insight panel (`js/insight/panel.js`) offers inspect, anomalies, record and replay scrubbing, and highlight. The performance
HUD (`js/insight/perfhud.js`) is a small overlay in the stage, hidden until enabled; it posts its numbers through `perf.report`.
The overlay classes are positioned and z-indexed in `Web/app.css` so they paint over the canvases.
