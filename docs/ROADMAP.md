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
  Automated tests never make an area accepted.
- Per-milestone `MILESTONE_*.md` files stopped after M13b and the mock client. Status now
  lives in PRs and in `docs/integration` and `docs/areas`.
- Bot QA files defects as GitHub issues labelled `bot-qa` (`src/ArcaneCore.Playerbots/Qa`).

## Decisions that still hold

| Topic | Decision |
|---|---|
| Client build | 1.12.1 (5875) is the only build served. A multi-version design is a draft only ([design](design/multi-version.md)). |
| World content | Imported from the CMaNGOS classic-db full dump, mapped by column name. The data is never committed. |
| Client DBCs | Required for a full setup. `import-dbc` in `tools/ArcaneCore.ContentImporter` fills the map, area, area trigger, taxi and spell tables. |
| Content storage | ArcaneCore's own world schema, loaded into immutable in-memory stores at startup. No per-request DB reads in game logic. |
| Databases | Three logical databases: auth, characters, world. Each has its own provider and connection string. MariaDB is primary; PostgreSQL and SQLite are supported. |
| Schema changes | Versioned SQL scripts in `sql/updates`, applied by `tools/ArcaneCore.DbUpgrade`. No EF migrations. A version mismatch fails closed at startup. Versions stay contiguous. The newest characters script is 0066, so the next is 0067; coordinate before taking it. |
| Threading | One authoritative world tick. Each map has a single writer. Socket decode, login loads and saves run off the world thread and post results back to it. Parallel map updates exist behind `World:Parallel:MapUpdateThreads` (default 1, so off). |
| Slow clients | Outbound queues are per session. A session whose queue grows past its limit is disconnected; it never stalls a map. |
| Update fields | One generated 1.12.1 field table from `tools/codegen` (gtker/wow_messages, cross-checked against vmangos). Objects keep values plus a changed mask; owner-only fields go to the owner only. |
| Scripting | Both data-driven (EventAI, SmartAI) and C# scripts (instances, bosses, zones). |
| Live reload | Content, config and code reload on a running server where documented ([tiers](design/hot-reload-tiers.md)). Reloads swap immutable snapshots on the owning thread. |
| Extensibility | Assembly-level pack discovery for features, updaters and commands. There is no generic event bus or plugin API. |
| Clustering | A separate gateway process plus world workers, with local single-process mode kept ([design](CLUSTERING_DESIGN.md)). |
| License | GPL-3.0. Behaviour and wire formats are reimplemented; no GPL or AGPL code is copied. |
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

## Delivered

Every row is implemented on `main`. Client acceptance against build 5875 is recorded only in
the root `MILESTONE_*.md` files (for example [M5](../MILESTONE_M5.md)); later areas are not client-accepted.

