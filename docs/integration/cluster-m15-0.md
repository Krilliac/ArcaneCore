# M15.0 scope record (documentation slice)

Branch `claude/ac-10a-cluster-docs`, base `c3dea16292756e146df0ddd0783f97fee734dd5c`.
This is the scope record for M15.0 "Ownership/session seams, durable fencing and saves,
ID allocation, schema authority" ([ROADMAP.md:139](../ROADMAP.md),
[CLUSTERING_DESIGN.md:315](../CLUSTERING_DESIGN.md)). It contains no code and no schema.
M15.0 stays **open**; nothing here implements any part of it beyond item 1 below.

## What this tranche delivers

| Deliverable | State |
|---|---|
| [cluster-transfer-state.md](cluster-transfer-state.md): per-feature memory-only vs durable inventory with `file:line` (M15.0 item 1) | done, static reading only |
| `CLUSTERING_DESIGN.md` corrections (below) | done |
| This record: A1-A5 and B1-B4, deferrals, decisions needing a go | done |
| Any code, schema, test or config change | none |

Not edited on purpose: `seams.md` and `claude-handoff-20261003.md` (owned by the lead),
`ROADMAP.md` (its M15.0 status text changes only when code lands), and
`docs/areas/grid-terrain.md` (see "Left for the lead").

Every statement about code is a reading of source at the base commit. Nothing was
compiled or run for this slice, so none of it is a build or test result.

## Corrections made to CLUSTERING_DESIGN.md

Each was checked against the base tree before editing.

| Line (before) | Was | Verified fact | Change |
|---|---|---|---|
| 82 | "`InstanceRegistry` assigns memory-only bindings and shares a Map among dungeon instances" | `InstanceRegistry` is gone from `src` (no match in `*.cs`); `WorldRuntime._maps` is keyed by `(MapId, InstanceId)` (`WorldRuntime.cs:20,191-201`); `InstanceManager` creates a Map per instance with a local id counter (`InstanceManager.cs:53,689`). The removal is recorded in `docs/integration/instances.md:164-183`, which also says this line was left stale | Rewritten; also records that only saves and character binds are durable |
| 93 | "Core/social queues can exhaust retries and drop writes" | The **core** queue does not drop: after 3 attempts it keeps the snapshot (`CharacterSaveQueue.cs:313,323`), merges it into the next save (`:203-206`) and throws at stop if undrained (`:251-254`). The queues that drop are social (`SocialWriteQueue.cs:79-82`), reputation (`ReputationWriteQueue.cs:97-100`) and instance (`InstanceWriteQueue.cs:99-102`). The real core gap is that the enqueue returns `void` (`IPlayerSession.cs:38`) | Rewritten; lists all seven write mechanisms |
| 88-90 | ID allocators: item, guild, group | Also instance ids (`InstanceManager.cs:53`) and mail/auction/item-text ids (`EconomyFeature.cs:89-93`); group counter starts at 1 and is never seeded (`GroupManager.cs:20`) | Added |
| 7-8 | Whole inventory "checked against" `e06d4e4` | Only the bullets named above were re-read at `c3dea16` | Added a sentence saying which |
| ~222 (as cited by the design draft) | "checks the current owner epoch in the same transaction" | The doc text is correct; only the draft's cited line number (about 218) was off. At the base the sentence is at line 221-222 (now 236-237 after the insert) | none |

An earlier critique also questioned whether pin `e06d4e4` can be checked. At the base
`InstanceRegistry` is already removed, so the pin is not reproducible there; the
re-reads above are what stand.

## M15.0 as defined, and where it stands

From `CLUSTERING_DESIGN.md:315`: inventory feature transfer state; separate
connection/simulation responsibilities around existing seams; durable epochs; ID
reservation; save acknowledgement/recovery; coordinated schema initialization.
Closure: local behavior and the mock stay green, and concurrent claims, old-owner saves,
ID allocation and exhausted write retries have meaningful tests on SQLite, MariaDB and
PostgreSQL.

| Item | State at base | Evidence |
|---|---|---|
| 1 Transfer-state inventory | done by this slice | [cluster-transfer-state.md](cluster-transfer-state.md) |
| 2 Connection/simulation separation | partly present: `IPlayerSession` is the only session surface Game sees (`IPlayerSession.cs:12-31`); `WorldSession` still mixes socket, cipher, scoped stores, settings and player (`WorldSession.cs:47-107`); in-world handlers take the concrete class | |
| 3 Durable epochs | absent. No ownership table or epoch type; `SaveStateAsync` updates unconditionally (`EfCharacterStore.cs:101-162`) | |
| 4 ID reservation | absent; all allocators are process-local (inventory section 4) | |
| 5 Save acknowledgement/recovery | absent as a contract; seven independent mechanisms | inventory section 1 |
| 6 Schema authority | absent: `Realm` and `World` both run auth initialization at start (`Realm/Program.cs:19`, `World/Program.cs:19`), `EnsureAsync` has no lock or validate-only mode and applies a step and writes its version as separate commands (`SchemaBootstrapper.cs:78-120`) | |

