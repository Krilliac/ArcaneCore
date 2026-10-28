# Rule engine vocabulary

Generated from the rule primitives and hosts of the Game assembly (`RuleVocabulary.Default`). Regenerate with `ARCANECORE_UPDATE_DOCS=1 dotnet test tests/ArcaneCore.World.Tests --filter Docs`. The design is [the rule engine design](../superpowers/specs/2026-10-28-rule-engine-design.md); `.rules vocab` prints the same list in game.

## Hosts

No host is registered in this build, so every `rule` row is rejected with "host N is not registered". The host ids are reserved as follows.

| `host` | Name | Registered | Owner kinds | Capabilities |
| ---: | --- | --- | --- | --- |
| 1 | bot | no | - | - |
| 2 | creature | no | - | - |
| 3 | gameobject | no | - | - |
| 4 | areatrigger | no | - | - |
| 5 | world_event | no | - | - |
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

Ids below 1000000 are shipped or converted content; server-authored rows use 1000000 and up. `playerbot_goal.goal_id` is a `PlayerbotGoalKind` value, or 1000 and up for a custom goal.

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

None in this build (each host brings its own events).

## actions

| Id | Name | Host | Parameters | Description |
| ---: | --- | --- | --- | --- |
| 1 | `set_phase` | shared | 1 `phase` (Phase): The new phase, 0..31. | Set the subject's rule phase; rules whose phase_mask lacks it stop firing. |
| 2 | `start_rule_list` | shared | 1 `list_id` (RuleListId): rule_list.id. | Start a rule list for the subject: its actions up to the first delay run now, the rest later; a list already running restarts. |
| 3 | `say` | shared | 1 `text_id` (TextId): rule_text.id. | The subject says a rule_text (its type picks say, yell, emote or whisper) to the target. |
| 4 | `cast` | shared | 1 `spell_id` (SpellId): The spell.<br>2 `cast_flags` (NonNegative): Host-defined cast flags (0 = a normal cast). | The subject casts a spell on the target. |

## targets

| Id | Name | Host | Parameters | Description |
| ---: | --- | --- | --- | --- |
| 1 | `self` | shared | - | The subject the rule runs for. |
| 2 | `invoker` | shared | - | Whoever caused the event (the attacker, the user of an object); none when the event has no invoker. |
