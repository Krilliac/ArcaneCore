# EventAI to rule conversion

How the EventAI executor's rows (`creature_ai_scripts`, `creature_ai_texts`, `broadcast_text`) become rows of the shared rule engine's
`creature` host (`rule`, `rule_action`, `rule_text`). The converter is `EventAiRuleConverter` in the Data assembly; the creature
vocabulary is under `ArcaneCore.Game.Rules.Creatures` and its ids are `CreatureRuleIds` in the Kernel. The rule engine itself is described in
[the generated reference](rules.md) and the design in
[the rule engine design](../superpowers/specs/2026-10-28-rule-engine-design.md).

Converted rows are content of the operator's database. They are written by the importer command and are never committed to the repository.
The old executor stays in place: this change adds the host, the converter and the parity harness, and does not switch any creature over.

## Running the converter

```
arcane-content-importer convert-rules --database <file> [--dry-run] [--report <file>]
```

It reads the imported `creature_ai_scripts`, `creature_ai_texts`, `broadcast_text` and `creature_ai_summons` rows, deletes the rows of its own
id sub-band in `rule`, `rule_action` and `rule_text` (2^30 up to 2^30 + 2^26 - 1) and writes the converted ones, so running it twice gives the same database. Every row it does
not convert is printed with its `creature_ai_scripts.id` and the reason (`--report` writes the whole list to a file outside the repository). A database
without `creature_ai_scripts` rows is refused with exit code 3 and nothing is written; `--provider` and `--connection-string` select a MariaDB or
PostgreSQL world instead of a SQLite file.

## Id scheme

All ids are a pure function of the source id, so a re-run writes the same ids and a database that was converted can be compared with a new run.

| Table | Id | Range |
|---|---|---|
| `rule.id` | `1073741824 + creature_ai_scripts.id` (`2^30` plus the source id) | `2^30` up to `2^30 + 2^26 - 1` |
| `rule_action.rule_id` | the rule's id | same |
| `rule_text.id` for a positive text id (a `broadcast_text` row) | `2^30 +` the broadcast text id | `2^30 + 1` to `2^30 + 499999` |
| `rule_text.id` for a negative text id `-N` (a `creature_ai_texts` row) | `2^30 + 500000 + N` | `2^30 + 500001` to `2^30 + 999999` |

The id plan (`RuleIdBands`, spec section 4): shipped rows are below 1,000,000, server-authored rows are 1,000,000 up to `2^30 - 1`, and converted rows are `2^30` and up, one sub-band of `2^26` (67,108,864) ids per converter. The EventAI converter owns `2^30` up to `2^30 + 2^26 - 1`; `2^30 + 2^26` is reserved for the DB-script converter and `2^30 + 2 * 2^26` for SmartAI. The importer replaces exactly its own sub-band and never reads, changes or removes a row outside it. The catalog rejects a `rule` or `rule_text` row at `2^30 + 3 * 2^26` or above (for example 1,500,000,000) at load; it cannot tell a converted row from an authored one inside a named sub-band, so authored rows must stay below `2^30`. A source id of `2^26` or more is reported as not convertible (EventAI ids are the creature entry times 100 plus an index, so entries up to 671,087 fit). A broadcast text id of 500000 or more, or a negative text id of `-500000` or less, cannot be represented and the row is reported.

## Row layout

One source row becomes one `rule` and its `rule_action` rows.

| Source | Rule |
|---|---|
| `creature_id` (no guid) | `owner_kind` 1 (entry), `owner_id` = the entry, `priority` 0 |
| `creature_guid` | `owner_kind` 2 (guid), `owner_id` = the spawn guid, `priority` 1 (guid rows run after entry rows, as before) |
| event type and parameters | `event_type` and `event_param1..4` (see Events) |
| repeat minimum and maximum | `cooldown_min_ms` and `cooldown_max_ms` |
| chance | `chance_pct` |
| inverse phase mask | `phase_mask` (see Phase masks) |
| event flags | the first action, `creature.row(flags, action_count)` (see Row options) |
| actions 1 to 3 | `rule_action` seq 1 to 3 (empty actions are left out and the others are numbered without a gap) |

`rule.flags` always has the Repeatable bit: the EventAI repeat rules (which event types repeat, the `repeatable` event flag, a repeat
maximum below the minimum) are kept by the creature host, not by the engine's fired-once check.

