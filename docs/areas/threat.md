# Area: threat, aggro and combat AI rules

Status: wave 4 lane `threat-and-aggro` (branch `claude/vw5-threat-and-aggro`). Everything below is checked against the
vmangos sources in `D:\refs\vmangos` (primary). Nothing here was run against a retail client or a vmangos server: the
evidence is the reference code plus this repository's tests.

## Delivered scope

### threat-list-core (Game/Combat/ThreatList.cs, Game/Combat/Threat/*)

`ThreatList` is the one ThreatManager of a creature (vmangos `ThreatManager` + `ThreatContainer` + `HostileReference`).
The mirror set `UnitCombat.ThreatenedBy` is the HostileRefManager. Behaviour that follows the reference:

| Rule | Reference |
|---|---|
| Who may hold a list: living creature, not a totem, not a pet of a player, not charmed by a player, not NO_THREAT_LIST; players never | Objects/Unit.cpp:7377-7405 (`ThreatRules.CanHaveThreatList`) |
| Ignored: the owner itself, dead target, GM target; threat never below 0 | Threat/ThreatManager.cpp:399-407, HostileReference::addThreat :101-106 |
| A non-negative change on a pet's entry also creates a 0-threat entry for its owner | ThreatManager.cpp:117-122 |
| Assist threat is 0 while the owner is confused or fleeing (the entry still exists) | ThreatManager.cpp:414-421 |
| `NoNewEntry`: raise an existing entry, never create one (EX_NO_THREAT) | ThreatManager.cpp:424, addThreatDirectly :427-447 |
| Offline list: a GM target's entry keeps its threat but is never selected; it comes back when the GM flag goes | ThreatManager.cpp:128-149, :526-543 |
| Percent modification: below -100 removes, -100 zeroes, otherwise scales | ThreatManager.cpp:250-262, ThreatManager.h addThreatPercent |
| Temp threat (taunt, Fade): folded into the stored threat and remembered; applied once, no stacking | ThreatManager.cpp:481-500, ThreatManager.h setTempThreat/resetTempThreat |
| Taunt caster list: latest valid taunter first | Objects/Unit.cpp:7513-7542 |
| Victim selection: two passes (second-choice targets only when nobody else), 110% in melee reach / 130% otherwise, out-of-area target abandons the selection | ThreatManager.cpp:286-370 |
| `HostileRefs`: add temp threat / scale / delete references over every list holding a target | Threat/HostileRefManager.cpp:39-55, :124-134 |

The list re-sorts lazily (only after a change), with a stable insertion sort, as vmangos does (ThreatManager.cpp:275-280).

## Limits (not delivered here, by design)

- No threat packets exist in vanilla 1.12 (`wow_messages` threat packets are 3.3.5 only), so nothing is sent.
- Assist threat zeroing for a stunned owner whose stun breaks on damage and for UNIT_STATE_ISOLATED needs aura-holder data
  that the spell lane does not publish; only confused and fleeing are applied.
- "Taxi flying" targets going offline wait for a taxi primitive on `Player`; the GM case is real.
- The second-choice selector predicate (`isLowPriority`) is supported by `SelectVictim`; the creature host passes feared and
  confused targets only (damage-immune, breakable-CC and totem second choices need aura-holder data and a spell catalog).

### victim-selection (Creatures/CreatureMapSystem.Combat.cs `SelectHostileTarget`)

Creature victim selection now follows vmangos `Unit::SelectHostileTarget` (Objects/Unit.cpp:7544-7612):

1. Dead, evading or unknown creature: false. A creature in its 5 s respawn pacify chooses nothing, does not switch and does not
   evade (:7559-7561).
2. A taunt target (latest taunter that is still a valid target) beats the threat list (:7563); then the list picks (two-pass,
   110%/130%); a NO_THREAT_LIST creature sticks to its current victim (:7569-7571).
3. A chosen target is attacked (and chased) unless the creature is stunned, confused or fleeing (:7573-7581).
4. No target: NO_THREAT_LIST returns false; not in combat, taunted or charmed returns false; a creature that is not chasing but
   still has a targetable attacker returns false (a pet sent at a far target, :7592-7603); anything else evades.

Limits: stun/fear/confuse are read from `UnitFlags` (no aura-holder query), the "prevents fleeing" and pending-stun states are
not modelled, second-choice targets are only feared or confused units (damage-immune, breakable-CC and the totem rule of
`Unit::IsSecondaryThreatTarget`, Objects/Unit.cpp:9644-9676, need the aura engine and a spell catalog the host does not have).
The unreachable-target timers (Creature.cpp:1017-1040) are not delivered: nothing in the repository reports a chase as
unreachable (`TargetNotReachableEvent` has no producer), so there is nothing honest to time.

