# ArcaneCore Roadmap (M5 onward)

> Status: living document. Updated at the end of every milestone.
> Decided 2026-10-02 after the developer's go-ahead: "work through everything … I'll let
> you pick and decide what goes in the emulator, how and why."

This file records **what** goes into ArcaneCore after M4, **in which order**, **how** each
piece is built, and **why** — including the alternatives that lost. The prime directives
of the charter (§1: ground truth over generation, no invented API surface, complete
within scope) are unchanged. What changed is the gating: see [§ Gating](#gating).

---

## Gating

| Before (charter §1.2, §6) | Now |
|---|---|
| One milestone at a time, explicit "go" between each | Milestones proceed back to back without waiting for approval |
| Acceptance test approved before code | Acceptance doc written with the milestone, run by the developer later (batched) |
| "Done" only after a real 1.12.1 client passes | "Implemented" when automated loopback tests + CI pass; "Accepted" when the developer runs the doc against build 5875 |

Each `MILESTONE_Mx.md` therefore has two status lines: **implemented** (CI) and
**client-accepted** (developer). A failure found during client acceptance is fixed in
place, before the next milestone that depends on it is built further.

### Local continuation qualification — October 4, 2026

The transform, group pet vitals and optional name-catalog slice has a complete
local Release gate: 14,481 passing tests, six existing fixture skips, zero
failures, zero build warnings/errors, and 59 native mock checks. The exported
source was replay-checked against base `7313b9eb089197b253dc51bd8bb5ceb803adcc6b`.
This is local qualification; CI publication and original build-5875 client
acceptance remain pending. Source remains unstaged and uncommitted.

The slice adds a live transform/polymorph producer, current pet body/power group
fields, and paired developer-supplied NamesProfanity/NamesReserved DBC loading
for character creation and pet naming. Original name catalogs, SQL reserved_name,
changing druid-form display/model geometry, and pet aura group masks remain
separate work. The integration documents record each boundary and source pin.

---

## Scope decisions — what goes in

| System | Decision | Why |
|---|---|---|
| Client build | 1.12.1 (5875) only | Multi-build support (vmangos does 1.2–1.12) multiplies every packet/field table for no player-facing value. |
| World content source | Import from the **CMaNGOS classic-db** full dump | Git-obtainable, 1.12.1-targeted, complete (creatures, items, quests, loot, `spell_template`, `playercreateinfo*`, level stats). The importer maps columns **by name**, so a vmangos dump can be added as a second source later. The data is GPL-3 — it is never committed here; the developer supplies the dump. |
| Content storage | ArcaneCore's own world schema, loaded into immutable in-memory stores at startup | Keeps the provider abstraction (MariaDB/MySQL/Postgres/SQLite); O(1) lookups on the world thread; no per-request DB reads in game logic. |
| DBC files | Reader for the WDBC format + only the tables content does not cover | The classic-db dump already carries the DBC-derived data the server needs (spells as `spell_template`). Client DBCs stay optional. |
| Terrain heights | Optional reader for CMaNGOS/vmangos-extracted `.map` files (later) | Needed for believable creature movement; extraction requires the developer's client. vmaps (line of sight) and mmaps (pathfinding) are out of scope for now. |
| Scripting | DB-driven only (gossip, EventAI-style creature scripts later) | Hard-coded C++-style boss scripts are a content project, not an emulator core. |
| Warden | **Out** | The 1.12 Warden needs Blizzard module binaries, and it does not stop modern cheats. |
| Battlegrounds, honor, LFG | Out of this plan | Large systems with no dependents; revisit after quests/social. |
| Clustering (gRPC) | Planned M15: explicit process ownership, gateway/session routing and map workers, delivered in gated phases | Developer request 2026-10-03. Compare incomplete MaNGOS Sharp / the developer's MaNGOS Zero fork and SparkEngine executable separation against current code; preserve build-5875 compatibility and local mode. [Design and implementation plan](CLUSTERING_DESIGN.md). |
| Live hot reload | Cross-cutting goal; [tiered design](design/hot-reload-tiers.md) exists, owner go on 2026-10-16 for tiers 1-3 (module lane widening, reloadable packs, gateway blue/green handoff) | Developer request 2026-10-03: wherever possible, content, configuration and feature code change on a running server without a restart. Each system must document what is reloadable versus restart-required. Reloads must keep the in-memory-store invariants (immutable snapshots swapped atomically, never mutated in place, applied on the owning thread) and must not bypass the verification policy or the vanilla-fidelity rule (retail behaviour by default; differences behind config). Design and go now exist (see link); it interacts with M15 (per-node reload and rollout). |
| Plugin host / event bus | Introduced with its first real consumer (GM commands → scripts) | A plugin API without callers would be invented surface. |
| SQLite provider | **Added** (dev + tests) | Zero-setup local runs and end-to-end tests of the real EF stores in CI. MariaDB stays primary. |

---

## Architecture decisions — how

### 1. Threading: authoritative world tick

```
socket reader task ──decode──► session inbound queue ─┐
                                                       │ (world thread, fixed tick)
                         WorldRuntime.Tick(diff) ◄─────┘
                           ├─ sessions not in world: handled in session context (async DB)
                           └─ for each Map: Map.Update(diff)
                                 ├─ process in-world packets of its players
                                 ├─ update objects (movement, AI, timers, spells)
                                 └─ flush visibility + values updates
                                         │
session outbound channel ◄───────────────┘
   └─ single writer task: header encrypt + socket write (ordered, lock-free)
```

* **Why:** game logic (combat, spells, AI, regeneration) is timer-driven and touches many
  objects at once. A single writer per map makes that logic lock-free and deterministic.
  This is the vmangos/cmangos model (`World::Update` → `Map::Update` →
  `WorldSession::Update`).
* **Lost:** keeping the M4 lock-per-map design (every gameplay feature would add lock
  fan-out and ordering bugs); actor-per-object (cross-object interactions everywhere make
  messaging the hot path).
* **Out-of-world work** (character screen, login load, saves) runs async off the world
  thread; results are posted to the world as commands.
* **Slow clients** never stall a map: outbound queues are per session, and a session
  whose queue grows past a limit is disconnected.

### 2. Object model and update fields

* One field table for 1.12.1 **generated** from gtker/wow_messages (MIT/Apache) and
  cross-checked against vmangos `UpdateFields_1_12_1.cpp` (offsets, sizes, visibility
  flags). The generator lives in `tools/codegen`.
* Objects hold `uint[]` values plus a changed-field mask. Each tick, changed fields are
  sent as `UPDATETYPE_VALUES` blocks to every observer; private/owner-only fields go to
  the owner only (vmangos `UF_FLAG_*`).

### 3. Persistence

* Three logical databases: **auth**, **characters**, **world** (content). Each has its
  own provider + connection string; all may point at one server or one database.
* Schema: created per component with a version row (`auth_schema`, `characters_schema`,
  `world_schema`). A version mismatch **fails closed** at startup with a clear message.
  EF migrations come at 1.0, when there is deployed data to migrate. (This also fixes an
  M1–M4 bug: `EnsureCreated` skips a context entirely when another context already has
  tables in the same database.)
* Character state is loaded async before entering the world and saved from snapshots
  taken on the world thread (logout, disconnect, periodic autosave).

### 4. Content

* The implemented creature importer library streams MySQL dump tables and maps
  supported columns into the world schema. A complete
  `tools/ArcaneCore.ContentImporter` CLI is future work; `tools/spell-import` is
  the current standalone DBC spell importer.
* The world daemon loads the content into in-memory stores at startup.
* Hot reload (planned, see the scope table): content stores are loaded as immutable snapshots at startup today; reload means building a replacement snapshot off-thread, validating it, and swapping the reference atomically at a tick boundary on the owning thread. This records intent only; no reload mechanism exists yet.

---

## Milestones

| # | Milestone | Scope | Status |
|---|---|---|---|
| M5 | Runtime core | Opcode registry + session states, outbound queues, world tick, generated update fields + values updates, persistence of position, DB split + schema versioning, SQLite | **implemented** — [MILESTONE_M5.md](../MILESTONE_M5.md) |
| M6 | Session essentials | Name query, logout, time/played, stand state, selection, account data, action buttons, tutorials, chat (say/yell/emote/whisper), text emotes, /who, GM commands | **implemented** — [MILESTONE_M6.md](../MILESTONE_M6.md) |
| M7 | Teleports | Near/far teleport, world-port ack, area triggers, `.tele` | integrated candidate — [scope and gaps](integration/grid-terrain.md); client acceptance pending |
| M8 | Content platform | World schema, dump importer, in-memory stores, WDBC reader | partial integrated candidate — [fleet scope](integration/fleet-20261003.md); complete importer/content acceptance pending |
| M9 | Items | Item/bag objects, inventory, equipment visuals, starting outfit, item query, equip/swap/split/destroy, persistence | integrated candidate — [scope and gaps](integration/items.md); client acceptance pending |
| M10 | Creatures | Grid/cell index, creature/gameobject spawns, queries, waypoints, respawn | partial integrated candidate — [scope and gaps](integration/creatures.md); gameobjects/loot now in the takeover candidate; client acceptance pending |
| M11 | Combat | Melee, hit table, creature AI, death/ghost/resurrect, regen, XP/levels, loot/money | partial integrated candidate — [scope and gaps](integration/combat.md); remaining scope and client acceptance pending |
| M12 | Spells | Cast pipeline, cooldowns, costs, core effects, auras, spellbook, trainers | integrated candidate — [scope and gaps](integration/spells.md); full effects/targeting and client acceptance pending |
| M13 | Quests & NPC services | Gossip, quest flow, objectives, rewards, vendors | partial candidate — [M13a](../MILESTONE_M13A.md) journal/query/timers, [M13b](../MILESTONE_M13B.md) ordinary NPC accept/abandon and [ordinary kill/item/money rewards](QUEST_REWARD_ACCEPTANCE.md); broader rewards/objectives/services and client acceptance pending |
| M14 | Social | Groups, channels, friends/ignore, guilds | integrated candidate — [scope and gaps](integration/social.md); client acceptance pending |
| Mock | Native client acceptance tool | Real SRP/M2, realm/world, character/journal, NPC accept/abandon and combat/reward/relog lifecycle | implemented candidate — [milestone](../MILESTONE_MOCK_CLIENT.md), expanded 59-check scenario; exact qualification in the [ledger](integration/fleet-20261003.md), real-client testing deferred |
| M15 | Clustered runtime and tools | Realm replicas, world gateways, map/instance workers, realm-wide social ownership, fenced persistence/placement and compatible offline tools | planned — [design and phased implementation](CLUSTERING_DESIGN.md); research/planning authorized, cluster deployment and runtime replacement are outside this tranche |

Each milestone ships: code + automated loopback tests + `docs/Mx_ACCEPTANCE.md` +
`MILESTONE_Mx.md` (verified-against table, decisions, limitations).

### M15 clustering worklist

M15 is tracked here as the canonical work item, requested 2026-10-03. The
[selected design](CLUSTERING_DESIGN.md) combines Sharp topology, Zero supervised
offload/fencing and Spark executable/handoff separation around ArcaneCore's actual
runtime. It records source pins and gaps, ownership, routing, contracts,
persistence, failure recovery, placement, compatibility, tests and migration.
The developer approved this module/process direction on 2026-10-03; implementation
phases remain planned and deployment is a separate gate.

| Phase | Work | Status |
|---|---|---|
| M15.0 | Ownership/session seams, durable fencing and saves, ID allocation, schema authority | planned — first implementation tranche |
| M15.1 | Separate gateway and one worker, versioned gRPC, replica session claims/readiness | planned — depends on M15.0 |
| M15.2 | Realm-wide identity/social and auction/mail authority, role-specific feature registration, and distinct map instances | planned — depends on M15.1; economy ownership must precede multiple game workers |
| M15.3 | Complete fenced handoff and static multiple-worker placement | planned — depends on M15.2 |
| M15.4 | Host supervision, recovery, drain and capacity-aware placement | planned — depends on M15.3 |
| M15.5 | Existing CLI/build/import/test jobs, content rollout, migration/rollback and cross-host acceptance | planned — depends on M15.4 |
| M15.6 | Auxiliary services, in value order ([design](CLUSTERING_DESIGN.md#auxiliary-services)): Warden hosted on the gateway (no separate process); an optional Remote playerbot chat/LLM service; an optional Remote playerbot planner; an optional Remote auction house bot planner with custody kept on the auction owner. Every service has a Local (default) or Remote mode. Metrics, AutoBroadcast and server mail get no service | planned — depends on M15.1 for transport; bot chat also depends on M15.2 directory routing; the AH bot depends on M15.0 ID allocation and fencing and on a realm auction owner; the no-bot-host decision depends on M15.3 placement |

Runtime code is introduced only in its bounded implementation phase. The current
request authorizes research/planning; no deployment or immediate runtime rewrite
is part of this tranche. The existing native mock milestone and intentionally
deferred real-client acceptance retain their gates.

## Verification policy

* Every protocol fact (opcode, field, layout, constant) cites the reference that confirmed
  it. Where references disagree, the comment says so and which one was chosen.
* ArcaneCore is GPL-3.0 (`LICENSE`, 2026-10-08), but no GPL/AGPL code is copied: behaviour
  and wire formats are reimplemented (charter §4), by the developer's choice of an
  independent implementation.

### Reference set (extended 2026-10-02 and 2026-10-04 at the developer's request)

| Reference | Client | License | Used for |
|---|---|---|---|
| vmangos/core | 1.12.1 (+older) | GPL-2 | Primary protocol + behaviour reference |
| cmangos/mangos-classic | 1.12.1 | GPL-2 | Protocol + behaviour cross-check |
| cmangos/classic-db | 1.12.1 | GPL-3 | World content source for the importer (never committed) |
| mangoszero/server | 1.12.x | GPL-2 | Vanilla cross-check |
| gtker/wow_messages | 1.12 (+TBC/WotLK) | MIT/Apache-2 | Machine-readable packet layouts, opcodes, update fields — permissive, so it is the source for generated tables |
| AscEmu/AscEmu | 1.12.1 … 5.4.8 | AGPL-3 / MIT (per file) | Third 1.12.1 cross-check; MIT-headed files only for anything beyond facts |
| MangosServer/MangosSharp | 1.12.x | GPL-3 | .NET idioms and the cluster split (lineage, charter §0) |
| mangosvb/serverZero | 1.12.x | GPL-2 | .NET lineage cross-check |
| TrinityCore (3.3.5 branch) | 3.3.5a | GPL-2 | Architecture only (map update, grids, movement splines, AI design) |
| arcemu/arcemu (Ascent lineage) | 3.3.5a | AGPL-3 | Architecture only |
| Krilliac/Worldforge | 1.12.1 client side | (developer's) | Client file formats (ADT/WDT/DBC/MPQ) for future terrain/height work |
| wowdev/noggit3, Noggit Red (gitlab prophecy-rp) | 1.12–3.3.5 map editors | GPL-3 | ADT/WDT terrain + liquid layout for height and terrain tooling |
| wowdev.wiki | all | CC | Neutral format and protocol documentation |
| [WoWWiki archive: patch 1.12.1](https://wowwiki-archive.fandom.com/wiki/Patch_1.12.1) | Original 1.12.1 / build 5875 when the cited revision confirms it | Site terms; research facts, do not vendor content | Patch notes, historical mechanics and revision history |
| [Wowhead Classic](https://www.wowhead.com/classic) | Version-selected Classic data; verify original 1.12.1 applicability | Site terms; research facts, do not vendor content | Item, spell and quest data, tooltips, dated historical observations |

Use the source-version policy in charter §4: identify the patch/expansion,
page ID, date and supporting vanilla reference. Evolving wiki pages and
Wowhead comments may include TBC/WotLK, Classic Era, Hardcore, Season of
Discovery or PTR behavior. Those do not establish original build-5875 values.
Corroborate gameplay observations against original client/data or vanilla
references; byte-level protocol values retain the existing verified sources.

**WotLK-era references (TrinityCore, ArcEmu) never supply 1.12.1 wire values** — opcodes,
update fields and packet layouts differ by build (charter §2). They inform design only.

## 2026-10-03 takeover

NPC services, instances, AI, gameobjects/loot, progression, spell persistence and mail/auction/trade economy sources are consolidated in draft #11. [Exact scope](integration/takeover-20261003.md), [Claude handoff and remaining work](integration/claude-handoff-20261003.md). Earlier pending-source labels are historical; client acceptance and complete content/effects remain pending.

## 2026-10-04 local continuation

The reconstructed uncommitted source now includes Soulstone/Reincarnation,
Twisting Nether, reagent costs, build-5875 item use and current-pet persistence
with effect 109. Characters schema version 21 is composed for the pet store.
This is a locally verified candidate; CI and client acceptance are separate,
pending gates. [Item/pet scope and verification](integration/server-item-pets-20261004.md)
and [next bounded slices](integration/next-slices-20261004.md) supersede earlier
blanket absence labels without claiming complete vanilla fidelity.

## Current state & next steps (2026-10-13)

**Master to-do list for any agent (Claude or Grok) continuing ArcaneCore.** Supersedes the 2026-10-10 section: everything in it (#68–#90, wave 17/18) is merged and `integrate/wave17` is gone.

### State
- `main` moves with every merge (read `git log origin/main`; the SHA that stood here went stale). Claude merges via "Train N" PRs from `claude/wNN-*` / `integrate/wNN-*`. The Grok lane works in `grok/w37-*` / `grok/w38-*` branches with PRs titled `[grok lane]` straight into `main`.
- Hosted CI is the test gate (`test (Game|World|Data|Rest)`); drafts skip CI. The box has no push creds (push via GitHub MCP) and builds stay serialized.

### Merged 2026-10-12/13
- Claude: #333 (deterministic eight-bot shutdown test).
- Grok lane: #401 + #405 area-trigger scripts (all 10 cmangos `areatrigger_scripts.cpp`) plus engine pieces (single-player GO traps, temp faction reset on respawn, AI custom events, per-map script data); #404 SmartAI slice 3; #403 pet untraining; #413 pet XP/leveling + training points; #422 pet happiness/loyalty (runaway); #425 hunters learning from pets; #410 helm/cloak toggles + mount special anim + ignored opcodes; #416 LFG/bug report/logout; #417 GM teleport opcodes; #415 comment cleanup; #427 VisibilityAllocationTests flake fix; #428 battleground spirit healers; #429 whois/setrawpos.

### Open PRs
None of the lane PRs this section used to list is open any more (checked with `gh pr view` on 2026-10-16):
- Merged: #411 zone scripts batch 1, #423 batch 2, #439 batch 3 (its WIP note is stale: the finished batch 3, Prospector Remtravel and the Barrens stealth-alert speakers, landed in `6f10bc4c` and came in through train #566), #412 SmartAI slice 3b, #434 pets possess summon limits, #435 BATTLEFIELD_JOIN refuse+log and spirit guide gossip, #436 Far Sight, #437 Outdoor PvP capture radius/time, and #326 Claude clustering (v60, `c96e5907`).
- Closed unmerged: #424 SmartAI slice 3c (KILL, SUMMONED_UNIT, RECEIVE_EMOTE events; SOUND, CALL_FOR_HELP). The same work reached `main` another way: commit `464eade1` is on `main`.
- Zone scripts batch 5 is in `docs/integration/zone-scripts-batch5-20261016.md`.
- Zone scripts batch 6 is in `docs/integration/zone-scripts-batch6-20261016.md`.

### Open work
- **World scripts:** about 72 of 186 mangos-classic SD2 scripts are still missing (82 before the Scourge invasion and go_scripts.cpp slice, `docs/integration/world-scripts-scourge-go-20261010.md`). The 10 area-trigger scripts (`areatrigger_scripts.cpp`) are all ported. Captured Arkonarin needs a DB path. Felwood kitten (q4506) needs an owned summon plus an entry change. Private Hendel's credit needs `creature_movement_template` 4967 in the DB.
- **SmartAI:** slices 1-3d are done (`docs/integration/smartai-slice3-20261012.md`). Slice 3d added events 3, 9, 11, 14 and actions 8, 10, 18, 19, 25, 49. Measured against the AzerothCore `smart_scripts` rows of creatures in the live `creature_template` (12,448 rows), 9,285 rows can now run in full, up from 7,874. Next by row count: events 38 DATA_SET, 40 WAYPOINT_REACHED, 62 GOSSIP_SELECT, and action 50 SUMMON_GO. Also open: AIName for game objects, and ending the area-trigger handler when a script ran.
- **Pets:** Far Sight view extension, the stable (needs `character_pet` slots and a stable-slot column, a schema change), the happiness emote. Done: training-point costs and beast training (`docs/integration/pet-training-20261010.md`), untraining (#403), pet XP and level-up training points (#413), happiness/loyalty (#422), learning from pets (#425), possess summon limits (summon limit, ALREADY_HAVE_CHARM, pet put away for the minion).
- **Warden:** API-hook/EndScene/login checks, a live 1.12.1 client test, Mac.
- **Metrics:** done (realm/logon packet counters, per-opcode labels). Docker is untested.
- **AH bot:** gaps listed in `docs/areas/auction-house-bot.md`.
- **Script hooks:** addon chat. (DuelReset pet cooldowns are done: `DuelResetScript.ResetPetCooldowns`.)
- **Multi-version:** S1 after #81 (build info on sessions, expansion columns, the gate, build-column migrations); S2 movement codec and typed serializers. Still undecided: which TBC/WotLK content DBs to use, and cross-version characters (decisions 4 and 6 on #61).
- **Autopatcher:** done (off by default).
- **Multithreading and parallel performance (later: after the playerbot policy model training, Phase 3 of `docs/design/playerbot-policy-phases.md`):** added 2026-10-15 at the maintainer's request.
  - **Today:** one world process runs the whole simulation on its single world thread ([§1](#1-threading-authoritative-world-tick)). The thread pool only takes side work: saves and write queues, policy telemetry, policy scoring, tile prefetch and the region-graph build. So one process uses about one core: the Node1 training pilot ran 240 bots on about 0.6 core. The bot training fleet scales out by running more instances.
  - **To do:** design and implement multithreaded world updates and the related techniques. That means parallel map/instance updates, keeping one writer per map, and moving bot think and pathfinding work off the world thread, with results applied on the tick. It also covers batched nav queries and an allocation/GC pass. Verify the per-map update-thread model against the references before writing code.
  - **Not included:** live switching to Release and server GC is a separate pass for the maintainer to call. Live stays on Debug, and Debug is never built with optimization.
  - Needs its own design and an explicit go, and it interacts with M15 clustering.
- The maintainer has approved Warden module bytes in the repo and the bot-stall pathing change.

### Known flakes
- `VisibilityAllocationTests`: GC-window sensitive on busy runners; #427 hardened it, so watch for recurrence.
- Mograine Divine Shield timing test: failed once, passed on rerun (timing-sensitive).

### Gaps by area
- **Pets:** untraining price, taught-ability state and loyalty need persistence, which needs a characters schema bump *after* v60 (held by Claude's #326). Still open: stable gaps, happiness emote (possess limits are done, #434; DuelReset pet cooldowns are done).
- **Handlers:** helm/cloak show flags aren't persisted (needs schema). Whois lacks account email. Spirit guide gossip timer is sent as 0. Far Sight needs real camera state in `Pets/Control/UnitControl.cs` (#436, merged, only resets/restores). More unhandled opcodes remain in `docs/reference/opcodes.md`.
- **Zone scripts:** about 62 cmangos zone scripts are unported; batches 1–6 cover ~31 (batch 6: the Scourge Cauldron, Ringo, Regthar Deathgate, the Aged/Dying/Ancient Kodos with the Kodo Kombobulator, Lady Katrana Prestor's gossip; batch 5: Spitelash naga, Rabid Thistle Bear, Feero Ironhand, Overlord Mok'Morokk, `go_lightning`, `go_covert_ops`; batch 4: Calvin Montague, Taskmaster Fizzule, Aquementas, Deathstalker Faerleia, `go_rune_of_the_defiler`, `go_mausoleum_door`, `go_mausoleum_trigger`; skipped candidates and reasons in `docs/integration/zone-scripts-batch6-20261016.md`, `docs/integration/zone-scripts-batch5-20261016.md` and `docs/integration/zone-scripts-batch4-20261016.md`). Continue in batches of 5–8 per PR.
- **SmartAI:** the rejected events, actions and targets left after slice 3d (see "Still open" in `docs/integration/smartai-slice3-20261012.md`), AIName for GOs.
- **Area triggers:** all 10 ported; leftovers are DB-side data (Captured Arkonarin, Felwood kitten q4506, Hendel `creature_movement_template` 4967).
- **Other (from 2026-10-10, still open):** Warden API-hook/EndScene/login checks and live client test; AH bot gaps (`docs/areas/auction-house-bot.md`); Docker untested; addon chat hook; multi-version S1/S2 (waiting on the maintainer's decisions 4 and 6 on #61).

### Lane ownership
- **Claude:** playerbots, clustering (#326), test flakes, performance, Train merges.
- **Grok lane:** zone/area scripts, SmartAI slices, pets, opcode handlers, outdoor PvP. Either agent can pick up the other's lane if it's idle; leave a note in the PR.
- The next characters schema version after v60 is unclaimed. Coordinate before taking it.

### Rules
- Schema versions stay contiguous; no placeholders left at merge.
- GPL-3.0; DCO sign-off (`Signed-off-by: Krilliac <7993979+Krilliac@users.noreply.github.com>`).
- No full test suites on the box; CI gates.
