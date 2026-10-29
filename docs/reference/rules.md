# Rule engine vocabulary

Generated from the rule primitives and hosts of the Game assembly (`RuleVocabulary.Default`). Regenerate with `ARCANECORE_UPDATE_DOCS=1 dotnet test tests/ArcaneCore.World.Tests --filter Docs`. The design is [the rule engine design](../superpowers/specs/2026-10-28-rule-engine-design.md); `.rules vocab` prints the same list in game.

## Hosts

| `host` | Name | Registered | Owner kinds | Capabilities |
| ---: | --- | --- | --- | --- |
| 1 | bot | no | - | - |
| 2 | creature | yes | 0 Global, 1 Entry, 2 Guid | Speak, Cast |
| 3 | gameobject | no | - | - |
| 4 | areatrigger | no | - | - |
| 5 | world_event | yes | 0 Global, 3 GameEvent | Speak |
| 6 | map | no | - | - |
| 7 | economy | no | - | - |

## Owner kinds (`owner_kind`)

| Id | Kind |
| ---: | --- |
| 0 | Global (owner_id 0: every subject of the host) |
| 1 | Entry |
| 2 | Guid |
| 3 | GameEvent |
| 4 | Profile |
| 5 | AreaTrigger |
| 6 | Map |

## Rule flags (`rule.flags`)

| Bit | Flag |
| ---: | --- |
| 0x01 | Repeatable |
| 0x02 | CombatOnly |
| 0x04 | OutOfCombatOnly |
| 0x08 | DebugTrace |
| 0x10 | StableShare |

## Subject flags (`rule_subject_filter.subject_flags`)

| Bit | Flag |
| ---: | --- |
| 0x01 | Player |
| 0x02 | Bot |
| 0x04 | Creature |
| 0x08 | Elite |
| 0x10 | GameObject |

## Evaluation

For each rule of the fired event, lowest `priority` first (ties by `id`), the evaluator checks in this order: the event's parameters, `phase_mask` (bit n = phase n, 0 = every phase), the combat flags, the once-only rule (without Repeatable a rule fires once until the host resets the subject), the cooldown, the subject filter's columns, the filter's `share_pct`, then `chance_pct` and last `condition_id` (an undecidable condition fails closed). Only a rule that passed every check draws a cooldown in `cooldown_min_ms..cooldown_max_ms`.

`phase_mask` is a positive mask: bit n set means the rule fires in phase n (0..31, the phase `set_phase` sets), and 0 means every phase. It is neither EventAI's mask (cmangos `event_inverse_phase_mask`, where a set bit n means the event does **not** fire in phase n) nor SmartAI's (TrinityCore `event_phase_mask`, where bit n means phase n + 1 and phase 0 matches no non-zero mask); the EventAI and SmartAI converters translate them. `cooldown_max_ms` and an action's `delay_ms` are at most 86400000 ms (24 hours); a larger value is a rejected row.

`chance_pct` is a fresh roll per firing. With the StableShare flag it is instead a stable share of subjects, as `share_pct` is: a subject is selected when `bucket(seed, rule_id, guid) < pct`, where bucket is two SplitMix64 finalisations of the three inputs, mod 100 (`RuleShare.Bucket`). A filter uses its `share_seed`; the StableShare chance uses the filter's seed (0 without a filter) xor 0x5EED5EED, so the two shares are independent. The same subjects stay selected across ticks and restarts; a new seed reshuffles them.

Every action names a target. An action's `delay_ms` counts from the previous action of its sequence; the rest of the sequence then waits in one of the subject's 4 pending slots. Lists nest at most 8 deep (checked when they run: a deeper start fails and is counted), and a list that starts itself again with no delay in between is rejected at load.

Rule and rule_text ids below 1000000 are shipped content; server-authored rows use 1000000 up to 1073741823. From 1073741824 (2^30) up the ids belong to the converters, one sub-band of 67108864 (2^26) ids each: EventAI at 1073741824, DB scripts at 1140850688 (reserved), SmartAI at 1207959552 (reserved). A converter rewrites only its own sub-band, and a row at or above 1275068416 is rejected at load. `playerbot_goal.goal_id` is a `PlayerbotGoalKind` value, or 1000 and up for a custom goal.

## Parameter kinds