### Phase masks

EventAI stores an inverse mask `I`: the row is hidden in every phase whose bit is set. The rule engine stores the allowed phases.

* `I = 0` stays `phase_mask = 0` (every phase).
* `I = 0xFFFFFFFF` hides the row in all 32 phases, so it can never run: the row is written **disabled** and reported.
* Anything else becomes `phase_mask = ~I` as 32 bits.

### Timers and caps

Timers are in milliseconds. The engine caps `cooldown_*_ms` and `delay_ms` at 24 hours (86,400,000 ms). The converter checks every timer
against that cap and **reports** a row that is over it (an initial timer of a timer event, the repeat range, a summon lifetime); it never clamps.
A negative repeat is reported. A repeat maximum below the repeat minimum is the EventAI way to say "once": the rule gets cooldown 0 and the one-shot row option.

### Row options

`creature.row(flags, action_count)` is the first action of every converted row (seq 0). It carries the EventAI options and draws the firing's
random value at the place the old executor draws it, so a seeded run draws in the same order.

| Flag | Value | EventAI flag |
|---|---|---|
| Repeatable | 1 | `0x01` repeatable |
| RandomAction | 2 | `0x20` random action: one of the actions runs |
| CombatAction | 4 | `0x400` combat action: a failed first action keeps the timer and skips the others |
| RangedModeOnly | 8 | `0x100` |
| MeleeModeOnly | 16 | `0x200` |
| OneShot | 32 | repeat maximum below the minimum |

### Rows written disabled

A row the old executor never runs is written with `enabled = 0` and listed in the report so that it stays visible: chance 0, the debug-only
flag (`0x80`) and the all-32-bits inverse mask. A chance above 100 is written as 100. A chance above 100 and a SET_PHASE outside 0 to 31 are written clamped (as the old executor reads them) and listed as informational `Adjusted` findings; the row still counts as converted.

The debug-only flag is a gate in the old executor (`Creatures:EventAi:DebugOnlyEvents`, off by default), so a debug-only row is written disabled: with `DebugOnlyEvents=true` the old executor runs it and the converted rule does not, and parity differs for those rows.

The repeat range of an EventAI row is carried in the `cooldown_min_ms` and `cooldown_max_ms` columns. That is the EventAI meaning (a timer re-armed after the row ran); the spec cooldown (a minimum gap between firings) does not apply to the creature host's non-timer events.

### Rows that are not converted

Reported by id with a reason, never written: an event or action type the executor does not support, an action target type it does not support
(13 and 14), a `spawned` condition other than 0 to 2, a text id that does not exist or is empty, a `TEXT_NEW` action with a text template
(random template texts arrive with the DB scripts lane), a combat-action row with an empty first action, a summon position that does not exist,
a parameter outside what the primitive accepts (a negative spell id, for example), a timer over the cap, a source id of `2^26` or more. One unconvertible action drops the whole row: no part of it is written.

## Events

EventAI event parameters (0-based `P0..P5`) become the rule's `event_param1..4`; the repeat range moves to the cooldown columns. "Never" in the
repeat column means the event does not repeat.

