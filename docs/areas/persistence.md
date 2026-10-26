# Area: Character persistence under load

Lane `claude/w19-persistence`. How character snapshots reach the database when a couple of hundred players (or bots) are online, what
the queue guarantees, and which knobs and log lines tell an operator that it is falling behind. Everything here is ArcaneCore's own
persistence design: vmangos saves players synchronously through its async-query worker threads and has no equivalent queue, so nothing
in this page claims retail fidelity. The charter rule it serves is "never lose a save".

Code: `src/ArcaneCore.World/Persistence/CharacterSaveQueue.cs`, `src/ArcaneCore.World/Spells/SpellbookCache.cs`,
`src/ArcaneCore.Data/Resilience/SqliteConnectionPragmaInterceptor.cs`, `src/ArcaneCore.Data/Stores/EfCharacterStore.cs` (the
action-button diff), `src/ArcaneCore.Game/Maps/WorldRuntime.Autosave.cs` (`Autosave`), `src/ArcaneCore.World/Npc/QuestNpcFeature.Rewards.cs`
(settlement timing). Tests: `tests/ArcaneCore.World.Tests/Persistence/CharacterSaveQueueTests.cs`,
`SaveQueueLoadBenchmarkTests.cs`, `tests/ArcaneCore.World.Tests/Spells/SpellbookCacheBatchingTests.cs`,
`tests/ArcaneCore.Game.Tests/Maps/WorldRuntimeAutosaveTests.cs`, `tests/ArcaneCore.Data.Tests/Resilience/SqliteConnectionPragmaTests.cs`,
`tests/ArcaneCore.Data.Tests/CharacterStoreActionButtonDiffTests.cs`, `tests/ArcaneCore.MockClient.Tests/QuestSettlementResponsivenessTests.cs`.
The diagnostics half of the lane (hang detail in the tick watchdog) is in [watchdog.md](../ops/watchdog.md).

## What went wrong under 200 live bots

A 200-bot run showed the character save queue 19 s or more deep, 71 quest reward settlements failing with `TaskCanceledException` while
saving their pre-reward snapshot, 39 more refused with "settlement capacity reached", and world ticks stalling for 2-9 s with nothing in
the log to say where. The causes, each of which this lane removes or exposes:

- The queue was one consumer over an unbounded FIFO with no per-character coalescing, and the periodic autosave queued a snapshot of every
  online player every `World:AutosaveIntervalMs` whether or not the previous one had been written. Level-ups and boosts queued more.
  A full snapshot costs well under a millisecond on disk, so the backlog was copies and waiting, not I/O.
- A settlement's pre-reward save sat behind that FIFO and its 5 s budget token expired before the save started; the cancellation
  surfaced as a "saving character failed" error although nothing had failed.
- About fifteen independent background writers share `characters.db` (save queue, spellbook, skills, talents, reputation, quests,
  explored zones, rest, taxi, honor, spell state, pets, playbot checkpoints, settlements). `Microsoft.Data.Sqlite` reacts to
  `SQLITE_BUSY` with its own retry loop that sleeps a fixed 150 ms per attempt, so a writer is admitted up to 150 ms after the lock
  frees and the writers starve each other.
- Each learned spell was its own transaction.

## Save queue

`CharacterSaveQueue` is still one consumer writing through a scoped `ICharacterStore`; what changed is what it holds. State lives in one
`Slot` per character under a single lock that is never held across an `await` or a store call. `Pending` counts characters with a
snapshot waiting or being written (the gauge `arcanecore.db.save_queue_depth`).

### Per-character coalescing

- **Latest wins.** `Enqueue` copies the snapshot on the calling (world) thread. If a snapshot of the same character is still waiting, the
  new one replaces it atomically; the queue holds at most one waiting snapshot per character, so its depth is bounded by the number of
  characters, not by the number of events.
- **Optional sections are inherited.** `CharacterState` has optional sections (action buttons, skills, talents, ... the same set
  `Merge` already handled for a retained failed save). A newer snapshot that leaves a section `null` takes that section from the
  superseded one, so superseding never loses a section that only the older snapshot carried. The same merge applies to a retained
  failure when the slot is written.
- **A write in flight is never replaced.** When the consumer takes a slot it moves the snapshot out of the slot and marks the slot in
  flight. A snapshot arriving meanwhile becomes the next waiting one and follows the write. A character's saves therefore never overtake
  each other, and the snapshot that is on disk is never older than one already confirmed.
