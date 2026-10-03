# Claude handoff: ArcaneCore, 2026-10-03

Nathan asked to finish this wave and move continuation to Claude. Start with the
published candidate [draft PR #11](https://github.com/Krilliac/ArcaneCore/pull/11),
branch `codex/integrate-feature-fleet-20261003`. All 19 original sources and all
10 later external sources are incorporated; none is excluded. Three historical
source objects are incorporated through their recorded squash commits. Read
[the exact source ledger](takeover-20261003.md), [machine accounting](takeover-accounting-20261003.json)
and [the original fleet record](fleet-20261003.md).

The final code merge checkpoint is `620cab672366a8c80d47b9e3a3a12ba8e3b86266`.
The published tip adds the corrected real-creature instance-loot test and this
handoff. Use the PR head and its exact-head checks as the current tip; do not
substitute a source branch's green checks for combined validation.

## Custody and environment

Local checkout: `C:\Users\Nathan\Documents\Codex\2026-10-02\task-2\ArcaneCore-continuation-20261003`,
work branch `codex/grok-salvage-20261003`. It is published to the canonical
candidate branch by ordinary fast-forward push. Existing original checkouts,
worktrees and partial feature histories were preserved. All Codex workers are
finished; there is no new gameplay wave or intentional background native build
after closeout. Recheck processes and CI before taking ownership.

Protected/default branch `claude/arcanecore-charter-rqadgw` stays at
`ac1204e00dd730ea04c081e0def0650879fd4492`; external Claude integration base
`claude/friendly-hamilton-cuz4j4` stays at
`0b5dcd0260d5919f2811e32f42f9d0ab720843c1`. Neither was merged, reset or rewritten.
No release/deployment/default merge was performed. Keep #11 draft until gates
and client acceptance justify a separate decision. Never force-push.

The linked original Claude session was inaccessible read-only:
`https://claude.ai/code/session_016eMgZECkSiMH9b9kBQRBtA`. Its private conversation
was not recovered or operated. Do not assume additional context from that URL.

Read the charter, current README, `docs/integration/seams.md`, global
`C:\Users\Nathan\.codex\AGENTS.md`, and any current Claude/local instructions.
The machine is shared with Sonder/Spark. .NET SDK 10.0.401 and existing EF/Pomelo
9 dependencies are installed; no paid infrastructure/new security access was
introduced. Serialize heavy native work. Preflight:

```powershell
powershell -NoProfile -File C:\Users\Nathan\.claude\scripts\fleet-preflight.ps1
git status --short
git rev-parse HEAD
git log -12 --format='%H %P %s'
gh pr view 11 --repo Krilliac/ArcaneCore --json headRefOid,isDraft,statusCheckRollup
```

Use a new isolated worktree for subsequent changes. Preserve unrelated edits,
stage specific paths, sign commits, and use bounded worker ownership/time limits.
No ExecutionPolicy bypass or speculative service restart is needed.

## Implemented in this continuation

- Separate reputation eligibility refuses Unfriendly NPC interaction without
  changing friendly combat reaction semantics.
- Repair snapshots persist money and final durability together.
- Instance unload retries survive in-flight worldport ACKs.
- Held spell aura/channel state stays frozen; weapon effects honor target masks.
- AI turns in melee reach, validates delayed assistance and contested PvP flags.
- Chest grid reload preserves partial contents and fresh object ownership;
  held eligible gold recipients retain their original shares; unknown nonzero
  locks refuse use; stale gameobject references cannot mutate replacements.
- GO/loot and creature systems route by exact `Map`; unload detaches NPC,
  progression, reputation and default-combat event roots. Same spawn GUIDs in
  different instances have independent corpse loot.
- Reward capabilities inspect actual effect/aura handlers and nested triggers.
  Permanent LearnSpell/CreateItem, teleport/summon without prerequisite contracts,
  and permanent/passive/unsupported aura grants refuse preparation.
- Login and deletion drain registered settlement barriers. Durable money
  publication and fresh wallet loading refresh accepted cash quests.
- Seller deletion conditionally matches all 12 scanned auction fields before
  deleting escrow. Unknown auctions retain reservations through fresh recovery;
  deletion invalidates older recovery callbacks.

- Integration of handoff items 1-6 (branch `claude/ac-integration`; **local verification only;
  exact-head hosted CI pending**):
  - Deletion outcome recovery: a durable `character_deletion` ledger, read-back after a thrown
    delete, a character-list sweep that finalizes pending deletions, an explicit-id create fence,
    and conditional (lifetime-fenced) post-delete removals that the hooks await.
  - Reputation write durability: failed writes are retained, never dropped, and carried by the next
    change, the login barrier, logout and shutdown.
  - Forward index repair: upgrades create the indexes of the tables they create; inline repair steps
    add the missing ones; startup is idempotent, resumable and serialized per component.
  - Durable consumed loot for dungeon-instance chests (`loot_state*` tables).
  - Reward collaborators: permanent `LearnSpell`/`CreateItem` and quest reputation rewards settle in
    the quest reward transaction, with destination and summon-owner preflight.
  - Economy recovery contract: a transactional auction snapshot, per-ID quarantine and backoff,
    allocator reseed after an external insert, and a finished `economy.md`.
  - Integration fix: the quest reward settlement now drains the reputation queue with
    `FlushCharacterAsync` (retry, refuse while not durable) rather than the pure barrier, so a
    retained older row cannot overwrite rows the reward writes; the reputation write queue's
    post-delete removal uses the conditional `DeleteDeletedCharacterAsync`.

Schema allocations at the integration of 2026-10-03: **Auth 2 / World 10 / Characters 13**.
Characters: reputation 7, instances 8, spell state 9, economy 10, forward index repair 11
(inline step, `CharacterDbContext.IndexRepairVersion`), deletion outcome ledger 12
(`CharacterDeletionDataModule.Version`), durable loot state 13 (`LootStateDataModule.Version`).
World: GO/loot 7, AI 8, forward index repair 9 (inline step, `WorldDbContext.IndexRepairVersion`),
quest reputation reward columns 10 (`QuestReputationRewardWorldModule.Version`). Each number lives in
one constant and tests reference the constants or `Schema.CurrentVersion`. Before this integration the
allocation was Auth2 / World8 / Characters10. Original feature branches retain provisional numbers.
Never apply a source branch's provisional schema to a database already using the final allocation. New
changes require coordinated forward versions and complete cleanup registration.

## Evidence and its limits

Ten correction/source groups have exact source CI artifacts outside the checkout
in the parent task directory (`takeover-*-source-ci-evidence.json` and full
`grok-monitor-ci-<run>.log/.json`). Source counts differ with ancestry; never add
them. Combined economy `230be5a27cb235fda25f6b75f045ef00f9d62a5b` passed
[37114887000](https://github.com/Krilliac/ArcaneCore/actions/runs/37114887000):
9,191 tests, zero failures/skips/warnings/errors and 59 mock checks/142 frames.
Auction `697869a` passed 9,174. Seller-deletion `0323c03` passed 9,179 including
all four MariaDB/PostgreSQL bid-race cells and nine provider controls. Caller-owned
race cases explicitly use ReadCommitted; default-owned cases use provider defaults.

Behavioral original-source failures preceded fixes for reputation, repair,
instance transit, spell holds/masks, AI, loot, quest capabilities, login/cash
publication, auction recovery and map lifetime. Additional invalidation and
deletion barrier cases passed fixed real socket/EF tests. The instance-loot
fixture initially lacked an owning CreatureMapSystem; it was corrected to use
SpawnTemporary and real combat death, then passed.

Final native logs are `takeover-integrated-native-build-20261003.log`,
`takeover-integrated-native-tests-final-20261003.log`,
`takeover-integrated-native-mock-20261003.log` in the parent task directory.
The local provider race is deliberately skipped on SQLite; hosted MariaDB10.11
and PostgreSQL16 establish that proof. Prefix migrations exercise every
contiguous step and repeated startup, but do not establish populated released-v6
index parity, interrupted DDL recovery or actual MySQL server support.

Check the delivered exact-head CI and these logs before continuing. Useful
serialized commands after restore/build:

```powershell
dotnet build ArcaneCore.slnx -c Release -m:1 -p:UseSharedCompilation=false
dotnet test ArcaneCore.slnx -c Release --no-build -m:1 --verbosity normal
dotnet run --project tools/ArcaneCore.MockClient -c Release --no-build -- self-test
```

## Remaining work, in priority order

Items 1-6 were delivered by the 2026-10-03 integration (`claude/ac-integration`). That is local
verification only; exact-head hosted CI pending. Do not read a source branch's result as combined proof.

1. **Deletion outcome recovery. Delivered; exact limits remain.** Details: `character-delete.md`.
   `CHAR_DELETE_SUCCESS` means the rows are durably gone; an ambiguous outcome answers failure and is
   finalized by a sweep run when the owning account requests its character list, or by a retry. Limits:
   no background sweep (after a restart a pending row mainly blocks explicit-id recreation until that
   account enumerates or deletes); a finalizer that keeps failing is retried at most once per session;
   a commit a server finishes after its connection failed is not seen at that request; generated ids
   are not fenced (reuse was not established, and the conditional removals are defense in depth,
   not observed client loss); economy re-sends `SMSG_RECEIVED_MAIL` for recently returned letters on a
   re-run; the lost acknowledgement is injected with an EF interceptor, not a real network fault;
   MariaDB/PostgreSQL proof of the conditional deletes, unique index and ledger test comes only from
   hosted CI; real-client deletion acceptance is deferred; no soft delete or audit log.
2. **Reputation failure durability. Delivered; exact limits remain.** Details: `reputation.md`.
   Retention is in process only: a crash, or storage still down at graceful shutdown, loses the
   retained gain (shutdown fails loudly naming the characters; a crash cannot). There is no
   periodic background retry (an online player is retried at the next change, logout, relog or
   shutdown). A character with an unrecovered write cannot log in until storage recovers. Merging
   assumes absolute rows and a last-wins store, so a future delta-style write must not reuse the
   path. Deletion is never blocked by retained writes; a later shutdown failure can name a character
   whose deletion already removed its rows.
3. **Forward index repair and upgrade parity. Delivered; exact limits remain.** Details:
   `schema-index-repair.md`. Limits: MariaDB and PostgreSQL lock SQL, catalog queries and
   non-transactional DDL resume are proved only by hosted CI (the differ output is proved offline);
   actual MySQL 8 server support is unqualified; the frozen populated baseline is SQLite only; repair
   is explicit, there is no automatic drift detection for an index dropped later; realm/content
   seeding after bootstrap is outside the lock; fresh create on PostgreSQL is resumable rather than
   one transaction; older binaries fail closed on the version-0 marker. Duplicate guild-member or
   auction rows stop the upgrade and are never deleted (operator guide in the doc).
4. **Durable consumed loot. Partially delivered.** Delivered: dungeon-instance chest contents
   stored with awards, tied to the logical instance save across unload, recreation and restart,
   cleared only on reset or deletion; ordinary shared chests keep working; held group gold shares
   preserved. **Not delivered: item containers** (`CMSG_OPEN_ITEM` on a lootable item still answers
   cannot-loot; vmangos behaviour for generated container loot was not verified). Temporary and
   runtime chests in instances stay unsupported and chest gold is not generated or stored; a lost
   queued `InstanceSaved` write makes that instance's chests refuse until restart; the remainder of
   a money split is dropped; contents persistence is this port's choice, not verified against
   vmangos; MariaDB/PostgreSQL only through hosted CI; no real world-dump chest test. See
   `gameobjects-loot.md`.
5. **Complete reward collaborators. Delivered; exact limits remain.** Permanent spell, item and
   reputation grants settle atomically in the reward transaction with destination and
   summon-owner preflight, and the area-aura family shares the finite/nonpassive guard. Limits:
   summon rewards stay refused in the daemon (no `ISpellSummonSink`), covered with fakes only;
   transient teleport/summon rewards can still fail after the commit and are logged and lost,
   never replayed; the area-aura and non-base teleport/summon holes were latent (nothing in `src`
   registers those handlers); SQLite only until hosted CI; the packet order and several vmangos
   behaviours were applied from recollection, not re-checked, and have no real-client capture;
   deliberate difference: a `CreateItem` reward that does not fit refuses the turn-in. See
   `quest-progression.md` and `quest-settlement-async.md`.
6. **Economy recovery contract depth. Delivered; exact limits remain.** `economy.md` is finished.
   Limits: single writer (an external online insert of a new auction is invisible until restart and
   an external edit converges only when an operation on it is refused); mailboxes can be torn
   (separate reads) and mail has no reserved-id recovery; expiry backoff is in memory; a poisoned
   expired auction stays locked until an operator repairs the cause; the snapshot is proved on SQLite
   locally and on MariaDB/PostgreSQL only by hosted CI (real MySQL unqualified); no bidder index and
   the ledger is never pruned; fidelity to a real 1.12.1 client is unproved.
7. **Spell acceptance/depth.** Category-only cooldown client UI remains unproved;
   death/logout/relog aura persistence needs broader coverage. Finish unsupported
   effects/targets and real two-player aura attribution/UI acceptance.
8. **Content and bounded gameplay completion.** Finish the unified content importer
   CLI and supported dump mappings; import user-supplied full world data, DBC and
   terrain/collision/navmesh assets without committing proprietary/GPL data.
   Follow each area document for profession locks, NPC metadata/taxi/trainer/
   vendor/bank/spirit healer flows, quest categories/objectives, creature EventAI,
   pathfinding/fallback and instance reset/boss/respawn-state limitations. Missing
   extracted collision data currently permits documented fallback behavior; green
   synthetic CI does not validate real maps. Review M7-M14 acceptance documents
   and `docs/ROADMAP.md` before marking a whole milestone complete.
9. **Real-client acceptance**, detailed below. Fix acceptance failures before
   dependent gameplay expansion.
10. **M15 clustering**, still planned: ownership/session seams and durable fences
    (M15.0), gateway/one worker and versioned gRPC (15.1), realm identity/social
    and distinct instances (15.2), fenced handoff/static placement (15.3), host
    supervision/recovery/drain/capacity placement (15.4), compatible tooling,
    content rollout/migration/cross-host acceptance (15.5). Start from
    `docs/CLUSTERING_DESIGN.md`; runtime replacement/deployment require their
    own gates. Battlegrounds, honor, LFG/Warden are outside current delivered scope.

## Client/session context

Nathan deferred computer use. This wave did not launch/control a real client,
acceptance server, alter realmlist, or inspect client/run directories. The supplied
baseline report belongs to his selected file-access session, at server pin
`f8ae6e8b5f805e94f145194a82f80e876fa2dec3`, build5875. It reports realm/auth,
Human male Warrior creation, Northshire entry, 80.5 seconds connected and basic
movement. Physical Escape stopped it before normal 20-second logout, fresh login,
saved-position restoration and restart/relog. Those checks remain pending.

The separate quest UI handoff remains pinned to
`248accc71acbe70144b92c33761d8f9ae0ab07cc`; publishing did not switch that selected
session. Continue only when Nathan resumes the intended client session. Test
NPC greeting/accept/authoritative combat/reward UI, durable reward relog/restart,
two-player visibility/aura ownership, terrain/collision, services, economy and
instance entry/reset behavior. Keep the old report's pin distinct from this
integration tip. Automated socket/SQLite evidence does not make the project
client-accepted or generally playable.
