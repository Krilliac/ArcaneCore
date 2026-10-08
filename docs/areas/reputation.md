# Reputation and factions (wave 4, lane `reputation-factions`)

Branch `claude/vw5-reputation-factions`, based on `claude/vw4-integration` at `7313b9e`. This page is the area doc of the lane: what
the lane delivered on top of the round-2 core, every deviation from retail with its switch, the limits, and the real-client checklist.
The round-2 core (catalog, per-player state, packets, storage, write durability) is documented in
[../integration/reputation.md](../integration/reputation.md); where that page and this one disagree, this one is current.

Retail 1.12.1 is the target for mechanics and data. References are read-only: `D:\refs\vmangos` (primary, HEAD `0e3ff01`; line numbers
below are for that checkout), `D:\refs\mangos-classic`, `D:\refs\wow_messages`, `D:\refs\classic-db` (Full_DB z2815), no code or data copied.

## Delivered

| Slice | What changed | vmangos reference |
| --- | --- | --- |
| core parity | `rand_dither` is `copysign(floor(abs(v) + roll), v)` (the roll is added, not compared); forced peace compares the relative standing; en-US rank names with prefix parsing | `shared/Utilities/Random.cpp:80-83`; `ReputationMgr.cpp:334-336`; `Language.h` 318-325 |
| price rounding | every discounted amount is `uint32(price * discount + 0.5f)` in single precision, per amount and per taxi leg (vendor list and buy, trainer list and buy, repair, taxi); the old code floored and ceiled the taxi sum | `ItemHandler.cpp:763`; `NPCHandler.cpp:114,309`; `Player.cpp:4955,17977,17997,18445` |
| item requirements | `ReputationItemRequirements` answers `IItemRequirements.ReputationRank` from the live standing (before this 322 of 326 classic-db items with a reputation requirement could never be equipped); stacks over the skills and stats decorators; installed at login when a catalog is loaded | `Player.cpp:10045` |
| spillover and reward rates | `ReputationMgr::SetReputation` spillover (templates first, rank checked before the change, `(int)(standing * rate)`, no packet of their own, absolute value on non-incremental sets, none for the team award or `RewRepSpilloverMask`); `reputation_reward_rate` scales kill, quest and spell gains and zero disables a source | `ReputationMgr.cpp:211-243`; `Player.cpp:6325-6350`; `ObjectMgr.cpp:8827-9070` |
| world tables | `reputation_spillover_template` and `reputation_reward_rate` as one world schema step (22 after wave-4 integration), two `CreateTableChange`, no inline data | `ObjectMgr.cpp:8827-9070`; classic-db column names |
| combat reactions | the `GetReactionTo` / `GetFactionReactionTo` / `IsValidAttackTarget` ladder as `ReputationReactionResolver`, `ReputationCombatHooks` and `ReputationCreatureHostility`: Hated players are attacked by their former friends, reputation factions are not attackable unless at war, contested guards attack contested players, GM neutral, forced ranks, duels, same raid, owner-controlled units | `Object.cpp:3608-3816` |
| forced reactions | in-memory forced-rank state on `PlayerReputation` (never persisted, as vmangos), consulted by every reaction path | `ReputationMgr.cpp:99-131` |
| kill credit | the first-damage tapper and the group of the tap (else the tapper's current group; the killer stands in when nobody tapped or the tapper is not on the map), whoever lands the killing blow: every member at reward distance, alive or dead, even with a dead killer, then a tapper who left the group if he is at reward distance; pet and player-controlled victims give nothing (`ReputationKillCredit.AwardKill`) | `Unit.cpp:978-1000, 1094-1097`; `Group.cpp:2295-2409`; `Player.cpp:19959-19980, 6355-6365` |
| quest objectives | a changed standing (main faction and every spillover target) completes or reverts reputation-objective quests | `Player.cpp:14239-14264` |
| GM commands | `.modify rep` (level 4), `.lookup faction` and `.character reputation` (level 2) with retail syntax and texts | `CharacterCommands.cpp:1955-1969, 4330-4422`; `LookupCommands.cpp:1384-1478` |
| diagnostics and reload | one ACTIVE/INACTIVE startup line, kill rows with a missing faction skipped once at load, quest reputation columns validated, `.reload` of the three tables | `ObjectMgr.cpp:8935-8957, 5702-5750, 6026-6039`; `Chat.cpp:826, 886-887` |
| spell handlers | `SPELL_EFFECT_REPUTATION`, `SPELL_AURA_FORCE_REACTION`, the gain auras (156 Diplomacy, 190) feeding `ReputationService.GainModifier` | `SpellEffects.cpp:5307-5323`; `SpellAuras.cpp:2785-2805`; `Player.cpp:6258-6262` |

## Configuration (`Reputation:*`)

| Key | Default | Meaning |
| --- | --- | --- |
| `FactionDbcPath` | unset | build-5875 `Faction.dbc`; unset means the whole feature is inert (the startup line says so) |
| `RateGain`, `RateLowLevelKill` | 1, 0.2 | `Rate.Reputation.Gain`, `Rate.Reputation.LowLevel.Kill` |
| `PeaceForcedUsesEffectiveStanding` | false (retail) | true compares the effective rank, base included, in the forced-peace war exception |
| `SpilloverEnabled` | true (retail) | false switches every spillover off |
| `CombatReactions` | true (retail) | false keeps the template-only combat and aggro rules; needs `Faction.dbc` and the FactionTemplate catalog (`Creatures:FactionTemplateDbcPath`) |
| `SendForcedReactions` | false | send SMSG_SET_FORCED_REACTIONS; off until a real client confirms the wire width (see below) |

A half-configured world (Faction.dbc without the FactionTemplate catalog, or the reverse) used to run silently without reputation
combat. `ReputationFeature` and `ReputationCombatFeature` now each log one line that says ACTIVE or INACTIVE and why.

## Deviations from retail

| Deviation | Why | Switch |
| --- | --- | --- |
| client packets must be exactly 5 bytes (war, inactive) or 4 (watched) with slot < 64; stored standing is clamped on load; an invalid watched slot loads as -1 and `CMSG_SET_WATCHED_FACTION` is validated | hardening against input a retail client never sends; unobservable to a retail client | none |
| the reaction ladder returns "cannot resolve" for a reputation faction whose player state is not loaded, and the hooks then use the template-only answer | the standings load on the session task before the player enters the world; never more permissive than before | none |
| free-for-all PvP reactions are not modelled | no FFA state exists | none |
| `SMSG_SET_FORCED_REACTIONS` is built but not sent | vmangos writes (u32 faction, u32 rank); wow_messages types the faction as a u16 | `SendForcedReactions` |
| the reputation templates reload as one pair: either `.reload` name refreshes both tables | one immutable `ReputationContent` swapped whole | none |
| the kill-credit exclusion of "units a player controls" covers pets, totems, charmed units (through their charmer GUID) and `IPlayerControlledUnit` | charm and possession exist since the unit-control lane (`docs/areas/unit-control.md`) | none |
| a player-tapped creature killed by an NPC (a guard, an escort) still credits the tapper and the group of the tap, whatever share of the damage players did | vmangos credits the tap only when `Creature::IsLootAllowedDueToDamageOrigin` holds (`Unit.cpp:988`, `Creature.h:548-554`: players dealt more than 35% of the damage, or the creature has `CORPSE_RAID`), otherwise the killer; the damage-origin counters are not kept here (review finding 114, `ReputationKillCredit`) | none |

## Limits (not delivered, recorded)

- **Temporary at-war on creature aggro** and its clearing on combat end (`Creature.cpp:3677-3686`, `Unit.cpp:6102-6112`,
  `Player.cpp:22737-22749`): the base has no creature enter-combat hook or player combat-end event. Seam to design when the threat lane lands:
  `IReputationCombatObserver { OnCreatureEnteredCombat(Creature, Player); OnPlayerLeftCombat(Player); }`.
- **Selecting a unit makes its faction visible**, and `SMSG_SET_FACTION_ATWAR` (`MiscHandler.cpp:398-410`, `Player.cpp:17067-17075`): not built;
  `SMSG_SET_FACTION_ATWAR` exists only in vmangos (wow_messages has no such message), so it needs a real-client check first.
- **Honor-rank price discounts** and the taxi variant (`Player.cpp:19470-19510`): need the honor lane. `ReputationPricing.Round` is the
  rounding they will use.
- **Dump importer wiring** for the two new tables: `ContentImporterCli`, the table specs and a `ReputationTemplatesDumpImporter` are not part of
  this lane; the world tables fill from SQL for now. classic-db has 12 spillover rows and 3 reward-rate rows (all 1/1/1).
- **Alterac Valley** Frostwolf/Stormpike reputation for killing enemy players (`Group.cpp:2305-2320`, battleground lane).
- **Stop-attack on a forced Friendly rank** handles the player's own victim only; the attacker set, pets and the threat references of the
  faction (`Unit.cpp:10006-10029`) belong to the threat lane.
- **Per-quest `RewRepSpilloverMask`** exists as `QuestReputationReward.NoSpillover` but nothing feeds it (the column is vmangos-only,
  absent from classic-db and `QuestTemplate`).
- Localised names: the GM commands print en-US only.
- Forced reactions are in memory; they return when the aura is reapplied on login, which needs the player's standings to be tracked before the
  spell state restores (not proven on a real login).
- The faction and FactionTemplate DBCs remain operator-supplied client files (`classic-db` has no `faction` tables).

## Persistence and CI

No characters schema change anywhere in this lane. The only schema change is the world step `ReputationTemplatesWorldModule` (`Version = 22`
at the base; the integrator renumbers the one constant, every test refers to it). MariaDB DDL is not transactional and implicitly commits,
so the step is exactly one `CreateTableChange` per table; PostgreSQL has no unsigned types, so ids and rates round-trip through the EF
mapping. `ReputationTemplatesStoreTests` are provider theories over `TestDatabases.AvailableProviders` (round trip, duplicate keys,
re-ensuring, an interrupted step). On this machine only SQLite ran; MariaDB and PostgreSQL run on hosted CI.

## Real-client acceptance checklist (Nathan clicks, Claude reads logs and rows)

1. Open the reputation pane: factions and scrolling; toggle at war and relog (persists).
2. Kill a Booty Bay pirate: Booty Bay rises and spills to Gadgetzan, Ratchet and Everlook.
3. Buy from a vendor at Honored: the price is rounded half up.
4. Equip an Argent Dawn or Timbermaw item at the right rank, and fail one rank below.
5. Drop to Hated with Stormwind: guards attack; return to Neutral: they stop.
6. With `Reputation:SendForcedReactions=true`, drink a Furbolg form: Timbermaw turns friendly. Record the byte width the client accepts
   for SMSG_SET_FORCED_REACTIONS and for the list slots of SMSG_SET_FACTION_STANDING / VISIBLE / CMSG_SET_FACTION_ATWAR (u16 or u32).
7. `.modify rep 21 honored 100`, `.lookup faction booty`, `.character reputation`.

## Proof

New and changed tests, all deterministic (an injected roll, fixture catalogs, world conditions awaited with deadlines):
`ReputationParityTests`, `ReputationPricingTests`, the vendor, trainer and taxi tests, `ReputationItemRequirementsTests` and
`ReputationItemsWorldTests`, `ReputationSpilloverTests`, `ReputationSpilloverWorldTests`, `ReputationTemplatesStoreTests`,
`ReputationCombatHooksTests`, `ReputationCombatWorldTests`, `ReputationKillCreditTests`, `ReputationQuestObjectiveTests`,
`ReputationQuestObjectiveWorldTests`, `ReputationGmCommandTests`, `ReputationContentValidatorTests`, `ReputationReloadTests`,
`ReputationSpellEffectTests`, `ReputationSpellWorldTests`. `CreatureWorldTests.Feature_WiresAiServices_AndSpawnedCreaturesGetAnAi` now expects
the reputation combat feature as the world's `ICreatureHostility` (`ICreatureHostility` became a feature seam interface; with no Faction.dbc it
answers exactly like the template-only default).

## Integration notes

- `ReputationSpellHandlers` guards every registration with `HasEffectHandler` / `HasAuraHandler`; its type name sorts after the built-in aura and effect
  modules. If the aura-engine lane registers aura 139, 156 or 190 or effect 103, either order works.
- `CombatHooks.Register` is last-writer-wins: `ReputationCombatFeature` installs only over `FactionCombatHooks` and warns otherwise.
- `.character` is defined as a root by `ReputationCharacterCommands`; if another lane adds a `character` root as an `ICommandGroup` the
  command table throws at startup and one of the two must become an `ICommandExtension`.
- `WorldFeatures.SeamInterfaces` gained `ICreatureHostility`.