| Kind | Checked at load |
| --- | --- |
| Int | any value |
| Bool | 0 or 1 |
| Percent | 0..100 |
| Milliseconds | 0 or more |
| Phase | 0..31 |
| SpellId | above 0, and a known spell when the host supplies the spell store |
| CreatureEntry | above 0, and a known creature entry when the host supplies the templates |
| TextId | a loaded rule_text id |
| ConditionId | a loaded conditions row |
| RuleListId | a rule_list id that loads for the rule's host |
| GoalId | a loaded playerbot_goal id |
| NonNegative | 0 or more |

## events

| Id | Name | Host | Parameters | Description |
| ---: | --- | --- | --- | --- |
| 1 | `creature.timer_in_combat` | creature | 1 `initial_min_ms` (Int): Initial delay minimum in ms.<br>2 `initial_max_ms` (Int): Initial delay maximum in ms (below the minimum: never fires). | While in combat, after an initial delay and then every repeat interval (the rule's cooldown range); the initial delay starts when combat starts. |
| 2 | `creature.timer_out_of_combat` | creature | 1 `initial_min_ms` (Int): Initial delay minimum in ms.<br>2 `initial_max_ms` (Int): Initial delay maximum in ms. | While out of combat and not evading, after an initial delay (armed at spawn and on reset) and then every repeat interval. |
| 3 | `creature.timer` | creature | 1 `initial_min_ms` (Int): Initial delay minimum in ms.<br>2 `initial_max_ms` (Int): Initial delay maximum in ms. | In and out of combat, after an initial delay (armed at spawn only) and then every repeat interval. |
| 4 | `creature.health_pct` | creature | 1 `max_pct` (Int): Upper bound, percent.<br>2 `min_pct` (Int): Lower bound, percent.<br>3 `allow_out_of_combat` (Bool): 1: also out of combat. | The creature's health percent is within [min, max]; in combat unless allow_out_of_combat is set. |
| 5 | `creature.mana_pct` | creature | 1 `max_pct` (Int): <br>2 `min_pct` (Int):  | In combat, the creature's mana percent is within [min, max]. |
| 6 | `creature.energy_pct` | creature | 1 `max_pct` (Int): <br>2 `min_pct` (Int):  | In combat, the creature's energy percent is within [min, max]. |
| 7 | `creature.target_health_pct` | creature | 1 `max_pct` (Int): <br>2 `min_pct` (Int):  | In combat, the victim's health percent is within [min, max]. |
| 8 | `creature.target_mana_pct` | creature | 1 `max_pct` (Int): <br>2 `min_pct` (Int):  | In combat, the victim's mana percent is within [min, max] (the victim needs mana). |
| 9 | `creature.target_in_range` | creature | 1 `min_yards` (Int): <br>2 `max_yards` (Int):  | In combat, the victim is inside [min, max] yards measured between the bounding radii (min only when above 0). |
| 10 | `creature.target_casting` | creature | - | In combat, the victim is casting a non-melee spell. |
| 11 | `creature.facing_target` | creature | 1 `front` (Int): 0 behind the victim, 1 in front of it. | In combat, the creature is within 5 yards of its victim and behind it (front 0) or in front of it (front 1). |
| 12 | `creature.target_unreachable` | creature | - | The creature chases a victim that its movement reports unreachable; considered at every batch. |
| 13 | `creature.has_aura` | creature | 1 `spell_id` (Int): <br>2 `stacks` (Int):  | The creature has the aura with at least the given stacks. |
| 14 | `creature.target_has_aura` | creature | 1 `spell_id` (Int): <br>2 `stacks` (Int):  | In combat, the victim has the aura with at least the given stacks. |
| 15 | `creature.missing_aura` | creature | 1 `spell_id` (Int): <br>2 `stacks` (Int):  | The creature has fewer than the given stacks of the aura (none counts). |
| 16 | `creature.target_missing_aura` | creature | 1 `spell_id` (Int): <br>2 `stacks` (Int):  | In combat, the victim has fewer than the given stacks of the aura. |
| 17 | `creature.friendly_health` | creature | 1 `deficit` (Int): Health missing, points or percent.<br>2 `radius_yards` (Int): <br>3 `is_percent` (Bool): 1: the deficit is a percent. | In combat, the friendly creature in radius that misses the most health (more than the deficit) becomes the event target. |
| 18 | `creature.friendly_crowd_controlled` | creature | 1 `radius_yards` (Int):  | In combat, a friendly creature in radius that is stunned, confused, fleeing or rooted becomes the event target. |
| 19 | `creature.friendly_missing_buff` | creature | 1 `spell_id` (Int): <br>2 `radius_yards` (Int): <br>3 `flags` (Int): 0 only in combat, 1 in and out of combat, 2 only out of combat. | A friendly creature in radius without the aura becomes the event target (flags: 0 in combat, 1 in or out, 2 out of combat only). |
| 20 | `creature.select_threat_target` | creature | 1 `min_yards` (Int): <br>2 `max_yards` (Int):  | A random unit of the threat list between min and max yards becomes the event target. |
| 21 | `creature.aggro` | creature | - | The creature enters combat (armed and considered at once; a reset leaves it alone). |
| 22 | `creature.kill` | creature | 1 `player_only` (Int): 1: only a killed player counts. | The creature killed a unit (only a player with player_only). |
| 23 | `creature.death` | creature | 1 `condition_id` (NonNegative): conditions.condition_entry; 0 none. An unknown row fails closed. | The creature died; with a condition, only when the conditions row holds for the player controlling the killer (none without a killer). |
| 24 | `creature.evade` | creature | - | The creature starts evading. |
| 25 | `creature.spell_hit` | creature | 1 `spell_id` (Int): 0: any spell.<br>2 `school_mask` (Int):  | A spell hit the creature: spell id 0 or equal, and the spell's school bit in the mask. |
| 26 | `creature.spawned` | creature | 1 `condition` (Int): 0 always, 1 on map value, 2 in zone or area value.<br>2 `value` (Int): The map, zone or area id. | The creature (re)spawned and the condition holds: 0 always, 1 map = value, 2 zone or area = value. |
| 27 | `creature.reached_home` | creature | - | The creature reached home after evading. |
| 28 | `creature.receive_emote` | creature | 1 `text_emote` (Int): EmotesText.dbc id.<br>2 `condition_id` (NonNegative): conditions.condition_entry; 0 none. | A player aimed the text emote at the creature (the player is the invoker); with a condition, only when it holds for that player. |
| 29 | `creature.ooc_line_of_sight` | creature | 1 `no_hostile` (Int): 0: a hostile unit triggers; otherwise a unit that is not hostile.<br>2 `range_yards` (Int): <br>3 `player_only` (Int): Non-zero: only a player triggers.<br>4 `condition_id` (NonNegative): conditions.condition_entry for the unit's player; 0 none. | While the creature has no victim, a unit moves within range and in line of sight (hostile, or not hostile with no_hostile). |
| 30 | `creature.summoned` | creature | 1 `entry` (Int): Creature entry. | A creature of the entry that this creature summoned entered the world. |
| 31 | `creature.summoned_died` | creature | 1 `entry` (Int): Creature entry. | A creature of the entry that this creature summoned died. |
| 32 | `creature.summoned_despawned` | creature | 1 `entry` (Int): Creature entry. | A creature of the entry that this creature summoned left the world. |
| 33 | `creature.receive_ai_event` | creature | 1 `ai_event_type` (Int): <br>2 `sender_entry` (Int): 0: any sender. | An AI event of the type reached the creature, from a sender of the entry (0: any sender); the sender is the event_sender target. |
| 34 | `creature.spell_hit_target` | creature | 1 `spell_id` (Int): 0: any spell.<br>2 `school_mask` (Int):  | A spell the creature cast landed on a unit (the invoker): spell id 0 or equal, and the school bit in the mask. |
| 1 | `world_event.started` | world_event | 1 `mode` (Int): 0 every start, 1 only a fresh start, 2 only a resume after a restart. | The game event started (a restart that only resumes an event that was running counts as a start of mode 0 and 2). |
| 2 | `world_event.stopped` | world_event | - | The game event stopped. |
| 3 | `world_event.update` | world_event | - | Fires on every world tick while the game event runs; give the rule the repeatable flag and a cooldown to run it on a period. |

## actions

| Id | Name | Host | Parameters | Description |
| ---: | --- | --- | --- | --- |
| 1 | `set_phase` | shared | 1 `phase` (Phase): The new phase, 0..31. | Set the subject's rule phase; rules whose phase_mask lacks it stop firing. |
| 2 | `start_rule_list` | shared | 1 `list_id` (RuleListId): rule_list.id. | Start a rule list for the subject: its actions up to the first delay run now, the rest later; a list already running restarts. |
| 3 | `say` | shared | 1 `text_id` (TextId): rule_text.id. | The subject says a rule_text (its type picks say, yell, emote or whisper) to the target. |
| 4 | `cast` | shared | 1 `spell_id` (SpellId): The spell.<br>2 `cast_flags` (NonNegative): Host-defined cast flags (0 = a normal cast). | The subject casts a spell on the target. |
| 100 | `creature.row` | creature | 1 `flags` (NonNegative): CreatureRuleIds.RowFlags: 1 repeatable, 2 random action, 4 combat action, 8 ranged only, 16 melee only, 32 one shot.<br>2 `action_count` (NonNegative): How many actions follow (the choices of a random-action row). | Row options of a converted EventAI row (first action): repeatable, random action, combat action, ranged/melee only, one shot. |
| 101 | `creature.say` | creature | 1 `text_id` (TextId): rule_text.id.<br>2 `text_id_2` (NonNegative): Second choice (0 none).<br>3 `text_id_3` (NonNegative): Third choice (0 none). | Say a rule_text; with a second (and third) text one is chosen at random. Pair with creature.text_audience for the EventAI text target. |
| 102 | `creature.set_faction` | creature | 1 `faction_template` (NonNegative):  | A faction template becomes the creature's faction until it respawns; 0 gives the template's faction back. |
| 103 | `creature.morph` | creature | 1 `entry` (NonNegative): Creature entry whose model to take (0 none).<br>2 `model_id` (NonNegative): Display id (used when entry is 0). | Take a model of the creature template (entry), else the model id; both 0 gives the native model back. |
| 104 | `creature.play_sound` | creature | 1 `sound_id` (NonNegative):  | Play a sound to the players that see the creature. |
| 105 | `creature.emote` | creature | 1 `emote_id` (NonNegative):  | Play an emote (a state emote is kept as the emote state). |
| 106 | `creature.random_sound` | creature | 1 `sound_1` (Int): <br>2 `sound_2` (Int): <br>3 `sound_3` (Int):  | Play one of three sounds by the firing's random value; -1 plays nothing. |
| 107 | `creature.random_emote` | creature | 1 `emote_1` (Int): <br>2 `emote_2` (Int): <br>3 `emote_3` (Int):  | Play one of three emotes by the firing's random value; -1 plays nothing. |
| 108 | `creature.cast` | creature | 1 `spell_id` (SpellId): <br>2 `cast_flags` (NonNegative): 1 interrupt the previous cast, 2 triggered, 0x20 only if the target lacks the aura. | Cast a spell on the target; fails when the creature is casting (unless triggered or interrupting), the target has the aura (aura-not-present) or the cast is refused. |
| 109 | `creature.summon` | creature | 1 `entry` (CreatureEntry): <br>2 `despawn_ms` (NonNegative): Lifetime in ms out of combat (0: as soon as it is out of combat). | Summon a creature at the creature's position that attacks the target (unless the target is the creature itself). |
| 110 | `creature.threat_single` | creature | 1 `threat` (Int): <br>2 `is_direct` (Bool): 1: add the value; 0: a percent change. | Add threat to the target (direct), or change its entry by a percent (below -100 removes it). |
| 111 | `creature.threat_all_pct` | creature | 1 `percent` (Int):  | Change every entry of the threat list by a percent (below -100 removes it). |
| 112 | `creature.quest_event` | creature | 1 `quest_id` (NonNegative): <br>2 `reward_group` (Bool):  | A player target completes the quest's exploration or event objective (with the reward group, its group near the creature too). |
| 113 | `creature.set_unit_field` | creature | 1 `field` (Int): Update field index (UNIT range).<br>2 `value` (Int):  | Set a unit update field of the target. |
| 114 | `creature.add_unit_flags` | creature | 1 `flags` (Int):  | Add flags to UNIT_FIELD_FLAGS of the target. |
| 115 | `creature.remove_unit_flags` | creature | 1 `flags` (Int):  | Remove flags from UNIT_FIELD_FLAGS of the target. |
| 116 | `creature.auto_attack` | creature | 1 `enabled` (Bool):  | Allow (1) or stop (0) the melee swing. |
| 117 | `creature.combat_movement` | creature | 1 `enabled` (Bool):  | Allow (1) or stop (0) running after the victim; no change, or a cast in progress, fails the action. |
| 118 | `creature.inc_phase` | creature | 1 `delta` (Int):  | Add a value to the phase (negative decrements); the result is kept within 0..31. |
| 119 | `creature.evade_now` | creature | 1 `combat_only` (Bool):  | The creature evades; with combat_only, combat just stops. |
| 120 | `creature.flee_for_assist` | creature | - | Flee to get help; fails without a victim to flee from. |
| 121 | `creature.remove_auras` | creature | 1 `spell_id` (NonNegative):  | The target loses the auras of the spell. |
| 122 | `creature.ranged_movement` | creature | 1 `distance` (Int): <br>2 `angle` (Int):  | Chase the victim at this distance from now on (the angle is not modelled). |
| 123 | `creature.random_phase` | creature | 1 `phase_1` (Int): <br>2 `phase_2` (Int): <br>3 `phase_3` (Int):  | The phase becomes one of three by the firing's random value. |
| 124 | `creature.random_phase_range` | creature | 1 `phase_min` (Int): <br>2 `phase_max` (Int):  | The phase becomes min + random % (max - min + 1); a maximum not above the minimum changes nothing. |
| 125 | `creature.summon_at` | creature | 1 `entry` (CreatureEntry): <br>2 `despawn_ms` (NonNegative): Lifetime in ms.<br>3 `x_bits` (Int): X as the bits of a single-precision float (BitConverter.SingleToInt32Bits).<br>4 `y_bits` (Int): Y as float bits.<br>5 `z_bits` (Int): Z as float bits.<br>6 `orientation_bits` (Int): Orientation as float bits. | Summon a creature at a position that attacks the target (unless the target is the creature itself); the position is in param3-6. |
| 126 | `creature.kill_credit` | creature | 1 `entry` (NonNegative):  | Kill credit for the entry goes to the player (and group) that tapped the creature, else to the target's player. |
| 127 | `creature.set_instance_data` | creature | 1 `field` (Int): <br>2 `data` (Int):  | The instance script of the creature's map stores data under a field; fails without an instance script. |
| 128 | `creature.set_instance_data64` | creature | 1 `field` (Int):  | The instance script stores the target's guid under a field; fails without a target or an instance script. |
| 129 | `creature.update_template` | creature | 1 `entry` (NonNegative):  | The creature becomes another template (health percent kept) until it respawns; its own entry fails. |
| 130 | `creature.die` | creature | - | The creature dies; fails on a dead creature. |
| 131 | `creature.zone_combat_pulse` | creature | - | In a dungeon or raid every living player of the map enters the fight; a creature with no victim attacks the closest unit of its threat list. |
| 132 | `creature.call_for_help` | creature | 1 `radius_yards` (Int):  | Call nearby creatures to help within a radius. |
| 133 | `creature.set_sheath` | creature | 1 `sheath` (Int):  | Set the weapon sheath (0 none, 1 melee, 2 ranged). |
| 134 | `creature.despawn` | creature | 1 `delay_ms` (NonNegative):  | The creature dies without a kill and its corpse goes after the delay (0 at once). |
| 135 | `creature.set_death_prevention` | creature | 1 `enabled` (Bool):  | While on, damage never takes the creature below 1 health. |
| 136 | `creature.mount` | creature | 1 `entry` (NonNegative): Creature entry whose model to mount (0 none).<br>2 `model_id` (NonNegative): Display id (used when entry is 0). | Mount the model of a creature template (entry) or a display id; both 0 dismounts. |
| 137 | `creature.send_ai_event` | creature | 1 `event_type` (NonNegative): <br>2 `radius_yards` (Int):  | Send an AI event to the creatures around, with the target as its invoker; fails when the target does not resolve. |
| 138 | `creature.set_stand_state` | creature | 1 `stand_state` (Int):  | Set the stand state. |
| 139 | `creature.change_movement` | creature | 1 `movement_type` (Int): <br>2 `wander_or_path` (Int): <br>3 `flags` (Int):  | Change the movement: idle, a wander around where the creature stands, or a waypoint path; unsupported types fail. |
| 140 | `creature.set_react_state` | creature | 1 `react_state` (Int):  | Set the react state (0 passive, 1 defensive, 2 aggressive); another value fails. |
| 141 | `creature.pause_waypoints` | creature | 1 `pause` (Bool):  | Pause (1) or resume (0) the waypoint movement. |
| 142 | `creature.start_relay_script` | creature | 1 `relay_id` (Int): dbscripts_on_relay id; negative: a relay template. | Start a relay script with the target as its source and the creature as its target; a negative id is a relay template. Replaced by start_rule_list when the relays are converted. |
| 143 | `creature.attack_start` | creature | - | The creature attacks the target; a target that does not resolve or is dead fails. |
| 144 | `creature.despawn_guardians` | creature | 1 `entry` (NonNegative):  | The creature's guardians of the entry go (0: every guardian). |
| 145 | `creature.set_ranged_mode` | creature | 1 `mode_type` (Int): <br>2 `chase_distance` (Int):  | Enter or leave ranged (caster) mode: type 0 off, 3 no-melee; the chase distance applies. |
| 146 | `creature.set_walk` | creature | 1 `walk_type` (Int):  | 0 run and 1 walk set how scripted moves go; 2 and 3 (chase speeds) change nothing here. |
| 147 | `creature.set_facing` | creature | 1 `reset` (Bool):  | Face the target; with reset, face the reset orientation (the target is then not needed). |
| 148 | `creature.set_immobilized` | creature | 1 `apply` (Bool): <br>2 `combat_only` (Bool):  | Root or free the creature; a combat-only root ends at the next reset. |
| 149 | `creature.set_immunity_set` | creature | 1 `set_id` (NonNegative):  | Switch the creature to a creature_immunities set of its entry. |
| 150 | `creature.set_follow_movement` | creature | 1 `state` (Int):  | 0 holds a follow movement still, 1 lets it follow again. |
| 100 | `world_event.start_event` | world_event | 1 `event_id` (Int): game_event.entry. | Start another game event (GameEventService.StartEvent). It does not change the event's schedule, so use it on serverside events (schedule_type 0); a scheduled event is stopped again by its own window. |
| 101 | `world_event.stop_event` | world_event | 1 `event_id` (Int): game_event.entry. | Stop a game event (GameEventService.StopEvent). It does not change the event's schedule, so use it on serverside events (schedule_type 0); a scheduled event inside its window is started again by the schedule. |

## targets

| Id | Name | Host | Parameters | Description |
| ---: | --- | --- | --- | --- |
| 1 | `self` | shared | - | The subject the rule runs for. |
| 2 | `invoker` | shared | - | Whoever caused the event (the attacker, the user of an object); none when the event has no invoker. |
| 10 | `creature.self` | creature | 1 `optional` (Bool): 1: an unresolved target becomes no unit and the action still runs. | The creature itself. |
| 11 | `creature.victim` | creature | 1 `optional` (Bool): 1: an unresolved target becomes no unit and the action still runs. | The creature's current victim. |
| 12 | `creature.second_on_threat` | creature | 1 `optional` (Bool): 1: an unresolved target becomes no unit and the action still runs. | The unit second on the threat list. |
| 13 | `creature.last_on_threat` | creature | 1 `optional` (Bool): 1: an unresolved target becomes no unit and the action still runs. | The unit last on the threat list. |
| 14 | `creature.random_on_threat` | creature | 1 `optional` (Bool): 1: an unresolved target becomes no unit and the action still runs. | A random unit of the threat list. |
| 15 | `creature.random_not_top` | creature | 1 `optional` (Bool): 1: an unresolved target becomes no unit and the action still runs. | A random unit of the threat list other than the top one. |
| 16 | `creature.invoker` | creature | 1 `optional` (Bool): 1: an unresolved target becomes no unit and the action still runs. | Whoever caused the event. |
| 17 | `creature.invoker_owner` | creature | 1 `optional` (Bool): 1: an unresolved target becomes no unit and the action still runs. | The beneficiary of the event's invoker (the invoker itself while there are no owners). |
| 18 | `creature.random_player` | creature | 1 `optional` (Bool): 1: an unresolved target becomes no unit and the action still runs. | A random player of the threat list. |
| 19 | `creature.random_not_top_player` | creature | 1 `optional` (Bool): 1: an unresolved target becomes no unit and the action still runs. | A random player of the threat list other than the top entry. |
| 20 | `creature.event_sender` | creature | 1 `optional` (Bool): 1: an unresolved target becomes no unit and the action still runs. | The unit that sent the AI event. |
| 21 | `creature.spawner` | creature | 1 `optional` (Bool): 1: an unresolved target becomes no unit and the action still runs. | The creature or unit that summoned this one. |
| 22 | `creature.event_target` | creature | 1 `optional` (Bool): 1: an unresolved target becomes no unit and the action still runs. | The target the event filled in (a friend that needs help, a unit picked from the threat list). |
| 23 | `creature.default_target` | creature | - | No unit target: a spell uses its own default target. |
| 24 | `creature.text_audience` | creature | - | Who a creature text goes to: the invoker when it is a player, else the victim when the event has no invoker. |
