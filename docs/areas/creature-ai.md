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
  - `Distract` (vmangos DistractMovementGenerator, IdleMovementGenerator.cpp:33-75): stands for a while (the 5 s after a stealth alert,
    the effect value of SPELL_EFFECT_DISTRACT), then faces its spawn orientation again. Any newly pushed generator expires it
    (MotionMaster::Mutate, MotionMaster.cpp:699-702). `CreatureMapSystem.SetFacingTo` sends the facing spline (Unit::SetFacingTo).
- **Splines.** Multi-point linear splines use SMSG_MONSTER_MOVE: the point count, the
  destination, then N−1 packed offsets from the path midpoint (11/11/10 bits at quarter-yard
  resolution, from gtker wow_messages / vmangos `MoveSplineInitArgs` packing). Facing can be
  none, an angle, a spot or a target. A late observer catches up on the remaining points.
- **AI host** (`CreatureMapSystem.Ai.cs`):
  - **Aggro radius** (vmangos `Creature::GetAttackDistance`, Objects/Creature.cpp:2193-2240): the template's detection range (`creature_template.Detection`, 18 yd by default; the earlier hard-coded 20 yd was wrong), minus the level difference (at most 25 levels counted below), never below `min(detection, 5)`, times `AggroRate`; a detection range under 1 means no proximity aggro. The detect-range auras are not applied.
  - **Proximity aggro rules** (`BasicAI::MoveInLineOfSight`, AI/BasicAI.cpp:30-77): the creature is alive, not evading, not stunned, confused or fleeing; the target is a living non-GM player of this map; a creature with a victim only picks up more targets in instanceable maps or with NO_LEASH_EVADE (`EnterCombatWithTarget` then only adds threat); `CanInitiateAttack` (alive, not stunned, not spawning or unselectable, react state aggressive, not temporarily pacified); the vertical distance minus both bounding radii is within 3 yd (flyers exempt); the plain 3D distance, without bounding radii, is strictly inside the radius; `CanAttack`, hostility and line of sight. The civilian column no longer matters here.
  - **React states** (`Creature.ReactState`, vmangos `InitializeReactState`, set at creation and every respawn): totems, triggers (the invisible flag), NO_TARGET and IGNORE_COMBAT creatures are passive (they neither aggro nor fight back), NO_AGGRO makes a creature defensive (fights back, no proximity aggro), the rest are aggressive.
  - **Respawn pacify**: a creature that respawns cannot initiate attacks for 5000 ms (`Creatures:RespawnPacifyMs`, vmangos `SetTempPacified(5000)`, Creature.cpp:877-878); the timer counts down while alive and clears when it enters combat.
