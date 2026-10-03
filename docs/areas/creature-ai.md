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
  - **Aggro radius** (vmangos `Creature::GetAttackDistance`, Objects/Creature.cpp:2193-2240): the template's detection range (`creature_template.Detection`, 18 yd by default; the earlier hard-coded 20 yd was wrong), minus the level difference (at most 25 levels counted below), never below `min(detection, 5)`, times `AggroRate`; a detection range under 1 means no proximity aggro. The detect-range auras are not applied.
  - **Proximity aggro rules** (`BasicAI::MoveInLineOfSight`, AI/BasicAI.cpp:30-77): the creature is alive, not evading, not stunned, confused or fleeing; the target is a living non-GM player of this map; a creature with a victim only picks up more targets in instanceable maps or with NO_LEASH_EVADE (`EnterCombatWithTarget` then only adds threat); `CanInitiateAttack` (alive, not stunned, not spawning or unselectable, react state aggressive, not temporarily pacified); the vertical distance minus both bounding radii is within 3 yd (flyers exempt); the plain 3D distance, without bounding radii, is strictly inside the radius; `CanAttack`, hostility and line of sight. The civilian column no longer matters here.
  - **React states** (`Creature.ReactState`, vmangos `InitializeReactState`, set at creation and every respawn): totems, triggers (the invisible flag), NO_TARGET and IGNORE_COMBAT creatures are passive (they neither aggro nor fight back), NO_AGGRO makes a creature defensive (fights back, no proximity aggro), the rest are aggressive.
  - **Respawn pacify**: a creature that respawns cannot initiate attacks for 5000 ms (`Creatures:RespawnPacifyMs`, vmangos `SetTempPacified(5000)`, Creature.cpp:877-878); the timer counts down while alive and clears when it enters combat.
  - **How aggro is triggered** (`Creatures:AggroScanMode`, default `Relocation`): a player or creature that moves or joins the map schedules one AI notify after 1000 ms (`Visibility.AIRelocationNotifyDelay`); the notify makes the creatures (for a player) or the players (for a creature) within `MaxCreatureAttackRadius` (40) times the aggro rate run `MoveInLineOfSight` for it (`AiRelocationNotifier`; vmangos Unit.cpp:10082-10160, GridNotifiersImpl.h:57-119). Standing still triggers nothing. `Poll` is the original behaviour: every creature checks every player every tick (development). Differences from vmangos: a plain 2D radius instead of grid cells, no stealth or detection, no creature-versus-creature notify.
  - **SMSG_AI_REACTION**: every time a creature starts attacking a unit it sends one (guid, u32 reaction 2 = hostile, gtker wow_messages `smsg_ai_reaction.wowm`) to the units that see it; the client plays the aggro sound from it (`Creatures:SendAiReaction`).
  - **Attack start**: melee, a zero-threat entry, combat state on both sides, the combat start point, the aggro hook, the assistance call and chase.
  - **Victim selection**: the threat list (110 % / 130 % rule), skipping invalid targets; a target out of the threat area, reached in list order before a victim is found, abandons the selection and the creature evades (vmangos `ThreatContainer::selectNextVictim`, Threat/ThreatManager.cpp:305-312; `ThreatList.SelectVictim` takes the area predicate).
  - **Soft leash** (`Creature::IsOutOfThreatArea`, Objects/Creature.cpp:2796-2815): never with NO_LEASH_EVADE or in an instanceable map. The threat area is a sphere around where the fight began with radius `max(1.5 x aggro radius, ThreatRadius)` (`Creatures:ThreatRadius`, 50 here; the earlier 60 was wrong). The target is out only when neither the creature nor the target is inside it and the leash extension clock is more than `Creatures:LeashExtensionSeconds` (12) whole seconds old. The clock starts at the first check outside the area, is refreshed at the 3 s check while the creature is stunned, confused or fleeing, is shared with creatures that joined through its assistance call, and is cleared when combat stops.
  - **Hard leash** (`creature_template.Leash`, Creature::Update :976-993): every `Creatures:LeashCheckIntervalMs` (3000) of world time a creature in combat whose distance from where the fight began exceeds its template leash range evades instead of running its AI.
  - **Evade**: interrupts the cast, `CombatStop`, clears threat, restores full health and mana, calls `OnEvade`, then runs home. A waypoint mover goes back to its combat start point; anything else goes to its spawn point. The creature is immune to new attacks until it is home.
  - **Assistance** (vmangos `CallAssistance`): once per fight, idle creatures of the same faction within 10 yd that can see the caller join after 1.5 s. Helpers do not call more help.
  - **Flee for assistance**: the creature runs to the nearest possible helper (within 30 yd) and calls for help when it arrives. If no helper is found, it flees for 7 s.
