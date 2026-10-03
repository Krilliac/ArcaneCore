# Economy: mail, auction house, player trade

Characters schema **v10** (reputation 7, instances 8, spell state 9, economy 10). The recovery-contract change documented here adds no schema version: it is a transactional read, in-memory backoff and policy. Auth stays at 2 and World at 8.

Related: [takeover scope and evidence](takeover-20261003.md), [character deletion](character-delete.md), [seams](seams.md), [Claude continuation handoff](claude-handoff-20261003.md).

## Delivered scope

All three systems are one feature, `ArcaneCore.World/Economy/EconomyFeature` (partial classes for core, mail, auction, trade, recovery and deletion), over one persistence contract, `IEconomyStore` (Kernel `EconomyContracts.cs`, EF implementation `EfEconomyStore`). The feature is inert, answering every request with an error, when no economy store is registered.

- **Mail.** Send, take money, take item, cash on delivery, mark as read, return to sender, delete, create text item (letter copy), item text query, next-mail-time query, 30-day expiry (3 days for cash on delivery), mailbox cap, cross-team rules by option.
- **Auction house.** Hello, list, owner list, bidder list, sell (2, 8 or 24 hours), bid, buyout, cancel, and an expiry sweep with sale, won, successful, expired, outbid and cancelled letters, deposit and cut by house (Alliance 2, Horde 6, neutral 7). Auction escrow is an `item_instance` row with owner 0, referenced by exactly one auction or letter.
- **Player trade.** Initiate, begin, busy, ignore, accept, unaccept, cancel, set and clear items and gold, settled as one two-character transaction.
- **Settlements** (`EconomySettlements`). Every item or money movement is one `IEconomyStore.CommitAsync`: a serializable local transaction in a dedicated context, idempotent by an operation id recorded in `economy_operation`, with each participant's durable money and complete inventory checked against its Before snapshot and a final pass proving that each touched item exists exactly once. Actors are frozen for the operation, at most 16 operations run at once, and an acknowledgement lost after commit resolves through a ledger read to Before, After or Unknown.
- **Character deletion.** `EconomyCharacterCleanup` runs inside the deletion transaction (returns or deletes letters, clears cash on delivery, removes unbid auctions with their escrow, keeps bid auctions and clears bids the character held). `OnCharacterDeletedAsync` then re-reads what was touched into the caches.
- **Configuration** (section `Economy`, `EconomyOptions`): postage, expiry days, mailbox size, cross-team mail and trade, letter item, deposit minimum, sweep period, house percentages, neutral auctioneer factions. The percentages are AuctionHouse.dbc-style values to check against an extracted 1.12.1 DBC.

## Ownership and recovery contract

This is the contract the code implements. It states what the feature promises and what it does not. M15 (explicit process ownership, see [CLUSTERING_DESIGN](../CLUSTERING_DESIGN.md)) replaces it.

**Who may write.**

1. One world process owns the economy tables. Its `EconomyFeature` caches live auctions in memory and is the only writer during normal operation.
2. The in-process character deletion cleanup is the one other concurrent writer. It runs in the deletion transaction and the feature re-reads what it touched.
3. Offline edits (an operator editing rows with the server stopped) are supported: the startup load reads whatever is there.
4. Online writes by anything else are not supported. They are detected where it matters, and the cache converges per auction ID instead of staying wrong until restart.

**Reads are snapshots, not two queries.** `GetAuctionSnapshotAsync(AuctionSnapshotFilter)` reads the matching auction rows and their owner-0 escrow items in one transaction. Startup, recovery and the deletion resync all use it.

