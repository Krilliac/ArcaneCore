# Zone scripts, batch 6 — 2026-10-16

Lane `zone-scripts-batch6`, base `df7e31c8`. References (read-only, local): mangos-classic `src/game/AI/ScriptDevAI/scripts/` at `8ec338a170`
(`<refs-root>\mangos-classic`), data from ClassicDB `ClassicDB_1_12_1_z2815` (`creature_template`, `creature`, `quest_template`, `script_texts`,
`spell_script_target`, `creature_movement_template`), spell ids and effects checked against the 5875 client `Spell.dbc`. Structure as in
[batch 5](zone-scripts-batch5-20261016.md): creature scripts are `CreatureAI` classes in `src/ArcaneCore.Game/Creatures/Scripts/`, registered by
creature entry in `CreatureAiFactory` (the entry-script table and, for an event quest, `_entryScriptQuests`); gossip scripts are
`INpcGossipScript` classes registered by entry in `QuestNpcFeature`; the spell script is an `ISpellScript` found by `SpellScriptRegistry`.

The five were chosen for how many players they touch: each is the whole of a quest (an escort-like follower, a scripted battle, a hand-in, a
gossip-only quest step, the key drops of four quest chains). Each has its ScriptName bound in z2815, and its creatures, quests, texts and spells in
the data we import. Line numbers below are of the mangos-classic file named in the row; every one was re-read against the file before this note.

## Ported

| Id | Script (ScriptName) | Reference | Port |
|---|---|---|---|
| Z1 | `npc_the_scourge_cauldron` (creature 11152; quests 5216/5229, 5219/5231, 5225/5235, 5222/5233) | western_plaguelands.cpp:41-101 (AI: `DoDie` :47-55, `MoveInLineOfSight` :57-100), :1001-1004 (register) | `ScourgeCauldronAI`. A player who comes into sight of a living cauldron with either quest of its area incomplete makes it summon the area's Cauldron Lord — Felstone Field (area 199) Bilemaw 11075, Dalson's Tears (200) Malvinious 11077, Gahrron's Withering (201) Soulwrath 11078, Writhing Haunt (202) Razarch 11076 — timed out-of-combat-or-dead 10 minutes, at contact distance in front of the cauldron facing 0, and die (`DoDie`). After the death a respawn delay under 600 s is raised to 600 s for the later deaths. No aggro on sight (the source does not call the base). |
| Z2 | `npc_ringo` (creature 9999, quest 4491 "A Little Help From My Friends") | ungoro_crater.cpp:156-320 (AI), :327-339 (`QuestAccept_npc_ringo`), :816-820 (register); texts :129-149 | `RingoAI`, a `FollowerAI`. He lies dead (stand state) until the quest is taken; then he stands and follows the player with faction 290. Following and not fighting, he faints after 30-60 s (one of four lines `-1000418..421`, sleep stand state, follow paused) and every 60-120 s after that; Spraggle's water (Revive Ringo 15591, the only `spell_script_target` of that spell is 9999) wakes him (one of `-1000422..425`) and he follows again. Spraggle Frock (9997) within interaction distance (5 yd, 3D, both bounding radii added) completes the quest for the player's group when it is incomplete, and either way plays the end scene (`-1000426..433`, Spraggle speaking `-1000427` and `-1000432`, timers 3/5/1/9/1/3/10/5 s) and then he goes. A dead or missing Spraggle ends the scene at once. |
| Z3 | `npc_regthar_deathgate` (creature 3389, quest 4021 "Counterattack!") | the_barrens.cpp:821-1050 (AI; spawn spots :784-819), :1057-1071 (`GossipHello`), :1073-1083 (`QuestAccept`), :1085-1095 (`GossipSelect`), :1141-1147 (register) | `RegtharDeathgateAI` and `RegtharDeathgateGossip`. Taking the quest, or the gossip line "Where is Warlord Krom'zar?" (shown with menu 2533 while the quest is incomplete; the menu closes), makes him say `-1010001` and start the battle: on the 7 Horde spots a Horde Defender 9457 or Axe Thrower 9458 (random), the first five walking their entry's waypoint path, and on the 15 Kolkar spots a Kolkar Invader 9524 or Stormseer 9523, all corpse-timed 40 s. A dead defender comes back on its spot 12 s later (1 s when more than three are already waiting; 50% chance of `-1010002`), a dead Kolkar 10 s later and counts; one of each per update, the list in death order. The 20th Kolkar killed brings Krom'zar (9456) with an Invader and a Stormseer on their three spots, and nothing respawns after. Krom'zar's death ends the battle: the yell `-1010003` and every slot summon still there despawns, a minute later if it is fighting. A respawn of Regthar does the same and resets him. |
| Z4 | `npc_aged_dying_ancient_kodo` (creatures 4700, 4701, 4702, 11627; quest 5561 "Kodo Roundup"), spell script `KodoKombobulator` (18153) | desolace.cpp:62-125 (AI), :133-161 (spell script), :163-177 (`GossipHello`), :677-681 and :712 (register); texts :42-44 | `AgedDyingAncientKodoAI`, `KodoKombobulatorScript`, `AgedDyingAncientKodoGossip`. The Kombobulator takes only a wild kodo (4700-4702; else bad targets), and is refused (caster aura state) while the caster has Kodo Kombo Player Buff 18172 or the kodo Kodo Kombo Despawn Buff 18377. Its effect: the kodo gives the caster 18172 (triggered), becomes a Tamed Kodo 11627 (`UpdateEntry`, the AI stays), casts 18377 on itself, stops a waypoint walk and follows the caster at the pet distance and angle. A kodo without the gossip flag that sees Smeed Scrabblescrew (11596) within 10 yd makes him say one of `-1000348..350` and casts Kodo Kombo Gossip (18362, triggered) on itself; the spell's hit stops it, gives it the gossip flag and starts a one-minute timer after which, out of combat, it dies and respawns as the wild kodo. Talking to a kodo with 18377 while having 18172 gives the quest's credit for the kodo's entry, removes 18172 and despawns the kodo after 10 s; the kodo's own menu is shown either way. |
| Z5 | `npc_lady_katrana_prestor` (creature 1749, quest 4185 "The True Masters") | stormwind_city.cpp:454-497 (`enum` :454-461, `GossipHello` :462-473, `GossipSelect` :475-497), :1368-1372 (register); option texts sql/scriptdev2/scriptdev2.sql:2539-2542 | `KatranaPrestorGossip`. Menu 2693 with her quests (when she is a quest giver) and, with 4185 incomplete, the first of four lines; each line opens the next menu (2694, 2695, 2696) with the next line, and the fourth closes the menu and completes the quest's event for the player (`AreaExploredOrEventHappens`). |