### taunt and threat auras (Spells/Effects/ThreatEffects.cs, Spells/Auras/ThreatAuras.cs, Combat/Threat/Taunt.cs)

| Piece | Behaviour | Reference |
|---|---|---|
| SPELL_EFFECT_ATTACK_ME (114) | Skipped when the (non-player) target already attacks the caster; otherwise the caster's threat is set to the current victim's and the caster becomes the current victim at once | SpellEffects.cpp:3356-3395 |
| SPELL_AURA_MOD_TAUNT (11) | Caster joins the target's taunt list (GUID list, latest first); a living target that can hold a list attacks the caster (not while confused/fleeing) and lifts the taunter's threat via the temp-threat rule; removal fades the temp threat out, evades on an empty list | SpellAuras.cpp:3939-3967, Unit.cpp:7443-7509 |
| SPELL_AURA_MOD_TOTAL_THREAT (103) | Fade: on a living player with a living caster the value is folded once into every list entry and taken out on removal | SpellAuras.cpp:3920-3937, HostileRefManager.cpp:39-55 |
| SPELL_EFFECT_MODIFY_THREAT_PERCENT (125) | The caster's entry changes by the value percent (below -100 removes) | SpellEffects.cpp:5638-5646 |
| EventAI 13 THREAT_SINGLE, 14 THREAT_ALL_PCT | Direct add or percent on the chosen target; percent on every entry | mangos-classic CreatureEventAI.cpp ProcessAction |

Immunity: unit immunity auras (EffectImmunity ATTACK_ME, StateImmunity MOD_TAUNT) stop taunts through the existing immunity rules
(vmangos applies exactly these two for CREATURE_IMMUNITY_TAUNT, Creature.cpp:444-448). The static creature taunt flag is not
read: `ICreatureImmunityProvider` carries mechanic and school masks only, so a creature template with that flag still
gets taunted until the creature data lane exposes it.

Limits: SMSG_CAST_RESULT with DONT_REPORT for "already attacking you" is not sent; the taunted creature is not turned to face
the taunter; after the taunt fades the victim is re-selected at the creature's next update (vmangos selects at once).

### threat pipeline (Combat/Threat/ThreatCalc.cs, Combat/MapCombat.Threat.cs, Spells/SpellSystem.Threat.cs, World/Combat/ThreatFeature.cs)

One formula for all threat, `ThreatCalc.Calc` (vmangos `ThreatCalcHelper::CalcThreat`, ThreatManager.cpp:35-52): no threat stays none; with a threat
spell the caster's SPELLMOD_THREAT talents apply (`SpellSystem.SpellModifiers`, identity until the spell-modifier lane installs the
storage) and a critical hit multiplies by the caster's MOD_CRITICAL_THREAT auras for the spell's school; then the caster's MOD_THREAT
multiplier for the first school of the mask (`Unit::ApplyTotalThreatModifier`, Unit.cpp:7409-7420; players only,
`Aura::HandleModThreat`, SpellAuras.cpp:3914; the two Naxxramas auras 26400/28862 add per-level threat, :3897-3912).

| Source | Behaviour | Reference |
|---|---|---|
| Melee swing | damage x physical MOD_THREAT | Unit.cpp:866-870 |
| Spell damage and DoT ticks | damage x spell_threat multiplier, the spell's school, crit flag from direct hits; NO_HARMFUL_THREAT adds nothing; EX_NO_THREAT only raises an existing entry | Unit.cpp:866-870, :7426, ThreatManager.cpp:424 |
| SPELL_EFFECT_THREAT | through the same formula; players only get MOD_THREAT (a deviation from the old code, which scaled any caster) | SpellEffects.cpp:3503-3514 |
| Heal | 0.5 x effective heal (paladin direct heal 0.25) x spell_threat multiplier, divided by the number of lists holding the target, assist threat per list (zero while that creature is confused or fleeing); NO_HELPFUL_THREAT adds none | Spell.cpp:1362-1366, HostileRefManager.cpp:62-76 |
| Heal over time | 0.5 for every class | SpellAuras.cpp:6013 |
| Flat spell_threat | once per hit target; harmful spells to the target's list, positive spells spread like healing; skipped when every selected effect is inverted | Spell.cpp:5172-5230 |
| Harmless hostile hit (debuff, CC, dispel) | both sides in combat, AttackedBy, zero-threat entry; not for triggered casts, EX_NO_THREAT, NO_INITIAL_THREAT, MOD_POSSESS | Spell.cpp:1649-1679 |
| Positive spell on an in-combat target | caster in combat and a zero-threat entry on every list holding the target | Spell.cpp:1713-1720 |

