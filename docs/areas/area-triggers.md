# Area triggers: entry requirements and exploration quests

Lane `L6-area-trigger-entry-and-exploration`. Two features share the area-trigger data path: what a player must have to be teleported
through an `areatrigger_teleport` row (attunement keys, quests, level, conditions), and the `areatrigger_involvedrelation` table that
credits exploration quests. The geometry check (`AreaTriggerZone`, the 5 yard client delta), the ghost entry rules
(`GhostEntryRules`) and the teleport itself (`TeleportService`) are older and unchanged.

Evidence: the reference cores are read from `/home/user/mangosserver` (mangos-zero `PlayerAreaTrigger.cpp`, `MiscHandler.cpp`
`HandleAreaTriggerOpcode`, `ObjectMgrAreaTrigger.cpp`, `ObjectMgr.cpp` `LoadQuestAreaTriggers`, `Language.h`). Anything about the 1.12.1
client that is not in the code or the references is marked UNVERIFIED below.

## What is implemented

### Entry requirements (`AreaTriggerRequirements`, `IAreaTriggerGate`)

`CMSG_AREATRIGGER` still runs: zone check, listeners (quest exploration), the ghost rules, and then, new, the requirements of the
destination row, evaluated by `Game/Teleport/AreaTriggerRequirements.Evaluate` on the world thread:

1. A game master (`.gm on`) passes everything (mangos-zero `GetAreaTriggerLockStatus`: "Gamemaster can always enter"). A GM account
   that has not switched GM mode on is checked like anybody else.
2. Level: `required_level`. Refusal text `You must be at least level N to enter.` (mangos_string 49).
3. Items: `required_item` or `required_item2` when both are set; either carried key passes. With only one set, that key is required.
   Bags, backpack and keyring count; the bank does not (`Player::HasItemCount` with its default). If neither alternative is held,
   the first is named in the refusal text `You must have item <name> to enter.` (mangos-classic
   `Entities/Player.cpp`, `Player::GetAreaTriggerLockStatus`; mangos_string 50, `LANG_REQUIRED_ITEM`).
   An item without a loaded template is named by its entry number instead of crashing (the reference dereferences the template).
4. Every `IAreaTriggerGate` the world features register. `QuestNpcFeature` is one: `required_quest_done` must be a quest the
   player has turned in (`QuestNpcServices.IsRewarded`: a repeatable quest never counts, a player whose journal is not loaded is
   refused). `InstanceFeature` is another: it asks the instance script of the map the player stands in
   (`InstanceData.BlocksAreaTriggerTeleport`, the ScriptDev2 AreaTrigger script that returns true to stop the database teleport;
   Naxxramas holds trigger 4156 until its four wings are cleared, mangos-classic `naxxramas.cpp` `DoHandleAreaTrigger`). The first
   refusal wins.
5. `condition_id` / `required_condition`: a row of the conditions table, evaluated by the world's `IConditionEvaluator`
   (`ConditionFeature`, with `source = null`). No evaluator, an unknown id or a failing condition refuse (fail closed).

Message rule: when the row's `message` (classic-db `status_failed_text`) is not empty it replaces the text of **every** refusal (the
reference's `failed_text_mangos_string_id` wins over the lock-status text). Otherwise level and item refusals use the stock texts, and
a refusal by a gate or the condition table is **silent** unless the gate supplies a text. The reference is silent for an unfinished
quest ("ToDo: SendAreaTriggerMessage") and for unknown errors, and so is this.

`IAreaTriggerGate` (`Game/Teleport/AreaTriggerRequirements.cs`) is the seam for vetoes this assembly has no state for. It is
discovered among the `IWorldFeature`s like `IAreaTriggerListener`, so a later feature (instance lockouts, raid-group rules) adds a gate
without editing the handler. `Check` runs once per teleport attempt (the client sends `CMSG_AREATRIGGER` on entering a volume, not per
tick) and returns a struct verdict, so a passing check allocates nothing; the handler's per-packet feature discovery is the only
allocation, on a path that runs a few times per minute per player at most.

### Data

* `AreaTriggerTeleport` (Kernel `WorldData`) gains `RequiredItem`, `RequiredItem2`, `RequiredQuestDone`, `RequiredCondition`
  (default 0 = none; the existing positional constructors keep working).
* `areatrigger_teleport` rows gain the same four columns (`AreaTriggerTeleportRow`); `EfMapDataStore` loads them, so
  `.reload areatrigger_teleport` (existing `AreaTriggerTeleportContentReloadable`) swaps the requirements together with the destinations.
* New table `areatrigger_involvedrelation` (`id` = trigger, `quest`), entity `AreaTriggerQuestRow`.
* World schema step **29**, `AreaTriggerQuestWorldModule` (`Data/Quests`): creates the relation table and adds the four columns to a
  database created before the step; a fresh database already has the columns (created from the current model) and the step is
  skipped column by column. The version constant is the one place to renumber when other world steps merge first.