The quest checks are the existing seams: the creature scripts use `CreatureMapSystem.QuestIncomplete` (`IScriptQuestEvents.IsQuestIncomplete`,
batch 5), and the two gossip scripts registered in `QuestNpcFeature` are given `ScriptQuestEvents.IsQuestIncomplete`, the same helper, built on
the feature's service provider and read at gossip time. Group credit (Ringo) is `CreatureMapSystem.RewardGroupEventExplored`; the kodo credit is
`TalkedToCreature`, as the source's `Player::TalkedToCreature` (a new default member of `IScriptQuestEvents`, carried by `CreatureMapSystem`
and `ScriptQuestEvents` to `QuestNpcServices.TalkedToCreature`); Katrana's event is `QuestNpcServices.AreaExploredOrEventHappens`. Ringo (4491) and Katrana (4185) are listed in
`_entryScriptQuests` so the quest service offers those event quests.

### Documented deviations

- **Regthar: a second battle can bring Krom'zar again.** The source's `StartEvent` (:948-959) does not clear `m_isKromzarSpawned`, and
  `FinishEvent` (:961-970) does not either, so after Krom'zar's death a new start (another player taking the quest, or the gossip line) spawns the
  slots but neither respawns a Kolkar nor brings Krom'zar until Regthar himself resets (evade or respawn) — the quest's banner cannot drop. The
  port's `StartEvent` clears the flag, the kill count and the stale slot and kill lists first. Pinned by `Regthar_ASecondBattleBringsKromzarAgain`.
- **Regthar: the 20-minute limit is unreachable, as in the source.** `m_eventTimer` is only decremented once Krom'zar is out (:1047-1048), and the
  check that ends the battle runs only while he is not (:1027-1028), so it never fires. Kept as it is; the decrement saturates at 0 instead of
  wrapping the unsigned timer.
- **Regthar: the respawn times use a clock the AI keeps** (summed update diffs), not the map's clock (`GetMap()->GetCurrentClockTime()`); the
  intervals are the same.
- **Regthar keeps the base victim update.** The source's `UpdateAI` never selects a victim or melees; `ScriptedAI.OnUpdate` (`UpdateVictim`) runs
  before the battle logic, so he fights back when attacked.
