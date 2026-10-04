# Handoff 2026-10-04 (session wrap-up)

Branch `ccr-896c06e5-3myqi2` is the integration branch for this work. `main` is untouched at `7313b9e`.
Nothing below is merged to `main`; a PR has not been opened (not asked for).

## Pushed and CI-green on `ccr-896c06e5-3myqi2` (head `1b6c6e1`)

| Content | Notes |
|---|---|
| Wave 4 (`claude/vw5-integration`, `7bcbc95`) | Rebased onto main as the base of this branch |
| Two CI test fixes | MariaDB FLOAT 6-digit text tolerance (graveyard); PostgreSQL advisory lock released synchronously in the ServerProbe test helper (latent race also on main) |
| Wave-4 review fixes | bans (bounded `.banlist`, truthful `.ban ip`), honor (`CanActOn` on setrp/reset/modify; `WorldRuntime.InvokeAsync` cancel-on-stop), taxi (retained write queue + departure-node fallback restored), creatures (event-data GUID with alternate entries, respawn queue retention, instance reset clears queue, spawnless loot decay), docs/tests hygiene |
| GM command lanes | gm-audit (pinfo, mute/unmute, tickets, gmannounce, `.arcane` operator root: bancheck/mutes/gmlog/queues), gm-lookup (lookup quest/skill/spell/area/map/taxinode, `.arcane content/maps/reloads`), gm-objects-npc (gobject/npc/respawn/spawninfo on runtime objects; DB-row spawn edits deferred: needs a spawn write API) |
| Gameplay wave A | fear/confuse movement, percent/flat stat auras, group loot rolls + master loot, item sets + ON_EQUIP spells, area-trigger entry gates + exploration credit, combat durability, transform/charge/blink, rested XP + character rename |
| Integration commits | `.arcane` root collision (lookup lane now extends the audit root), regenerated `docs/reference/*`, schema renumbering (characters: GmAudit 26, Rest 27, Rename 28; world: AreaTriggerQuest 29, Tavern 30), audit-test ordering fix, guild-load shutdown race (`WorldRuntime.IsStopped`) |
| `docs/integration/gm-command-matrix-raw.md` | 6-core GM command matrix: EVIDENCE, not a plan. TrinityCore extraction incomplete. Reference cores are references, not authority (developer decision). |

Verification on the merged tree before each push: solution build 0 warnings; World/Realm/Game suites green; Docs golden tests green after `ARCANECORE_UPDATE_DOCS=1 dotnet test tests/ArcaneCore.World.Tests --filter Docs`; Data suite 1775/1775 (SQLite + MariaDB + PostgreSQL) on the command-lane head, schema/upgrade filter 547/547 on the wave-A head.

## In flight: lane branches pushed as `origin/ccr-build/*` (NOT merged)

Each lane was built by one agent and adversarially reviewed by another; `fix-first` verdicts were remediated on the
same branch and re-reviewed. Merge order used so far: fixes -> GM lanes -> gameplay -> infra. The integration recipe:

1. `git merge --no-ff ccr-build/<lane>` into the integration branch, one at a time.
2. Expect conflicts only in: shared docs (`docs/areas/*.md` appended sections), `IntegratedSchemaTests.cs` version
   tuples (union both sides), schema `Version =` constants (renumber the later lane above what is merged), and
   command roots (two lanes declaring the same root: convert one to `ICommandExtension`).
3. `dotnet build ArcaneCore.slnx -c Release`, then regenerate docs (`ARCANECORE_UPDATE_DOCS=1 ... --filter Docs`),
   then World/Realm/Game suites, then the Data suite with the provider env vars.

### Infra wave 1 (`ccr-build/infra-*`), remediation was running (`wf_8eddd689-0ad`)

| Lane | Review | Remediation state at wrap-up |
|---|---|---|
| infra-logging | fix-first (dirty FileStream in Fail(); colour capability tied to initial mode; two validation rule sets) | fixed `6be021e`, re-review pending |
| infra-resilience | fix-first (BLOCKER: TimeoutPolicy callback on disposed CTS; SQLite ignores cooperative timeout) | fixed `1079cba`, re-review pending |
| infra-invariants | merge (medium: global counter asserts under xunit parallelism) | fixed `6d3826f`, re-review pending |
| infra-netguard | fix-first (saturated per-IP table locks out newcomers; IndexOutOfRange classed as client fault; MaxConnectionsPerIp default vs deviation register) | in progress |
| infra-watchdog | fix-first (withheld heartbeat advances a full interval; realm Program.cs returns 0) | in progress |