| EventAI type | Creature event | Event parameters | Repeat |
|---|---|---|---|
| 0 | `creature.timer_in_combat` | P0 initial min, P1 initial max (ms) | P2, P3 |
| 1 | `creature.timer_out_of_combat` | P0, P1 initial (ms) | P2, P3 |
| 2 | `creature.health_pct` | P0 max %, P1 min %, P4 allow out of combat | P2, P3 |
| 3 | `creature.mana_pct` | P0 max %, P1 min % | P2, P3 |
| 4 | `creature.aggro` | none | never |
| 5 | `creature.kill` | P2 player only | P0, P1 |
| 6 | `creature.death` | P0 condition id | never |
| 7 | `creature.evade` | none | never |
| 8 | `creature.spell_hit` | P0 spell id, P1 school mask | P2, P3 |
| 9 | `creature.target_in_range` | P0 min yards, P1 max yards | P2, P3 |
| 10 | `creature.ooc_line_of_sight` | P0 no hostile, P1 range, P4 player only, P5 condition id | P2, P3 |
| 11 | `creature.spawned` | P0 condition (0 always, 1 map, 2 zone or area), P1 value | never |
| 12 | `creature.target_health_pct` | P0 max %, P1 min % | P2, P3 |
| 13 | `creature.target_casting` | none | P0, P1 |
| 14 | `creature.friendly_health` | P0 deficit, P1 radius, P4 is percent | P2, P3 |
| 15 | `creature.friendly_crowd_controlled` | P1 radius | P2, P3 |
| 16 | `creature.friendly_missing_buff` | P0 spell id, P1 radius, P4 flags | P2, P3 |
| 17 | `creature.summoned` | P0 creature entry | P1, P2 |
| 18 | `creature.target_mana_pct` | P0 max %, P1 min % | P2, P3 |
| 21 | `creature.reached_home` | none | never |
| 22 | `creature.receive_emote` | P0 text emote, P1 condition id | P2, P3 |
| 23 | `creature.has_aura` | P0 spell id, P1 stacks | P2, P3 |
| 24 | `creature.target_has_aura` | P0 spell id, P1 stacks | P2, P3 |
| 25 | `creature.summoned_died` | P0 creature entry | P1, P2 |
| 26 | `creature.summoned_despawned` | P0 creature entry | P1, P2 |
| 27 | `creature.missing_aura` | P0 spell id, P1 stacks | P2, P3 |
| 28 | `creature.target_missing_aura` | P0 spell id, P1 stacks | P2, P3 |
| 29 | `creature.timer` | P0, P1 initial (ms) | P2, P3 |
| 30 | `creature.receive_ai_event` | P0 AI event type, P1 sender entry | P2, P3 |
| 31 | `creature.energy_pct` | P0 max %, P1 min % | P2, P3 |
| 32 | `creature.select_threat_target` | P0 min yards, P1 max yards | P2, P3 |
| 33 | `creature.facing_target` | P0 front (1) or behind (0) | P2, P3 |
| 34 | `creature.spell_hit_target` | P0 spell id, P1 school mask | P2, P3 |
| 36 | `creature.target_unreachable` | none | P2, P3 |

Event types 19, 20, 35 and 37 onward are not supported by the executor and are not converted.

## Actions

Action parameters (`P1..P3`, as in `creature_ai_scripts.actionN_param1..3`) become `param1..` of the rule action. Where EventAI has a target
parameter, it becomes the `target_type` (see Targets) and no longer takes a parameter slot. An action whose old handler ignored a target that did
not resolve gets `target_param1 = 1` ("optional"): the action still runs.
An action with no target in the table below ("none") is written with `creature.self`: every rule action names a target, and a creature target is also
where the row's random-action and combat-action choice is applied to the action.