- Detection-range auras do not modify the base aggro radius, and `IsNeutralToAll` is covered only through the hostility seam. Stealth does restrict aggro inside that radius (see the threat area doc).
  - **SMSG_AI_REACTION**: every time a creature starts attacking a unit it sends one (guid, u32 reaction 2 = hostile, gtker wow_messages `smsg_ai_reaction.wowm`) to the units that see it; the client plays the aggro sound from it (`Creatures:SendAiReaction`).
  - **Attack start**: melee, a zero-threat entry, combat state on both sides, the combat start point, the aggro hook, the assistance call and chase.
  - **Victim selection**: the threat list (110 % / 130 % rule), skipping invalid targets; a target out of the threat area, reached in list order before a victim is found, abandons the selection and the creature evades (vmangos `ThreatContainer::selectNextVictim`, Threat/ThreatManager.cpp:305-312; `ThreatList.SelectVictim` takes the area predicate).
  - **Soft leash** (`Creature::IsOutOfThreatArea`, Objects/Creature.cpp:2796-2815): never with NO_LEASH_EVADE or in an instanceable map. The threat area is a sphere around where the fight began with radius `max(1.5 x aggro radius, ThreatRadius)` (`Creatures:ThreatRadius`, 50 here; the earlier 60 was wrong). The target is out only when neither the creature nor the target is inside it and the leash extension clock is more than `Creatures:LeashExtensionSeconds` (12) whole seconds old. The clock starts at the first check outside the area, is refreshed at the 3 s check while the creature is stunned, confused or fleeing, is shared with creatures that joined through its assistance call, and is cleared when combat stops.
  - **Hard leash** (`creature_template.Leash`, Creature::Update :976-993): every `Creatures:LeashCheckIntervalMs` (3000) of world time a creature in combat whose distance from where the fight began exceeds its template leash range evades instead of running its AI.
  - **Evade**: interrupts the cast, `CombatStop`, clears threat, restores full health and mana, calls `OnEvade`, then runs home. A waypoint mover goes back to its combat start point; anything else goes to its spawn point. The creature is immune to new attacks until it is home.
  - **Unreachable target** (`CreatureMapSystem.Combat.cs`, `Movement/CombatMovement.cs`): the chase generator asks the map system for the path and its verdict (`ICreaturePathQuery`: no path or an `Incomplete` path is unreachable; a straight line without navigation data is reachable), keeps vmangos' `m_bReachable` as `IsReachable` and re-paths every 100 ms while stuck without launching zero-length splines. The host keeps vmangos' `m_targetNotReachableTimer` (`Creature.TargetNotReachableMs`, Objects/Creature.cpp:1013-1046) before the AI each update: it grows while the top generator is a melee chase with an unreachable victim, the creature has no NO_UNREACHABLE_EVADE flag, is not owned or charmed by a player, and is out of melee reach or out of sight of the victim; otherwise it is 0 (and combat stop clears it). Past `Creatures:UnreachableTargetSoftEvadeMs` (3000, `IsEvadeBecauseTargetNotReachable`, Creature.h:510) the creature is in evade mode where it stands (`IsInEvadeMode`: attacks and spells on it evade), its AI does not update and it regenerates as if out of combat, while it keeps its victim and its chase; past `Creatures:UnreachableTargetEvadeMs` (24000, :1039) it evades home. The victim stays on the threat list (only Alterac Valley, map 30, drops its threat after one second, :1026-1027); the earlier mangos rule that dropped the victim and switched targets after 5 s (Object/UnitThreat.cpp:342-361) is gone. EventAI event 36 reads the same flag. Not delivered: the knock-back exemption for a player victim (no knock-back state on the server).
  - **Assistance** (vmangos `CallAssistance`): once per fight, idle creatures of the same faction within 10 yd (`Creatures:AssistanceRadius`) that can see the caller join after 1.5 s. Helpers do not call more help. They attack the enemy stored with the call, even if the caller has switched victims during the delay (vmangos `AssistDelayEvent::Execute`). A charmed creature calls nobody. The template range depends on the row dialect: for vmangos rows, `call_for_help_range` 0 calls nobody and any other value searches the configured radius (Creature.cpp:2522-2526). For cmangos rows and rows with no recorded dialect, a positive `CallForHelp` replaces the radius, and NO_CALL_ASSIST (0x800) calls nobody (cmangos Creature.cpp:2168-2173).
  - **NO_MELEE_FLEE panic** (`CreatureMapSystem.NoMeleeFlee.cs`): a creature with static flag 0x00100000 never swings (both
    references) and, with `Creatures:NoMeleeFleeOnAggro` (default true), runs in panic for `Creatures:NoMeleeFleeMs` (30000) when a
    player or a player's pet or charm engages it, then evades (cmangos Unit::SetInCombatWithVictim, Entities/Unit.cpp:7993-7998, and
    CreatureAI::TimedFleeingEnded, AI/BaseAI/CreatureAI.cpp:254-258). Not for a summon, a rooted or sessile creature, one already
    fleeing or casting, nor against a creature. The flight starts after the aggro hook, so a cast on aggro comes first. vmangos only
    takes the melee away for the same bit (CREATURE_STATIC_FLAG_NO_MELEE, original comment "Flee"); `false` keeps that. Critters with
    the flag (deer, sheep, cows) already run through CritterAI.
  - **Flee for assistance**: the creature runs to the nearest possible helper (within 30 yd) and calls for help when it arrives. If no helper is found, it flees for 7 s.
