# Shared rule engine: design

Status: approved by the project owner 2026-10-28 ("integrate all the modifications"). Built in phases P0-P6.

## 1. Intent

ArcaneCore keeps growing new behaviour: bot goals for the auction house, holiday scripts, outdoor PvP, creature scripts imported from three dump formats. Each one is either hard-coded C# or has its own small table format with its own loader, validator and executor. The goal is one shared, database-driven rule engine, so extending behaviour is an SQL update (or, for a new primitive, one small C# class) instead of a code change in a dozen places.

Owner decisions (project owner, 2026-10-28):

- **One shared engine** for every system, not one per system.
- **Anyone with DB access extends it** by inserting rows. Rows ship as idempotent per-provider SQL update files (the existing `sql/content` rules).
- **New, uniform format.** EventAI, SmartAI and the DB-script tables are deprecated and then removed as runtime formats. Imported content is converted into the new format.
- **Bots and creature AI first.**
- Bot goals keep their ids/enums and can be extended with rules that apply to a chosen share of bots.

## 2. Goals, non-goals, anti-goals

Goals

1. One rule format, one loader, one validator, one reload path, one set of GM tools, shared by every host system.
2. Every rule-like behaviour in the inventory (section 9) can be expressed as rows, or is explicitly classed as plain data and moved to a plain table.
3. Imported vanilla content (cmangos/vmangos EventAI, DB scripts, AzerothCore SmartAI) converts losslessly, proven by a parity harness, before any old executor is deleted.
4. Bad rows are always reported by id with a reason. Nothing is ignored silently.
5. No new work on the world thread. Rules run where their host already runs (map threads, bot think budget) and are precompiled at load.

Non-goals (v1)