The spell_threat table is the `ISpellThreatCatalog` seam (`MapCombat.SpellThreatCatalog`); the data side is below.

`MapCombatDamageSink` (Game/Spells) is the production sink the world daemon uses (`WorldSpellDamageSink` derives from it), so tests exercise
it directly.

Limits: spell crit threat is applied for direct spell hits only (damage over time never crits in vanilla); stealth/visibility checks before a
hostile hit starts combat, the Pickpocket back-attack and the refusal of flat threat for spells that are partly positive are not modelled; a
triggered cast stands in for "triggered by an aura"; druid bear-form and talent threat modifiers flow once the forms and talent lanes apply
their auras and spell modifiers.

### evade fidelity (Creatures/CreatureMapSystem.Evade.cs, Creatures/AI/CreatureAuraReset.cs)

`EnterEvadeMode` follows vmangos `CreatureAI::EnterEvadeMode` (AI/CreatureAI.cpp:323-346):

- No instant heal. Health and mana are left alone; once out of combat the creature regenerates a third of its maximum per 5 s tick
  (`Creature::RegenerateAll`, Objects/Creature.cpp:1087-1161; map combat already does this). The old instant snap is the
  development switch `Creatures:EvadeRestoresFullHealth` (default false). The earlier code and two test comments cited vmangos for the
  snap; the citation was wrong.
- `Creature::RemoveAurasAtReset` (:3611-3630) through the optional `ICreatureAuraReset` of the spell caster: every aura goes except a
  non-permanent positive one cast by a player; KEEP_POSITIVE_AURAS_ON_EVADE removes only the negative ones (`Creatures:EvadeResetsAuras`,
  default true). The per-spell "not removed on evade" custom flag of vmangos' spell_template is data this repository does not have.
- A charmed creature keeps its auras and does not run home.
- `CreatureMapSystem.Evaded` is raised once per evade (not for a dead creature, not for an evade already running).

Not delivered: combo points other players hold on the creature are not cleared (no evade event reaches the combo service yet; the new
event is the hook), a creature's pets and totems are not sent home (creatures have no controlled-unit links), the loot recipient is
not cleared (no tapping primitive in Game).

### spell_threat data (World schema step, importer, table, reload)

| Piece | What it does | Where |
|---|---|---|
| World schema step | one table `spell_threat` (entry, threat, multiplier, ap_bonus, inverse_effect_mask, build_min, build_max); `SpellThreatDataModule.Version` = **21, provisional**: other wave-4 lanes claim the same number, the integrator renumbers | `Data/World/Threat/SpellThreatDataModule.cs` |
| Importer | `SpellThreatDumpImporter.Parse` reads a mysqldump file by column name (case-insensitive: dumps mix `Threat` and `threat`), cmangos/classic-db dialect (`entry, Threat, multiplier, ap_bonus`) and vmangos dialect (`entry, threat, multiplier, inverse_effect_mask, build_min, build_max`, only rows whose range holds build 5875 are kept, SpellMgr.cpp:839); `ImportAsync` replaces the table in one transaction. Malformed input, a threat outside 0..65535, a non-zero `ap_bonus` (nothing applies it) and two rows for one spell are errors | `Data/World/Threat/SpellThreatDumpImporter.cs` |
| Content and rank fill | `SpellThreatContent.Resolve` ports vmangos' `SpellRankHelper`/`DoSpellThreat` (SpellMgr.cpp:127-182, :770-827): unknown spells dropped, a custom rank needs its own threat, higher ranks inherit the first rank, redundant and orphaned custom ranks reported | `Kernel/WorldData/Threat/SpellThreatContent.cs` |
| Live table | `SpellThreatTable` (the `ISpellThreatCatalog`), rows swapped by `Replace`; ranks resolved at the first lookup after a swap so the skill content and spell store may finish loading later | `Game/Combat/Threat/SpellThreatTable.cs` |
| Feature and reload | `SpellThreatFeature` loads the table at startup when a store is registered; `.reload spell_threats` (the vmangos name) swaps it (an empty table empties it, vmangos clears first, unless `HotReload:EmptyTables = KeepLoaded`) | `World/Combat/SpellThreatFeature.cs`, `World/Reload/SpellThreatReloadable.cs` |

