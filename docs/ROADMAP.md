# ArcaneCore Roadmap

Last updated: 2026-10-26.

This file says what is in ArcaneCore, what is still open, and the decisions that shape it.
The charter's prime directives still hold: ground truth over generation, no invented API
surface, complete within scope.

## How work is gated

- Work lands as `claude/<topic>` branch PRs into `main`. Read `git log origin/main` for the
  current state and `gh pr list` for open PRs. This file does not list PRs.
- The merge gate is local and is defined in [CLAUDE.md](../CLAUDE.md). Builds and the four
  `tests/ci/*.slnf` suites (Data, Game, Rest, World) run through
  `tools/ops/ci/run-in-slot.ps1`. Check the test counts, not just the exit codes.
- Hosted CI also runs. MariaDB and PostgreSQL provider tests run only there; locally, only
  SQLite runs.
- Two statuses stay separate. **Implemented** means the automated tests pass.
  **Client-accepted** means the area was run against a real 1.12.1 (build 5875) client.
  Automated tests never make an area accepted. No area is client-accepted yet: every root
  `MILESTONE_*.md` records real-client acceptance as pending (see Open work 1).
- Per-milestone `MILESTONE_*.md` files stopped after M13b and the mock client. Status now
  lives in PRs and in `docs/integration` and `docs/areas`.
- Bot QA files defects as GitHub issues labelled `bot-qa` (`src/ArcaneCore.Playerbots/Qa`).

## Decisions that still hold

| Topic | Decision | Why |
|---|---|---|
| Client build | 1.12.1 (5875) is the only build served. A multi-version design is a draft only ([design](design/multi-version.md)). | Supporting several builds multiplies every packet and field table. |
| World content | Imported from the CMaNGOS classic-db full dump, mapped by column name. The data is never committed. | The dump is obtainable from git, targets 1.12.1 and is complete. The data is GPL-3, so it is never committed here. |
| Client DBCs | Required for a full setup. `import-dbc` (in `src/ArcaneCore.Data/Content/Import/Cli`, hosted by `tools/ArcaneCore.ContentImporter`) fills the map, area, area trigger, taxi and spell tables. | The map, area, area trigger and taxi tables are filled from client DBCs, and `verify` fails (exit 6) while any of them is empty ([installation](guide/installation.md)). Spells are read from five spell DBCs by `import-dbc` ([content-import](areas/content-import.md)). |
| Content storage | ArcaneCore's own world schema, loaded into immutable in-memory stores at startup. No per-request DB reads in game logic. | Keeps the provider abstraction and gives O(1) lookups on the world thread. |
| Databases | Three logical databases: auth, characters, world. Each has its own provider and connection string. MariaDB is primary; PostgreSQL and SQLite are supported. | SQLite gives zero-setup local runs and end-to-end tests of the real EF stores. The reason for the other two providers is not recorded. |
| Schema changes | Schema steps are defined by versioned data modules in code and applied by `arcane-db upgrade` (`tools/ArcaneCore.DbUpgrade`). `sql/updates` holds the generated per-provider SQL for each step ([README](../sql/updates/README.md)). There are no EF Migrations. A version mismatch fails closed at startup. Versions stay contiguous. The newest characters script is 0066, so the next is 0067; coordinate before taking it. | Per-component version rows replaced `EnsureCreated`, which skipped a context when another context already had tables in the same database. |
| Threading | One authoritative world tick. Each map has a single writer. Socket decode, login loads and saves run off the world thread and post results back to it. Saves use snapshots taken on the world thread. Parallel map updates exist behind `World:Parallel:MapUpdateThreads` (default 1, so off). | A single writer per map keeps timer-driven combat, spell and AI logic lock-free and deterministic. A lock per map and an actor per object were rejected: locks add fan-out and ordering bugs, and actors make cross-object messaging the hot path. |
| Slow clients | Outbound queues are per session. A session whose queue grows past its limit is disconnected; it never stalls a map. | One slow socket must not delay a map tick. |
| Update fields | One generated 1.12.1 field table from `tools/codegen` (gtker/wow_messages, cross-checked against vmangos). Objects keep values plus a changed mask; owner-only fields go to the owner only. | Offsets and visibility flags come from a machine-readable source with a permissive license. |
| Scripting | Both data-driven (EventAI, SmartAI) and C# scripts (instances, bosses, zones); both exist in code (`Creatures/AI`, `Instances/Scripts`). | (rationale not recorded) |
| Live reload | Content, config and code reload on a running server where documented ([tiers](design/hot-reload-tiers.md)). Reloads swap immutable snapshots on the owning thread. | Snapshots are never mutated in place, so readers on the world thread stay consistent. |
| Extensibility | Assembly-level pack discovery for features, updaters and commands. There is no generic event bus or plugin API. | A plugin API without callers would be invented surface. |
| Clustering | A separate gateway process plus world workers, with local single-process mode kept ([design](CLUSTERING_DESIGN.md)). | Requested by the developer on 2026-10-03; build-5875 compatibility and local mode must survive. |
| License | GPL-3.0. Behaviour and wire formats are reimplemented; no GPL or AGPL code is copied. | Charter section 4. |