## Work items

A-items are the proposed first implementation tranche; B-items follow. Verdicts fold in
the critique of the original design, which found several cited facts wrong. None of
these is started.

| # | Work | Verdict | Why |
|---|---|---|---|
| A1 | Kernel contracts (only members with a consumer) + one characters data module with an `ownership_fence` table + an ownership store; claim = atomic epoch increment | keep, narrowed | Drop the `id_range` table and any `GetAsync`/range contract until a caller exists (charter 1.4). New files only; the module is reflection-discovered (`seams.md`). Must implement a no-op `ICharacterDataCleanup` (guard in `CharacterDeletionTests.cs:32-37`). The characters version is renumbered by the lead at merge |
| A2 | Fenced character save: same-transaction check of the held epoch, a terminal `StaleOwnerException`, claim in `WorldHost` before the save queue and world start, fail-closed handling, default off behind `Cluster:Fencing:Enabled` | keep, but not first, and larger than first stated | (a) `StageStateAsync` is also called by `EfEconomyStore.cs:87-90` and `EfCharacterQuestRewardStore.cs:82` through `new EfCharacterStore(db)`, so a fence in `SaveStateAsync` alone is bypassed by every economy and quest-reward settlement; (b) `CharacterSaveQueue` retries any exception three times and retains the snapshot (`:282-328`), so a stale-owner failure must be made terminal there or a re-claimed node can replay an old snapshot; (c) touches contested files (below) |
| A3 | Durable item-GUID ranges (`ItemGuidAllocator` block source, opt-in) | defer to M15.1 | No consumer until a second worker exists (charter 1.4). If pulled forward: never reserve synchronously inside `Next()`, which runs on the world thread (`PlayerInventory.Storage.cs:465`); prefetch a block; keep the overflow fail-closed (`ItemTemplateStore.cs:71-75`). The "precedent" of blocking startup seeds (`EconomyFeature.cs:89-95`) is a one-shot, not a hot call |
| A4 | Extract `ISessionClaims` from `SessionRegistry`, add a per-account session generation | defer to M15.1 | No consumer until gateway claims exist; the server-id field is still discarded (`WorldSession.cs:405`). Its true file list is wider than first stated: `WorldSession.cs:68`, `WorldServer.cs`, service registration, `WorldTestHost.cs:81`, `WorldServerShutdownTests.cs:62`, `WorldLifecyclePersistenceTests.cs:461`, and `SessionRegistry.Find` returns the concrete session |
| A5 | Transfer-state inventory document | done here | |
| B1 | Save-acknowledgement contract and one "character flush" barrier across core, quest, spellbook, spell state, social, reputation and instance writes; retire drop-after-3 | defer; large | Depends on the reputation, delete-recovery and reward lanes landing |
| B2 | Schema authority: per-engine advisory lock around `EnsureAsync`, validate-only mode for serving nodes, fix the Realm/World auth-init race | defer; medium | Edits `SchemaBootstrapper.cs`, which the index-repair lane also edits. Needs a real concurrent-startup test on MariaDB/PostgreSQL |
| B3 | Durable allocators for guild, group, instance, then mail/auction/item-text ids | defer | Mail/auction/text overlap the economy lanes |
| B4 | Apply the same fence to non-core stores (quest, spell, social, reputation, instance, economy) | defer; large | A fence on core rows alone does **not** stop an old owner writing those rows; reporting "fenced persistence" after A2 would be wrong |
| B5 | Docs: stale instance paragraph and core-queue claim, `MILESTONE_M15_0.md` | the two corrections are done here; `MILESTONE_M15_0.md` only when code lands (charter 8.5) | |

## Deliberately deferred, and out of scope

- All of M15.1 and later: gateway and worker processes, gRPC packages, transfer
  participant interfaces, supervision, placement. Charter 1.4 forbids pre-creating them.
- Durable item-GUID ranges and `ISessionClaims` (A3, A4) until a consumer exists.
- Fencing the non-core stores (B4) and the settlement paths until their lanes merge.
- Any default-on behavior change (see decisions).
- Lease renewal, heartbeat and automatic failover. A claim that increments an epoch is
  "newest start wins", not safe failover. Local monotonic lease budgets are M15.4 and
  must not be implied by an A1/A2 delivery.
- XP persistence and other gaps found by the inventory. They are existing gameplay
  defects, listed in inventory section 3, not M15.0 items.