- A general scripting language (Lua, C# in rows). Primitives stay compiled C#.
- Changing retail behaviour during migration. Conversion is parity first; fixes come after, in their own PRs.
- Replacing the boss/instance C# scripts (`ScriptName`). Those stay code, and they may call into rules.

Anti-goals: things that count as failure even if they "work"

- An imported creature behaving differently after conversion without a recorded reason.
- A row that loads, does nothing and says nothing.
- A tick-rate regression on live (baseline 14.85 Hz) caused by rule evaluation.
- Two systems growing their own parallel rule tables again.

## 3. Concepts

A **rule** says: when *event* fires for a *subject*, and the *subject filter* matches, and the *chance* roll passes, and the *conditions* hold, and the *cooldown* allows, run an ordered list of *actions* on *targets*.

- **Host.** A system that owns subjects and fires events: `bot`, `creature`, `gameobject`, `areatrigger`, `world_event`, `map`, `economy`. A host contributes its own event types and may contribute host-only actions. Hosts are C# (one class each) and are registered at startup.
- **Owner.** What a rule is attached to: `(owner_kind, owner_id)`. Examples: creature entry, creature spawn guid, gameobject entry, area trigger id, game event id, bot profile id, or `global` (owner_id 0) for "all subjects of this host".
- **Event.** A typed trigger with up to four integer parameters, such as `creature.health_below(pct)`, `creature.timer(min, max, repeat_min, repeat_max)`, `bot.goal_evaluate`, or `bot.gold_below(copper)`.
- **Subject filter.** An optional row that narrows which subjects a global rule applies to (class/race/level/team/zone masks, bot-only), plus a percentage share. See 5.4.
- **Condition.** The existing `conditions` table and `ConditionEvaluator`, extended. This is already the one cross-system predicate engine (EventAI, DB scripts, SmartAI, gossip, vendors, quests, area triggers, spawn groups and bot group runs use it), so the rule engine reuses it rather than inventing a second one.
- **Action.** A typed effect with up to six integer parameters and a target, such as `cast(spell, flags)`, `say(text_id)`, `set_phase(n)`, `start_rule_list(id)`, or `bot.propose_goal(goal_id, weight)`.
- **Target.** A typed selector: self, victim, random hostile, nearest creature by entry, the invoker, the owner's group, a stored target slot, and so on.
- **Rule list.** A named, ordered list of actions that can be started by an action (replacing relay scripts, SmartAI timed action lists and DB script chains).

## 4. Tables (world DB)

Names are proposals. Every table gets an `IDataModule` with schema steps and per-provider SQL update files, following `sql/updates/README.md`.

```
rule
  id                int      PK
  host              tinyint  -- bot, creature, gameobject, areatrigger, world_event, map, economy
  owner_kind        tinyint  -- entry, guid, event, profile, global ...
  owner_id          int
  event_type        smallint
  event_param1..4   int
  phase_mask        int      -- positive: bit n = fires in phase n (0..31); 0 = all phases (see "Phase masks" in 6)
  filter_id         int      -- rule_subject_filter.id, 0 = none
  chance_pct        tinyint  -- 0..100, applied per firing unless STABLE_SHARE flag
  condition_id      int      -- conditions.condition_entry, 0 = none
  cooldown_min_ms   int
  cooldown_max_ms   int      -- at most 86 400 000 (24 h, P0 cap; larger is rejected); delay_ms has the same cap
  priority          smallint -- order among rules firing on the same event
  flags             int      -- repeatable, combat-only, out-of-combat-only, debug-trace, stable-share, ...
  enabled           tinyint
  comment           varchar

rule_action
  rule_id           int      -- or rule_list id when list_flag set
  seq               tinyint
  action_type       smallint
  param1..6         int
  target_type       smallint
  target_param1..3  int
  delay_ms          int      -- relative to previous action (rule lists)
  flags             int
  PK (rule_id, seq)

rule_list           -- reusable action lists (relay scripts, timed action lists)
  id, comment       -- actions stored in rule_action with a list flag, or a sibling table

rule_subject_filter
  id                int PK
  class_mask, race_mask    int
  level_min, level_max     tinyint
  team              tinyint
  map_id, zone_id, area_id int  -- 0 = any
  subject_flags     int      -- bots only, players only, elite only ...
  share_pct         tinyint  -- stable share of matching subjects
  share_seed        int
  comment           varchar

rule_text           -- unified creature text (replaces creature_ai_texts and the script texts)
  id, content_default, sound, type, language, emote, broadcast_text_id, comment

playerbot_goal      -- the goal catalogue, keyed by the existing enum value
  goal_id           smallint PK  -- PlayerbotGoalKind value, custom goals in the reserved range
  name              varchar
  base_priority     smallint     -- reproduces today's ladder order
  enabled           tinyint
  driver            smallint     -- which C# goal driver runs it (quest, town, auction, grind ...)
  driver_param1..4  int
  comment
```

**Id ranges** (amended in P2a; applies to `rule` and `rule_text`). Shipped content uses ids below 1 000 000. Rows authored on a server (custom) use 1 000 000 up to 2^30 - 1 (1 073 741 823), so they never clash with shipped updates. Converted content uses ids from 2^30 (1 073 741 824) up, split into one sub-band of 2^26 (67 108 864) ids per converter: EventAI at 2^30, DB scripts at 2^30 + 2^26 (reserved), SmartAI at 2^30 + 2 * 2^26 (reserved). Converted rows get deterministic ids derived from their source (EventAI: 2^30 + the `creature_ai_scripts` id, so source ids reach 2^26 - 1; texts use the same sub-band), so re-running the importer is idempotent, and each converter deletes and rewrites only its own sub-band. The catalog accepts an id from 2^30 up only inside a named sub-band, so an authored or shipped row at 1 500 000 000 is rejected at load; it cannot tell a converted row from an authored one, so a custom row inside a sub-band is accepted and is the operator's to avoid. A source id that does not fit its sub-band is reported as not convertible, never wrapped. This follows the existing reserved custom range for game events (900-999).

## 5. Behaviour

### 5.1 Loading and validation

- One `RuleCatalog` loader reads all rule tables into an immutable, precompiled form: per host, per owner, per event type, an array of compiled rules sorted by priority. There are no per-tick lookups by string and no allocation in the evaluation path.
- The validator checks each row against the registered vocabulary. It checks that event, action and target ids exist for the host; that the parameter count and types match each primitive's declared signature; that referenced spells, texts, entries, conditions, filters and lists exist; and that phase masks and chance ranges are valid.
- It follows the existing pattern: the loader returns `Rejected` entries `(table, id, reason)`, never throws for one bad row, and the feature logs the first 20 plus a summary count. A `.rules check [host] [owner]` GM command lists every rejection.
- Reload: `RuleCatalog` implements `IContentReloadable` (`.reload rules`). Validation failures reject the reload and keep the current catalog. The `conditions` table becomes reloadable too, which it is not today, because rules depend on it.

### 5.2 Vocabulary and extension

- Every event, action and target is a C# class with an attribute giving `(host, kind, id)` and a parameter signature, discovered by reflection like `EventAiRegistry` today. Two classes with the same id fail at startup.
- Shared primitives (cast, say, set phase, set data, start list, move to, despawn, summon, emote, set faction, kill credit, and so on) are host-neutral and work for any host whose subject supports them.
- New primitives can come from a hot-loaded code module (`ModuleHost`). A module may add ids but never override a built-in one (the current EventAI rule).
- The vocabulary is self-describing: `.rules vocab [host]` prints every id, its parameters and its description, and a generated `docs/reference/rules.md` is checked by a docs test like the other generated references.

### 5.3 Execution

- **Creature, gameobject and areatrigger hosts** run on the owning map's thread, at the same points the EventAI and SmartAI executors run today. The engine replaces those executors; it is not an extra layer.
- **Bot host** runs inside the bot's existing think budget. Goal selection becomes:
  1. Fire `bot.goal_evaluate`.
  2. Each matching rule's `bot.propose_goal(goal_id, weight)` actions add candidates.
  3. The built-in ladder is expressed as shipped rules on that event, with weights that reproduce today's order.
  4. The highest weight wins, with ties broken by `playerbot_goal.base_priority`.
  5. The existing policy layer (`RuleBasedPolicy` / `PlayerbotModelPolicy`) keeps working as one more proposal source.
- Guards that are code today, such as bags full, gear broken or resurrection sickness, become **condition types** in a reserved range (section 5.5). The ladder rows reference them, so a server can reorder, disable or add rungs by editing rows.
- Effects that touch other maps or the world go through the existing posting mechanisms. Rule evaluation itself never takes the world lock.
- A per-rule `debug-trace` flag and a `.rules trace <guid>` GM command log each firing, the condition result, the roll and the actions run. This is the tool for authors.

### 5.4 Subject filters and chance

- `chance_pct` on a rule is a fresh roll each time the rule fires, which is the EventAI/SmartAI meaning.
- `share_pct` on a filter (or a rule with the `stable-share` flag) selects a stable share of subjects. The rule applies when `hash(share_seed, rule_id, subject_guid) mod 100 < share_pct`. The same bots stay selected across ticks and restarts, which is what "30% of warriors 20-40 in Horde zones do X" needs. Changing the seed reshuffles the share.

### 5.5 Conditions

- Reuse the `conditions` table and `ConditionEvaluator` unchanged in meaning.
- Add new condition types for host predicates in a reserved range, for example 1000 and up:
  - bot: bag free slots, gear durability, gold, has auctionable items, has a profession, near a capital, current goal, and so on;
  - creature: health, mana, has aura, in phase, and so on.
- Each new type is a C# class like the other primitives. Cmangos condition type ids stay as they are, so imported conditions keep working.
- An undecidable condition fails closed, as today.

## 6. Migration of EventAI, SmartAI and DB scripts

1. **Unified vocabulary first.** Map every supported EventAI event/action/target, SmartAI event/action/target and SCRIPT_COMMAND to a rule primitive. Today that is 34+51+14 EventAI, 23+33+17 SmartAI and 44 script commands (recon, 2026-10-28). The mapping table lives in code and in `docs/reference/rules-conversion.md`.
2. **Converter in the content importer.** `ContentImport` gains a step that reads the imported `creature_ai_scripts`, `creature_ai_texts`, `smart_scripts`, `dbscripts_*`, `dbscript_relay_template` and `script_waypoint` rows and writes `rule*` rows with deterministic ids. Each source row that cannot be converted is reported by id and reason in the import report. The old tables stay in the schema as **import input only** for as long as dumps arrive in those formats; no runtime code reads them after cutover.
3. **Parity harness.** A test suite runs the same scripted scenarios through the old executor and through the engine on converted rows, and compares the effect logs: casts, texts, emotes, phase changes, summons, movement starts, despawns, timers and their order. The corpus is every creature in the test content set plus a sampled slice of the full vmangos/cmangos import. A difference fails the suite unless it has an approved `parity_exception` with a reason.
4. **Cutover per source format:** EventAI first (largest and simplest), then DB scripts and relays, then SmartAI.
   - **Live's world DB** (project owner, 2026-10-28): take a verified backup, then convert in place through a schema update step, applied on a live restart. A backup of auth, characters and world was taken on 2026-10-28 before any of this work.
   - **Deletion** (project owner, 2026-10-28): "When a surface is ready to be deleted, do so. Don't keep dead surfaces." There is no long dual-runtime period and no config switch kept for a release.
     - An old executor, its loader and its config are deleted in the same PR series once the gate parity tier (a) is green and a recorded operator run of tier (b) passes on the current dumps.
     - The old source tables stay only as import input, while dumps still arrive in that format.
5. **Ref-core fixes after parity.** Divergences from retail found during conversion are recorded and fixed in their own PRs afterwards (pre-authorised; retail wins), so a parity diff never hides a behaviour change.

**Phase masks (verified against the references).** The rule `phase_mask` is positive: bit n set means the rule fires in phase n, and 0 means every phase. The two script systems the converters read differ from it:

- EventAI (mangos-classic 8ec338a1704e7dcb1c0213eb7ed58f9231ade40f, `src/game/AI/EventAI/CreatureEventAI.cpp:263-264`) stores an *inverse* mask: an event is skipped when bit `m_Phase` is set, and phase 0 is an ordinary phase. The converter writes `phase_mask = ~event_inverse_phase_mask` (32 bits), and 0 when the inverse mask is 0. An inverse mask of all 32 bits (the event never fires) would invert to 0 (every phase): the converter writes such a row disabled instead.
- SmartAI (TrinityCore 44b718c1d79260d417fbb8dd852586aec2d05f1e, `src/server/game/AI/SmartScripts/SmartScript.cpp:4364-4369`, used at :3181 and :3782) stores a positive mask in which bit n means phase n + 1; a zero mask always passes, and phase 0 matches no non-zero mask. The converter writes `phase_mask = event_phase_mask << 1` (and 0 for 0).

## 7. Phased roadmap

| Phase | Scope | Done when |
|---|---|---|
| P0 Engine core | Tables, modules, SQL updates; `RuleCatalog` loader/validator; primitive registry; evaluator; reload; `conditions` reload; `.rules check/vocab/trace`; generated reference doc | Unit tests for loader, validator, chance/share hashing, priority, cooldown, reload rejection; no host wired yet |
| P1 Bot host | `playerbot_goal`; ladder as shipped rules; bot condition types; `bot.propose_goal` and the other bot actions; AH goals expressed as rules; subject filters | Replay test: on a fixed seed, goal choices match the hard-coded ladder for a population run; a custom row (insert only) adds a goal for a 30% share of a class and is observed |
| P2 Creature host | Unified creature vocabulary; converter for EventAI → rules; parity harness; switch; then DB scripts/relays; then SmartAI (creature source) | Parity suite green on the corpus; live runs on the engine for one release; old executors deleted |
| P3 Gameobject + areatrigger hosts | SmartAI gameobject/areatrigger sources; gameobject/areatrigger events | Parity green; SmartAI runtime deleted |
| P4 World events | `world_event` host: holiday scripts (ElementalInvasion, Fireworks, Lunar Festival), `game_event_playerbot_*` folded in, holiday catalog | Each holiday script replaced by rows with parity tests |
| P5 Economy and tuning data | AH bot value/loot matrices and bot AH pricing move to plain tuning tables (not rules); bot errand thresholds | Config CSV strings removed; values in rows; reload works |
| P6 PvP and misc | Outdoor PvP catalogs, AV upgrade turn-ins, guard posts, profession gossip, BG buffs | Per item, as in section 9 |

## 8. Constraints

- Every schema and content change ships idempotent per-provider SQL update files that install on a clean DB (sqlite, mariadb, postgresql).
- Live stays Debug; the live world DB is not edited by hand. Converted content arrives through the importer and update files.
- Clean-room: the reference cores (`ARCANECORE_REFS_ROOT`) are read for behaviour only and never copied.
- Non-vanilla behaviour that rules make possible ships disabled or config-gated; shipped rule rows reproduce retail.
- Performance: evaluation allocates nothing per call and does no string lookups; the parity harness also records evaluation cost per tick so P2 can show no tick-rate regression.

## 9. Inventory: everything that eventually goes into the system

"Rules" means behaviour expressed as rule rows; "Data" means plain rows in a dedicated table (no events or actions), loaded and validated by the same machinery. File references are from the 2026-10-28 recon at main 215123d4c.

| # | Area | Today | Kind | Phase |
|---|---|---|---|---|
| 1 | Bot goal ladder (16 rungs, inline guards) | `Playerbots/PlayerbotBrain.cs:1243-1403` | Rules | P1 |
| 2 | Bot goal catalogue (`PlayerbotGoalKind`, 18) and policy goals (8) | `Kernel/Characters/ManagedPlayerbot.cs:5`, `Playerbots/Policy/PlayerbotPolicy.cs:10-101` | Data (`playerbot_goal`) | P1 |
| 3 | Bot open-goal probes | `PlayerbotBrain.Policy.cs:68-83` | Rules | P1 |
| 4 | Bot AH behaviour (list chance, listings, budgets, visit interval) | `PlayerbotEconomyOptions.cs:19-50`, `PlayerbotEconomy.cs:53-78` | Rules + Data | P1 |
| 5 | Bot AH pricing (duration, min price, start bid, undercut, cap) | `PlayerbotAuctionPricing.cs:20-35` | Data | P1 |
| 6 | Bot town errand thresholds (ammo, junk, backoffs) | `PlayerbotTownGoals.cs:34-63,993` | Rules + Data | P1 |
| 7 | Bot opportunity and risk weights | `PlayerbotOpportunityModel.cs:103-118`, `Risk/PlayerbotRiskModel.cs:167-173,454` | Data | P1 |
| 8 | Population classes, level bands, faction share | `PlayerbotPopulationOptions.cs:150-171` | Data (with subject filters) | P1 |
| 9 | Town progression (46 towns) | `PopulationTownProgression.cs:68-134` | Data | P1 |
| 10 | Race start areas and radii | `PopulationStartAreas.cs:35-62` | Data | P1 |
| 11 | Bot world-event goals | `PlayerbotWorldEventGoals.cs:22-28` | Rules | P1/P4 |
| 12 | EventAI (`creature_ai_scripts`, texts) | `Game/Creatures/AI/EventAi/*` | Rules (converted) | P2 |
| 13 | DB scripts, relays, waypoint scripts, movement scripts | `CreatureMapSystem.RelayScripts*.cs`, `RelayScriptRunner.cs` | Rules (converted) | P2 |
| 14 | `creature_linking`, `spell_script_target` | `MovementScriptDataModule.cs` | Data (kept), referenced by rules | P2 |
| 15 | SmartAI creature source | `Game/Creatures/AI/SmartAi/*` | Rules (converted) | P2 |
| 16 | SmartAI gameobject and areatrigger sources | `SmartGameObjectAi.cs`, `SmartAreaTrigger.cs` | Rules (converted) | P3 |
| 17 | Quest/gossip/event DB scripts | `dbscripts_on_quest_*`, `_gossip`, `_event` | Rules (converted) | P2 |
| 18 | Conditions (kept, extended, made reloadable) | `Game/Conditions/*`, `World/Npc/ConditionFeature.cs` | Shared predicate | P0 |
| 19 | Holiday catalog (event id → festival, emotes) | `Playerbots/Events/PlayerbotHolidayCatalog.cs:26,81-91` | Data | P4 |
| 20 | Elemental Invasion | `Game/Holidays/ElementalInvasion.cs:23-26` | Rules + Data | P4 |
| 21 | Fireworks, Lunar Festival | `FireworksShow.cs:17-27`, `LunarFestivalScripts.cs:12-18` | Rules | P4 |
| 22 | `game_event_playerbot_*` (group, post, siege, waypoint) | `Playerbots/EventScenarios/*` | Rules (folded in) | P4 |
| 23 | AH bot value and loot matrices (CSV in config) | `AuctionBotOptions.cs:67-147` | Data | P5 |
| 24 | Transport networks (ships, elevators, trams) | `PlayerbotShipNetwork.cs:42`, `PlayerbotElevator.cs:105`, `PlayerbotTram.cs` | Data | P5 |
| 25 | Outdoor PvP catalogs (Silithus, EPL) | `SilithusCatalog.cs:4-13`, `EasternPlaguelandsCatalog.cs:57-68` | Data + Rules | P6 |
| 26 | Alterac Valley upgrade turn-ins | `AlteracValley.Upgrades.cs` | Rules | P6 |
| 27 | Guard posts | `GuardPostTable.cs:33-45` | Data | P6 |
| 28 | Profession specialization gossip | `ProfessionSpecializationGossip.cs` | Rules | P6 |
| 29 | Bot BG buffs | `PlayerbotBattlegroundBuffs.cs:51-131` | Rules | P6 |
| 30 | Loot `condition_id` (verified in P0: evaluated per recipient by `LootService.Generate` through `ConditionFeature`, references included; `LootConditionsWorldTests`) | `GameObjectLootRows.cs:228` | Verified; nothing to wire | P0 (done) |

The inventory is a living list. New candidates go in this table, and a test (`RuleInventoryTests`, deferred from P0 to P2, when the first converters land and there are known patterns to pin) can later grep for known hard-coded patterns, such as magic SCRIPT_COMMAND switches, to catch regressions back into code.

## 10. Left to the builder

- Exact column types and widths per provider, and whether `rule_list` is a sibling table or a flag on `rule_action`.
- Class layout of the engine (namespaces, how hosts register).
- The deterministic id scheme for converted rows, as long as it is stable and collision-free.
- The parity harness's effect-log format.
- The order of primitives within each phase.

## 11. Calls made by Claude (not decided by the project owner)

- Reuse the existing `conditions` table and evaluator instead of a new predicate system.
- The table names and the custom id range (1 000 000 up to 2^30 - 1), with converted content from 2^30 in one 2^26-id sub-band per converter (EventAI, DB scripts, SmartAI; section 4).
- Two chance kinds: a per-firing roll and a stable per-subject share.
- The bot ladder becomes shipped rules with weights, and the policy layer remains a proposal source.
- Old script tables stay as import input only; the runtime reads rules only.
- (Superseded by the project owner: delete each old surface as soon as parity proves it replaced.)
- Some inventory items are plain data tables, not rules.
- Boss/instance C# scripts stay code.
- Bot ladder semantics (builder check). Fall-through is kept:
  - Candidates are sorted by weight, and their drivers are tried in that order until one acts this tick, which is how the ladder behaves today.
  - Driver guards stay inside the drivers. Rule conditions are extra gates, checked just before that candidate's driver runs, so RNG draw order is unchanged.
  - Non-goal rungs (class upkeep, return home, resurrection-sickness stand-down, the after-ladder pulls) are rows on the same event too, each with a fixed driver.
  - P1 changes where the ladder order and gates are defined. It does not split drivers into separate feasibility and execution steps.
- Weight contract:
  - Shipped rungs get weights 100 apart in today's order.
  - The policy proposal keeps its current slot: its weight sits between urgent errands and quests.
  - Custom rows may use any weight, including one above urgent errands. That is the author's choice, and `.rules trace` shows it.
- Converted rows are never committed (clean-room rule plus the dump hygiene test). Each server generates them locally from its own imported tables using deterministic ids. Hand-authored `smart_scripts` rows (that table is not imported) are converted the same way into the shipped range, with a source tag.
- Parity corpus, in two tiers:
  - **(a)** A committed, clean-room fixture set of hand-written test creatures covering every primitive and edge case. This runs in the merge gate.
  - **(b)** An operator-run suite against a local dump, named by an `ARCANECORE_*` path variable. When the dump is absent, it reports "not run" and never "pass".
  - Deleting an old executor needs (a) green plus a recorded (b) run on the current vmangos and cmangos dumps.
- Config keys during data moves:
  - Rows become the source of the values.
  - An existing `*Options` key still overrides its row value for one deprecation cycle and logs a warning at startup.
  - The keys are removed later in their own PR, with docs and `DocKeyAuditTests` updated together.

### Notes from P0 for the host phases

- A delayed run (`PendingRun`) keeps a strong reference to its invoker subject until it runs or the state is reset. P1 should keep the invoker's guid in the pending run and re-resolve it when the run resumes, so a despawned or logged-out invoker is neither kept alive nor acted on.
- Module vocabularies (`RuleVocabulary.WithModule`) are API only in P0: no module host feeds them yet, and `RuleFeature` builds from `RuleVocabulary.Default`.
- No spell or creature existence check is wired at runtime (`RuleBuildOptions.SpellExists` and `CreatureEntryExists` are unit-tested), because the spell and creature features attach after `RuleFeature`. The first host phase that uses `cast` or creature entries wires them and re-validates on reload.
- `ConditionTableGate` evaluates without a player or a source; a host that needs player conditions supplies its own `IRuleConditionGate`.
- The evaluator calls host code (`RuleEvent.Matches`, `RuleTarget.Resolve`, `RuleAction.Execute`, the condition gate) without a try/catch. The first host wraps its `Fire`/`Update` calls in the fault-ledger idiom, and guards against an action that synchronously fires the same event again (`MaxListDepth` limits list nesting only).
- A chain of more than 8 lists with no delay between them passes the catalog and is cut at run time (`DepthLimited`); the first host phase either rejects such chains at load or keeps this documented.
- The 24-hour caps on `cooldown_max_ms` and `delay_ms` may reject converted EventAI/SmartAI/DB-script rows with longer timers; the P2 converters check their corpus against them.
- P2a (EventAI creature host): `CreatureRuleAI` is registered as "RuleAI" but nothing in `src` assigns it to a creature, and EventAI stays the live executor; the cutover is a later step. Before cutover the host needs P1's `TryFire` (fault-ledger wrapping and the re-entrancy guard) and its invoker-guid resolver, so that a delayed run re-resolves the invoker; until then the host holds the invoker unit itself and wraps `Fire` at its own boundary.
- P2a (EventAI converter): a debug-only row is written disabled, so parity differs when `Creatures:EventAi:DebugOnlyEvents` is true; the cooldown columns carry the EventAI repeat range (the spec cooldown does not apply to creature non-timer events); one unconvertible action drops the whole row. All three are in `docs/reference/rules-conversion.md`.
- The evaluator counters (`RuleEvaluatorCounters`) belong to each host's evaluator; P0 has none in the world, so no command prints them yet.