- **AI selection** (`CreatureAiFactory`, from `creature_template.AIName`): `NullAI`, `ReactorAI`,
  `PassiveAI` (= reactor), `AggressorAI`, `CritterAI`, `GuardAI`, `EventAI` and `GuardEventAI`, plus registered C# scripts. An empty
  name gives GuardAI for a template with the GUARD extra flag (0x400 in both dialects; mangos CreatureAISelector.cpp:85-88 puts
  the guard check after the script name and before the permit contest), Reactor for civilians and Aggressor otherwise. Unknown
  names are reported once and get the default.
- **GuardAI** (`Creatures/AI/GuardAI.cs`, vmangos AI/GuardAI.cpp re-implemented): only the on-sight rule differs from
  AggressorAI (`CreatureMapSystem.CanGuardAggroOnSight`, GuardAI::MoveInLineOfSight, GuardAI.cpp:50-77): a guard without a victim
  attacks a unit in its aggro radius that is hostile to players as such (`ICreatureHostility.IsHostileToPlayers`: the faction
  template's hostile mask carries FACTION_MASK_PLAYER, DBCEnums.h:71), that its own hostility calls an enemy (opposing faction,
  Hated reputation, a contested-PvP player for a contested guard), or a player the guard is not friendly to who is contested-PvP,
  attacking a unit the guard is friendly to, or attacking someone on a taxi (`IsAttackingPlayerOrFriendly`, GuardAI.cpp:35-48); for
  that player the radius is at least 30 yd. With `Creatures:GuardsDefendFriendlies` (default true) a *creature* fighting a creature
  the guard is friendly to is an enemy too (an ArcaneCore extension: mangos keeps the clause commented out, Object/GuardAI.cpp:74, so
  that half is UNVERIFIED). The height limit applies to creature targets only (GuardAI.cpp:56-57); ONLY_ATTACK_PVP_ENABLING is not
  applied (the guard does not go through BasicAI); the other gates are the common ones (alive, in control, `CanInitiateAttack`, the
  combat hooks' attackability, stealth, line of sight). A guard with a victim ignores everyone else.
- **GuardEventAI** (vmangos AI/GuardEventAI.cpp; CreatureAISelector.cpp:66-69): a GUARD-flagged template whose AIName is `EventAI`,
  or any template named `GuardEventAI`, runs `CreatureEventAI` with `UsesGuardSightRules`: its script runs as usual and whom it attacks
  on sight is the guard rule. Not delivered: SMSG_ZONE_UNDER_ATTACK on a guard's death (mangos GuardAI::JustDied; neither vmangos AI
  sends it and the message layout is unverified) and the reference's `IsInAccessablePlaceFor` (water and air).
