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
| Live hot reload | Cross-cutting goal, planned; no milestone assigned yet | Developer request 2026-10-03: wherever possible, content, configuration and feature code change on a running server without a restart. Each system must document what is reloadable versus restart-required. Reloads must keep the in-memory-store invariants (immutable snapshots swapped atomically, never mutated in place, applied on the owning thread) and must not bypass the verification policy or the vanilla-fidelity rule (retail behaviour by default; differences behind config). Needs its own design and an explicit go before runtime code; it interacts with M15 (per-node reload and rollout). |
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
| M15.2 | Realm-wide identity/social authority and distinct map instances | planned — depends on M15.1 |
| M15.3 | Complete fenced handoff and static multiple-worker placement | planned — depends on M15.2 |
| M15.4 | Host supervision, recovery, drain and capacity-aware placement | planned — depends on M15.3 |
| M15.5 | Existing CLI/build/import/test jobs, content rollout, migration/rollback and cross-host acceptance | planned — depends on M15.4 |

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
