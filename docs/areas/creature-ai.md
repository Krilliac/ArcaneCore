# Area: creature AI and movement

Status: implemented on `feat/creature-ai`; the integration notes are in
docs/integration/creature-ai.md.

## Scope

- **MotionMaster** (vmangos `MotionMaster`). It is a stack of movement generators over the
  spawn's default generator (idle, random or waypoint). Only the top generator updates. A
  generator that finishes is popped, and the one beneath resumes. In combat the default
  generator stays interrupted.
- **Generators**:
  - `Chase`: runs to the contact point until melee reach (`MapCombat.CanReachWithMeleeAutoAttack`). It re-checks every 100 ms and re-paths only when the target has moved more than 0.5 yd.
  - `Follow`: holds a distance and angle from the target. It runs while the target runs.
  - `Fleeing`: sets UNIT_FLAG_FLEEING and uses quiet distances of 28/43 yd, pausing 0.5–1 s between legs. It can be timed.
  - `Home`: runs back, faces the home orientation, then fires the reached-home hook.
  - `Point`: moves to one point, then fires movement-inform.
  - `Waypoint`: the `creature_movement` path, with a per-node `Run` flag and wait times. It resumes the current node after an interruption.
  - `Random`: unchanged from the creatures area.
- **Splines.** Multi-point linear splines use SMSG_MONSTER_MOVE: the point count, the
  destination, then N−1 packed offsets from the path midpoint (11/11/10 bits at quarter-yard
  resolution, from gtker wow_messages / vmangos `MoveSplineInitArgs` packing). Facing can be
  none, an angle, a spot or a target. A late observer catches up on the remaining points.
- **AI host** (`CreatureMapSystem.Ai.cs`):
  - **Aggro radius** (vmangos `Creature::GetAttackDistance`): 20 yd, minus the level difference, never below 5, counting at most 25 levels below, times `AggroRate`. The checks are 3D distance plus both bounding radii, at most 3 yd vertical difference, line of sight, hostility, `CanAttack`, a living non-GM player, and a creature that is not civilian and has no NO_AGGRO extra flag.
  - **Attack start**: melee, a zero-threat entry, combat state on both sides, the combat start point, the aggro hook, the assistance call and chase.
  - **Victim selection**: the threat list (110 % / 130 % rule), skipping invalid and leashed targets. The leash is max(`ThreatRadius`, aggro radius) from the combat start, with no leash in instances. If no victim is left, the creature evades.
  - **Evade**: interrupts the cast, `CombatStop`, clears threat, restores full health and mana, calls `OnEvade`, then runs home. A waypoint mover goes back to its combat start point; anything else goes to its spawn point. The creature is immune to new attacks until it is home.
  - **Assistance** (vmangos `CallAssistance`): once per fight, idle creatures of the same faction within 10 yd that can see the caller join after 1.5 s. Helpers do not call more help.
  - **Flee for assistance**: the creature runs to the nearest possible helper (within 30 yd) and calls for help when it arrives. If no helper is found, it flees for 7 s.
- **AI selection** (`CreatureAiFactory`, from `creature_template.AIName`): `NullAI`, `ReactorAI`,
  `PassiveAI` (= reactor), `AggressorAI` and `EventAI`, plus registered C# scripts. An empty
  name gives Reactor for civilians and Aggressor otherwise. Unknown names are reported once
  and get the default.
- **EventAI** (re-implemented from cmangos-classic `doc/EventAI.txt` semantics; no code
  copied):
  - Events: in-combat and out-of-combat timers, health percent, aggro, kill, death, evade, spell
    hit, spawned, reached home.
  - Actions: text, cast (with the interrupt-previous, triggered and aura-not-present flags),
    summon, auto attack, combat movement, set and increment phase, evade, flee for assist, die,
    call for help.
  - Phases use the inverse phase mask. Chance, the repeatable flag and the random-action flag
    are honoured. Rows using anything else are reported once per entry, and only their
    unsupported parts are skipped.
- **Texts** (`creature_ai_texts`): say 25 yd, yell 300 yd, text emote 25 yd, boss emote and
  zone yell map-wide, whisper to the target. `$N` becomes the target name, and the text can
  carry an emote (SMSG_EMOTE).
- **Data**: `CreatureDumpImporter` reads cmangos `creature_ai_scripts` (only when it has
  `action1_type`) and `creature_ai_texts`, which must have negative entries, plus `AIName` and
  the movement `Run` column. A vmangos `creature_ai_events` table is reported and not imported.

## References

vmangos (`MotionMaster`, `TargetedMovementGenerator`, `FleeingMovementGenerator`,
`HomeMovementGenerator`, `Creature::SelectHostileTarget`/`CallAssistance`/`GetAttackDistance`/
`IsOutOfThreatArea`, `CreatureAI`) and cmangos-classic (`CreatureEventAI`, EventAI.txt) were
used for behaviour. gtker wow_messages (MIT) and vmangos `PacketBuilder` were used for the
SMSG_MONSTER_MOVE and SMSG_MESSAGECHAT monster layouts. Everything was re-implemented; no GPL
code was copied.

## Known gaps

- No vmangos generic-script AI (`creature_ai_events`/`creature_ai_scripts` in vmangos format). Only cmangos EventAI rows are imported.
- Hostility comes from the faction template only. Reputation, at-war and forced reactions are not used; that seam is for the reputation area.
- Paths and line of sight come from `map.Collision` (feat/vmap-los). Without installed vmaps or mmaps they are straight and always clear. There is no re-path while a long path is being walked, other than chase's own re-check timer.
- Evade interrupts the cast but does not remove auras. vmangos `RemoveAllAurasOnEvade` is not modelled.
- Creatures do not aggro on other creatures (guards against hostile mobs, pets).
- Flee-for-assist is simplified: there is no "attempts to run away in fear" emote, and help is called once on arrival.
- No guard AI, pet AI, totem AI or formation/linking (`creature_linking`).
- Detection-range auras and stealth do not modify the aggro radius.
- Per-instance map updaters and instance resets belong to `feat/instances`.