- **Calling the guards** (static flag CALLS_GUARDS 0x08000000; `CreatureMapSystem.Guards.cs`, `Guards/GuardPostTable.cs`): vmangos
  BasicAI::MoveInLineOfSight / SummonGuard (AI/BasicAI.cpp:49-105), Creature::OnEnterCombat (Creature.cpp:3689-3690), GuardMgr
  (GuardMgr.cpp) and Creature::CallNearestGuard (Creature.cpp:3932-3949). A creature with the flag whose AI does not attack the unit
  itself (a civilian, a defensive creature, or the unit is already its victim) calls the guards when a hostile player comes within its
  detection range (3 yd of height, attackable, in sight), and any creature with the flag calls them when it enters combat. In an area
  with a guard post (the 57 posts of vmangos GuardMgr at build 5875, keyed by area id, build 5875 elites for Sepulcher, Menethil and Hammerfall) the
  post spends a charge (10 per post, one back per minute; 10 s cooldown after a use, shared by every map), the civilian says its call
  (broadcast text by its model from CreatureDisplayInfo.dbc when the display metadata is installed, else by its faction template;
  Razor Hill always says "Grunts! Attack!") and the post's guard for the team opposite the enemy's player appears 5 yd east of it,
  attacks the enemy and despawns after 2 minutes. A post that is cooling down or empty refuses and the civilian keeps trying on sight;
  after a successful call it stops calling on sight until the guard it called is gone or it respawns. In an area without a post the
  nearest idle friendly guard within 50 yd in sight attacks. Data: classic-db z2815 sets CALLS_GUARDS on no template (vmangos data
  does), so nothing calls until such rows are imported; a call against a creature that no player controls summons nobody (vmangos
  takes the civilian's own team from Faction.dbc `team`, which the faction catalog does not carry). The area comes from the map
  terrain (`CreatureAiServices.AreaOf` overrides it); without extracted maps the area is 0 and the nearest-guard fallback applies.
- **Creature-versus-creature aggro** (`Creatures:CreatureAggroOnCreatures`, default true): `CanAggroOnSight` takes any living unit
  of the map (a GM player and an evading creature are excluded; the hostility seam decides: reputation for players, the faction
  templates between creatures, mangos AggressorAI::MoveInLineOfSight), and the relocation notify of a moving creature visits the
  creatures around it in both directions (mangos CreatureCreatureRelocationWorker, GridNotifiersImpl.h:67-84), so a hostile mob and a
  guard, or a mob and an aggressive pet, acquire each other. The notify now draws its candidates from the map's cell index
  (`GridContainer.CollectObjects`) instead of scanning every creature of the map. `Poll` mode stays player-only.
- **Pets** (`Pets/PetAI.cs`): `MoveInLineOfSight` is mangos PetAI::MoveInLineOfSight (Object/PetAI.cpp:79-108): an aggressive,
  enabled pet without a victim attacks a hostile unit the common on-sight rule accepts, through its own attack (command and PvP
  flags, chase unless told to stay); defensive and passive pets only react to attacks.
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
    health, 13 target casting (repeat timers are parameters 1 and 2), 18 target mana, 21 reached home, 22 receive emote
    (a player's CMSG_TEXT_EMOTE aimed at the creature readies the rows whose emote id matches, the player being the invoker;
    a condition id is checked against the conditions table for that player, and never passes without one: cmangos
    CreatureEventAI::ReceiveEmote :1829-1842 and CheckEvent :467-472; the world's text emote handler calls
    `Creature.ReceiveEmote` like vmangos HandleTextEmoteOpcode, ChatHandler.cpp:751-752), 23/24
    aura and target aura (at least N stacks), 27/28 missing aura and target missing aura (fewer than N), 29
    generic timer (in and out of combat), 31 energy percent, 33 facing target (within 5 yd, victim's back or
    front half circle), 36 target not reachable. Aura stacks and the victim's casting state come from the
    `IUnitSpellQueries` seam (`SpellSystemUnitSpellQueries` over the spell system, bound by
    `CreatureAiServicesBinder`; without a spell system nobody has auras or casts).
  - **Actions with a handler**: 1 text (the 1/2/3-way choice by `rnd % 3` / `rnd % 2`), 11 cast (aura-not-
    present, triggered and interrupt flags; a creature that is casting only casts again when the spell is
    triggered or interrupts; success is the cast being accepted), 12 summon, 13 threat single (direct add or percent) and 14 threat all percent (docs/areas/threat.md), 20 auto attack, 21 combat
    movement (no change or casting fails), 22 and 23 phases, 24 evade (with the combat-only parameter), 25
    flee for assistance, 37 die, 39 call for help, 53 start relay script (see "Relay scripts" below), 54 target-aware
    text (direct id or random template).
  - **Targets**: 0-6, 7 (the invoker; there are no pets), 10, 12 and 15 (no unit). Others fail the action.
  - Event 36 (target not reachable) is checked at every batch and fires while the chase generator reports its victim
    unreachable (see "Unreachable target" above; nothing is unreachable without navigation data); death-prevented (35) needs
    the death-prevention action and a combat hook and is not implemented.
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
  `creature_ai_summons`, `dbscript_random_templates` (string type 0), `AIName`, the movement `Run` column and the template behaviour columns
  described in "Content model" below. A vmangos `creature_ai_events` table is reported and not
  imported.

Action 54 follows CMaNGOS `CreatureEventAI.cpp`'s `ACTION_T_TEXT_NEW`: parameter 2 resolves
the target; parameter 3 selects a random string template when nonzero, otherwise parameter 1
is the text id. Weighted choices run first with cumulative percentage thresholds; remaining
probability chooses uniformly among chance-zero rows. A missing template or selected zero
performs no chat, and a missing target fails the action. Positive ids resolve broadcast text,
negative ids resolve the existing creature AI text catalog. World schema 27 stores signed
text choices separately from relay templates; import reports count only string choices.
The primary contract is `ScriptMgr::GetRandomScriptTemplateId` and
`LoadDbScriptRandomTemplates` in CMaNGOS classic revision `8ec338a1704e7dcb1c0213eb7ed58f9231ade40f`.
Legacy `MangosStringLocale` fallback and ranged action 57 remain pending.

## Relay scripts (EventAI action 53)

cmangos EventAI's ACTION_T_START_RELAY_SCRIPT (53: relay id, target; CreatureEventAI.cpp:1227-1247) starts a DB script of
`dbscripts_on_relay` with the resolved target as the script's source and the creature as its target (a negative id is a relay
template, `dbscript_random_templates` type 1). vmangos has no such action: its EventAI rows call generic scripts instead, so the
semantics are cmangos'. classic-db z2815 has 141 action-53 rows reaching 109 relay ids.

