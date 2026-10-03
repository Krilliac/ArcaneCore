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

Schema allocations: **Auth2 / World8 / Characters10**. Characters reputation7,
instances8, spell state9, economy10; world GO/loot7 then AI8. Original feature
branches retain provisional numbers. Never apply a source branch's provisional
schema to a database already using the final allocation. New changes require
coordinated forward versions and complete cleanup registration.

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

1. **Deletion outcome recovery.** A durable delete whose acknowledgement throws
   can return false and skip runtime/cache finalizers; a retry for a missing row
   skips them again. Add durable operation identity/reconciliation and real
   commit-then-throw tests. Keep online/ownership checks and caller transaction
   ownership. Redundant queued spell/state/reputation post-delete removal also
   needs a lifetime fence under explicit ID reuse; ordinary generated-ID reuse
   was not established. Do not report the conditional gap as observed client loss.
2. **Reputation failure durability.** Its three-attempt queue drops failed writes
   after dirty state has cleared and a later flush may succeed. Choose a retention,
   retry/quarantine and shutdown contract; prove gain/watched-state persistence
   after repeated failures, relog and restart.
3. **Forward index repair and upgrade parity.** Historical bootstrap filters
   separate index operations. Already-versioned databases can miss owner/spawn
   indexes and the unique guild-member CharacterId index. Use a frozen populated
   released baseline, preserve all feature rows, compare fresh/upgraded indexes,
   allocate forward repair, and test repeated/interrupted/concurrent startup.
   Actual MySQL qualification remains separate from MariaDB.
4. **Durable consumed loot.** Item containers and nonzero-instance chests remain
   deliberately refused. Store generated/remaining/consumed contents atomically
   with awards, tied to the logical instance save across unload/recreation/restart;
   clear only on real reset/deletion. Keep ordinary shared chests working and
   preserve original group gold shares while a recipient is held.
5. **Complete reward collaborators.** Bring permanent item/spell/reputation grants
   into atomic settlement or durable effect intent/recovery. Add destination and
   summon-owner preflight before allowing those rewards. Preserve journal and
   reward availability on refusal; never replay old callbacks onto replacement
   players. Area-aura capabilities share the finite/nonpassive handler guard.
6. **Economy recovery contract depth.** Row and escrow reads are separate queries;
   per-ID quarantine and deletion generations handle local races but do not claim
   an atomic external-writer snapshot. Decide/document external same-ID ownership,
   crash recovery and expiry/trade edge cases. Finish the current economy.md stub
   with delivered scope, limits, shared-file changes and provenance.
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