- MariaDB, MySQL and PostgreSQL use a repeatable-read transaction: an MVCC snapshot from the first statement, with no locking reads.
- SQLite uses a deferred transaction on the underlying `SqliteConnection`. Microsoft.Data.Sqlite promotes repeatable read to `BEGIN IMMEDIATE`, which makes a pure read fail against any writer that holds a transaction open. This was exercised: with the promoted transaction the SQLite snapshot tests fail with "database is locked". A deferred transaction in WAL mode (EF Core's SQLite creator enables WAL) is a true snapshot that neither blocks nor is blocked by a writer.
- `GetAuctionsAsync` and `GetEscrowItemsAsync` remain for tests and for the mail path.

**Reservation.** An auction ID is reserved (`_busyAuctions` plus an entry in `_auctionRecoveries`) when its true state is not known. A reserved auction is not listed, bid on, cancelled or expired, and its cache entry is only ever patched, for that ID alone, from a fresh snapshot. Reservation happens when:

| Cause | Reservation ends when |
| --- | --- |
| A commit outcome is Unknown (acknowledgement lost, ledger unreadable) | a snapshot read repairs the cache (row gone, or row with a matching escrow item) |
| A same-ID update or delete is refused (`Before`): the row no longer equals the cached `Expected`, or a participant mismatched | the same |
| The row exists but its escrow item is missing or differs in GUID, entry or count (startup, deletion resync, any recovery read) | a later snapshot finds a matching escrow item |

`Before` covers a storage Conflict, `CharacterMissing`, and a commit that threw with a ledger read saying it did not commit (timeouts and serialization failures included). Reserving on all of them is safe: the cost is one extra read, and the read finds the same row.

**Recovery timing.** Reads run on a one-second timer even when the expiry sweep is disabled. A failed read retries on the next tick for the first three failures, then backs off 2, 4, 8 and so on seconds up to 300. A mismatch waits 30 s, then 60, 120, 240 and 300 s, on the feature clock (`TimeProvider`), and logs an Error once when it enters the mismatch state. A startup or deletion-resync mismatch waits one backoff before its first read, because the caller has just observed it. A character deletion that touches a reserved auction replaces its entry with a fresh one, so the post-deletion read is immediate. A read in flight carries the identity of the entry that started it and cannot publish over a newer one.

**IDs.** Mail, auction and item-text IDs come from in-memory allocators seeded from `GetIdSeedAsync`, read after the startup snapshot so every loaded ID is at or below the seed. When an operation that inserts an auction or a letter is refused (`Before`), the allocators are re-seeded from storage (raised, never lowered), so an ID taken by another writer stops colliding. Only the colliding operation fails.

**Expiry.**

- An auction whose expiry settlement is refused (`Before`) is skipped by later sweeps with a backoff of 60 s doubled per failure, up to one day. The base is a constant, independent of `ExpirySweepSeconds`. The backoff is in memory only, is cleared when the auction settles or leaves the cache, and is cleared when a recovery read shows the row changed. A poisoned auction (for example one whose seller has no `characters` row in a deployment without a character directory) therefore cannot starve later expired auctions.
- A sweep stops at the first operation that is not started (the 16-operation settlement cap, or shutdown) instead of being refused and logging a warning for each of up to 50 candidates. A sweep can briefly fill every settlement slot, so a player mail, bid or trade started during it can be refused and must be retried.
- An expired auction can no longer be cancelled by its seller; the answer is the one given for an unknown auction. It belongs to the sweep, which returns the item as an Expired letter. Consequence: an expired auction whose settlement keeps being refused stays locked, with its escrow intact, until an operator repairs the cause. The seller's cancel is no longer a way out.

## Evidence

Tests added or reworked for this contract (SQLite unless noted):

| Test | Proves |
| --- | --- |
| `AuctionRecoveryTests.Conflict_after_external_same_id_update_resyncs_only_that_auction` | an external same-ID write converges the cache for that ID and no other |
| `AuctionRecoveryTests.Known_commit_releases_and_conflict_resyncs_then_releases` | replaces the old test that required no reservation after a conflict; a conflict now reserves, resyncs, releases, and the next update commits |
| `AuctionRecoveryTests.Conflicting_expiry_backs_off_and_does_not_starve_later_expired_auction` | 16 always-refused expired auctions do not block a later good one |
| `AuctionRecoveryTests.Startup_reserves_auction_without_matching_escrow_and_lists_it_after_repair` | a startup mismatch is reserved, not dropped, and lists after repair |
| `AuctionRecoveryTests.Escrow_mismatch_backs_off_on_the_feature_clock` | a mismatch is not re-read every second; one read per backoff step on the injected clock |
| `AuctionRecoveryTests.Auction_id_collision_with_an_external_insert_refuses_that_operation_and_reseeds_the_allocator` | an ID taken by another writer refuses one operation and raises the allocator |
| `AuctionRecoveryTests.Deletion_resync_reserves_a_kept_auction_whose_escrow_does_not_match_instead_of_dropping_it` | the third silent-drop site now reserves |
| `EconomyAuctionSnapshotTests` | filters, mismatch shape, caller-transaction refusal, context left reusable, SQLite behavior beside an open writer, no tear against a release between the two statements, and the legacy two-query read torn under the same interleaving as the positive control |
| `EconomyAuctionExpiryEdgeTests.Expired_auction_cannot_be_cancelled_before_the_sweep` | native client wire: cancel after expiry is refused, the row stays, the sweep sends the Expired letter |

The existing lost-acknowledgement and deletion-resync tests are kept. Their store double now hooks the snapshot read and each asserts that its hook fired. The `failEscrow` case became "row readable, escrow does not match, then repaired", driven on an injected clock.

The two interleaving theories in `EconomyAuctionSnapshotTests` run over the available providers: SQLite always, MariaDB and PostgreSQL when `ARCANECORE_TEST_MARIADB` and `ARCANECORE_TEST_POSTGRES` are set, as hosted CI does (MariaDB 10.11, PostgreSQL 16). A local run proves SQLite only; the repeatable-read path is proved on MariaDB and PostgreSQL only by a hosted run.

## Limits

- **Single writer.** Online external inserts of new auctions are invisible until restart (the cache is loaded at startup and patched per reserved ID only). An online external edit of a cached auction converges only after an operation on it is refused or a character deletion touches it; until then the cache can serve stale data.
- **Mailboxes can be torn.** `LoadMailbox` reads letters and their item data in separate queries, so a mailbox cache can be torn, not only stale, until the next mailbox open. Mail has no reserved-ID recovery.
- **Backoff is in memory.** Expiry backoff is lost on restart (a poisoned expiry is retried once after restart). The reserved set is re-derived from storage only for startup mismatches.
- **A poisoned expired auction stays locked** until an operator repairs the cause (see Expiry).
- **Expiry uses settlement capacity** and can briefly refuse other operations.
- **Provider evidence.** The snapshot is proved locally on SQLite only. MariaDB and PostgreSQL rely on the repeatable-read transaction and are proved only by hosted CI. Real MySQL is unqualified.
- **Indexes and the ledger.** There is no index on the bidder column and the ledger (`economy_operation`) is never pruned. Either fix needs a coordinated characters v11.
- **Fidelity.** House percentages, postage and durations follow vmangos/cMaNGOS behavior (re-implemented, GPL code not copied) and still need acceptance against a real 1.12.1 client and an extracted DBC. Mail, auction and trade packets are verified by byte-layout tests and the native mock client, not by a retail client.

## Shared-file edits

Economy is isolated under `*/Economy/`. Edits outside those folders:

| File | Change | Commit |
| --- | --- | --- |
| `src/ArcaneCore.World/Features/WorldFeatures.cs`, `src/ArcaneCore.World/Handlers/PlayerHandlers.cs` | settlement-barrier seam registration; the mail-time handler moved to the economy feature | `23d8850` |
| `src/ArcaneCore.World/Characters/CharacterDeleteHooks.cs`, `src/ArcaneCore.World/Handlers/CharacterHandlers.cs`, `src/ArcaneCore.World/Npc/QuestNpcFeature.cs` | settlements drain before character login and deletion | `b2edc8c` |

`IEconomyStore` gained `GetAuctionSnapshotAsync` (with `AuctionSnapshot` and `AuctionSnapshotFilter`) in `src/ArcaneCore.Kernel/Economy/EconomyContracts.cs`. Any other implementer of the interface must add it.

## Provenance

| Commit | Content |
| --- | --- |
| `866158e9b5eae41c17afc3f45ba364382a540398` | Game: mail, auction and trade packets, staged inventory transfers |
| `438756eabfdf4a0f9343b272c68f70e71ccf5cef` | Game tests: economy rules, packet layouts, staged transfers |
| `87589c0553b19808e168c266c71f5005cee88d9b` | World: settlements (frozen actors, idempotent commit, reconciliation) and access checks |
| `b1f3b603aef2305ff0d61672fa1ec03904ebf00c` | World: feature core and opcode handlers |
| `57c8a8c997880ced033f558c63a448fd5ac8953a` | World: mail |
| `dd31d9cb341057aeb905c6e8110e564bf7487522` | World: auction house and the character-delete hook |
| `738eacb175cb6c8f3171288c71d5aad27a08546b` | World: player trade as one two-character transaction |
| `23d8850a8630aaeefd2b307eb4488c4f1dad5c66` | World: shared wiring (the head of source PR #18) |
| `0323c03c2204def7e667a9987f14c0a7d5f4e329` | Refuse seller deletion when the scanned auction has changed |
| `b2edc8c5c5fb2a30441d3b4cbb1b4f234bdc052a` | Drain settlements before character login and deletion |
| `697869ad241b10f5e710e7779064679197cbc58b` | Recover quarantined auctions without stale deletion publication |

The contract depth documented above (snapshot read, convergence on conflict, startup and resync mismatch reservation, ID reseed, expiry backoff and the cancel rule) is the change on branch `claude/ac-6-economy-contract`, stacked on `c3dea16292756e146df0ddd0783f97fee734dd5c`.