- **Scheduling** (`Scripts/RelayScriptRunner.cs`, cmangos Map::ScriptsStart / ScriptsProcess, Maps/Map.cpp:2166-2272): per map; a
  relay already scheduled for the same source and target is not started again; steps without delay run at once, the rest at
  start + delay in delay, priority and dump order; a TERMINATE_SCRIPT that fires drops the rest of that run.
- **Who acts** (`CreatureMapSystem.RelayScripts.cs`, ScriptAction::GetScriptProcessTargets, DBScripts/ScriptMgr.cpp:1360-1645): the
  buddy by entry (nearest creature of `buddy_entry` within `search_radius` of the source, dead with BUDDY_IS_DESPAWNED) replaces the
  source, or the target with BUDDY_AS_TARGET; then REVERSE_DIRECTION swaps and SOURCE_TARGETS_SELF copies. A step whose buddy is not
  found is skipped (except TERMINATE_SCRIPT). A `condition_id` is evaluated for the player among source and target (the conditions
  table, as for EVENT_T_RECEIVE_EMOTE); without a player or a conditions table the step is skipped.
- **Commands carried out** (ScriptAction::ExecuteDbscriptCommand): 0 TALK (dataint, one of dataint..4, or string template datalong;
  creature speakers), 1 EMOTE (datalong or one of dataint..4; an EMOTE_STATE_* id becomes UNIT_NPC_EMOTESTATE, 0 clears it, others play
  once: vmangos Unit::HandleEmote with the SharedDefines.h state ids standing in for Emotes.dbc), 3 MOVE_TO (home with dataint 1/2,
  turn to `o`, move by z, or walk to x/y/z after clearing the pushed movement; datalong is a relay started on arrival), 15 CAST_SPELL
  (datalong or a dataint at random; datalong2 bit 0x01 triggered; COMMAND_ADDITIONAL casts without a target), 18 DESPAWN_SELF
  (temporary creatures, after datalong ms), 21 SET_ACTIVEOBJECT (nothing to do here), 25 SET_RUN (script moves run, and the client is
  told), 28 STAND_STATE, 29 MODIFY_NPC_FLAGS (datalong2 0 remove, 1 add, 2 toggle: the code, not the header comment), 31
  TERMINATE_SCRIPT (npc entry datalong within datalong2 yd, else the step's buddy; COMMAND_ADDITIONAL inverts), 32 PAUSE_WAYPOINTS
  (MotionMaster::PauseWaypoints(0)/UnpauseWaypoints: the waypoint generator stops and later sets off for the same node), 36 SET_FACING
  (face the target, or the reset facing with datalong), 45 START_RELAY_SCRIPT.
- **Not carried out** (skipped and reported once per relay id): every other command, the data flags BUDDY_BY_GUID, BUDDY_IS_PET,
  BUDDY_BY_POOL, BUDDY_BY_SPAWN_GROUP, ALL_ELIGIBLE_BUDDIES, BUDDY_BY_GO and BUDDY_BY_STRING_ID, game-object buddies, a player as the
  speaker, emoter, mover or caster, the MOVE_TO teleport, speed and forced movement, DESPAWN_SELF of a database spawn (no forced
  despawn with a respawn timer exists), TERMINATE_SCRIPT by pool and its waypoint pause adjustment. The commands the 109 relays reached
  from EventAI use most: MOVE_TO 99, TALK 74, EMOTE 53, TERMINATE_SCRIPT 27, SET_ACTIVEOBJECT 24, SET_FACING 20, PAUSE_WAYPOINTS 16,
  ACTIVATE_OBJECT 14, SET_RUN 14, MODIFY_NPC_FLAGS 14, TEMP_SPAWN_CREATURE 13, MOVEMENT 11, STAND_STATE 11.
- **Data** (world step 40, `RelayScriptDataModule`): `dbscripts_on_relay` (every column but the comment, plus the dump order per id)
  and `dbscript_relay_template`; `CreatureDumpImporter` reads `dbscripts_on_relay` and the type-1 rows of `dbscript_random_templates`
  (a later dump file replaces every row of a relay id it carries), `EfCreatureDataStore` loads them into
  `CreatureAiContent.RelayScripts`. A database imported before this step has no relay rows: re-import the dump.

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
  `HomeMovementGenerator.cpp:52`. Both default to the previous behaviour. `TargetedMovementGenerator` overrides `IsReachable`
  from the `ICreaturePathQuery` verdict (`CreatureMapSystem` implements that query beside `ICreatureMover`).
- `ICreatureHostility` gained default members `IsHostileToPlayers` and `IsFriendly` (false by default; the faction
  implementation answers from the templates, the reputation implementations forward to it).

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
  (`CreatureTemplate.DefaultDetectionRange`). A template without the call-for-help column takes its dialect's schema default:
  5 for a vmangos row (`call_for_help_range DEFAULT '5'`, vmangos sql/old_migrations/20190123062532_world.sql:28) and 0 for a
  cmangos row (`CallForHelp DEFAULT '0'`, mangos.sql:1258). The aggro radius reads `Detection`, the hard leash reads `Leash`, and
  the assistance call reads `CallForHelp` (see **Assistance** above).
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
- Creature-versus-creature aggro reads the faction templates only (the reputation seam answers for players); creature stealth
  and invisibility are not modelled for creature targets; `Poll` mode scans players only. Mobs aggro on pets and totems alike
  (no totem exemption exists in the references' on-sight rules; UNVERIFIED against the client).
- Flee-for-assist is simplified: there is no "attempts to run away in fear" emote, and help is called once on arrival.
- No totem AI or formation/linking (`creature_linking`); no SMSG_ZONE_UNDER_ATTACK from a guard's death (mangos only).
  - **How aggro is triggered** (`Creatures:AggroScanMode`, default `Relocation`): a player or creature that moves or joins the map schedules one AI notify after 1000 ms (`Visibility.AIRelocationNotifyDelay`); the notify makes the creatures (for a player) or the players and, with `Creatures:CreatureAggroOnCreatures`, the creatures (for a creature, both directions) within `MaxCreatureAttackRadius` (40) times the aggro rate run `MoveInLineOfSight` for it (`AiRelocationNotifier`; vmangos Unit.cpp:10082-10160, GridNotifiersImpl.h:57-119). Standing still triggers nothing. `Poll` is the original behaviour: every creature checks every player every tick (development). The aggro predicate asks the stealth and invisibility visibility service whether the creature detects the player: a stealthed player is attacked only when the creature detects it, and one just outside detection range raises the stealth alert (docs/areas/threat.md). Differences from vmangos: a plain 2D radius over the touched cells instead of the exact cell visit.
- Per-instance map updaters and instance resets belong to `feat/instances`.