## Decisions that need an explicit go

The 2026-10-02 charter amendment removes per-milestone waiting (`ARCANECORE_CHARTER.md`,
section 9, "Blanket go-ahead"), but `ROADMAP.md:146-149` still says this request
authorizes research and planning, with runtime code only in its bounded phase. Ask once,
explicitly, before any A-item code.

1. **Start M15.0 implementation** and confirm which of A1-A4 are in the first tranche.
   Recommendation: A1 and A2 only.
2. **Schema allocation.** One new characters module step (next free version; the
   index-repair lane also adds a forward characters version). Who assigns the number
   and in what merge order. Tests must read the version from
   `CharacterDbContext.Schema.CurrentVersion`, never a literal.
3. **Default off.** Fencing on by default would make a second `World` process on the same
   characters database fail fast. Recommendation: ship off; flipping it is a separate go.
   A fencing-**enabled** end-to-end run (login, move, logout, relog restores position,
   on SQLite, through the World/MockClient fixtures) must exist, or the default-off path
   is the only one ever exercised.
4. **Claim semantics.** Starting a second `World` instance would invalidate the healthy
   one's saves. Confirm "newest start wins, the fenced node exits loudly" is acceptable
   until M15.4.
5. **Terminal stale-owner rule in `CharacterSaveQueue`** (no retry, snapshot dropped or
   quarantined, surfaced to the host). It edits a contested file.
6. **Fence scope.** Core character rows first (A2) versus all stores (B4). Recommendation:
   core first, with a generic scope string so finer scopes need no schema change; do not
   add a second realm-identity source if one already exists in the realm options.
7. **Schema authority (B2).** Lock plus opt-in validate mode first; changing the default
   for serving nodes changes Realm and World startup.
8. **Real-client acceptance.** Per charter 1.3/8.1, M15.0 cannot be "accepted" without a
   real 5875 run, which the developer has deferred. "Implemented" means CI plus automated
   tests only.

## Collisions with running lanes

Worktrees seen at the base (`git worktree list`): `ac-1-delete-recovery`,
`ac-2-reputation-durability`, `ac-3-index-repair`, `ac-4-durable-loot`,
`ac-5-reward-collaborators`, `ac-6-economy-contract`, `ac-7a-death-removes-auras`, plus
the economy, loot, instance-unload, reputation and spell-hold fix worktrees.

| A/B item | Shared files | With |
|---|---|---|
| A1 | none, except the characters version number | index-repair (renumber) |
| A2 | `EfCharacterStore.cs`, `CharacterSaveQueue.cs`, `WorldHost.cs`, `DataServiceCollectionExtensions.cs:44` | delete-recovery, reward-collaborators |
| A2 follow-up | `EfEconomyStore.cs`, `EfCharacterQuestRewardStore.cs` | economy, reward-collaborators |
| B1 | all queues | reputation, delete-recovery, reward-collaborators |
| B2 | `SchemaBootstrapper.cs`, `DataModules.cs` | index-repair |
| B3, B4 | economy and instance stores | economy, durable-loot |

Sequence A2 after delete-recovery and reward-collaborators land, and re-read the
inventory entries for reputation, loot, instances, economy and spells at that point.

## What a test can and cannot show

For whoever implements A1/A2. Genuinely RED against the base (they fail because the
behavior is absent, not just because a type is missing): a stale-epoch save is rejected
and leaves the character and item rows unchanged (today `StageStateAsync` overwrites);
N parallel first claims produce epochs exactly 1..N with no unique-key error escaping;
a stale-owner failure in the save queue is called exactly once and not retained (today
three calls and retained); a fencing-enabled lifecycle run.

Characterization only, passing today and not proof of the change: retained-failure
behavior of the core queue (`CharacterSaveQueueTests.cs:66,131,308`), the data-module
cleanup guard, version contiguity, and "fencing off behaves as today".

Not implementable as first written: a claim "interleaved between the epoch read and the
write", since a single `UPDATE ... WHERE Epoch = @held` has no such window. A real
interleaving test needs a two-connection harness that holds the fence row lock, runs
only on MariaDB/PostgreSQL, and is a Skip on SQLite (whose database-level lock shows
nothing about row locking).

Not provable by automated tests at all: a real 1.12.1 client, two real OS processes
contending on one database, MySQL (CI has MariaDB 10.11 and PostgreSQL 16 only,
`.github/workflows/ci.yml:15-35`).

## Left for the lead

- `docs/areas/grid-terrain.md:111-114` still describes the removed `InstanceRegistry`
  stub. `instances.md:181-183` already says so. Left alone because this slice was
  limited to the cluster documents.
- `docs/integration/economy.md` is a stub that predates its own lane (it describes
  itself as a work in progress); outside this slice.