- **Hold, quarantine and failure retention are unchanged.** `Hold` suppresses new ordinary snapshots until `Resume`; `Quarantine` refuses new snapshots
  and drops a waiting one when the consumer reaches it (its settlement waiters fail with "quarantined"); a snapshot that fails after `MaxAttempts` (3) is retained as the authoritative one and merged
  into the next write for that character. The final drain at `StopAsync` still writes every waiting slot.

### Priority lane and the 1-in-8 ordinary pick

`SaveForSettlementAsync` (the pre-reward save of a quest, vendor, loot or economy settlement) and `FlushCharacterAsync` (a barrier) add the
character to a **priority lane**. The consumer takes from that lane first, so a settlement no longer waits behind unrelated
characters' autosaves. To keep the lane from starving ordinary snapshots, **every eighth pick is an ordinary one** (`PriorityBurst` = 7
consecutive priority picks, then one ordinary pick if any is waiting). Ordering is per character only, as before.

- A settlement's snapshot is merged over any snapshot of the same character already waiting (callers capture it on the world thread and
  hold the character in the same critical section, so a waiting one is always older).
- If a settlement's token fires **before its write starts**, the returned task is cancelled, nothing is logged as a failure, and the
  snapshot stays queued as an ordinary one: the newest snapshot is never dropped. Once the write has started, cancellation is
  cooperative as before.
- Several settlements for one character that share a write do not cancel each other: the write runs without a token when more than one
  settlement is attached.
- `QuestNpcFeature.Rewards` now bounds `FlushCharacterAsync` with the same budget token (`WaitAsync(budget.Token)`), so the budget governs
  the whole pre-reward drain and cancellation of the wait never removes retained state.
