# Playerbots: scenarios on game events

A bot scenario is data. Rows in the world database, keyed by `game_event.entry`, say which bots a game event wants, where they stand
and what they do (guard a post, or go on a siege). `.event start <id>` brings the scenario up and `.event stop <id>` takes it down;
nothing about the scenario is typed by a GM. The shipped content (the Undercity siege) is idempotent SQL that installs on a clean
database ([sql/content/README.md](../../sql/content/README.md)).

Code: `src/ArcaneCore.Playerbots/EventScenarios/**` (the engine, the catalog that validates the rows, the real host, the feature and
the commands), `src/ArcaneCore.Data/World/WorldState/GameEventPlayerbotDataModule.cs` (the tables). The groups, defend posts and siege
it drives are described in [playbots-groups.md](playbots-groups.md); the event service in
[game-events-weather.md](game-events-weather.md).

## Tables (world schema version 54)

All four are operator content: the schema step creates them empty. Every table has `event` (a `game_event.entry`) and `group_id`
(1 to 255, per event) in its key.

| Table | Key | Columns |
|---|---|---|
| `game_event_playerbot_group` | `event`, `group_id` | `name_prefix`, `count`, `team` (0 Alliance, 1 Horde), `class_spec`, `level`, `provision`, `start_map`, `start_x`, `start_y`, `start_z`, `start_o`, `role` (0 none, 1 defend, 2 siege), `comment` |
| `game_event_playerbot_post` | `event`, `group_id`, `post_id` | `map`, `x`, `y`, `z`, `radius` |
| `game_event_playerbot_siege` | `event`, `group_id` | `objective_entry`, `raids` (1 or 2), `min_ready_pct` |
| `game_event_playerbot_waypoint` | `event`, `group_id`, `seq` | `map`, `x`, `y`, `z` |

- `name_prefix` and `count`: the group's bots are the managed bots whose name starts with the prefix (case-insensitive). Missing bots
  are created with a generated name, the prefix plus four letters.
- `class_spec`: `race:class` pairs separated by commas, for example `2:1,5:5`. Created bots take the pairs in turn. Races must be of
  the group's team, classes are those of 1.12 (no death knight).
- `provision`: when true the engine creates the missing bots and boosts them to `level` (the same path as `.character boost`). When
  false it only claims bots that already exist and reports the shortfall as "not provisioned".
- `start_*`: the ring the bots are teleported to when they are further than 60 yards from it (or on another map).
- `post`: a defend post, the same thing `.playerbot defend` sets (the radius is clamped as the command clamps it). A defend group needs at
  least one valid post; a bot takes the posts in turn.
- `siege` and `waypoint`: a siege group needs one siege row and 1 to 64 waypoints, all on the map of `start_map`. `min_ready_pct` 0 means 100.

## Validation

The rows are checked once, against `game_event`, when the scenario feature builds its catalog. A bad row is logged as
`game_event_playerbot: ...` at warning level and left out, together with the group it belongs to; it never takes a good group with it
and never throws. Rejected: an event that is not in `game_event`, a prefix that is not 2 to 8 letters, `count` outside 1 to 500, `team`
not 0/1, `level` above 60, an unknown `role`, a bad `class_spec` (including a race of the other team), a non-finite position, a defend
group without a post, a siege group without a siege row, without waypoints or with waypoints on more than one map or a map other than
`start_map`, `raids` outside 1 to 2, an `objective_entry` of 0, duplicate keys, and post, siege or waypoint rows with no group. The
`.playerbot eventscenario status` output shows what loaded.

## The reconciler

`PlayerbotEventScenarioFeature` is a world feature (`DetachKind.Stateless`, so it hot-reloads with the playerbot pack). It does nothing
unless `World:Playerbots:Enabled` is on and the tables have rows; with no rows it subscribes to nothing. It listens to `GameEventFeature`
through `AddListener`, which returns an `IDisposable`, and the registration goes into the feature's subscription bag, so a detach
removes it and a re-attach leaves exactly one listener. Every 2 seconds of world time (a fixed interval; there is no configuration key
for it) it ticks `EventScenarioEngine`, which holds the desired state (the running events) and works the world towards it:

1. For each group of a running event it drops claims of bots that are gone, claims bots by prefix (never a bot that is held by a GM
   command, belongs to the other team, or is claimed by another group), creates missing bots (2 per group per tick, 4 in flight,
   only when `provision` is on and the bot limit leaves room), starts them (6 in flight), boosts them (4 per tick), places them
   (10 teleports per tick, retried after 15 seconds), and then gives each a defend post or hands the group to the siege.
2. A refused step is shown in the status and retried (30 seconds, siege 4 seconds). A bot that was not yet restored after a restart is
   waited for up to 60 seconds before it is started.
3. A siege group starts one siege when `min_ready_pct` percent of its bots are online, at level and placed (and at least two per
   raid). A siege that has ended (won or stopped) is not started again while the event runs.
4. When the event stops, the engine clears the defend posts it set, stops the siege only if it started that siege (the outcome reads
   `stopped: event stopped`), and forgets its claims. The bots themselves stay where they are and stay managed bots.

Work with nothing to do costs one comparison per tick and allocates nothing (a test pins it).

Scenario defend posts use ids with bit 30 set (`0x40000000 | event << 16 | group << 8 | post`), above the counter of the GM defend
command, so the two never collide and an id is the same after a reload.

### Limits

- The tables are read when the feature attaches (server start or a playerbot hot reload). There is no reload command; after
  installing or editing a content file, reload the playerbot pack or restart.
- A hot reload during a running siege: the feature re-attaches and treats the bots that are held by the raid as unavailable, so the
  siege keeps running with its raids and the group is reported "unavailable" until the siege ends.
- Event ids 900 to 999 are reserved for shipped scenarios; the SQL deletes and recreates the `game_event` row of its own id.

## Events, restarts and `start_time`

The shipped event is a serverside event: `schedule_type` 0, `occurence` equal to `length` (5184000 minutes, about ten years), no
`start_time`/`end_time`. The schedule never starts or stops a serverside event, only `.event start` and `.event stop` do.

- **Restart.** Retail restores nothing for a serverside event: after a server restart the event is off and the scenario with it.
  Set `World:GameEvents:RestoreServersideEvents` to `true` (it needs the CMangos dialect) to have the event service re-apply the
  serverside events recorded in `game_event_status` at start-up, with the resume flag; the scenario feature then sees the event as
  running on its first tick and brings the bots back (bots already placed are recognised by where they stand).
- **Dialect.** `World:GameEvents:Dialect` `Auto` becomes VMangos as soon as any `game_event` row has a `start_time`, and VMangos
  validates events differently (length above 0, patch range). The shipped file therefore leaves `start_time` NULL; keep it that way in
  your own scenario events unless you set the dialect explicitly.
- A scenario can also hang on a scheduled holiday event; then the schedule starts and stops the scenario like any event.

## Commands

`.playerbot eventscenario` (Administrator, read only, no GM position needed):

| Command | Replies |
|---|---|
| `.playerbot eventscenario list` | The events that have a scenario and whether each is running. |
| `.playerbot eventscenario status [#event]` | Per group: bots claimed of wanted, online, at level, placed, posts set (defend) or the siege state (`waiting: n/m ready`, `N running`, `ended: ...`, `refused: ... (retrying)`), bots short (`no room`, `not provisioned`), a refused create, and the refusals of single bots. |

Before the first tick after an attach the commands say that the scenarios are read on the first world tick.

## Tests

`tests/ArcaneCore.Data.Tests/GameEventPlayerbotDataTests.cs` (tables, the shipped content applied to a clean database twice, all three
providers), `tests/ArcaneCore.World.Tests/Playerbots/EventScenarios/` (catalog validation, the engine on a fake server, the feature in a
real world host including a detach and re-attach, the shipped file validated, and defend and siege scenarios on the scenario world),
`GameEventFeatureListenerTests` and `GameEventListenerRemovalTests` (listener removal).