- **AI selection** (`CreatureAiFactory`, from `creature_template.AIName`): `NullAI`, `ReactorAI`,
  `PassiveAI` (= reactor), `AggressorAI` and `EventAI`, plus registered C# scripts. An empty
  name gives Reactor for civilians and Aggressor otherwise. Unknown names are reported once
  and get the default.
- **EventAI** (`CreatureEventAI` + `Creatures/AI/EventAi/`, re-implemented from mangos-classic
  `src/game/AI/EventAI/CreatureEventAI.cpp`; no code copied). `CreatureEventAI` only forwards the AI hooks
  to an `EventAiEngine`; the engine owns the machinery and the event/action types are handler classes
  found by reflection (`EventAiRegistry`; a duplicate type id fails at startup), so a new event or action
  is one new file.
  - **Engine** (cmangos citations): holders per row, entry rows then spawn-guid rows (`InitAI`
    :102-163); timer-driven events evaluated in batches every `Creatures:EventAi:UpdateIntervalMs`
    (500, `EVENT_UPDATE_TIME` CreatureEventAI.h:32) with the strict `<` of `UpdateEventTimers`
    (:1929), so with 100 ms ticks a batch lands every 600 ms; timers do not count down while the inverse
    phase mask hides the event; `CheckEvent` generic gates (:255); `ProcessEvent` (:598): a failed chance
    roll runs `ResetEvent` (re-arms the repeat timer, disables a non-repeating row), RANDOM_ACTION (0x20)
    runs one action, COMBAT_ACTION (0x400) keeps the timer when the first action fails so the row retries
    (the first action must succeed, otherwise the others are skipped); `ResetEvent` (:575); ready lists
    with a nesting depth so an action can trigger events; the lifecycle order of `JustRespawned` (fresh
    holders), `Reset`, `JustReachedHome` (events, then reset), `EnterEvadeMode`, `JustDied` (reset,
    events, phase 0) and `EnterCombat`. The phase only returns to 0 on death. Rows flagged DEBUG_ONLY
    (0x80) are skipped unless `Creatures:EventAi:DebugOnlyEvents`.
  - **Events with a handler**: 0 timer in combat, 1 timer out of combat, 2 health percent (with the
    allow-out-of-combat parameter), 3 mana percent (needs a mana creature in combat), 4 aggro, 5 kill
    (parameters: repeat min, repeat max, player only; the old implementation read the wrong columns), 6
    death, 7 evade, 8 spell hit (spell id and school mask must both match), 9 range (the victim between the
    min and max yards, bounding radii added, Object.cpp:1401-1420), 11 spawned (always, or map id), 12 target
    health, 13 target casting (repeat timers are parameters 1 and 2), 18 target mana, 21 reached home, 23/24
    aura and target aura (at least N stacks), 27/28 missing aura and target missing aura (fewer than N), 29
    generic timer (in and out of combat), 31 energy percent, 33 facing target (within 5 yd, victim's back or
    front half circle), 36 target not reachable. Aura stacks and the victim's casting state come from the
    `IUnitSpellQueries` seam (`SpellSystemUnitSpellQueries` over the spell system, bound by
    `CreatureAiServicesBinder`; without a spell system nobody has auras or casts).
  - **Actions with a handler**: 1 text (the 1/2/3-way choice by `rnd % 3` / `rnd % 2`), 11 cast (aura-not-
    present, triggered and interrupt flags; a creature that is casting only casts again when the spell is
    triggered or interrupts; success is the cast being accepted), 12 summon, 13 threat single (direct add or percent) and 14 threat all percent (docs/areas/threat.md), 20 auto attack, 21 combat
    movement (no change or casting fails), 22 and 23 phases, 24 evade (with the combat-only parameter), 25
    flee for assistance, 37 die, 39 call for help.
  - **Targets**: 0-6, 7 (the invoker; there are no pets), 10, 12 and 15 (no unit). Others fail the action.
  - Event 36 (target not reachable) is checked at every batch but nothing marks a chase unreachable until the no-path
    chase generator exists, so it does not fire in play yet; death-prevented (35) needs the death-prevention action and
    a combat hook and is not implemented.
  - **Not supported, reported once per entry** (`CreatureEventAI.Unsupported`): every other event and action
    type; a death event with a condition id (no conditions system); a spawned event with the zone condition
    (no zone lookup); cast flags beyond the three above, SET_RANGED_MODE and caster mode (ranged mode is
    always off, so RANGED_MODE_ONLY rows never run); the combat-movement melee packet parameter.
  - Summons still despawn on a flat timer (cmangos `TEMPSPAWN_TIMED_OOC_OR_DEAD_DESPAWN` is a later slice).