| EventAI type | Creature action | Parameters | Target |
|---|---|---|---|
| 1 | `creature.say` | the TEXT action: up to three text ids (see Texts) | `creature.text_audience` |
| 2 | `creature.set_faction` | P1 faction template | none |
| 3 | `creature.morph` | P1 entry, P2 display id | none |
| 4 | `creature.play_sound` | P1 sound id | none |
| 5 | `creature.emote` | P1 emote id | none |
| 9 | `creature.random_sound` | P1, P2, P3 sound ids | none |
| 10 | `creature.random_emote` | P1, P2, P3 emote ids | none |
| 11 | `creature.cast` | P1 spell id, P3 cast flags | P2 |
| 12 | `creature.summon` | P1 entry, P3 lifetime ms | P2 (optional) |
| 13 | `creature.threat_single` | P1 threat, P3 is direct | P2 |
| 14 | `creature.threat_all_pct` | P1 percent | none |
| 15 | `creature.quest_event` | P1 quest id, P3 reward group | P2 (optional) |
| 17 | `creature.set_unit_field` | P1 field, P2 value | P3 (optional) |
| 18 | `creature.add_unit_flags` | P1 flags | P2 (optional) |
| 19 | `creature.remove_unit_flags` | P1 flags | P2 (optional) |
| 20 | `creature.auto_attack` | P1 enabled | none |
| 21 | `creature.combat_movement` | P1 enabled | none |
| 22 | `set_phase` | P1 phase, clamped to 0 to 31 (the shared action) | none |
| 23 | `creature.inc_phase` | P1 delta | none |
| 24 | `creature.evade_now` | P1 combat only | none |
| 25 | `creature.flee_for_assist` | none | none |
| 28 | `creature.remove_auras` | P2 spell id | P1 (optional) |
| 29 | `creature.ranged_movement` | P1 distance, P2 angle | none |
| 30 | `creature.random_phase` | P1, P2, P3 phases | none |
| 31 | `creature.random_phase_range` | P1 min, P2 max | none |
| 32 | `creature.summon_at` | P1 entry; the lifetime and the position come from the `creature_ai_summons` row P3, as float bits | P2 (optional) |
| 33 | `creature.kill_credit` | P1 creature entry | P2 (optional) |
| 34 | `creature.set_instance_data` | P1 field, P2 data | none |
| 35 | `creature.set_instance_data64` | P1 field | P2 |
| 36 | `creature.update_template` | P1 entry | none |
| 37 | `creature.die` | none | none |
| 38 | `creature.zone_combat_pulse` | none | none |
| 39 | `creature.call_for_help` | P1 radius | none |
| 40 | `creature.set_sheath` | P1 sheath state | none |
| 41 | `creature.despawn` | P1 delay ms | none |
| 42 | `creature.set_death_prevention` | P1 enabled | none |
| 43 | `creature.mount` | P1 entry, P2 display id | none |
| 45 | `creature.send_ai_event` | P1 event type, P2 radius | P3 |
| 47 | `creature.set_stand_state` | P1 stand state | none |
| 48 | `creature.change_movement` | P1 movement type, P2 wander distance or path, P3 flags | none |
| 50 | `creature.set_react_state` | P1 react state | none |
| 51 | `creature.pause_waypoints` | P1 pause | none |
| 53 | `creature.start_relay_script` | P1 relay id (negative: a relay template) | P2 |
| 54 | `creature.say` | the TEXT_NEW action: P1 text id (a template in P3 is not converted) | P2 |
| 55 | `creature.attack_start` | none | P1 |
| 56 | `creature.despawn_guardians` | P1 entry | none |
| 57 | `creature.set_ranged_mode` | P1 mode, P2 chase distance | none |
| 58 | `creature.set_walk` | P1 walk or run | none |
| 59 | `creature.set_facing` | P2 reset | P1 (none when P2 is set) |
| 61 | `creature.set_immobilized` | P1 apply, P2 combat only | none |
| 63 | `creature.set_immunity_set` | P1 immunity set id | none |
| 64 | `creature.set_follow_movement` | P1 state | none |

The relay action (53) still runs the old relay script catalog; converting relay scripts is a later lane.

### Texts

The TEXT action says one of up to three texts: all three when the second and third are set, two when only the second is, otherwise the first
(a third text without a second is ignored, as before). The converter writes one `rule_text` per referenced text, with the line, chat type,
language, emote and sound of the source, and the broadcast text id. `creature.say` hands the line to the same speech routine as the old action.

## Targets

| EventAI type | Creature target |
|---|---|
| 0 | `creature.self` |
| 1 | `creature.victim` |
| 2 | `creature.second_on_threat` |
| 3 | `creature.last_on_threat` |
| 4 | `creature.random_on_threat` |
| 5 | `creature.random_not_top` |
| 6 | `creature.invoker` |
| 7 | `creature.invoker_owner` |
| 8 | `creature.random_player` |
| 9 | `creature.random_not_top_player` |
| 10 | `creature.event_sender` |
| 11 | `creature.spawner` |
| 12 | `creature.event_target` |
| 15 | `creature.default_target` |

The TEXT action's audience (the invoker when it is a player, else the victim when there is no invoker) is `creature.text_audience`. A target
parameter of 13 or 14 is not supported by the executor and the row is reported.

## Known differences

These are the places where a converted row does not behave exactly like the old executor. The parity harness lists each as an approved
exception with its reason.

* A chance-0 row written disabled no longer draws a random number each tick from the creature's random stream (a debug-only row is filtered at load and an all-bits-masked row fails the phase gate before the roll, so neither drew); the stream shifts for the rows around it.
* With `Creatures:EventAi:DebugOnlyEvents=true` the old executor runs debug-only rows and the converted rules do not.
* `creature.summon_at` keeps the summon position as the bits of single-precision floats.
* While a rule is processed its invoker is held by the host's own unit wrapper; a rule list that outlives the invoker is not re-resolved until the
  engine keeps the invoker's guid (tracked with the engine core).
* TEXT_NEW with a text template is not converted.