### Importer

`LocationDumpImporter` (the importer of `areatrigger_teleport`) now also:

* reads `required_item`, `required_item2`, `required_quest_done` by name, and maps both condition spellings to `RequiredCondition`:
  classic-db `condition_id` and vmangos `required_condition`;
* reads `areatrigger_involvedrelation` (`id`, `quest`), dropping rows whose `patch_min`/`patch_max` do not cover patch 10 (the rule of the
  other quest relations) and collapsing duplicates; `ContentTableSpecs` has the table spec, the CLI reports and verifies the count
  (`areatrigger_involvedrelation`). `--replace` empties the table first, without it an existing key fails the write and nothing changes.

The importer CLI no longer lists `required_item` and friends as unmapped.

### Exploration quests from the table

`QuestContent.AreaTriggerQuests` carries the rows; `QuestStore` (immutable, `FrozenDictionary`) indexes them by trigger
(`AreaTriggerQuestsOf`) and by quest (`HasAreaTrigger`) and drops a row whose quest is not loaded (vmangos `LoadQuestAreaTriggers`
skips it). `QuestNpcServices.AreaTriggerReached` credits every quest the trigger is related to that the player has incomplete and the
server supports; `HasAreaTrigger` is what makes an exploration quest "supported" (an exploration quest with no relation anywhere stays
unavailable, as before). Nothing is read from the database on the world thread: the store is built at startup and by
`.reload quest_template` (`QuestContentReloadable` already reads the whole `QuestContent`, so the relation table reloads with the
quests, as vmangos `all_quest` does).

**Configuration.** `Quests:AreaTriggerQuests` (the earlier source of these rows) is kept as an additive override for a world without
the table; a quest named by both is credited once. There is no new option.

## Tests (no game client)

* `Game.Tests/Teleport/TeleportRequirementsTests`: level, items (bag, either alternative item, unknown template), message override, GM bypass, gates
  (silent, text, order, first refusal), condition (no evaluator, false, true, id 0), check order.
* `Game.Tests/Npc/QuestExplorationTests`: store indexes, credit from the table without configuration, withheld without a relation,
  table plus configuration credits once, several quests per trigger, dead player and trigger 0, store swap by a reload.
* `World.Tests/Teleport/TeleportRequirementWorldTests`: over real sessions, the attunement trigger refuses without the key (message,
  no transfer) and teleports with it, the row message, GM mode, the quest-done gate (refused silently, then allowed once the quest is
  turned in; a logged but not turned-in quest does not count), a condition requirement refuses.
* `World.Tests/Npc/QuestExplorationWorldTests`: the imported relation completes the logged exploration quest (SMSG_QUESTUPDATE_COMPLETE,
  journal flag) with `Quests:AreaTriggerQuests` empty; a trigger without a relation credits nothing.
* `Data.Tests/QuestExplorationDataTests`, `IntegratedSchemaTests` (version tuple and the upgrade of a pre-step database, all three
  providers), `ContentImporterCliTests` (plan, import, verify of the relation table; the requirement columns are mapped).

## Known gaps and UNVERIFIED

* UNVERIFIED: the exact retail 1.12.1 wording of the attunement message. `You must have item %s to enter.` is the stock mangos string
  (`LANG_REQUIRED_ITEM`); retail-looking texts ("You must have the Drakefire Amulet...") are per-row `message` data, which wins.
* UNVERIFIED: the column names `required_item`, `required_item2`, `required_quest_done` and `required_condition` against a real
  vmangos 1.12 dump. They are the names the existing importer documentation and the UDB/classic-db layouts in the references use;
  no real dump is in the repository. The vmangos `required_condition` is read into ArcaneCore's cmangos-style conditions table: a
  vmangos world that uses vmangos' own `conditions` semantics needs its conditions imported through the condition importer first.
* The reference's other lock statuses are not implemented: the raid-group requirement for raid maps, instance-full, encounter in
  progress and permanent instance binds (those belong to the instance lane). Heroic keys and the PvP-rank lock do not exist in 1.12.
* vmangos `LoadQuestAreaTriggers` adds the exploration flag to a quest that lacks it ("quest modified to require objective"); the
  immutable quest here does not change its flags. A relation for a quest without `SpecialFlags` exploration credits nothing visible.
* `docs/areas/hot-reload.md` still says the quest area triggers have no store (lines about `areatrigger_involvedrelation`): they now
  reload with `.reload quest_template`. The orchestrator owns that page's regeneration.
* A trigger id that is not in `areatrigger_template` never reaches the listeners, so a relation row for it is stored and credits nothing
  (the handler's zone check needs the template).