- **Texts** (`creature_ai_texts`): say 25 yd, yell 300 yd, text emote 25 yd, boss emote and
  zone yell map-wide, whisper to the target. `$N` becomes the target name, and the text can
  carry an emote (SMSG_EMOTE).
- **Data**: `CreatureDumpImporter` reads cmangos `creature_ai_scripts` (only when it has
  `action1_type`), `creature_ai_texts` (negative entries), `broadcast_text`,
  `creature_ai_summons`, `AIName`, the movement `Run` column and the template behaviour columns
  described in "Content model" below. A vmangos `creature_ai_events` table is reported and not
  imported.

## Code layout

The AI host is split by concern so parallel work does not share a file (a pure move: no behaviour
changed, checked by identical test totals and a line-multiset comparison of the old and new sources):

- `CreatureMapSystem` partials: `.cs` (construction, update loop, movement helpers),
  `.Lifecycle.cs` (death, corpse, respawn, grid load/unload), `.Host.cs` (AI plumbing, spells seam,
  hooks), `.Aggro.cs`, `.Combat.cs` (attack start, victim selection, leash), `.Evade.cs`,
  `.Assist.cs`, `.Text.cs`, `.Summons.cs`.
- `Creature` partials: `.cs`, `.Ai.cs`, `.Movement.cs`, `.Addon.cs`. `CreatureOptions` is a partial
  class in its own file.
- `CreatureAiServicesBinder` (World) builds the AI services: the built-in defaults, then every
  settable property of `CreatureAiServices` is bound by reflection from the container, so a new
  seam is a new property and never an edit of the creature feature. A registered
  `ICreatureSpellCaster` now replaces the spell-system adapter.