Program.cs wiring: each lane adds ONE `services.AddXxx()` line to both daemons' `Program.cs`; merge those lines by hand.
Developer decisions recorded: logging stays in-house (no Serilog/Polly; zero new third-party packages); protections on
by default with the deviation register saying so and `0` to disable.

### Gameplay wave B (`ccr-build/L3-*`, `L9-*`, `L11-*`, `L12-*`, plus four promoted lanes), building (`wf_8fc6a7fb-232`)

Committed at wrap-up: L3 guard AI + unreachable evade (`5f4a857`), L9 loot conditions + chest gold (`df092a2`).
In progress: L11 quest GM tooling (tickets removed: gm-audit shipped them), L12 regen auras, player-summon-meeting-stones,
new-character-outfit-and-validation, pvp-flag-and-honorless-target, persistent-groups. Reviews not started.
Base for all of them: `75bc9c7`.

## Queued next (briefs in this order; not started)

Briefs were written in the session scratchpad and are reproduced here in short form so they survive the container.

- **Per-area log toggles** (after infra-logging merges): stable `LogArea` enum (Net, Auth, Session, Movement, Spells,
  Auras, Combat, Threat, CreatureAi, Pets, Items, Loot, Quests, Npc, Social, Chat, Economy, Instances, Persistence,
  Content, Reload, HotCode, Gm, Ops); `loggers.ForArea(area, typeof(T))` -> category `ArcaneCore.Area.<Area>.<Type>`;
  `Logging:ArcaneCore:Areas:{Spells: Debug, ...}` per-area minimum levels, unknown area = startup refusal; hot-reload on
  World; one `int[]` indexed by area swapped atomically, zero-alloc `IsEnabled`; the six MEL levels unchanged; GM
  surface `.log area <name> <level>` / `.log areas` for the command lane; per-sink minimum levels (the logging lane's
  deferred item) belong here too.
- **Hot-path budget diagnostics** (after infra merges): `BudgetScope` ref struct (elapsed + allocated bytes per named
  section, nested, zero-alloc), `Diagnostics:Budgets:{Section:{MaxMs,MaxAllocBytes}}` hot-reloadable, overrun ->
  Debug assert / Release rate-limited WARNING with section + top-3 children, `AllocationGuard` for must-be-zero-alloc
  sections, slowest-tick per-section breakdown in the watchdog ring, per-opcode handler time/alloc, EF command count per
  tick via `DbCommandInterceptor`, world-thread synchronous-DB detector (the taxi bug class), `dotnet-counters`/`trace`
  recipes in `docs/ops/profiling.md`.
- **Infra wave 2a**: fault injection hooks (`FaultPoint` ids; Release double-gated by env + option, CRITICAL banner),
  per-session packet audit ring with opcode-table redaction and dump-on-disconnect, `--check-config` dry run with
  effective-config provenance + startup banner.
- **Infra wave 2b** (after gameplay merges): `IWorldClock` + seeded `IGameRandom` (then deterministic session replay),
  leak/census detectors, save checksums + `DbUpgrade --verify` orphan scan.
- **Infra wave 2c** (design review with the developer first): overload shedding (login queue: packet layout UNVERIFIED
  until read from wow_messages; tick-budget shedding of tagged non-critical work only; ops drain mode).
- **Gameplay "later" list** (new subsystems, need a go): proc engine, hunter pets + stable, battlegrounds, transports,
  instance-state persistence, charm/possess, creature linking/pools, random item properties, escort + DB scripts,
  swim paths/terrain LOS, persistent spawn edits (`gobject`/`npc` DB rows).

## Open items for the developer

- gm-audit's ticket protocol (`CMSG_GMTICKET_CREATE` layout, `SMSG_GMTICKET_GETTICKET` status tail, response codes) is
  from MaNGOS Zero and marked UNVERIFIED for 1.12.1: needs a real-client acceptance run.
- The GM command matrix addendum sources were cloned read-only under `/home/user/mangosserver/` in the container
  (MangosSharp `9f4f9f4`, mangoszero `b7fface`, arcemu `71bff6d`, WCell `4f00937` 2013 HEAD, TrinityCore 3.3.5
  `25f7080`, azerothcore `ca7e501`); they are not in the repo.
- Environment this session relied on: `/root/dotnet` (SDK 10.0.401), local MariaDB 10.11 (root/arcane) and
  PostgreSQL 16 (arcane/arcane) for the provider matrix.