- **Regthar: his first spawn yells `-1010003`.** `JustRespawned` calls `FinishEvent`, and cmangos calls `JustRespawned` from `AIM_Initialize` too;
  our `OnRespawn` runs at the first spawn the same way. Faithful, noted because it looks like a bug.
- **Regthar: no `CLOSE_GOSSIP_MENU` on quest accept.** The quest-accept hook has no menu to close here; the gossip line closes it.
- **Cauldron: the summon point is `BoundingRadius + 0.5` in front of the cauldron**, height from `GroundZ`. cmangos's all-zero `SummonCreature`
  coordinates use `GetClosePoint` with the summoned creature's radius and a collision search, neither ported (as for Feero in batch 5).
- **Cauldron: the 600 s respawn applies from the next death.** cmangos draws this death's respawn delay at JUST_DIED, before `DoDie` calls
  `SetRespawnDelay(600)`; our creature consumes its delay at death the same way, so the first death keeps the DB's 315 s. The override is set only
  when the current delay is under 600, as `GetRespawnDelay() < 600` reads.
- **Cauldron: the sight range is the engine's** `MoveInLineOfSight` range; the source relies on cmangos's.
- **Kodo: a despawn timer at 0 stays off.** The source runs `m_uiDespawnTimer -= diff` even at 0 (an unsigned wrap to about 49.7 days); the port
  counts only while it is set.
- **Kodo: the death and respawn after the minute are `KillCreature` then `ForceRespawn`**, our pair for `SetDeathState(JUST_DIED)` plus
  `Respawn()`; the respawn restores the original template entry.
- **Kodo: the spell script is bound by spell id 18153.** mangos-classic registers it by the name `spell_kodo_kombobulator` (:712); z2815 has no
  `spell_scripts` binding for that name, and ArcaneCore binds spell scripts by id.
- **Kodo (observation, faithful): a tamed kodo never makes Smeed speak.** z2815's `creature_template` 11627 already has npcflag 1 (gossip), so
  the `!HasFlag(UNIT_NPC_FLAG_GOSSIP)` check (:77) is false for every tamed kodo; the Smeed lines and the 18362 cast happen only for a wild kodo
  without the flag. Pinned by `Kodo_TheTamedKodoTemplateAlreadyHasTheGossipFlag_SoSmeedNeverSpeaksToIt`.