- `ICreatureMovementGenerator` gained default members `GetResetPosition` (evade runs to the default
  generator's reset position when it supplies one; none do yet) and `IsReachable`
  (`MotionMaster.IsReachable`); vmangos `MovementGenerator.h:61-64`,
  `HomeMovementGenerator.cpp:52`. Both default to the previous behaviour.

## Content model (world schema step `CreatureBehaviourDataModule`)

The world step that follows the first AI step (`CreatureBehaviourDataModule.Version`, 12 after the
2026-10-03 vanilla-wave integration; it was 11 on the lane branch) widens the data the AI reads. It is additive
only (new tables and columns), so a database at the previous version upgrades in place.

- **EventAI rows** (`CreatureAiEvent`): `Flags` is a full `uint32` (new column `EventFlags32`; the
  world-8 byte column stays and keeps the low byte, and is only read when the new column is 0).
  classic-db z2815 has 5,446 rows with flags 1025 and 824 with 1024; the old byte column stored
  255 for both, which also set the random-action bit. `Param5` and `Param6` are kept. A negative
  `creature_id` is a spawn-guid key (`CreatureGuid`, `CreatureAiContent.GetGuidEvents`) instead of
  creature 0: 39 rows. `event_chance` is clamped to 100 with a warning and 0 is reported, as cmangos
  does. The content carries `Dialect` (`EventAiDialect.CMangos`; vmangos rows are still refused).
- **Texts**: `broadcast_text` is imported (`BroadcastTextCatalog`, 11,104 rows in classic-db).
  Every EventAI text action in classic-db carries a positive broadcast id and `creature_ai_texts`
  is empty there, so `CreatureAiContent.FindText` resolves a positive id through the catalog
  (male text, chat type, language, first emote, sound). The existing `Say` path therefore speaks
  those lines. Female text selection, sound and emote playback and the `$N`-style substitutions
  of `DoDisplayText` are the presentation slice's work and are not claimed here.
- **Summons**: `creature_ai_summons` (`CreatureAiSummon`, 38 rows) for the SUMMON_ID action.
- **Template behaviour**: `Detection`, `CallForHelp`, `Pursuit`, `Leash`, `Timeout`, `StaticFlags1/2`
  (cmangos names; vmangos `detection_range`, `call_for_help_range`, `leash_range`, `static_flags1/2`),
  and a dialect tag for `ExtraFlags`. A template without the detection column gets 18 yd
  (`CreatureTemplate.DefaultDetectionRange`). These are stored and exposed; the aggro, leash and
  assist code still uses its constants until the aggro and combat-control slices consume them.
- **ExtraFlags dialects.** The two references give the same bits different meanings (0x01 is
  INSTANCE_BIND in cmangos and NO_LEASH_EVADE in vmangos; 0x20 is RUN_DURING_WANDER versus
  NO_MOVEMENT_PAUSE; 0x40 is unused versus ALWAYS_RUN; 0x10000 is CIVILIAN versus NO_ASSIST).
  The importer records the dialect per template (detected from the column name, `ExtraFlags` versus
  `flags_extra`, or forced with `CreatureDumpImporter.ExtraFlagsDialect`) and
  `CreatureTemplate.Behaviour` decodes it into `CreatureBehaviourFlags`. Static flags are the same
  bits in both engines. New game code must read `Behaviour`, not raw `ExtraFlags`.
  Limit: the three existing raw readers (`Creature.ExtraFlagAlwaysRun`, `ExtraFlagNoAggro`,
  `InstanceManager` bind) are not rewired in this step.
- **Provenance**: columns and defaults from mangos-classic `sql/base/mangos.sql`
  (creature_template, creature_ai_scripts, creature_ai_summons) and the classic-db z2815 dump
  (broadcast_text, creature_ai_texts); loader semantics from mangos-classic
  `CreatureEventAIMgr.cpp:211-279` and `ObjectMgr.cpp:7786-7821, 9978-10040`; flag enums from
  mangos-classic `Entities/Creature.h:49-72`, `Entities/CreatureDefines.h:27-99` and vmangos
  `Objects/CreatureDefines.h:98-176, 250-252`. The importer is checked against the real dump
  with `ARCANECORE_CLASSICDB_DUMP` (see `CreatureBehaviourImportTests`); no dump data is committed.

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
- Detection-range auras and stealth do not modify the aggro radius, and `IsNeutralToAll` is covered only through the hostility seam.
- Per-instance map updaters and instance resets belong to `feat/instances`.