Data provenance. classic-db z2815 has 103 `spell_threat` rows (flat threat 0..600, one multiplier other than 1: entry 8092 = 2, `ap_bonus` zero everywhere); vmangos'
own migrations add 313 further entries (267 with a multiplier other than 1, most of them 0, several limited to build 5875) and use the
`inverse_effect_mask` column. **The retail-accurate table is therefore not in classic-db**: for fidelity the operator must import a vmangos
world database. No dump is bundled and none was available while this was written, so the vmangos dialect is built from the loader's
SELECT, not from a real vmangos dump.

Import is a library call, like the world-state importer (`SpellThreatDumpImporter.Parse` + `ImportAsync`); it is not wired into the
unified `arcane-content-importer` CLI (the table has no entry in `ContentTableSpecs` and no drift test).

Provider coverage. The schema and store tests are theories over `TestDatabases.AvailableProviders`; on this machine only SQLite ran. MariaDB DDL
is not transactional and commits implicitly, so the step is only re-runnable (the upgrade test drops the table and runs the step twice);
PostgreSQL folds unquoted identifiers to lower case, so EF quotes every identifier and the importer matches headers case-insensitively in
code, never in SQL. The MariaDB and PostgreSQL cases have not been run.

### AI selection (Creatures/AI/CreatureAiServices.cs `CreatureAiFactory.Create`)

A creature with no `AIName` and `creature_ai_scripts` rows for its entry (or its spawn guid: a negative `creature_id`) now runs EventAI
(`Creatures:ImplicitEventAi`, default true); an explicit `AIName` always wins, and a summoned pet, guardian or totem never gets it. This is a
**data-dialect bridge, not the vmangos rule**: vmangos selects EventAI only for `ai_name = 'EventAI'` (AI/CreatureAISelector.cpp:37-100, AI/EventAI/CreatureEventAI.cpp:51-56),
while mangos-classic selects it for every creature that is not a pet, totem or guard (CreatureEventAI::Permissible, AI/EventAI/CreatureEventAI.cpp:51-63).
The classic-db dump this server imports has no `AIName` column at all (79 `creature_template` columns), yet 4,325 of its 10,384 templates have
`creature_ai_scripts` rows (1,284 of them the "flee at 15%" script); without the bridge those rows never run. Counts: python over the z2815 dump,
not verified by a run of this server. Switch it off to get vmangos' AIName-only selection.

Limits: the vmangos selector's other branches (GuardAI for guards, CritterAI for critters, GuardEventAI/PetEventAI, the permit contest, PetAI/TotemAI by owner)
are not delivered; pets and totems get theirs from the pets and totems areas. Unsupported EventAI events and actions in the newly attached rows are
reported once per entry (`Creatures:EventAi:ReportUnsupported`) and skipped, so scripts that need a missing primitive are partly inert.

### stealth and alert (Creatures/CreatureMapSystem.Aggro.cs, .Host.cs, CreatureAlert.cs)

The line promised in docs/integration/rogue-creature-stealth.md is applied:

- `CanAggroOnSight` asks `StealthServices.CanCreatureSee` (when the map has the stealth services): a stealthed player the creature cannot detect
  is not attacked (vmangos Unit::CanDetectStealthOf, Objects/Unit.cpp:6543-6616; sniffed: a level 4 creature notices a level 1 rogue inside
  3.3 yd).
- `CallAiMoveInLineOfSight` is vmangos `CallAIMoveLOS` (Maps/GridNotifiersImpl.h:57-69): a visible unit gets `MoveInLineOfSight`; a stealthed player the creature
  cannot see but whose stealth it nearly breaks (the 5 yd alert band) gets `CreatureAI.OnMoveInStealth`.
- The alert (`CanTriggerAlert` / `TriggerAlert`, AI/CreatureAI.cpp:349-385): a creature that is alive, not in combat, not stunned, confused or
  fleeing, not a civilian, not passive, with a hostile target in line of sight and no alert in the last 10 s sends SMSG_AI_REACTION (alert, wow_messages
  smsg_ai_reaction.wowm), stops and turns to the player. Options: `Creatures:StealthAlertEnabled` (default true), `Creatures:StealthAlertCooldownMs` (10000).

Limits: the 5 s MoveDistract that follows the alert needs a movement generator this server does not have (the creature carries on moving); the turn is
the orientation field, no facing spline packet is sent; the alert comes only from the relocation-driven scan (`Poll` mode calls `MoveInLineOfSight` directly and has no
stealth awareness); detect-range auras and creature-versus-creature detection are not modelled; the Vanish 1 s window belongs to the rogue lane.