- **Kodo: the spell data as the 5875 client `Spell.dbc` has it** (read from `client-dbc-5875`): 18153 CastingTimeIndex 6 (5000 ms), effect 3
  (dummy) on target 6, range 3; 18172 CastingTimeIndex 1 (0 ms), DurationIndex 5 (300 s), APPLY_AURA 4 on target 21, range 6, attributes
  0x4000100 (the tests' row leaves out 0x4000000, the debuff bit, which the hand-in does not read); 18377 CastingTimeIndex 1 (0 ms),
  DurationIndex 5 (300 s), APPLY_AURA 23 (periodic trigger, amplitude 300000) on target 1, range 1. None of the three has reagents or a power cost.
- **Ringo: the dead stand state is set only in the constructor**, as in the source (:158-162), so a respawned Ringo is not laid down again.
- **Katrana: the option texts come from `sql/scriptdev2/scriptdev2.sql`** (`gossip_texts` -3000120..-3000123); z2815 has no such rows, so they are
  constants in the script.

## Skipped, and why

| Script | Reason |
|---|---|
| `npc_kitten` / `npc_corrupt_saber` (Felwood) | Changes entry with `SetEntry` without `UpdateEntry`, is a follower summoned by its owner, and its gossip lives on the morphed entry; needs an owner-summoned follower seam. |
| `npc_captured_sprite_darter`, `npc_kindal_moonweaver`, `go_cage_door` (Feralas) | Read and write per-map script data (`ScriptedMap`) shared between the cages and Kindal. |
| `npc_theramore_spar_controller` | Ambient sparring with no quest; low player impact. |
| The hunter-quest demons (`npc_solenor`, `npc_klinfran`, `npc_artorius`, `npc_simone`, `npc_precious`) | Need the respawn-as-other-creature handling and guardian AI the batch does not have. |
| The four herald gossips (Runthak, Saurfang, Mattingly, Afrasiabi) | Gossip-only flavour for the Onyxia/Nefarian head turn-ins; low impact. |
| `boss_maws`, `npc_blackwood_*`, Cork/Rigger Gizelton, `npc_ogron`, `npc_arei`, `npc_tooga`, `npc_twiggy_flathead`, `npc_wizzlecranks_shredder`, `npc_felhound_tracker`, `npc_captured_arkonarin` | Unchanged from [batch 5](zone-scripts-batch5-20261016.md): each still needs a missing seam or is large enough for a batch of its own. |

## Tests

- `ZoneScriptBatch6Tests` (Game, 70): registration of the seven entries, the two event quests listed as scripted, the Kombobulator found by id;
  the cauldron (each area with each of its two quests: the right lord in front and the death; the engine's sight check; the 600 s override under
  600 s with this death keeping 315 s, and left alone at 600 and 900; no quest, another area's quest, an unknown area and the zone id as area do
  nothing; a creature in sight and a dead cauldron do nothing; the area table); Ringo (dead until 4491, another quest ignored, stands, follows,
  faction 290; first faint at the drawn 30-60 s with a faint line and the 60-120 s redraw; no faint before the quest; the water wakes him with a
  wake line and he follows, another spell does not, the water before the follow does nothing; Spraggle in reach with the quest: group credit,
  the whole scene in order with Spraggle's two lines, then he goes; the scene's timers; Spraggle without the quest: no credit, scene plays;
  Spraggle out of reach and another creature do nothing; a dead Spraggle ends it); Regthar (accept: line, 7 + 15 summons on their spots, another
  quest and a second accept add nothing; defender back after 12 s on its spot; the fifth waiting defender's 1 s behind the first four; Kolkar
  back after 10 s and counted; Krom'zar at the 20th kill, not the 19th, and nothing respawning after; Krom'zar's death: yell, the slots gone, the
  guards kept; a second battle brings Krom'zar again; the gossip option only with 4021 incomplete and the quest flag; the option starts the
  battle and closes, another action does nothing); the kodos (Smeed within 10 yd: his line and the 18362 cast; beyond 10 yd, a gossip-flagged
  kodo, another unit, and the tamed template do nothing; 18362 gives the flag, the minute and stops it, another spell does not; the respawn as the
  wild kodo after the minute and not before; no timer without the gossip; held in combat; the hand-in with both buffs, and nothing with either
  missing); the Kombobulator on a real `SpellSystem` (each wild entry tamed with both buffs and the follow; a tamed kodo and a player refused as
  bad targets; refused with the caster's buff or the kodo's buff; the hand-in after it credits once); Katrana (the first line only with 4185
  incomplete, the quest flag, the four-step chain with its menus, the event and the close at the last, other actions ignored).
- The quest helper the gossip registrations use, `ScriptQuestEvents.IsQuestIncomplete`, keeps its batch 5 World tests (incomplete, complete,
  failed, absent, another quest, no quest feature).
- Random draws come from the map's seeded `Random(1)`; summoned creatures and players that can be hit have 1,000,000 health.

## Verification

All runs went through `<lanes-root>\tools\run-in-slot.ps1` (BelowNormal, with the terrain and DBC test variables set).

- Build: `dotnet build ArcaneCore.slnx -m:2 -nodeReuse:false -p:UseSharedCompilation=false`, 0 warnings, 0 errors.
- Full Game tests: 9545 passed, 7 skipped, 0 failed (9552). Full World tests: 7566 passed, 49 skipped, 0 failed (7615).
- The first full Game run had two failures, `ScriptEnginePrerequisiteTests.AGossipOrQuestOnlyScript_CountsAsImplemented_OnlyBecauseAHandlerIsRegistered`
  (both cases): the test used entry 9999 as an entry with no script, and 9999 is now Ringo's entry script. The test now uses 990999; its intent is
  unchanged.
- Flake check: `ZoneScriptBatch6Tests` together with `ScriptEnginePrerequisiteTests` (83 tests) run 20 times in a row: 20 of 20 runs passed
  83/83, no reruns needed.
- RED: with three behaviours reverted at once (Regthar's `StartEvent` clearing the Krom'zar flag, the cauldron's `< 600` check made `< 1`, the
  kodo's one-minute timer never firing) exactly the pinning tests failed (4 of 70): `Regthar_ASecondBattleBringsKromzarAgain`,
  `Cauldron_ARespawnUnderTenMinutesIsRaisedToTenForTheLaterDeaths_ThisDeathKeepsItsOwn`,
  `Kodo_AMinuteAfterTheGossipItDiesAndRespawnsAsTheWildKodo` and `Kodo_AFightingKodoIsHeldUntilTheFightEnds`. The changes were reverted.