| Area | Main source | Notes |
|---|---|---|
| Runtime core and sessions | `src/ArcaneCore.World/Net`, `src/ArcaneCore.Kernel` | Opcode registry, session states, world tick, update fields, chat, GM commands. |
| Realm and login | `src/ArcaneCore.Realm` | SRP login and realm list. Autopatcher exists, off by default (`AutoPatchOptions`). |
| Teleports | `src/ArcaneCore.Game/Teleport` | Near and far teleports, area triggers. |
| Content import | `tools/ArcaneCore.ContentImporter`, `src/ArcaneCore.Data/ClientData` | Commands `import`, `import-dbc`, `refresh`, `verify`. |
| Items | `src/ArcaneCore.Game/Items` | Inventory, equipment, item use. |
| Creatures and game objects | `src/ArcaneCore.Game/Creatures`, `src/ArcaneCore.Game/GameObjects`, `src/ArcaneCore.Game/Loot` | Spawns, queries, waypoints, respawn, loot. |
| Combat and AI | `src/ArcaneCore.Game/Combat`, `src/ArcaneCore.Game/Creatures/AI` | Hit table, threat, regen, death and ghost, EventAI and SmartAI (slices 1-3d; game objects through `SmartGameObjectAi`). |
| Spells | `src/ArcaneCore.Game/Spells` | Cast pipeline, auras, cooldowns, spell scripts. |
| Quests and NPC services | `src/ArcaneCore.Game/Quests`, `src/ArcaneCore.Game/Npc` | Gossip, quests, vendors, trainers, taxi. |
| Pets | `src/ArcaneCore.Game/Pets` | Summons, taming, training, happiness and loyalty, Far Sight, the stable master (`PetStableController`, `SummonService.Stable`). Pet loyalty and training points are persisted. |
| Social | `src/ArcaneCore.Game/Social`, `src/ArcaneCore.Game/Groups`, `src/ArcaneCore.Game/Guilds`, `src/ArcaneCore.Game/Channels` | Groups, channels, friends and ignore, guilds. |
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
| Anti-cheat | `src/ArcaneCore.Game/AntiCheat` | Movement checks. |
| Warden | `src/ArcaneCore.World/Warden` | Windows 5875 module embedded ([area doc](areas/warden.md)). |
| GM tickets | `src/ArcaneCore.World/Tickets` | Client ticket flow. |
| Hot reload | `src/ArcaneCore.World/Reload`, `src/ArcaneCore.World/HotCode` | Content, config and code module reload. |
| Clustering | `src/ArcaneCore.Gateway`, `src/ArcaneCore.World/Cluster` | Gateway, handoff, social, supervision, tooling, transfer and auxiliary services. The M15 phases are in code. |
| Playerbots | `src/ArcaneCore.Playerbots` | Autonomous bots, and bot QA that files `bot-qa` issues. |
| Tools | `tools/ArcaneCore.Atlas`, `tools/ArcaneCore.MockClient`, `tools/ArcaneCore.DbUpgrade`, `tools/ArcaneCore.Sniff`, `tools/botctl`, `tools/playerbot-policy`, `tools/ArcaneCore.ChatModelProbe` | Telemetry web tool, native mock client, DB upgrade, packet sniffer, bot control, bot policy training, chat model probe. |
| Docker | `apps/docker` | Images are built and compose is validated by the docker-smoke workflow. |

## Open work

Ranked by impact. Evidence is a file or audit line in this repo.

| # | Item | Scope | Evidence |
|---|---|---|---|
| 1 | Real-client acceptance | Run the implemented areas against a build 5875 client. | The root `MILESTONE_*.md` files stop at M13b and the mock client. |
| 2 | Bot QA defects | Work the open `bot-qa` issues. | `gh issue list --label bot-qa` |
| 3 | SmartAI next events and action | Add events 38 (DATA_SET), 40 (WAYPOINT_REACHED), 62 (GOSSIP_SELECT) and action 50 (SUMMON_GO). | Not in the enums in `src/ArcaneCore.Game/Creatures/AI/SmartAi/SmartTypes.cs`. |
| 4 | Warden scans | API-hook scans, the EndScene and sysinfo scans, the InitialLogin flag. Mac clients cannot load the module. | [warden.md](areas/warden.md), "Not ported" and "Windows only". |
| 5 | Helm and cloak show flags | Persist the toggles across relog. Needs characters schema 0067. (unverified) | No saved column found in `src/ArcaneCore.Data` or `sql/updates/characters`. |
| 6 | Auction house bot gaps | Four gaps: loot table choice, no max item level option, a deleted seller's bid counted as spent, refill shares 16 transaction slots. | [auction-house-bot.md](areas/auction-house-bot.md), "Gaps". |
| 7 | Unhandled opcodes | Candidates: CMSG_CHAT_FILTERED, CMSG_GM_UPDATE_TICKET_STATUS, MSG_MOVE_TIME_SKIPPED, MSG_DELAY_GHOST_TELEPORT. Whether the 1.12.1 client sends each one is unverified. | [opcodes.md](reference/opcodes.md), rows marked unhandled. |
| 8 | World and zone script coverage | Recount by creature entry, not by script name, then port what is missing. | The old name-based count (72 of 186) cannot be reproduced. Zone batches 4-16, 21 and 22 are in `docs/integration`. |
| 9 | Whois email | `.whois` prints "e-mail: Unknown". | `src/ArcaneCore.World/Handlers/GmClientHandlers.cs` |
| 10 | Pet happiness emote | Send the emote when happiness changes. (unverified) | No emote send found in `src/ArcaneCore.Game/Pets`. |
| 11 | Multi-version S1 and S2 | Build info on sessions, expansion columns, a content gate, then the movement codec. Decisions 4 and 6 are still open. | [multi-version.md](design/multi-version.md) is a draft with no code. |
| 12 | Multithreading next steps | Bot think per map and stages inside one map. Design only; needs the owner's go. | [map-local stages design](research/map-local-stages-design-20261024.md), [parallel maps status](research/parallel-maps-status-20261017.md). |

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