- **Busy pause.** After a "database is locked" failure the queue pauses (see [A locked database does not stall the write queues](#a-locked-database-does-not-stall-the-write-queues)).
  The pause holds back ordinary snapshots only: a settlement or barrier is taken at once (`TakeFreshPriority`) unless its own character's
  snapshot is the one retained by the lock, or the backoff has reached its top (`BusyStormStreak` = 3 locked writes in a row). In a lock
  storm every attempt would sit out the whole write bound on the one consumer and a burst of settlements would queue behind each other's
  doomed writes, so then they wait out the pause like the rest.

### Settlement budgets

`Quests:SettlementBudgetSeconds`, `Economy:SettlementBudgetSeconds` and `GameObjects:LootSettlementBudgetSeconds` (shipped **15 s**, valid
1-300) bound one settlement end to end: the pre-reward save, the drains, the reward transaction. A settlement can meet up to three lock
waits in a row, each typically one `Database:SqliteWriteTimeoutMs` (5 s; a write-gate wait gives up after that long without a hand-off, or after 20 s of active waiting at most (20 s is 4 x the characters database's 5 s bound; a connection with another bound caps at 4 x that bound, never beyond 30 s unless the bound is larger), and the settlement's budget token ends any wait it is passed to sooner): an unrelated character's write already in flight on the queue's
one consumer (the priority lane cannot overtake a write that has started, and SQLite's busy wait cannot be cancelled), its own save, and
the transaction. The budget used to be 5 s, equal to one write bound, so a single ordinary write sitting in the busy wait ahead of it used
up the whole budget: on the live server on 2026-10-16 the settlement of character 3262 for quest 761 timed out at 23:54:09.424 with its
own save never written ("saved at -1 ms"), while the queue's backlog was 15 characters and no autosave snapshots were queued; the busy
pause opened only afterwards (23:54:09.546). `CharacterSaveQueueTests.ASettlementBehindAnOrdinaryWriteStuckInTheBusyWaitStillFinishesWithinItsBudget`
replays that order with the shipped values.

### Autosave: one timer per character

The periodic save is `WorldRuntime.Autosave()` and runs at the end of the tick body (`SaveAll()` is unchanged and still used for the
final save). Each online character has its own timer, as vmangos counts `Player::m_nextSave` down from `PlayerSave.Interval` in
`Player::Update`: it is snapshotted once per `World:AutosaveIntervalMs`. A character's first save comes between half an interval and an
interval after the tick that first sees it, at a point fixed by its id (`AutosaveStagger`, Fibonacci hashing), so characters that
entered the world together (a server start, a bot fleet) are spread over the interval instead of all being queued in one tick, as the
old whole-world pass did (one write per online character at once). The next turn is one interval after the last one, not after the
tick that ran it, so the spread survives late ticks; a stall longer than an interval skips the missed turns. A logout drops the timer;
the next login starts a new one.

Differences from vmangos: vmangos also staggers the first save, but randomly over a wider window (`Player::Player` sets
`m_nextSave = urand(interval / 2, interval * 3 / 2)`), where ArcaneCore uses a deterministic point in `[interval / 2, interval)`; and vmangos'
`SaveToDB` resets `m_nextSave` after any save, where here a settlement or other save does not restart the autosave timer.

A character whose previous snapshot is still **waiting** (`CharacterSaveQueue.IsPending`: a snapshot in flight does not count) or whose
quest settlement is pending is skipped until its next turn: the queue is behind in exactly that case, and another snapshot would only
supersede the waiting one at the cost of a copy. The timers are scanned only when the earliest one is due or a player came online since
the last scan, so most ticks do no autosave work. A tick that scanned records the time the scan took and the number of snapshots it
queued (`TickPhases.AutosaveMicros` / `AutosaveSnapshots`, shown in the slow-tick detail).

### Backlog warnings

The consumer (and `Enqueue`) check the backlog at most once per second and warn through the log:

| Key | Default | Meaning |
|---|---|---|
| `World:SaveQueueWarnDepth` | `1000` | Warn while this many characters or more have a snapshot waiting or being written. 0 disables the depth check. |
| `World:SaveQueueWarnAgeMs` | `5000` | Warn while the oldest waiting or in-flight snapshot has waited this long. 0 disables the age check. The default is the characters database's write bound (`Database:SqliteWriteTimeoutMs`): a snapshot that old has waited out at least one write-gate bound (5 s without a hand-off). |

The warning is `character save backlog: N characters pending, oldest waiting M ms (warns at ...)`, repeated at most every 30 s while
the backlog persists, followed by one `character save backlog cleared: ...` line (Information) when it ends. An idle queue wakes once a
second so the cleared line is not delayed until the next save. Both keys are checked by `check-config` (integer, 0 or more) and listed in
the generated [configuration reference](../reference/configuration.md). The age is also exported as the gauge
`arcanecore.db.save_queue_oldest_age` ([metrics](../ops/metrics.md)) and appended to every watchdog hang line. Both keys are live: `.reload config` applies them at the next once-a-second check.

## SQLite busy timeout

`SqliteConnectionPragmaInterceptor` (added to the SQLite case of `DataServiceCollectionExtensions.ConfigureProvider` only) runs
`PRAGMA busy_timeout = N` on every connection EF opens. SQLite's own busy handler then waits inside the call and retries within a few
milliseconds, instead of reporting busy at once and leaving the wait to `Microsoft.Data.Sqlite`'s fixed 150 ms sleeps.

| Key | Default | Meaning |
|---|---|---|
| `Database:SqliteBusyTimeoutMs` | `5000` | 0 to 60000 ms. 0 leaves SQLite's "fail at once" behaviour. |
| `Database:<Component>:SqliteBusyTimeoutMs` | unset | Per component (Auth, Characters, World); unset inherits the shared value. |
| `Database:SqliteSynchronous` | `Full` | `Full` or `Normal` (any case; anything else is a start-up validation error). Applied as `PRAGMA synchronous` by the same interceptor on every connection, and `Normal` only to a database in WAL mode (checked with `PRAGMA journal_mode` at every open): otherwise that database stays on `Full` and an error is logged once per file. With `Normal`, an empty database (`page_count` 0: a new file, or a zero-byte file that already existed) is switched to WAL on open before the check, on any open including a validate-only start, so the first open of a new install no longer logs a false refusal (EF's create switches a new file to WAL right after that open, and the schema bootstrap repeats the switch once it has accepted the schema; startup completed either way, and no manual `PRAGMA journal_mode=WAL` is needed). A populated file is never switched by the interceptor. See "Decision" below for the durability trade-off. |
| `Database:<Component>:SqliteSynchronous` | unset | Per component (Auth, Characters, World); unset inherits the shared value. |

The timeout never exceeds the connection's own `Default Timeout`, and when the database guard is on (`Resilience:Database:Enabled`, `QueryTimeoutMs` above 0) it is **clamped, silently, to `Resilience:Database:QueryTimeoutMs`** (the effective value is `min(configured, QueryTimeoutMs)`; a configured value above it is not an error). Reason: the guard abandons a call past its deadline, but the abandoned call keeps waiting in SQLite's busy handler and disposing its scope blocks until it returns, so a longer busy timeout lengthened every refused logon (measured 7.0-7.6 s against a 2 s deadline before the clamp). `DatabaseGuard`'s pessimistic-timeout contract (the outer bound on
a whole operation) is unchanged. MariaDB, MySQL and PostgreSQL ignore the key. Out-of-range values are a `check-config` error and the
interceptor refuses them at start.

### A locked database does not stall the write queues

A writer that holds the SQLite file for a minute (the 2026-10-11 incident) used to hold the single save consumer for ~97 s: each save waited the
connection's 30 s `Default Timeout` and was retried inline three times, while every other character save and quest reward waited behind it.

| Key | Default | Meaning |
|---|---|---|
| `Database:SqliteWriteTimeoutMs` | `5000` | SQLite, characters database only. 0 to 60000 ms (rounded up to whole seconds). The longest any command on a characters connection waits for another connection's write lock. Written into the connection string's `Default Timeout` when `ConfigureProvider` builds the options (it replaces a longer value and 0, unlimited), because a command copies that timeout when it is created, which for ExecuteUpdate/Delete and raw SQL is before EF opens the connection; the interceptor repeats it at open and derives `busy_timeout` from it, so `busy_timeout` is clamped to it too. 0 keeps the connection string's timeout. |
| `Database:Sqlite:Checkpoint:Enabled` | `true` | SQLite file databases only (no effect on MariaDB, MySQL, PostgreSQL, in-memory or read-only SQLite). When true, every connection EF opens sets `PRAGMA wal_autocheckpoint = 0` and `journal_size_limit`, and `SqliteCheckpointService` (a hosted service, one loop per distinct database file among Auth, Characters and World) checkpoints on its own short-lived connection off the world thread: `wal_checkpoint(PASSIVE)` every `IntervalMs`, and one `TRUNCATE` at host stop, after the world has drained its saves. Why: SQLite's own auto-checkpoint runs inside whichever commit crosses 1000 pages (about 4 MB) and fsyncs the main database file there, which stalled a game save (`EfCharacterStore.SaveStateAsync`) for 2.7 to 3.3 s on the live server even under `synchronous=NORMAL`. There is deliberately no TRUNCATE during operation: it takes the exclusive writer lock and holds it through the copy of the frames written since the last PASSIVE, the WAL fsync and the database fsync, and a PASSIVE that reports a full backfill does not prove the database file was synced (SQLite syncs it only when the copy reaches the live end of the log, so a commit landing mid-copy skips it; the later sync then flushes every page earlier runs wrote, on Windows up to the whole WAL). `journal_size_limit` trims the WAL file when the log restarts (continuous overlapping readers can postpone that); a warning is logged when the file stays above 4 x max(limit, 1 MiB) for 30 s while commits keep arriving or the copy is incomplete. PASSIVE takes no writer lock and waits on no lock, but its I/O still competes for the same disk. If Auth is a file shared by the world and realm daemons, either daemon's shutdown TRUNCATE can briefly stall the other's auth writes. The shutdown TRUNCATE waits at most about 250 ms for readers and writers and holds the writer lock through its copy and syncs. A warning is logged (at most one a minute) when checkpoints have not caught up for 30 s, i.e. a reader or writer is holding the log open. False restores SQLite's default. |
| `Database:Sqlite:Checkpoint:IntervalMs` | `2000` | 100 to 3600000. |
| `Database:Sqlite:Checkpoint:JournalSizeLimitBytes` | `33554432` | 0 to 1099511627776 (32 MiB default). `PRAGMA journal_size_limit` on every connection: the writer that restarts the log after a full checkpoint truncates the file to this size. This, not a TRUNCATE, is what shrinks the -wal file while the server runs. |
| `Database:SlowTransactionWarnMs` | `2000` | All providers. A transaction open longer than this is logged at WARN when it commits, rolls back or fails, naming the DbContext, the code that closed it, and the time held by the application before the end call versus the time inside the commit/rollback call (WAL fsync = disk I/O). A transaction disposed without commit or rollback is logged the same way (via EF's `TransactionDisposed` diagnostic event; EF has no interceptor hook for it) (`SlowTransactionInterceptor`). 0 disables. |
| `Database:SlowCommandWarnMs` | `2000` | All providers. A single command whose execution takes at least this long is logged at WARN with the DbContext, duration, autocommit versus inside a transaction, write versus read, the first 80 characters of the SQL (parameter values are never logged; in the SQL text single- and double-quoted text and numeric literals print as `?`, comments are dropped, a backslash escapes the next character inside quotes, and the only double-quoted names kept are table names straight after FROM, INTO, UPDATE, JOIN or TABLE) and the calling code (`SlowCommandInterceptor`). It covers what the transaction warning cannot see: autocommit writes (single-command `SaveChanges`, `ExecuteUpdate`/`ExecuteDelete`, raw SQL) and commands that fail as "database is locked". Identical warnings (same caller and SQL prefix) are limited to one per 10 s and the next one states how many were suppressed since the previous warning for that caller. A `WITH ...` statement is classified by its main statement. Cost: registering any command interceptor makes EF build command event data for every command on every database (EF's logging-suppression shortcut is off while one is registered), regardless of the threshold; the interceptor's own check is one comparison. 0 disables. |

- **Interaction with `busy_timeout`.** `busy_timeout` makes SQLite wait inside one call; the driver repeats the call until the command timeout has elapsed. Because the write timeout is already in the connection's `Default Timeout` when the pragma is set, `busy_timeout = min(SqliteBusyTimeoutMs, write timeout)` on a characters connection, so with the defaults a locked command fails after about 5 s (previously 30 s). The driver ignores cancellation tokens during the wait, so a timeout is the only bound. Auth and World connections are not bounded.
- **`CharacterSaveQueue`.** A busy/locked failure (SQLite 5/6, also inside `DbUpdateException`) is not retried inline. The snapshot is retained, the ordinary snapshots pause (1 s, 2 s, then 5 s while it keeps happening; reset by the next successful write; settlements and barriers of other characters are not held by the first two steps, see [Priority lane](#priority-lane-and-the-1-in-8-ordinary-pick)) and the consumer moves on. Retained snapshots are retried when the queue is idle and at least every 16th pick, by a barrier (`FlushCharacterAsync`, which waits until the snapshot is written or its token fires), or by a newer snapshot of the character (merged over it). Shutdown ignores the pause and keeps the three inline attempts per snapshot with their 200/400 ms backoff (a locked database included, since the drain has nothing else to do), then tries each retained snapshot once more in the final pass, exactly as before; what is new is that no attempt starts, and no retry follows a failure, once the save queue's own stop-drain lane (`ShutdownDrain.Saves`, 15 s = three characters `busy_timeout`s of completed attempts that landed nothing since the last save that did) has stalled. Such a snapshot is retained with a `StopDrainExhaustedException` and named; one that was attempted keeps its real failure.
- **Other queues.** Creature respawn and keyed (GM audit, groups, last address) queues no longer repeat a lock wait inline: the write stays retained and their retained-retry timers (30 s) land it. The honor, reputation, taxi, rested-state, explored-zones and social queues keep three inline attempts per write (about 5 s each under the characters write bound when nothing hands off; one attempt can last up to the 20 s hard cap while the gate keeps handing off to other waiters) and share `RetainedRetryTimer`: every 30 s while the queue holds a retained write, one sweep marker is posted into the queue's single consumer (never stacked), which makes one attempt per retained character or key and stops at the first locked database, so a lock outlasting the inline attempts costs about one 5 s wait per 30 s and the write lands once the lock clears, with no new trigger and no one online. The sweep runs on the consumer, ordered with every other item, and never replaces a newer value. Shutdown still drains and names whatever is not durable, bounded by **stall**, not by work: once a queue's `StopAsync` starts, every write gets exactly one attempt (a queued write when the consumer reaches it, a retained write once more in the final pass), busy or not, with no inline retry. All feature queues share one `StopDrainBudget` lane (`ShutdownDrain.Features`, a singleton), and the character save queue has its own (`ShutdownDrain.Saves`), begun by `WorldHost.StopCoreAsync` before the features stop and again before the save queue stops. A lane counts only attempts that have **ended** without a write landing, as the wall time they covered since the last write that landed on the lane (an attempt still running counts nothing until its verdict, so a slow write that will land never costs anyone; overlapping failed attempts count once; retry backoff and idle time count nothing). When the feature lane has accumulated 10 s of that stalled time (two characters `busy_timeout`s) it is exhausted: no later attempt on it starts at all, each such write is retained with a `StopDrainExhaustedException`, named in the `InvalidOperationException` the queue's `StopAsync` throws, counted in the queue's `write-dropped-on-shutdown` report (`refusedByStopDrain=`, `stalledMs=`) and recorded once per queue in the fault ledger (the host only logs each lane's summary). A refused write is never a verdict: the taxi queue keeps the route (it abandons only on a real, non-busy store failure), a social or instance write keeps its place, nothing is converted or dropped. The true worst case is therefore: a healthy drain runs to completion however many writes it holds (a large rest or save drain is never cut); a drain against a database that stays locked costs at most the budget plus the one attempt that crosses it per lane, 10 s + 20 s (the gate's hard cap) when the gate keeps handing off, about 10 s when a foreign process holds the file, plus the attempt a queue was in the middle of when its stop began (at most one hard cap per such queue, as before), and that figure does not add up across the feature queues because the lane is shared: after the first queue exhausts it the others make no attempt. The lane starts on the first `Begin()` anywhere (the host's, or the first feature queue's `StopAsync`); a feature stopped early would start it early. No feature owner detaches today. The save queue's lane is separate (15 s) so player saves always get a fresh budget after the features, and keeps base's inline retries while it lasts. `HostOptions:ShutdownTimeout` (60 s, `appsettings.json`) is **not** a cut-off: the .NET host only cancels the token it passes to `WorldHost.StopAsync`, which does not forward it to the queues, and the host awaits each service's stop to its end (verified against `Microsoft.Extensions.Hosting` 9.0.0, `Host.StopAsync`/`ForeachService`). Before this bound one locked write could take 3 x 20 s + 0.6 s alone, per queue. A taxi save with an abandon fallback keeps the route (it is not abandoned) when the failure is a locked database. The hunter pet writes (`SummonService`, one serialized chain per character, no inline retries) retain a failed snapshot unless a later save or abandon of the character is already queued behind it: a newer save replaces it, an abandon or character delete discards it, and the per-character barrier `FlushCharacterAsync` (login, character delete, cluster handoff, GM pet commands) and `StopAsync` try it once more and fail while it is still not durable, so a login never reads back the older `character_pet` row. There is no retained-retry timer: with no barrier and no newer save, the snapshot waits in memory until the next login or shutdown.

### Decision: keep `BEGIN IMMEDIATE`, WAL and `synchronous=FULL` by default (`NORMAL` is opt-in)

The obvious ways to make a write-heavy SQLite file faster were considered and **rejected**; two tests pin the current behaviour so a later
change has to be deliberate.

- **WAL** (`PRAGMA journal_mode=WAL`, applied by the schema bootstrap): kept. Readers never block the writer and the writer never blocks
  readers. `WalAndSynchronousFullArePinned` asserts it.
- **`synchronous=FULL`** (SQLite's default, value 2): kept. In WAL mode `NORMAL` skips the fsync at commit and can lose the most recent
  committed transactions on power loss or an OS crash. The charter rule is "never lose a save", and the save queue reports a snapshot as
  written once the store call returns, so a commit that can silently vanish would break the retained-failure logic that depends on it.
  It is also not where the time goes: a full snapshot measured about 0.7 ms on disk (`SaveQueueLoadBenchmarkTests`, below).
  **Opt-in `NORMAL`.** On a disk where the WAL fsync stalls for seconds (`SlowTransactionInterceptor` reports it as "N ms inside the end call"
  and the stall makes other writers fail as "database is locked"), `Database:Characters:SqliteSynchronous = NORMAL` (or the shared
  `Database:SqliteSynchronous`) removes the fsync at commit. In WAL mode the database stays consistent and an application crash loses
  nothing (the WAL is in the OS cache); a power loss or OS crash can lose the most recent commits, which the save queue had already reported as
  written. `NORMAL` is applied only when the database is actually in WAL mode: the schema bootstrap's WAL switch is best effort (it logs a
  warning and carries on if another connection holds the file), and `NORMAL` in a rollback-journal mode can corrupt the database on power
  loss, so the interceptor fails closed to `FULL` and logs an error (`NormalIsRefusedOnADatabaseNotInWalMode_FallsBackToFull_AndLogsAnError`).
  On the very first start of a brand-new file the bootstrap's own connection opens before the WAL switch and logs that error once; later connections get `NORMAL`. That is an accepted operator decision for that deployment, not the default. Set it only on the databases that need it, e.g.
  `--set Database:Characters:SqliteSynchronous=NORMAL`. `WalAndSynchronousFullArePinned` still asserts the default; `SynchronousIsNormalOnlyWhereConfigured_AndFullByDefault`
  asserts the override. The slow-transaction warning names the setting in effect for the context.
- **`BEGIN IMMEDIATE`**: kept. EF's `BeginTransaction` on `Microsoft.Data.Sqlite` takes the write lock at `BEGIN`, before any statement
  runs. A writer therefore queues at the lock (now waiting inside SQLite under `busy_timeout`) instead of failing later when a read
  transaction tries to upgrade to a write and gets `SQLITE_BUSY` that no timeout can resolve. `EfTransactionsOnSqliteBeginImmediate`
  asserts it.

The per-snapshot cost was also cut: `EfCharacterStore.SaveStateAsync` writes the action bars as a **diff** (unchanged buttons issue no
statement, removed ones are deleted, new ones inserted) instead of deleting and reinserting every button on every save
(`CharacterStoreActionButtonDiffTests` counts the statements on the wire).

## In-process single writer (always on for file-backed SQLite)

SQLite admits one writer at a time, even in WAL mode. About a dozen background queues (character saves, respawns, rest, reputation, social,
honor, taxi, explored zones, instances, keyed stores, bot rows, spellbook) each write to `characters.db` on their own connection, and a writer
that finds the lock taken polls it from SQLite's busy handler (sleeps of 1 to 100 ms, no queue). Under load that polling is unfair: some writers
win again and again while others reach the 5 s write bound and fail as "database is locked" although the total work fits in the time (live
2026-10-17: 246 locked log entries in 24 h, a 1000-character save backlog with the oldest snapshot 130 s old at 2 PM CT).

Since 2026-10-17 (a fix, not a setting; the only key left is `Database:Sqlite:SingleWriter:StatsIntervalMs`, the cadence of its INFO stats line) `SqliteWriteGateInterceptor` puts every EF write behind one in-process gate per
database file (`SqliteWriteGate`, an async FIFO semaphore keyed on the full path):

- A transaction EF begins (`BEGIN IMMEDIATE`, which takes the write lock at once) takes the gate before the BEGIN and gives it back when it is
  committed, rolled back, disposed (EF's `TransactionDisposed` diagnostic event) or its connection closes.
- A write statement run outside a transaction (single-statement `SaveChanges`, `ExecuteUpdate`/`ExecuteDelete` with the fence off, raw SQL)
  takes it around the statement (a reader's statement until the reader is disposed).
- Reads, statements inside a transaction, and transactions adopted with `UseTransaction` (the deferred read snapshots of `ReadSnapshot` and the
  auction snapshot) never take it. WAL readers do not block the writer.
- An async waiter is bounded by progress, not by total elapsed time: it gives up when the gate has handed off to no one for its connection's
  write bound (`Database:SqliteWriteTimeoutMs` on the characters database; every hand-off to any waiter restarts that countdown, measured from the
  hand-off itself) or, as a hard cap, after four times the bound of active waiting (at most 30 s unless the bound itself is larger, so 20 s on the
  characters database). Time the whole process was suspended (GC pauses only) counts toward neither. A blocking (synchronous) waiter keeps the
  single bound as its total (the only blocking write path today is `EfTransferDecisionStore.SeedCharacterOwnership`, which has no production caller; two startup steps block on async store calls with `GetAwaiter().GetResult()`, which take the async path, so on a contended characters database each can block up to 20 s per call instead of 5 s: `HonorFeature` (the maintenance state read at `GetMaintenanceAsync` and, on the very first start, the write `SaveMaintenanceAsync`) and `HonorMaintenanceFeature.Attach` (`RunAsync(live: false).GetAwaiter().GetResult()`, which runs `HonorMaintenanceRunner.RunDueAsync` and its maintenance writes at startup)). A caller's cancellation token or settlement budget supersedes all of these: it ends the wait it is passed to
  earlier, whatever the gate would still allow. A waiter that gives up then fails with SQLite's own busy error (code 5), so every existing "locked: retain and retry" path behaves as before; the
  message names the holder (context type, and the calling method when someone was queued behind it).
- A hold older than 60 s (a transaction that was never ended) is released by the next waiter and logged at ERROR. That only
  lets the next writer try SQLite's lock itself, as it would without the gate.
- A writer that waited 1 s or more logs one WARN; every `Database:Sqlite:SingleWriter:StatsIntervalMs` (default 5 min) each gate logs one INFO line: writes,
  queued writes, mean and max wait, longest hold and its owner, timeouts, stuck holds released, and the lifetime count of holds whose transaction
  ended unseen.
- A hold whose transaction already ended without an event the interceptor saw (the write fence rolls back straight on the `DbTransaction`;
  more often the gap between Microsoft.Data.Sqlite running `COMMIT` and EF raising `TransactionCommitted`) is found by the next waiter, which
  asks SQLite whether the connection is back in autocommit (`sqlite3_get_autocommit`), and released at once (DEBUG log, counted as "ended unseen").

The gate does not shorten a slow holder: a transaction held for 3 s by the application still makes everyone wait 3 s
(`SlowTransactionInterceptor` names those holders). It removes the failures that came from unfair polling, and the wasted sleeps.
The two worst holders on live, `EfManagedPlayerbotStore.UpdateManyAsync` (a LINQ `ExecuteUpdate` per row) and
`EfCreatureRespawnStore.SaveAsync` (tracked read, then `SaveChanges`), now run one raw statement per row on SQLite (`SqliteRowCommand`: SQL
built once from the EF model, values bound through EF's type mappings, still on the EF pipeline so every interceptor sees it), so the lock
covers the statements rather than EF's per-row query work.
Another process on the same file (the realm daemon on a shared auth file, a DB tool) still meets SQLite's lock as before.

`SqliteWriteGateTests.ManyConcurrentWriteTransactions_AllCommit_WithoutABusyFailure` runs 16 concurrent EF writers x 25 transactions, each
holding the lock about 8 ms, against one file with a 300 ms busy timeout (the test runs with the live 5 s write bound). With a 1 s write bound
**33 of 400 transactions failed as locked with the gate off, none with it on** (Debug build, the maintainer's PC, 2026-10-17).

Live (enabled at redeploy #4, 8:31 PM CT 2026-10-17): the previous 24 h log had 246 "database is locked" entries (33 writer failures,
the rest crash-ledger copies), 37 save-backlog warnings (peak 1000 characters, oldest 130 s) and 34 slow transactions. The first minutes with
the gate (first 6 min): 1700-3800 writes a minute on characters.db, 0.5-2.5% queued, max wait 383 ms and longest hold 535 ms during
the startup burst, then waits under 100 ms and holds under 200 ms; no timeouts, no locked errors.

## Spellbook learn batching

`SpellbookCache` writes spell changes on one background task. Each learn used to be its own store call and transaction, so a character
learning 18 spells paid 18 transactions. `EnqueueAdd` now **joins the entry at the tail of the write queue** when that entry is an Add for
the same character that the writer has not taken yet (`Taken` is set under the writes lock the moment the writer dequeues it); the spells
of one tick then reach the store as one call and one transaction whenever the writer is busy or behind.

- It is merged only with the **tail** and only when nothing else sits after it. A forget, a delete, a flush or another character's
  write between two learns ends the run, so every write keeps its position relative to forgets, deletes and flushes. A learn that
  follows a forget is never folded into an Add queued before it.
- An entry the writer already took is not appended to; the next learn starts a new entry.
- `FlushAsync` and `FlushCharacterAsync` still queue behind everything already queued, so a settlement that flushes the spellbook sees
  every spell learned before it.

## Measuring it

`SaveQueueLoadBenchmarkTests` reproduces the live 200-character shape (file-backed WAL SQLite with the live `Pooling=False;Default
Timeout=5` settings, autosave tenths, a level-up burst, quest settlements under a 5 s budget, spell learns) against the real queue and
spellbook. It is **skipped unless `ARCANECORE_SAVE_BENCH=1`**, and a skipped run says so rather than passing silently; set the variable
to run it. The unit tests in `CharacterSaveQueueTests` cover the coalescing, merge, in-flight, priority and burst rules, cancellation
before and after the write starts, hold, quarantine and retained-failure behaviour, and the warning thresholds.

## Not delivered and known limits

- One consumer: coalescing and the priority lane remove the queueing delay, but a single slow store call still delays every other
  character. Parallel writers were not added; a full snapshot costs well under a millisecond on disk, so one consumer is not the limit at 200 characters.
- Other background writers (skills, talents, reputation, quests, ...) keep their own queues and connections; this lane gave them the
  shared busy timeout, and the in-process write gate queues them in arrival order, but their writes are not yet merged into shared transactions.
- The backlog thresholds are warnings only; nothing sheds load or refuses a save when they are crossed.