## Verification policy

* Every protocol fact (opcode, field, layout, constant) cites the reference that confirmed
  it. Where references disagree, the comment says so and which one was chosen.
* No GPL or AGPL code is copied, so each reference below is read for behaviour and wire
  formats only.

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
| Worldforge (the developer's client-tools repository) | 1.12.1 client side | (developer's) | Client file formats (ADT/WDT/DBC/MPQ) for future terrain/height work |
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

## Delivered

Every row is implemented on `main`. No area is client-accepted: each root `MILESTONE_*.md`
(M1-M6, M13A, M13B) records real-client acceptance as pending, with its procedure in the matching
`docs/M*_ACCEPTANCE.md`.

| Area | Main source | Notes |
|---|---|---|
| Runtime core and sessions | `src/ArcaneCore.World/Net`, `src/ArcaneCore.Kernel` | Opcode registry, session states, world tick, update fields, chat, GM commands. Implemented, not client-accepted ([M5](../MILESTONE_M5.md), [M5 acceptance procedure](M5_ACCEPTANCE.md)). |
| Realm and login | `src/ArcaneCore.Realm` | SRP login and realm list. Autopatcher exists, off by default (`AutoPatchOptions`). |
| Teleports | `src/ArcaneCore.Game/Teleport` | Near and far teleports, area triggers. Scope and gaps: [grid-terrain](integration/grid-terrain.md). |
| Content import | `src/ArcaneCore.Data/Content/Import/Cli` (commands), `tools/ArcaneCore.ContentImporter` (13-line host), `src/ArcaneCore.Data/ClientData` | Commands include `plan`, `import`, `import-dbc`, `import-map-dbc`, `verify`, `class-masks`, `proc-events`, `spell-threat`, `refresh` and `refresh-portals`. |
| Items | `src/ArcaneCore.Game/Items` | Inventory, equipment, item use. Scope and gaps: [items](integration/items.md). |
| Creatures and game objects | `src/ArcaneCore.Game/Creatures`, `src/ArcaneCore.Game/GameObjects`, `src/ArcaneCore.Game/Loot` | Spawns, queries, waypoints, respawn, loot. Scope and gaps: [creatures](integration/creatures.md). |
| Combat and AI | `src/ArcaneCore.Game/Combat`, `src/ArcaneCore.Game/Creatures/AI` | Hit table, threat, regen, death and ghost, EventAI and SmartAI (slices 1-3d; game objects through `SmartGameObjectAi`). Scope and gaps: [combat](integration/combat.md). |
| Spells | `src/ArcaneCore.Game/Spells` | Cast pipeline, auras, cooldowns, spell scripts. Scope and gaps: [spells](integration/spells.md). |
| Quests and NPC services | `src/ArcaneCore.Game/Quests`, `src/ArcaneCore.Game/Npc` | Gossip, quests, vendors, trainers, taxi. Reward acceptance: [QUEST_REWARD_ACCEPTANCE.md](QUEST_REWARD_ACCEPTANCE.md). |
| Pets | `src/ArcaneCore.Game/Pets` | Summons, taming, training, happiness and loyalty, Far Sight, the stable master (`PetStableController`, `SummonService.Stable`). Pet loyalty and training points are persisted. |
| Social | `src/ArcaneCore.Game/Social`, `src/ArcaneCore.Game/Groups`, `src/ArcaneCore.Game/Guilds`, `src/ArcaneCore.Game/Channels` | Groups, channels, friends and ignore, guilds. Scope and gaps: [social](integration/social.md). |
| Economy | `src/ArcaneCore.Game/Economy`, `src/ArcaneCore.World/Economy` | Economy and trade rules, the auction house bot (`Economy/AuctionBot`). Mail and auction storage is in `src/ArcaneCore.Data/Economy`. |
| Instances and scripts | `src/ArcaneCore.Game/Instances/Scripts`, `src/ArcaneCore.World/Scripting` | Dungeon and raid scripts; script hooks including addon chat. |
| Battlegrounds | `src/ArcaneCore.Game/Battlegrounds` | Battlegrounds with spirit guides. |
| Honor | `src/ArcaneCore.Game/Honor` | Honor ranking. |
| LFG | `src/ArcaneCore.Game/Lfg` | LFG queue. |
| Outdoor PvP | `src/ArcaneCore.Game/OutdoorPvP` | World PvP objectives. |
| World events | `src/ArcaneCore.Game/WorldState`, `src/ArcaneCore.Game/Holidays` | Scheduled world events and holidays. |
| Transports | `src/ArcaneCore.Game/Transports` | Boats, zeppelins, the tram, elevators. |
| Professions | `src/ArcaneCore.Game/Crafting`, `src/ArcaneCore.Game/Fishing` | Crafting and fishing. |
| Other mechanics | `src/ArcaneCore.Game/Conditions`, `src/ArcaneCore.Game/Stealth`, `src/ArcaneCore.Game/Totems` | Conditions, stealth, totems. |
| Collision and pathing | `src/ArcaneCore.Game/Maps/Collision` | VMaps (line of sight, height) and MMaps (navmesh pathfinding). |
| Parallel maps | `src/ArcaneCore.Game/Maps/Parallel` | Off by default. |
| Anti-cheat | `src/ArcaneCore.Game/AntiCheat`, `src/ArcaneCore.World/AntiCheat` | Movement checks, packet heuristics (the `PacketHeuristics` namespace), commands and the feature ([area doc](areas/anticheat.md)). |
| Warden | `src/ArcaneCore.World/Warden` | Windows 5875 module embedded ([area doc](areas/warden.md)). |
| GM tickets | `src/ArcaneCore.World/Tickets` | Client ticket flow. |
| Hot reload | `src/ArcaneCore.World/Reload`, `src/ArcaneCore.World/HotCode` | Content, config and code module reload. |
| Clustering | `src/ArcaneCore.Gateway`, `src/ArcaneCore.World/Cluster` | Gateway, handoff, social, supervision, tooling, transfer and auxiliary services. Merged per phase; closure criteria are still open (see [Clustering phase status](#clustering-phase-status)). |
| Playerbots | `src/ArcaneCore.Playerbots` | Autonomous bots, and bot QA that files `bot-qa` issues. |
| Tools | `tools/ArcaneCore.Atlas`, `tools/ArcaneCore.MockClient`, `tools/ArcaneCore.DbUpgrade`, `tools/ArcaneCore.Sniff`, `tools/botctl`, `tools/playerbot-policy`, `tools/ArcaneCore.ChatModelProbe` | Telemetry web tool, native mock client, DB upgrade, packet sniffer, bot control, bot policy training, chat model probe. |
| Docker | `apps/docker` | Images are built and compose is validated by the docker-smoke workflow. |

## Clustering phase status

This table owns M15 phase status; [CLUSTERING_DESIGN.md](CLUSTERING_DESIGN.md#phased-implementation-and-acceptance)
owns the closure criteria. "Merged" means the named merge commit is an ancestor of `main`
(check: `git merge-base --is-ancestor <sha> origin/main`). A merge does not mean the phase
closed. Open work 3 lists the gaps from [gateway.md](areas/gateway.md#not-done-yet-rest-of-3a-and-beyond).

| Phase | Merge commit(s) | What the merge subjects say | Closure status |
|---|---|---|---|
| M15.0 | `42f7cf9e3`, `8b801352e`, `1fbf3d37a`, `361a19dd3`, `ab0378823` | Reputation fencing, schema authority, durable ID block allocators, session generation claims, character flush barrier. | Merged. Closure not independently verified (unverified). |
| M15.1 | `379768eed` | Roles, readiness and gateway-worker transport. | Merged. Open gap (gateway.md): real-client acceptance of the gateway path has not been run. Closure criteria: design doc. |
| M15.2 | `7ef7ae3aa` | Presence directory, cross-worker social router, fenced economy lease. | Merged. Closure not independently verified (unverified). |
| M15.3 | `c6f749c98` | Continent leases, instance placement, character handoff. | Merged. Open gap (gateway.md): the handoff moves ownership and saved state but still disconnects the client; the seamless reroute is not built. Closure criteria: design doc. |
| M15.4 | `38caa3a00` | Host supervisor, capacity admission, console multiplexer. | Merged. Supervisor hosted as opt-in mode (`Cluster:Supervisor:Enabled`), `MinClusterProcesses` enforced at start. Open gap: coordinator, restart reconciliation, placement of new/empty maps. |
| M15.5 | `f3d42215d` | CLI maintenance guard, content revision compatibility, rollback coordinator, diagnostics. | Merged. Open gap (gateway.md): the gateway has no conf catalog entry or metrics. Closure criteria: design doc. |
| M15.6 | `86a30ffcc` | Battleground matchmaking, world-state broadcast, LFG directory, GM command routing, SOAP lease fencing. | Merged. Open gap (gateway.md): the design targets Warden on the gateway; it runs on the worker. Closure criteria: design doc. |
| All | `d1feacfce` | Integration merge of M15.0 to M15.6. | Merged. |

To settle an "unverified" row, read that phase's "Required closure" cell in the design doc and
find the test that covers each clause.

## Open work

Not ranked by a measured score. Evidence is a file or audit line in this repo.

| # | Item | Scope | Evidence / how to check |
|---|---|---|---|
| 1 | Real-client acceptance | No area is accepted. Run the implemented areas against a build 5875 client. | Every root `MILESTONE_*.md` records acceptance as pending. Procedures: `docs/M1_ACCEPTANCE.md` to `docs/M6_ACCEPTANCE.md`, `docs/M13A_ACCEPTANCE.md`, `docs/M13B_ACCEPTANCE.md`, [MOCK_CLIENT_ACCEPTANCE.md](MOCK_CLIENT_ACCEPTANCE.md), [quest-client-acceptance.md](integration/quest-client-acceptance.md). |
| 2 | Bot QA defects | Work the open `bot-qa` issues. | `gh issue list --label bot-qa` |
| 3 | Clustering closure gaps | The seamless reroute; Warden on the gateway instead of the worker; a gateway conf catalog entry, `.conf.dist`, `--check-config` and metrics; real-client acceptance of the gateway path; the movement anticheat receive time stamped on the worker (link jitter is added); manifest schema and content versions that are operator labels, not read from the databases; the restart of a gateway or World dropping every client, with no authenticated operator trigger; the link protocol living in `ArcaneCore.World` with no shared assembly. | [gateway.md](areas/gateway.md#not-done-yet-rest-of-3a-and-beyond); closure criteria in [CLUSTERING_DESIGN.md](CLUSTERING_DESIGN.md#phased-implementation-and-acceptance). |
| 4 | SmartAI remaining events and actions | Events 38 (DATA_SET), 40 (WAYPOINT_REACHED), 62 (GOSSIP_SELECT), 20 (REWARD_QUEST) and 52 (TEXT_OVER); actions 50 (SUMMON_GO) and 34 (SET_INST_DATA); MOVEMENTINFORM; the other rejected events, actions and targets; ending the area-trigger handler once a script ran. Basis: 9,285 of 12,448 `smart_scripts` rows of imported creatures can run in full. | Not in the enums in `src/ArcaneCore.Game/Creatures/AI/SmartAi/SmartTypes.cs`; list and counts in [smartai-slice3-20261012.md](integration/smartai-slice3-20261012.md), "Still open". |
| 5 | Warden scans | API-hook scans, the EndScene and sysinfo scans, the InitialLogin flag. Mac clients cannot load the module. | [warden.md](areas/warden.md), "Not ported" and "Windows only". |
| 6 | Helm and cloak show flags | Persist the toggles across relog. Needs a characters schema step (the next is 0067). | The `characters` table in `sql/updates/characters/sqlite/base_v0059.sql` has no player-flags column, and scripts 0060-0066 add none (0064 adds a playerbot relation table with its own `Flags`). `src/ArcaneCore.World/Handlers/AppearanceHandlers.cs` only toggles `player.Flags` in memory. |
| 7 | Auction house bot gaps | Four gaps: loot table choice, no max item level option, a deleted seller's bid counted as spent, refill shares 16 transaction slots. | [auction-house-bot.md](areas/auction-house-bot.md), "Gaps". |
| 8 | Unhandled opcodes | Candidates: CMSG_CHAT_FILTERED, CMSG_GM_UPDATE_TICKET_STATUS, MSG_MOVE_TIME_SKIPPED, MSG_DELAY_GHOST_TELEPORT. Whether the 1.12.1 client sends each one is unverified. | [opcodes.md](reference/opcodes.md), rows marked unhandled. To verify: read the Oracle column and [wire-oracle-histogram-20261008.tsv](integration/wire-oracle-histogram-20261008.tsv) (MSG_MOVE_TIME_SKIPPED is already "decoded"), check the vmangos `Opcodes.cpp` handler mapping for each opcode, or capture a 5875 client. |
| 9 | World and zone script coverage | Recount by creature entry, not by script name, then port what is missing. | The old name-based count (72 of 186) could not be reproduced (unverified). To recount: list the cmangos ScriptDev2 script registrations, map them to creature entries, and compare with the entries registered in `src/ArcaneCore.Game/Creatures/AI/CreatureAiServices.cs`. Zone batches 4-16, 21 and 22 and dungeon batches 17-20 are in `docs/integration`. |
| 10 | Whois e-mail | The admin-only CMSG_WHOIS handler prints "e-mail: Unknown"; it needs an account e-mail column (an auth schema step). | `src/ArcaneCore.World/Handlers/GmClientHandlers.cs` (`HandleWhois`; ArcaneCore keeps no e-mail). There is no e-mail column under `sql/updates/auth`. |
| 11 | Multi-version S1 and S2 | Build info on sessions, expansion columns, a content gate, then the movement codec. Decisions 4 and 6 (sections 6.1 and 6.2) are still open. | [multi-version.md](design/multi-version.md) is a draft with no code. |
| 12 | Multithreading next steps | Bot think per map and stages inside one map. Design only; needs the owner's go. | [map-local stages design](research/map-local-stages-design-20261024.md), [parallel maps status](research/parallel-maps-status-20261017.md). |
| 13 | Known test flakes | `VisibilityAllocationTests` (GC-window sensitive on busy runners) and the Mograine Divine Shield timing test (failed once, passed on rerun). Watch for recurrence. | `tests/ArcaneCore.Game.Tests/GridTerrain/VisibilityAllocationTests.cs` (PR #427, merge `2917b77f3`, hardened `AllocationProbe`, which it uses; this is why recurrence is watched) and `tests/ArcaneCore.Game.Tests/Instances/ScriptFidelityTests.cs` (`Mograine_DivineShield_WaitsFortySeconds_AndIsReadyAgainAfterTheRevival`). |

## History

- **2026-10-02:** The owner let the agents choose what goes in and how. Milestones ran back to
  back, with client acceptance batched for later.
- **2026-10-03 takeover:** NPC services, instances, AI, game objects and loot, progression,
  spell persistence and the mail, auction and trade economy were consolidated and later
  merged into `main` ([scope](integration/takeover-20261003.md)). Clustering and live reload
  were added as goals.
- **2026-10-04 local continuation:** Soulstone and Reincarnation, reagent costs, build-5875
  item use, current-pet persistence, the transform producer and the name-catalog DBCs were
  qualified locally and later merged ([items and pets](integration/server-item-pets-20261004.md),
  [next slices](integration/next-slices-20261004.md)).
- **2026-10-13 state:** Area-trigger scripts, SmartAI slices 1-3d
  ([slice 3](integration/smartai-slice3-20261012.md)), pet training, happiness and loyalty,
  battleground spirit healers and zone-script batches landed. The PR lists of that time are
  gone; use `gh pr list` and `git log origin/main`.
- **Since then:** Clustering (M15) phases, hot reload, Warden, playerbots and bot QA,
  parallel maps (off by default) and characters schema up to 0066 landed on `main`.